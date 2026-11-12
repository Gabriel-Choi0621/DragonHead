namespace DragonHead.Core;

/// <summary>차트 봉 단위. 토스 API 에서는 1분봉·일봉만 받고 나머지는 합쳐서 만든다.</summary>
public enum ChartInterval { Tick, Min1, Min5, Min10, Day, Week, Month, Year }

public static class ChartIntervals
{
    public static bool IsMinute(this ChartInterval i) => i is ChartInterval.Min1 or ChartInterval.Min5 or ChartInterval.Min10;

    /// <summary>장중 차트 (틱·분봉): 13시 음영·당일 고가 표시</summary>
    public static bool IsIntraday(this ChartInterval i) => i == ChartInterval.Tick || i.IsMinute();

    /// <summary>틱 차트의 봉 하나에 들어가는 체결 수</summary>
    public const int TicksPerBar = 10;

    public static string Label(this ChartInterval i) => i switch
    {
        ChartInterval.Tick => "틱", ChartInterval.Min1 => "1분", ChartInterval.Min5 => "5분", ChartInterval.Min10 => "10분",
        ChartInterval.Day => "일", ChartInterval.Week => "주", ChartInterval.Month => "월", _ => "년",
    };

    /// <summary>이동평균 이름 (일봉이면 20일선, 나머지는 20봉 평균)</summary>
    public static string MaName(this ChartInterval i, int n) => i switch
    {
        ChartInterval.Day => $"{n}일선",
        ChartInterval.Week => $"{n}주선",
        ChartInterval.Month => $"{n}개월선",
        ChartInterval.Year => $"{n}년선",
        _ => $"{n}봉 평균",
    };

    /// <summary>
    /// 1분봉 → 5·10분봉, 일봉 → 주·월·년봉. 입력은 시간순(오래된 것 먼저).
    /// 분봉 묶음은 하루 안에서 09:00 기준으로 끊는다 (09:00~09:04 가 첫 5분봉).
    /// </summary>
    public static List<Bar> Aggregate(IReadOnlyList<Bar> src, ChartInterval to)
    {
        if (to is ChartInterval.Min1 or ChartInterval.Day) return src.ToList();
        var result = new List<Bar>();
        Bar? cur = null;
        DateTimeOffset key = default;
        foreach (var b in src)
        {
            var k = BucketStart(b.Time, to);
            if (cur is null || k != key)
            {
                if (cur is not null) result.Add(cur);
                key = k;
                cur = new Bar(k, b.Open, b.High, b.Low, b.Close, b.Volume);
            }
            else
            {
                cur = cur with
                {
                    High = Math.Max(cur.High, b.High), Low = Math.Min(cur.Low, b.Low),
                    Close = b.Close, Volume = cur.Volume + b.Volume,
                };
            }
        }
        if (cur is not null) result.Add(cur);
        return result;
    }

    public static DateTimeOffset BucketStart(DateTimeOffset t, ChartInterval to)
    {
        var day = DateOnly.FromDateTime(t.DateTime);
        switch (to)
        {
            case ChartInterval.Min5:
            case ChartInterval.Min10:
                int n = to == ChartInterval.Min5 ? 5 : 10;
                var open = Kst.At(day, 9, 0);
                var mins = (int)Math.Floor((t - open).TotalMinutes);
                if (mins < 0) return t;
                return open.AddMinutes(mins / n * n);
            case ChartInterval.Week:
                int back = ((int)day.DayOfWeek + 6) % 7;   // 월요일 시작
                return Kst.At(day.AddDays(-back), 0, 0);
            case ChartInterval.Month:
                return Kst.At(new DateOnly(day.Year, day.Month, 1), 0, 0);
            case ChartInterval.Year:
                return Kst.At(new DateOnly(day.Year, 1, 1), 0, 0);
            default:
                return t;
        }
    }

    /// <summary>체결 n건씩 묶어 봉으로 (n틱봉). 봉 시각은 그 묶음 첫 체결 시각.</summary>
    public static List<Bar> FromTicks(IReadOnlyList<Trade> ticks, int n)
    {
        var bars = new List<Bar>();
        for (int i = 0; i < ticks.Count; i += n)
        {
            var chunk = ticks.Skip(i).Take(n).ToList();
            bars.Add(new Bar(chunk[0].Time, chunk[0].Price, chunk.Max(t => t.Price), chunk.Min(t => t.Price), chunk[^1].Price, chunk.Sum(t => t.Volume)));
        }
        return bars;
    }

    /// <summary>정규장(09:00~15:30) 1분봉만 남긴다. 프리·애프터마켓은 차트에서 뺀다.</summary>
    public static bool IsRegularSession(DateTimeOffset t)
    {
        var m = t.Hour * 60 + t.Minute;
        return m >= 9 * 60 && m <= 15 * 60 + 30;
    }
}

/// <summary>
/// 한 종목·한 단위(1분/일)의 봉을 과거 쪽으로 이어 받는 저장소.
/// 차트를 왼쪽으로 끌면 LoadOlderAsync 로 더 옛날 페이지를 붙인다.
/// </summary>
public sealed class CandleHistory
{
    private readonly string _apiInterval;
    private DateTimeOffset? _nextBefore;
    private readonly SortedDictionary<DateTimeOffset, Bar> _bars = new();

    public CandleHistory(string apiInterval) { _apiInterval = apiInterval; }

    public bool Exhausted { get; private set; }
    public bool Loaded => _bars.Count > 0 || Exhausted;
    public int Count => _bars.Count;
    public IReadOnlyList<Bar> Bars => _bars.Values.ToList();

    /// <summary>과거 쪽으로 최대 pages 페이지(200개씩) 더 받는다. 새로 붙은 개수를 돌려준다.</summary>
    public async Task<int> LoadOlderAsync(TossClient toss, string symbol, int pages, CancellationToken ct = default)
    {
        int added = 0;
        for (int p = 0; p < pages && !Exhausted; p++)
        {
            var (bars, next) = await toss.GetCandlesAsync(symbol, _apiInterval, 200, _nextBefore, ct);
            added += Merge(bars);
            _nextBefore = next;
            if (bars.Count == 0 || next is null) Exhausted = true;
        }
        return added;
    }

    /// <summary>최신 쪽 다시 받기 (새로고침). 첫 페이지만 받아 겹치는 봉은 새 값으로 덮는다.</summary>
    public async Task RefreshLatestAsync(TossClient toss, string symbol, CancellationToken ct = default)
    {
        var (bars, next) = await toss.GetCandlesAsync(symbol, _apiInterval, 200, null, ct);
        if (_bars.Count == 0) _nextBefore = next;
        Merge(bars);
    }

    public int Merge(IEnumerable<Bar> bars)
    {
        int added = 0;
        foreach (var b in bars)
        {
            if (_apiInterval == "1m" && !ChartIntervals.IsRegularSession(b.Time)) continue;
            if (!_bars.ContainsKey(b.Time)) added++;
            _bars[b.Time] = b;
        }
        return added;
    }
}

/// <summary>
/// 틱(체결) 모음. 토스 API 는 최근 50건만 주고 과거·웹소켓이 없어서, 창을 열어 둔 동안 1초마다 받아 이어 붙인다.
/// 1초 사이 체결이 50건을 넘으면 그 사이는 빠진다 (Gaps 로 센다).
/// </summary>
public sealed class TickBuffer
{
    private readonly List<Trade> _ticks = new();
    private readonly HashSet<(DateTimeOffset, decimal, decimal)> _seen = new();
    private const int Max = 20000;

    public IReadOnlyList<Trade> Ticks => _ticks;
    public int Gaps { get; private set; }
    public DateTimeOffset? Since => _ticks.Count > 0 ? _ticks[0].Time : null;

    /// <summary>새로 받은 체결(순서 무관)을 붙인다. 붙은 건수를 돌려준다.</summary>
    public int Add(IEnumerable<Trade> latest)
    {
        var fresh = latest.OrderBy(t => t.Time).Where(t => !_seen.Contains((t.Time, t.Price, t.Volume))).ToList();
        if (fresh.Count == 0) return 0;
        // 받은 50건이 전부 새 것이면, 지난번과 이번 사이에 놓친 체결이 있을 수 있다
        if (_ticks.Count > 0 && fresh.Count >= 50) Gaps++;
        if (_ticks.Count > 0 && fresh[0].Time.Date != _ticks[^1].Time.Date) { _ticks.Clear(); _seen.Clear(); Gaps = 0; }
        foreach (var t in fresh)
        {
            if (_ticks.Count > 0 && t.Time < _ticks[^1].Time) continue;   // 늦게 온 옛 체결은 버림
            _ticks.Add(t);
            _seen.Add((t.Time, t.Price, t.Volume));
        }
        if (_ticks.Count > Max)
        {
            var drop = _ticks.Count - Max;
            // 봉 경계가 흔들리지 않게 n틱 단위로 자른다
            drop = (drop + ChartIntervals.TicksPerBar - 1) / ChartIntervals.TicksPerBar * ChartIntervals.TicksPerBar;
            foreach (var t in _ticks.Take(drop)) _seen.Remove((t.Time, t.Price, t.Volume));
            _ticks.RemoveRange(0, drop);
        }
        return fresh.Count;
    }
}
