using System.Globalization;

namespace DragonHead.Core;

/// <summary>미국 종목 하루 등락. SessionDate 는 미국 동부 날짜.</summary>
public sealed record UsMove(string Symbol, string Name, DateOnly SessionDate, decimal Close, decimal ChangePct, bool Live = false);

/// <summary>
/// 밤사이 미국 반도체. 토스 Open API 는 미국 종목을 티커 그대로 받는다(prices·candles).
/// SOX 지수 대신 SOXX(지수 추종 ETF)를 쓴다.
/// 판단 규칙에는 아직 넣지 않는다 — 판단 기록에 같이 쌓아 두고 나중에 효과를 본다.
/// </summary>
public static class UsSemis
{
    public static readonly (string Symbol, string Name)[] Default =
    {
        ("SOXX", "반도체ETF"),
        ("MU", "마이크론"),
        ("NVDA", "엔비디아"),
    };

    /// <summary>미국 지수 대신 지수 추종 ETF (토스 시장지표 API 는 국내 지수·국채만 지원)</summary>
    public static readonly (string Symbol, string Name)[] Indexes =
    {
        ("QQQ", "나스닥100"),
        ("SPY", "S&P500"),
    };

    public static readonly (string Symbol, string Name)[] All = Indexes.Concat(Default).ToArray();

    public static string NameOf(string symbol) => All.FirstOrDefault(x => x.Symbol == symbol).Name ?? symbol;

    public static IReadOnlyList<UsMove> Semis(IEnumerable<UsMove> moves) => Default.Select(d => moves.FirstOrDefault(m => m.Symbol == d.Symbol)).OfType<UsMove>().ToList();
    public static IReadOnlyList<UsMove> Idx(IEnumerable<UsMove> moves) => Indexes.Select(d => moves.FirstOrDefault(m => m.Symbol == d.Symbol)).OfType<UsMove>().ToList();

    /// <summary>
    /// 미국 일봉의 거래일. 토스가 미국 봉 시각을 어떤 기준으로 찍는지 문서에 없어서,
    /// 동부 날짜로 바꿨을 때 주말이 나오면 하루씩 밀어 보고 주말이 사라지는 쪽을 쓴다.
    /// </summary>
    public static List<(DateOnly date, Bar bar)> Sessions(IReadOnlyList<Bar> bars)
    {
        var raw = bars.OrderBy(b => b.Time).Select(b => (d: EtDate(b.Time), b)).ToList();
        static int Weekend(IEnumerable<DateOnly> ds) => ds.Count(d => d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        int best = 0, bestW = Weekend(raw.Select(x => x.d));
        if (bestW > 0)
            foreach (var shift in new[] { 1, -1 })
            {
                var w = Weekend(raw.Select(x => x.d.AddDays(shift)));
                if (w < bestW) { best = shift; bestW = w; }
            }
        return raw.Select(x => (x.d.AddDays(best), x.b)).GroupBy(x => x.Item1).Select(g => g.Last()).OrderBy(x => x.Item1).ToList();
    }

    /// <summary>미국 동부 시각. 서머타임: 3월 둘째 일요일 02:00 ~ 11월 첫째 일요일 02:00 (현지).</summary>
    public static DateTimeOffset ToEt(DateTimeOffset t)
    {
        var std = t.ToOffset(TimeSpan.FromHours(-5));
        var y = std.Year;
        var start = NthSunday(y, 3, 2).ToDateTime(new TimeOnly(2, 0));
        var end = NthSunday(y, 11, 1).ToDateTime(new TimeOnly(1, 0));   // 표준시 기준 01:00 = 서머타임 02:00
        bool dst = std.DateTime >= start && std.DateTime < end;
        return dst ? t.ToOffset(TimeSpan.FromHours(-4)) : std;
    }

    private static DateOnly NthSunday(int year, int month, int n)
    {
        var d = new DateOnly(year, month, 1);
        while (d.DayOfWeek != DayOfWeek.Sunday) d = d.AddDays(1);
        return d.AddDays(7 * (n - 1));
    }

    public static DateOnly EtDate(DateTimeOffset t) => DateOnly.FromDateTime(ToEt(t).DateTime);

    /// <summary>미국 정규장 (09:30~16:00 ET, 평일) 중인가. 휴장일은 캔들 유무로 걸러진다.</summary>
    public static bool IsOpen(DateTimeOffset now)
    {
        var et = ToEt(now);
        if (et.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        var tod = TimeOnly.FromDateTime(et.DateTime);
        return tod >= new TimeOnly(9, 30) && tod < new TimeOnly(16, 0);
    }

    /// <summary>그 미국 날짜의 정규장이 끝났나</summary>
    public static bool SessionDone(DateOnly etDate, DateTimeOffset now)
    {
        var et = ToEt(now);
        var d = DateOnly.FromDateTime(et.DateTime);
        return d > etDate || (d == etDate && TimeOnly.FromDateTime(et.DateTime) >= new TimeOnly(16, 0));
    }

    /// <summary>
    /// 한국 날짜 krDate 직전에 끝난 미국 정규장의 등락 (그날 한국장이 보고 시작하는 '밤사이 미국').
    /// bars: 미국 일봉(시간 무관, 날짜는 미국 동부로 환산). now: 아직 안 끝난 세션을 빼기 위한 현재 시각.
    /// </summary>
    public static UsMove? Overnight(string symbol, IReadOnlyList<Bar> bars, DateOnly krDate, DateTimeOffset now)
    {
        var done = Sessions(bars).Where(x => x.date < krDate && SessionDone(x.date, now)).ToList();
        if (done.Count < 2) return null;
        var (d1, last) = done[^1];
        var prev = done[^2].bar;
        if (prev.Close <= 0) return null;
        return new UsMove(symbol, NameOf(symbol), d1, last.Close, Math.Round((last.Close / prev.Close - 1m) * 100m, 2));
    }

    /// <summary>지금 열려 있는 미국장 등락 (직전 마감 대비). 열려 있지 않으면 null.</summary>
    public static UsMove? Live(string symbol, IReadOnlyList<Bar> bars, decimal? lastPrice, DateTimeOffset now)
    {
        if (!IsOpen(now)) return null;
        var today = EtDate(now);
        var ses = Sessions(bars);
        var prev = ses.Where(x => x.date < today).Select(x => x.bar).LastOrDefault();
        var price = lastPrice is > 0 ? lastPrice.Value : ses.Where(x => x.date == today).Select(x => x.bar.Close).LastOrDefault();
        if (prev is null || prev.Close <= 0 || price <= 0) return null;
        return new UsMove(symbol, NameOf(symbol), today, price, Math.Round((price / prev.Close - 1m) * 100m, 2), Live: true);
    }

    public static string Line(IEnumerable<UsMove> moves) =>
        string.Join(" · ", moves.Select(m => $"{m.Name} {SignalEngine.Signed(m.ChangePct)}%"));

    /// <summary>반도체 ETF 기준 한 줄 해석 (규칙 아님, 참고)</summary>
    public static string? Mood(IReadOnlyList<UsMove> moves)
    {
        var sox = moves.FirstOrDefault(m => m.Symbol == "SOXX") ?? moves.FirstOrDefault();
        if (sox is null) return null;
        return sox.ChangePct switch
        {
            <= -3m => "미국 반도체가 크게 빠진 밤",
            <= -1.5m => "미국 반도체 약세",
            >= 3m => "미국 반도체가 크게 오른 밤",
            >= 1.5m => "미국 반도체 강세",
            _ => "미국 반도체 보합권",
        };
    }

    public static string D(DateOnly d) => d.ToString("M/d", CultureInfo.InvariantCulture);
}
