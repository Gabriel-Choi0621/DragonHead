using System.Globalization;
using System.Text;

namespace DragonHead.Core;

/// <summary>백테스트용 하루치 데이터 (일봉 + 외국인 확정 순매수)</summary>
public sealed record DayRow(DateOnly Date, decimal Open, decimal High, decimal Low, decimal Close, long? ForeignNet, long? InstitutionNet = null,
                            decimal Volume = 0);

/// <summary>기본 몫 타이밍 한 줄: 같은 매수 일정에서 '언제 사느냐'만 바꿨을 때</summary>
public sealed class TimingResult
{
    public required string Name { get; init; }
    public required string Rule { get; init; }
    /// <summary>회차별 (매수일, 정해진 날 바로 산 가격 대비 매수가 %, 20거래일 뒤 수익률 %)</summary>
    public List<(DateOnly date, decimal vsPlain, decimal? fwd)> Cycles { get; } = new();
}

/// <summary>명분일에 각 조건을 통과한 횟수 (어떤 조건이 문을 막는지 보기용)</summary>
public sealed record ConditionCount(string Label, int Passed, int Total);

public sealed record SignalHit(string Symbol, DateOnly Date, decimal Close, string? Catalyst, decimal?[] Forward);

public sealed class StrategyResult
{
    public required string Name { get; init; }
    public required string Rule { get; init; }
    public List<SignalHit> Hits { get; } = new();
}

public sealed class BacktestReport
{
    public required IReadOnlyList<int> Horizons { get; init; }
    public required List<StrategyResult> Strategies { get; init; }
    /// <summary>지침을 조금씩 바꾼 변형들 (비교표)</summary>
    public List<StrategyResult> Variants { get; init; } = new();
    public List<ConditionCount> Conditions { get; init; } = new();
    /// <summary>명분일을 하락폭·코스피 동반·수급·거래량으로 쪼갠 것</summary>
    public List<StrategyResult> Splits { get; init; } = new();
    /// <summary>명분 없는 날의 눌림 규칙들</summary>
    public List<StrategyResult> NoCatalyst { get; init; } = new();
    /// <summary>며칠씩 이어지는 수급 이탈 (외국인 연속 순매도 등)</summary>
    public List<StrategyResult> Supply { get; init; } = new();
    /// <summary>기본 몫(정기 분할) 타이밍: 간격이 차면 바로 사는 것과 '매수 창' 안에서 고르는 것 비교</summary>
    public List<TimingResult> CoreTiming { get; init; } = new();
    /// <summary>요일별: (요일, 날짜, 당일 등락 %, 코스피 당일 등락 %, 그날 종가 매수 뒤 5·20거래일 수익률 %)</summary>
    public List<(DayOfWeek dow, DateOnly date, decimal chg, decimal? mkt, decimal? f5, decimal? f20)> Weekdays { get; init; } = new();
    public bool HasMarket { get; init; }
    /// <summary>앞·뒤 기간을 나누는 날 (이 날부터 뒤 기간)</summary>
    public DateOnly? SplitDate { get; init; }
    public required Dictionary<string, (DateOnly from, DateOnly to, int days, DateOnly? flowFrom)> Coverage { get; init; }
    public required int CatalystCount { get; init; }
    public List<string> Notes { get; } = new();
}

/// <summary>
/// 지침을 일봉으로 근사해 과거에 적용해 본다.
///   장중 조건(오후 지지·14:50 고점 부근)은 과거 분봉이 없어 '종가가 당일 고가 대비 -N% 이내로 마감'으로 대신한다.
///   외국인 수급은 확정치(일별)라서 장중 잠정치보다 정확하다 — 실전보다 약간 유리한 조건.
///   매수는 신호 당일 종가, 성과는 N거래일 뒤 종가 기준. 수수료·세금 제외.
/// </summary>
public static class Backtester
{
    public static readonly int[] DefaultHorizons = { 5, 10, 20 };

    // ───────────────────────── 데이터 모으기 ─────────────────────────

    public static async Task<List<DayRow>> LoadAsync(TossClient toss, string symbol, DateOnly from,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // 일봉: 200개씩 과거로
        var bars = new Dictionary<DateOnly, Bar>();
        DateTimeOffset? before = null;
        for (int page = 0; page < 40; page++)
        {
            var (chunk, next) = await toss.GetCandlesAsync(symbol, "1d", 200, before, ct);
            foreach (var b in chunk) bars[DateOnly.FromDateTime(b.Time.DateTime)] = b;
            progress?.Report($"{symbol} 일봉 {bars.Count}개");
            if (chunk.Count == 0 || next is null || chunk.Min(b => DateOnly.FromDateTime(b.Time.DateTime)) <= from) break;
            before = next;
        }

        // 외국인 확정 순매수: 100개씩 과거로 (제공 범위가 일봉보다 짧을 수 있다)
        var flows = new Dictionary<DateOnly, long>();
        var inst = new Dictionary<DateOnly, long>();
        DateOnly? until = null;
        for (int page = 0; page < 60; page++)
        {
            var (recs, next) = await toss.GetInvestorTradingPageAsync(symbol, 100, until, ct);
            foreach (var r in recs) { flows[r.Date] = r.ForeignerNet; inst[r.Date] = r.InstitutionNet; }
            progress?.Report($"{symbol} 수급 {flows.Count}일");
            if (recs.Count == 0 || next is null || recs.Min(r => r.Date) <= from) break;
            until = next;
        }

        var today = Kst.Today;
        return bars
            .Where(kv => kv.Key >= from && kv.Key < today)   // 오늘(미완성 봉) 제외
            .OrderBy(kv => kv.Key)
            .Select(kv => new DayRow(kv.Key, kv.Value.Open, kv.Value.High, kv.Value.Low, kv.Value.Close,
                                     flows.TryGetValue(kv.Key, out var f) ? f : null,
                                     inst.TryGetValue(kv.Key, out var ins) ? ins : null, kv.Value.Volume))
            .ToList();
    }

    /// <summary>코스피 일별 등락률(%) — 명분일 하락이 시장 탓인지 종목 탓인지 가르는 데 쓴다</summary>
    public static async Task<Dictionary<DateOnly, decimal>> LoadMarketChangesAsync(TossClient toss, DateOnly from, string index = "KOSPI",
        CancellationToken ct = default)
    {
        var closes = new SortedDictionary<DateOnly, decimal>();
        DateTimeOffset? before = null;
        for (int page = 0; page < 40; page++)
        {
            var (chunk, next) = await toss.GetIndicatorCandlesPageAsync(index, before, ct);
            foreach (var b in chunk) closes[DateOnly.FromDateTime(b.Time.DateTime)] = b.Close;
            if (chunk.Count == 0 || next is null || chunk.Min(b => DateOnly.FromDateTime(b.Time.DateTime)) <= from.AddDays(-10)) break;
            before = next;
        }
        var result = new Dictionary<DateOnly, decimal>();
        decimal? prev = null;
        foreach (var (d, c) in closes)
        {
            if (prev is > 0) result[d] = Math.Round((c / prev.Value - 1m) * 100m, 2);
            prev = c;
        }
        return result;
    }

    // ───────────────────────── 계산 ─────────────────────────

    /// <summary>명분일 하루의 조건 통과 여부</summary>
    private sealed record Ctx(bool Up, bool Near1, bool Near2, bool ForeignToday, bool Streak, bool Avoid)
    {
        public decimal Chg { get; init; }            // 당일 등락률 %
        public decimal? MktChg { get; init; }        // 코스피 당일 등락률 %
        public decimal? VolRatio { get; init; }      // 거래량 ÷ 직전 20일 평균
        public long? F { get; init; }                // 외국인 순매수 (주)
        public long? I { get; init; }                // 기관 순매수 (주)
        public decimal? FlowPct { get; init; }       // (외국인+기관) 순매수 ÷ 거래량 × 100
    }

    /// <summary>언제 사는지: 명분일 종가 / 다음날 종가 / 명분일 뒤 5일 안에 -3% 눌림 / 5일 안에 기준가(전일 종가)까지 복귀</summary>
    private enum Entry { Today, NextDay, Pullback3, BackToBase }

    public static BacktestReport Run(
        IReadOnlyDictionary<string, List<DayRow>> data,
        IReadOnlyList<CatalystEvent> catalysts,
        StrategySettings cfg,
        IReadOnlyList<int>? horizons = null,
        IReadOnlyDictionary<DateOnly, decimal>? market = null)
    {
        horizons ??= DefaultHorizons;
        var nearHigh = cfg.NearHighPct / 100m;

        var guide = new StrategyResult
        {
            Name = "옛 지침 (명분 + 차트 + 수급, 지금은 안 씀)",
            Rule = $"명분일 · 상승 마감 · 종가가 고가 대비 -{cfg.NearHighPct}% 이내 · 외국인 순매수 · 외국인 {cfg.MinForeignStreak}일 이상 연속",
        };
        var catalystOnly = new StrategyResult { Name = "명분일 무조건", Rule = "명분일이면 조건 없이 종가 매수" };
        var supplyOnly = new StrategyResult
        {
            Name = "명분 없이 차트 + 수급",
            Rule = "명분 조건만 빼고 나머지 지침 조건 그대로 (명분일 포함 모든 날)",
        };
        var anyDay = new StrategyResult { Name = "아무 날 (기준선)", Rule = "매일 종가 매수 — 다른 전략이 이것보다 나아야 의미가 있다" };

        // 변형: 명분일에서 조건을 하나씩 풀거나 바꿔 본다. (이름, 규칙 설명, 조건, 다음 거래일 종가에 사는지)
        var variantDefs = new (string name, string rule, Func<Ctx, bool> pass, Entry entry)[]
        {
            ("⓪ 명분일 무조건", "수급 자료가 있는 명분일 전부 (비교 기준)", c => true, Entry.Today),
            ("① 옛 지침(용의 머리)", $"상승 · 고가 -{cfg.NearHighPct}% · 외국인 당일 · 외국인 {cfg.MinForeignStreak}일 연속", c => c.Up && c.Near1 && c.ForeignToday && c.Streak, Entry.Today),
            ("② 연속 조건만 뺌", $"상승 · 고가 -{cfg.NearHighPct}% · 외국인 당일", c => c.Up && c.Near1 && c.ForeignToday, Entry.Today),
            ("③ ② + 고가 -2%", "상승 · 고가 -2% · 외국인 당일", c => c.Up && c.Near2 && c.ForeignToday, Entry.Today),
            ("④ ③ + 회피 아님", "상승 · 고가 -2% · 외국인 당일 · 회피 아님", c => c.Up && c.Near2 && c.ForeignToday && !c.Avoid, Entry.Today),
            ("⑤ 상승 · 외국인 당일", "상승 · 외국인 당일 (고가 조건 없음)", c => c.Up && c.ForeignToday, Entry.Today),
            ("⑥ 상승 · 고가 -2%", "상승 · 고가 -2% (수급 조건 없음)", c => c.Up && c.Near2, Entry.Today),
            ("⑦ 상승만", "명분일에 상승 마감", c => c.Up, Entry.Today),
            ("⑧ 회피만 아니면", "명분일 중 회피 판정이 아닌 날", c => !c.Avoid, Entry.Today),
            ("⑨ 하락 마감", "명분일에 하락 마감 (싸게 산다)", c => !c.Up, Entry.Today),
            ("⑩ 회피 판정일", $"명분일 -{cfg.AvoidDropPct}% 이하 + 외국인 매도 (옛 지침이 피하라던 날)", c => c.Avoid, Entry.Today),
            ("⑪ 다음날 종가", "명분일 다음 거래일 종가 매수 (조건 없음)", c => true, Entry.NextDay),
            ("⑫ 하락 다음날", "명분일이 하락 마감이면 다음 거래일 종가 매수", c => !c.Up, Entry.NextDay),
        };
        var variants = variantDefs.Select(v => new StrategyResult { Name = v.name, Rule = v.rule }).ToList();

        // 쪼개 보기: 하락일(D)·상승일(U)을 하락폭·코스피 동반·수급·거래량으로 나눈다
        var splitDefs = new (string name, string rule, Func<Ctx, bool> pass, Entry entry)[]
        {
            ("D1 하락 0~-2%", "명분일 0% 미만 ~ -2% 초과 하락 마감", c => c.Chg < 0 && c.Chg > -2, Entry.Today),
            ("D2 하락 -2~-5%", "명분일 -2% 이하 ~ -5% 초과", c => c.Chg <= -2 && c.Chg > -5, Entry.Today),
            ("D3 하락 -5% 이하", "명분일 -5% 이하 급락", c => c.Chg <= -5, Entry.Today),
            ("D4 하락·코스피도 하락", "명분일 하락 + 코스피도 하락 (시장 탓)", c => c.Chg < 0 && c.MktChg < 0, Entry.Today),
            ("D5 하락·혼자 하락", "명분일 하락 + 코스피는 보합·상승 (종목 탓)", c => c.Chg < 0 && c.MktChg >= 0, Entry.Today),
            ("D6 하락·외인기관 동반매도", "명분일 하락 + 외국인·기관 둘 다 순매도", c => c.Chg < 0 && c.F < 0 && c.I < 0, Entry.Today),
            ("D7 하락·외인or기관 매수", "명분일 하락 + 외국인이나 기관 중 하나는 순매수", c => c.Chg < 0 && (c.F > 0 || c.I > 0), Entry.Today),
            ("D8 하락·거래량 1.5배↑", "명분일 하락 + 거래량이 20일 평균의 1.5배 이상", c => c.Chg < 0 && c.VolRatio >= 1.5m, Entry.Today),
            ("D9 하락·거래량 보통", "명분일 하락 + 거래량 20일 평균의 1.5배 미만", c => c.Chg < 0 && c.VolRatio < 1.5m, Entry.Today),
            ("D10 쇼크 의심", "명분일 -5% 이하 + 코스피는 보합·상승 (혼자 급락)", c => c.Chg <= -5 && c.MktChg >= 0, Entry.Today),
            ("U1 상승·거래량 1.5배↑", "명분일 상승 + 거래량 20일 평균의 1.5배 이상", c => c.Up && c.VolRatio >= 1.5m, Entry.Today),
            ("U2 상승·거래량 보통", "명분일 상승 + 거래량 1.5배 미만", c => c.Up && c.VolRatio < 1.5m, Entry.Today),
            ("U3 상승·외인기관 동반매수", "명분일 상승 + 외국인·기관 둘 다 순매수", c => c.Up && c.F > 0 && c.I > 0, Entry.Today),
            ("U4 상승·수급강도 10%↑", "명분일 상승 + (외국인+기관) 순매수가 거래량의 10% 이상", c => c.Up && c.FlowPct >= 10, Entry.Today),
            ("U5 상승·외인or기관 매도", "명분일 상승인데 외국인이나 기관 중 하나는 순매도", c => c.Up && (c.F < 0 || c.I < 0), Entry.Today),
            ("U6 상승→-3% 눌림 매수", "명분일 상승이면 바로 안 사고, 5거래일 안에 명분일 종가 -3% 이하로 내려온 첫날 종가 매수", c => c.Up, Entry.Pullback3),
            ("U7 상승→기준가 복귀 매수", "명분일 상승이면 5거래일 안에 명분일 전날 종가 이하로 내려온 첫날 종가 매수", c => c.Up, Entry.BackToBase),
        };
        var splits = splitDefs.Select(v => new StrategyResult { Name = v.name, Rule = v.rule }).ToList();

        // 명분 없는 날: 어떤 '싼 날'이 아무 날보다 나았나. cooldown > 0 이면 한 번 사고 그 거래일 수 동안은 다시 안 산다 (겹침 제거).
        var plainDefs = new (string name, string rule, Func<Plain, bool> pass, int cooldown)[]
        {
            ("N0 명분 없는 날 전부", "명분 없는 날 매일 (비교 기준)", c => true, 0),
            ("N1 하락 마감", "명분 없는 날 하락 마감", c => c.Chg < 0, 0),
            ("N2 -2% 이하", "명분 없는 날 -2% 이하 하락", c => c.Chg <= -2, 0),
            ("N3 -3% 이하", "명분 없는 날 -3% 이하 하락", c => c.Chg <= -3, 0),
            ("N4 상승추세 -2% 눌림", "전날 종가가 20일선 위 + 20일선 상승 중 + 오늘 -2% 이하", c => c.UpTrend && c.Chg <= -2, 0),
            ("N5 20일선 터치", "전날까지 20일선 위였는데 오늘 저가가 20일선 이하 (종가 무관)", c => c.TouchMa20, 0),
            ("N6 60일선 근처", "종가가 60일선의 -1% ~ +2% 안 (중기 지지선 근처)", c => c.NearMa60, 0),
            ("N7 고점 대비 -10%↓", "20일 고가 대비 -10% 이하로 내려온 날", c => c.FromHigh20 <= -10, 0),
            ("N8 3일 연속 하락", "3거래일 연속 하락 마감", c => c.DownDays >= 3, 0),
            ("N9 -3%↓·외인기관 매도", "-3% 이하 + 외국인·기관 동반 순매도", c => c.Chg <= -3 && c.F < 0 && c.I < 0, 0),
            ("N10 -3%↓·코스피도 하락", "-3% 이하 + 코스피도 하락 (시장 탓)", c => c.Chg <= -3 && c.MktChg < 0, 0),
            ("N11 -3%↓ (10일 쿨다운)", "N3 와 같지만 한 번 사면 10거래일 동안 다시 안 삼 (연속 신호 겹침 제거)", c => c.Chg <= -3, 10),
            ("N12 상승추세 눌림 (10일 쿨다운)", "N4 와 같지만 10거래일 쿨다운", c => c.UpTrend && c.Chg <= -2, 10),
        };
        var plains = plainDefs.Select(v => new StrategyResult { Name = v.name, Rule = v.rule }).ToList();

        // 수급 이탈이 며칠씩 이어질 때 (명분일 포함 모든 날). cooldown > 0 이면 한 번 사고 그 거래일 수 동안 다시 안 삼.
        var supplyDefs = new (string name, string rule, Func<SupplyCtx, bool> pass, int cooldown)[]
        {
            ("S1 외인 3일 연속 매도 중", "외국인 3거래일 이상 연속 순매도 중인 날", c => c.SellStreak >= 3, 0),
            ("S2 외인 5일 연속 매도 중", "외국인 5거래일 이상 연속 순매도 중인 날", c => c.SellStreak >= 5, 0),
            ("S3 10일 누적 대량 매도", "최근 10거래일 외국인 순매도 합이 그 기간 거래량의 5% 이상", c => c.Cum10Pct <= -5m, 0),
            ("S4 매도 끝나고 첫 매수", "외국인 3일 이상 연속 순매도가 끊기고 처음 순매수로 돌아선 날", c => c.PrevSellStreak >= 3 && c.F > 0, 0),
            ("S5 연속 매도 중 투매", "외국인 3일 이상 연속 매도 중 + 오늘 외국인·기관 동반 매도 + -3% 이하", c => c.SellStreak >= 3 && c.F < 0 && c.I < 0 && c.Chg <= -3, 0),
            ("S6 S1 (10일 쿨다운)", "S1 과 같지만 한 번 사면 10거래일 쉼", c => c.SellStreak >= 3, 10),
            ("S7 S4 (10일 쿨다운)", "S4 와 같지만 10거래일 쿨다운", c => c.PrevSellStreak >= 3 && c.F > 0, 10),
            ("S8 5일 연속 매도 중 하락일", "외국인 5일 이상 연속 매도 중이고 오늘도 하락 마감 (계속 떨어지는 칼)", c => c.SellStreak >= 5 && c.Chg < 0, 0),
        };
        var supplies = supplyDefs.Select(v => new StrategyResult { Name = v.name, Rule = v.rule }).ToList();
        var counts = new Dictionary<string, int>
        {
            ["상승 마감"] = 0, [$"고가 -{cfg.NearHighPct}% 이내 마감"] = 0, ["고가 -2% 이내 마감"] = 0,
            ["외국인 당일 순매수"] = 0, [$"외국인 {cfg.MinForeignStreak}일 이상 연속"] = 0, ["회피 아님"] = 0,
        };
        int catalystWithFlow = 0;

        var coverage = new Dictionary<string, (DateOnly, DateOnly, int, DateOnly?)>();
        int catalystDays = 0;

        foreach (var (symbol, rows) in data)
        {
            if (rows.Count < 2) continue;
            var flowFrom = rows.FirstOrDefault(r => r.ForeignNet is not null)?.Date;
            coverage[symbol] = (rows[0].Date, rows[^1].Date, rows.Count, flowFrom);

            int streak = 0, sellStreak = 0;
            var volWindow = new Queue<decimal>();
            var closesAll = rows.Select(x => x.Close).ToList();
            var ma20 = ChartMath.Ma(closesAll, 20);
            var ma60 = ChartMath.Ma(closesAll, 60);
            var lastBuy = new int[plainDefs.Length];
            Array.Fill(lastBuy, int.MinValue / 2);
            int downDays = 0;
            var supplyLast = new int[supplyDefs.Length];
            Array.Fill(supplyLast, int.MinValue / 2);
            var flow10 = new Queue<(long f, decimal v)>();
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                int prevSellStreak = sellStreak;
                streak = r.ForeignNet is > 0 ? streak + 1 : 0;
                sellStreak = r.ForeignNet is < 0 ? sellStreak + 1 : 0;
                decimal? volRatio = volWindow.Count >= 10 && r.Volume > 0 && volWindow.Average() > 0 ? r.Volume / volWindow.Average() : null;
                if (r.Volume > 0) { volWindow.Enqueue(r.Volume); if (volWindow.Count > 20) volWindow.Dequeue(); }
                if (i == 0) continue;

                var fwd = horizons.Select(h => i + h < rows.Count ? (decimal?)(rows[i + h].Close / r.Close - 1m) * 100m : null).ToArray();
                var cat = catalysts.FirstOrDefault(e => e.Matches(symbol, r.Date));
                var hit = new SignalHit(symbol, r.Date, r.Close, cat?.Title, fwd);

                bool up = r.Close > rows[i - 1].Close;
                bool nearHighOk = r.High > 0 && r.Close >= r.High * (1m - nearHigh);
                bool hasFlow = r.ForeignNet is not null;
                bool supply = r.ForeignNet is > 0 && streak >= cfg.MinForeignStreak;
                bool chartSupply = up && nearHighOk && supply;

                anyDay.Hits.Add(hit);
                if (cat is not null)
                {
                    catalystDays++;
                    catalystOnly.Hits.Add(hit);
                    if (chartSupply) guide.Hits.Add(hit);

                    if (hasFlow)
                    {
                        var chg = (r.Close / rows[i - 1].Close - 1m) * 100m;
                        var ctx = new Ctx(
                            Up: up,
                            Near1: nearHighOk,
                            Near2: r.High > 0 && r.Close >= r.High * 0.98m,
                            ForeignToday: r.ForeignNet is > 0,
                            Streak: r.ForeignNet is > 0 && streak >= cfg.MinForeignStreak,
                            Avoid: chg <= -cfg.AvoidDropPct && r.ForeignNet is < 0 && (r.InstitutionNet is < 0 || sellStreak >= 2))
                        {
                            Chg = chg,
                            MktChg = market is not null && market.TryGetValue(r.Date, out var mc) ? mc : null,
                            VolRatio = volRatio,
                            F = r.ForeignNet, I = r.InstitutionNet,
                            FlowPct = r.Volume > 0 && r.ForeignNet is { } fn && r.InstitutionNet is { } inn ? (fn + inn) / r.Volume * 100m : null,
                        };
                        catalystWithFlow++;
                        if (ctx.Up) counts["상승 마감"]++;
                        if (ctx.Near1) counts[$"고가 -{cfg.NearHighPct}% 이내 마감"]++;
                        if (ctx.Near2) counts["고가 -2% 이내 마감"]++;
                        if (ctx.ForeignToday) counts["외국인 당일 순매수"]++;
                        if (ctx.Streak) counts[$"외국인 {cfg.MinForeignStreak}일 이상 연속"]++;
                        if (!ctx.Avoid) counts["회피 아님"]++;
                        for (int v = 0; v < variantDefs.Length; v++)
                            if (variantDefs[v].pass(ctx) && EntryHit(rows, i, variantDefs[v].entry, symbol, cat.Title, horizons) is { } eh)
                                variants[v].Hits.Add(eh);
                        for (int v = 0; v < splitDefs.Length; v++)
                            if (splitDefs[v].pass(ctx) && EntryHit(rows, i, splitDefs[v].entry, symbol, cat.Title, horizons) is { } sh)
                                splits[v].Hits.Add(sh);
                    }
                }
                if (hasFlow && chartSupply) supplyOnly.Hits.Add(hit);

                downDays = r.Close < rows[i - 1].Close ? downDays + 1 : 0;

                if (r.ForeignNet is { } fNow)
                {
                    flow10.Enqueue((fNow, r.Volume));
                    if (flow10.Count > 10) flow10.Dequeue();
                    var volSum = flow10.Sum(x => x.v);
                    var sc = new SupplyCtx
                    {
                        SellStreak = sellStreak, PrevSellStreak = prevSellStreak, F = fNow, I = r.InstitutionNet,
                        Chg = (r.Close / rows[i - 1].Close - 1m) * 100m,
                        Cum10Pct = flow10.Count == 10 && volSum > 0 ? flow10.Sum(x => (decimal)x.f) / volSum * 100m : 0,
                    };
                    for (int v = 0; v < supplyDefs.Length; v++)
                    {
                        if (!supplyDefs[v].pass(sc)) continue;
                        if (supplyDefs[v].cooldown > 0 && i - supplyLast[v] < supplyDefs[v].cooldown) continue;
                        supplyLast[v] = i;
                        supplies[v].Hits.Add(hit);
                    }
                }
                if (cat is null && i >= 21)
                {
                    var high20 = rows.Skip(i - 19).Take(20).Max(x => x.High);
                    var pc = new Plain
                    {
                        Chg = (r.Close / rows[i - 1].Close - 1m) * 100m,
                        MktChg = market is not null && market.TryGetValue(r.Date, out var pm) ? pm : null,
                        F = r.ForeignNet, I = r.InstitutionNet,
                        UpTrend = ma20[i - 1] is { } m1 && ma20[i - 6] is { } m6 && rows[i - 1].Close > m1 && m1 > m6,
                        TouchMa20 = ma20[i - 1] is { } p20 && rows[i - 1].Close > p20 && ma20[i] is { } t20 && r.Low <= t20,
                        NearMa60 = ma60[i] is { } t60 && r.Close >= t60 * 0.99m && r.Close <= t60 * 1.02m,
                        FromHigh20 = high20 > 0 ? (r.Close / high20 - 1m) * 100m : 0,
                        DownDays = downDays,
                    };
                    for (int v = 0; v < plainDefs.Length; v++)
                    {
                        if (!plainDefs[v].pass(pc)) continue;
                        if (plainDefs[v].cooldown > 0 && i - lastBuy[v] < plainDefs[v].cooldown) continue;
                        lastBuy[v] = i;
                        plains[v].Hits.Add(hit);
                    }
                }
            }
        }

        // 기간 나누기: 전체 일봉 범위의 한가운데 (월 초로 맞춤)
        DateOnly? split = null;
        if (coverage.Count > 0)
        {
            var lo = coverage.Values.Min(c => c.Item1).DayNumber;
            var hi = coverage.Values.Max(c => c.Item2).DayNumber;
            var mid = DateOnly.FromDayNumber((lo + hi) / 2);
            split = new DateOnly(mid.Year, mid.Month, 1);
        }

        var report = new BacktestReport
        {
            SplitDate = split,
            Horizons = horizons,
            Strategies = new() { guide, catalystOnly, supplyOnly, anyDay },
            Variants = variants,
            Splits = splits,
            NoCatalyst = plains,
            Supply = supplies,
            CoreTiming = SimulateCoreTiming(data, market, 10, 5, horizons[^1]),
            Weekdays = data.Values.SelectMany(rows => rows.Skip(1).Select((r, k) =>
            {
                int i = k + 1;
                decimal? Fwd(int h) => i + h < rows.Count ? (rows[i + h].Close / r.Close - 1m) * 100m : null;
                return (r.Date.DayOfWeek, r.Date, (r.Close / rows[i - 1].Close - 1m) * 100m,
                        market is not null && market.TryGetValue(r.Date, out var mk) ? mk : (decimal?)null, Fwd(5), Fwd(20));
            })).ToList(),
            HasMarket = market is { Count: > 0 },
            Conditions = counts.Select(kv => new ConditionCount(kv.Key, kv.Value, catalystWithFlow)).ToList(),
            Coverage = coverage,
            CatalystCount = catalystDays,
        };

        if (catalystDays == 0)
            report.Notes.Add("명분일이 하나도 없습니다. DART 인증키를 넣거나 설정의 Events 에 과거 날짜를 넣어야 명분 전략이 계산됩니다.");
        if (guide.Hits.Count is > 0 and < 10)
            report.Notes.Add($"지침 신호가 {guide.Hits.Count}번뿐이라 평균이 몇 번의 결과에 크게 흔들립니다. 결론보다는 참고로 보세요.");
        if (coverage.Values.Any(c => c.Item4 is { } f && f > c.Item1.AddDays(30)))
            report.Notes.Add("외국인 수급 데이터가 일봉보다 짧게 제공돼, 수급이 없는 앞쪽 기간은 수급 조건을 쓰는 전략에서 빠졌습니다.");
        report.Notes.Add("2025년 3월 넥스트레이드 출범 이후 토스 일봉 종가에는 애프터마켓 체결이 섞여 있어, 최근 기간의 '고가 대비 마감' 조건은 약간 부정확합니다.");
        report.Notes.Add("공시 접수 시각을 알 수 없어, 장 마감 후 공시도 접수일 당일을 명분일로 봤습니다.");
        report.Notes.Add("수급만 전략은 신호가 며칠 연달아 나와 같은 상승 구간이 여러 번 세어집니다. 횟수보다 평균·승률을 보세요.");
        return report;
    }

    /// <summary>
    /// 기본 몫 타이밍 시뮬레이션. interval 거래일마다 '매수 예정일'이 오고, 각 방식은 그날부터 window 거래일 안에서 조건이 맞는 첫날 산다
    /// (끝까지 안 맞으면 창 마지막 날). 다음 예정일은 실제 매수일과 상관없이 고정이라 방식끼리 회차가 같다.
    /// </summary>
    private static List<TimingResult> SimulateCoreTiming(IReadOnlyDictionary<string, List<DayRow>> data,
        IReadOnlyDictionary<DateOnly, decimal>? market, int interval, int window, int horizon)
    {
        var ma60Cache = new Dictionary<List<DayRow>, (decimal?[] m20, decimal?[] m60)>();
        bool Broken(List<DayRow> r, int j)
        {
            if (!ma60Cache.TryGetValue(r, out var m))
            {
                var cl = r.Select(x => x.Close).ToList();
                m = (ChartMath.Ma(cl, 20), ChartMath.Ma(cl, 60));
                ma60Cache[r] = m;
            }
            return m.m60[j] is { } a60 && m.m20[j] is { } a20 && r[j].Close < a60 && a20 < a60;
        }
        var defs = new (string name, string rule, Func<List<DayRow>, int, decimal?[], decimal?[], bool> ok)[]
        {
            ("T0 예정일에 바로", "간격이 차면 그날 종가에 산다 (비교 기준)", (r, j, m5, m20) => true),
            ("T1 첫 하락 마감일", "창 안에서 처음으로 하락 마감한 날", (r, j, m5, m20) => r[j].Close < r[j - 1].Close),
            ("T2 -2% 이하 날", "창 안에서 처음으로 -2% 이하로 빠진 날", (r, j, m5, m20) => r[j].Close <= r[j - 1].Close * 0.98m),
            ("T3 급등일 피하기", "+2% 이상 오른 날은 건너뛰고 그 외 첫날 (추격 금지)", (r, j, m5, m20) => r[j].Close < r[j - 1].Close * 1.02m),
            ("T4 과열 피하기", "종가가 20일선보다 5% 이상 높으면 건너뜀", (r, j, m5, m20) => m20[j] is { } a && r[j].Close <= a * 1.05m),
            ("T5 5일선 아래", "종가가 5일 평균보다 낮은 첫날 (단기 눌림)", (r, j, m5, m20) => m5[j] is { } a && r[j].Close < a),
            ("T6 2일 연속 하락", "2거래일 연속 하락 마감한 첫날", (r, j, m5, m20) => j >= 2 && r[j].Close < r[j - 1].Close && r[j - 1].Close < r[j - 2].Close),
            ("T7 약세 경계일 피하기", "-2% 이하 + 외국인·기관 동반 매도인 날은 건너뜀", (r, j, m5, m20) => !(r[j].Close <= r[j - 1].Close * 0.98m && r[j].ForeignNet < 0 && r[j].InstitutionNet < 0)),
            ("T8 앱 평소 판단", "지금 앱 규칙: 하락·보합 마감일이면 '사도 괜찮음'(오른 날·과열·급등은 기다림, 창 마지막 날엔 삼)",
                (r, j, m5, m20) => r[j].Close <= r[j - 1].Close),
        };
        var results = defs.Select(d => new TimingResult { Name = d.name, Rule = d.rule }).ToList();

        foreach (var (_, rows) in data)
        {
            if (rows.Count < 80) continue;
            var closes = rows.Select(x => x.Close).ToList();
            var m5 = ChartMath.Ma(closes, 5);
            var m20 = ChartMath.Ma(closes, 20);
            for (int d = 60; d + window - 1 < rows.Count; d += interval)
            {
                var plain = rows[d].Close;
                for (int v = 0; v < defs.Length; v++)
                {
                    int at = d + window - 1;
                    for (int j = d; j < d + window; j++)
                        if (defs[v].ok(rows, j, m5, m20)) { at = j; break; }
                    decimal? fwd = at + horizon < rows.Count ? (rows[at + horizon].Close / rows[at].Close - 1m) * 100m : null;
                    results[v].Cycles.Add((rows[at].Date, (rows[at].Close / plain - 1m) * 100m, fwd));
                }
            }
        }
        return results;
    }

    private sealed class SupplyCtx
    {
        public int SellStreak { get; init; }
        public int PrevSellStreak { get; init; }
        public long F { get; init; }
        public long? I { get; init; }
        public decimal Chg { get; init; }
        public decimal Cum10Pct { get; init; }
    }

    /// <summary>명분 없는 날 하루의 상태</summary>
    private sealed class Plain
    {
        public decimal Chg { get; init; }
        public decimal? MktChg { get; init; }
        public long? F { get; init; }
        public long? I { get; init; }
        public bool UpTrend { get; init; }
        public bool TouchMa20 { get; init; }
        public bool NearMa60 { get; init; }
        public decimal FromHigh20 { get; init; }
        public int DownDays { get; init; }
    }

    /// <summary>진입 방식에 따라 실제로 사는 날과 그 뒤 수익률</summary>
    private static SignalHit? EntryHit(List<DayRow> rows, int i, Entry entry, string symbol, string title, IReadOnlyList<int> horizons)
    {
        int at = -1;
        switch (entry)
        {
            case Entry.Today: at = i; break;
            case Entry.NextDay: at = i + 1 < rows.Count ? i + 1 : -1; break;
            case Entry.Pullback3:
            case Entry.BackToBase:
                var target = entry == Entry.Pullback3 ? rows[i].Close * 0.97m : rows[i - 1].Close;
                for (int k = 1; k <= 5 && i + k < rows.Count; k++)
                    if (rows[i + k].Close <= target) { at = i + k; break; }
                break;
        }
        if (at < 0) return null;
        var b = rows[at];
        var fwd = horizons.Select(h => at + h < rows.Count ? (decimal?)(rows[at + h].Close / b.Close - 1m) * 100m : null).ToArray();
        return new SignalHit(symbol, b.Date, b.Close, title, fwd);
    }

    // ───────────────────────── 보고서 ─────────────────────────

    public static string Format(BacktestReport r, IReadOnlyDictionary<string, string> names)
    {
        var ko = CultureInfo.GetCultureInfo("ko-KR");
        var sb = new StringBuilder();
        sb.AppendLine($"용머리 알리미 · 백테스트  ({Kst.Now:yyyy-MM-dd HH:mm})");
        sb.AppendLine(new string('═', 72));
        sb.AppendLine("데이터 범위");
        foreach (var (sym, c) in r.Coverage)
            sb.AppendLine($"  {names.GetValueOrDefault(sym, sym),-10} 일봉 {c.from:yyyy-MM-dd} ~ {c.to:yyyy-MM-dd} ({c.days}일)" +
                          $" · 외국인 수급 {(c.flowFrom is { } f ? f.ToString("yyyy-MM-dd") + "부터" : "없음")}");
        sb.AppendLine($"  명분일(종목별 합계) {r.CatalystCount}일");
        sb.AppendLine();

        string Pct(decimal? v) => v is { } x ? (x > 0 ? "+" : "") + x.ToString("0.00", ko) + "%" : "-";

        foreach (var s in r.Strategies)
        {
            sb.AppendLine($"■ {s.Name}  —  신호 {s.Hits.Count}번");
            sb.AppendLine($"  규칙: {s.Rule}");
            if (s.Hits.Count == 0) { sb.AppendLine("  (신호 없음)"); sb.AppendLine(); continue; }

            sb.AppendLine($"  {"보유기간",-8}{"표본",6}{"평균",10}{"중앙값",10}{"승률",8}{"최악",10}{"최고",10}");
            for (int h = 0; h < r.Horizons.Count; h++)
            {
                var v = s.Hits.Where(x => x.Forward[h] is not null).Select(x => x.Forward[h]!.Value).OrderBy(x => x).ToList();
                if (v.Count == 0) continue;
                var median = v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2m;
                var win = v.Count(x => x > 0) * 100m / v.Count;
                sb.AppendLine($"  {r.Horizons[h] + "거래일",-8}{v.Count,6}{Pct(v.Average()),10}{Pct(median),10}{win.ToString("0", ko) + "%",8}{Pct(v[0]),10}{Pct(v[^1]),10}");
            }
            sb.AppendLine();
        }

        // 기준선 대비 한 줄 요약
        var baseline = r.Strategies.Last();
        sb.AppendLine("기준선(아무 날) 대비 평균 차이");
        for (int h = 0; h < r.Horizons.Count; h++)
        {
            var b = Avg(baseline, h);
            if (b is null) continue;
            var parts = r.Strategies.Take(r.Strategies.Count - 1)
                .Select(s => (s.Name, a: Avg(s, h)))
                .Where(x => x.a is not null)
                .Select(x => $"{x.Name} {Pct(x.a - b)}p");
            sb.AppendLine($"  {r.Horizons[h]}거래일: " + string.Join(" · ", parts));
        }
        sb.AppendLine();

        // 조건별 통과 수
        if (r.Conditions.Count > 0 && r.Conditions[0].Total > 0)
        {
            sb.AppendLine($"명분일 {r.Conditions[0].Total}일 중 조건별 통과 (어떤 조건이 문을 막는지)");
            foreach (var c in r.Conditions)
            {
                var pct = c.Passed * 100 / Math.Max(1, c.Total);
                sb.AppendLine($"  {PadW(c.Label, 24)} {c.Passed,4}일  {new string('█', pct / 5),-20} {pct}%");
            }
            sb.AppendLine();
        }

        // 변형 비교표 · 쪼개 보기 (같은 표 형식)
        var bIdx = r.Horizons.Count - 1;
        if (r.Variants.Count > 0)
            Tables($"지침 변형 비교 (명분일에서 조건만 바꿈 · 보유 {r.Horizons[bIdx]}거래일 기준, 5·10일 평균 함께)", r.Variants,
                new[] { "신호가 10번 미만인 변형은 평균이 몇 번의 결과에 크게 흔들립니다.",
                        "중앙값·승률이 기준선(아무 날)보다 높고 신호가 1년에 몇 번은 나오는 변형이 쓸 만한 후보입니다." });
        if (r.Splits.Count > 0)
            Tables($"명분일 쪼개 보기 (D = 하락 마감한 명분일, U = 상승 마감한 명분일 · 보유 {r.Horizons[bIdx]}거래일)", r.Splits,
                new[] { "D: 어떤 하락이 사도 되는 하락이었나 (하락폭 · 코스피 동반 · 누가 팔았나 · 거래량)",
                        "U: 오른 날 중에도 괜찮았던 경우가 있나 (거래량 · 수급) · 오른 날 바로 안 사고 눌림을 기다리면 어떤가",
                        r.HasMarket ? "코스피 등락은 토스 지수 일봉 기준." : "코스피 데이터를 못 받아 D4·D5·D10 은 비어 있습니다.",
                        "수급 강도 = (외국인+기관 순매수 주식 수) ÷ 그날 거래량. 거래량에는 넥스트레이드 체결이 섞여 있을 수 있습니다." });
        if (r.Weekdays.Count > 0)
        {
            var lastDate = r.Weekdays.Max(w => w.date);
            var recentFrom = lastDate.AddYears(-1);
            sb.AppendLine("요일별 (삼성·하이닉스 일봉 · 당일 등락과 그날 종가에 샀을 때)");
            sb.AppendLine($"  {PadW("요일", 6)}{LPadW("일수", 6)}{LPadW("당일 평균", 11)}{LPadW("하락 비율", 11)}{LPadW("-3%↓ 급락", 11)}{LPadW("코스피 평균", 12)}" +
                          $"{LPadW("5일 뒤", 10)}{LPadW("20일 뒤", 10)}{LPadW("당일 앞/뒤 기간", 19)}{LPadW("최근1년 당일", 14)}{LPadW("최근1년 하락", 13)}{LPadW("최근1년 급락", 13)}");
            foreach (var dow in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
            {
                var all = r.Weekdays.Where(w => w.dow == dow).ToList();
                if (all.Count == 0) continue;
                var rec = all.Where(w => w.date > recentFrom).ToList();
                string WAvg(IEnumerable<decimal?> v) { var l = v.Where(x => x is not null).Select(x => x!.Value).ToList(); return l.Count > 0 ? Pct(l.Average()) : "-"; }
                string Rate(IEnumerable<bool> v) { var l = v.ToList(); return l.Count > 0 ? (l.Count(x => x) * 100 / l.Count) + "%" : "-"; }
                var name = "월화수목금"[(int)dow - 1] + "요일";
                sb.AppendLine($"  {PadW(name, 6)}{all.Count,6}{LPadW(WAvg(all.Select(w => (decimal?)w.chg)), 11)}{LPadW(Rate(all.Select(w => w.chg < 0)), 11)}" +
                              $"{LPadW(Rate(all.Select(w => w.chg <= -3)), 11)}{LPadW(WAvg(all.Select(w => w.mkt)), 12)}" +
                              $"{LPadW(WAvg(all.Select(w => w.f5)), 10)}{LPadW(WAvg(all.Select(w => w.f20)), 10)}" +
                              $"{LPadW(r.SplitDate is { } sdw ? WAvg(all.Where(w => w.date < sdw).Select(w => (decimal?)w.chg)) + " / " + WAvg(all.Where(w => w.date >= sdw).Select(w => (decimal?)w.chg)) : "-", 19)}" +
                              $"{LPadW(WAvg(rec.Select(w => (decimal?)w.chg)), 14)}{LPadW(Rate(rec.Select(w => w.chg < 0)), 13)}{LPadW(Rate(rec.Select(w => w.chg <= -3)), 13)}");
            }
            sb.AppendLine($"  · 최근 1년 = {recentFrom:yyyy-MM-dd} 이후. 요일 차이가 앞·뒤 기간과 최근 1년에서 같은 방향으로 크게 나올 때만 의미가 있습니다.");
            sb.AppendLine("  · 요일 5개를 비교하면 하나쯤은 우연으로도 튀어 보입니다. 하루 평균 차이가 0.1~0.2%p 수준이면 판단에 쓰기 어렵습니다.");
            sb.AppendLine();
        }
        if (r.CoreTiming.Count > 0 && r.CoreTiming[0].Cycles.Count > 0)
        {
            sb.AppendLine($"기본 몫 타이밍 (10거래일마다 매수 예정 · 예정일부터 5거래일 '매수 창' 안에서 언제 사나 · 보유 {r.Horizons[bIdx]}거래일)");
            const int NW = 24;
            sb.AppendLine($"  {PadW("방식", NW)}{LPadW("회차", 6)}{LPadW("매수가 차이", 13)}{LPadW("더 싸게 산 비율", 17)}{LPadW("20일 평균", 11)}{LPadW("중앙값", 10)}{LPadW("승률", 7)}" +
                          (r.SplitDate is not null ? $"{LPadW("매수가 차이 앞/뒤", 22)}" : ""));
            foreach (var t in r.CoreTiming)
            {
                var c = t.Cycles;
                var f = c.Where(x => x.fwd is not null).Select(x => x.fwd!.Value).OrderBy(x => x).ToList();
                string med = f.Count == 0 ? "-" : Pct(f.Count % 2 == 1 ? f[f.Count / 2] : (f[f.Count / 2 - 1] + f[f.Count / 2]) / 2m);
                string win = f.Count == 0 ? "-" : (f.Count(x => x > 0) * 100 / f.Count) + "%";
                var cheaper = c.Count(x => x.vsPlain < 0) * 100 / Math.Max(1, c.Count);
                string split = "";
                if (r.SplitDate is { } sd)
                {
                    var a = c.Where(x => x.date < sd).Select(x => x.vsPlain).DefaultIfEmpty().Average();
                    var b = c.Where(x => x.date >= sd).Select(x => x.vsPlain).DefaultIfEmpty().Average();
                    split = LPadW($"{Pct(a)} / {Pct(b)}", 22);
                }
                sb.AppendLine($"  {PadW(t.Name, NW)}{c.Count,6}{Pct(c.Average(x => x.vsPlain)),13}{LPadW(cheaper + "%", 17)}{Pct(f.Count > 0 ? f.Average() : null),11}{med,10}{win,7}{split}");
            }
            foreach (var t in r.CoreTiming) sb.AppendLine($"    {t.Name}: {t.Rule}");
            sb.AppendLine("  · 매수가 차이 = 같은 회차에 예정일 바로(T0) 산 가격 대비. 음수일수록 싸게 산 것입니다.");
            sb.AppendLine("  · 앞·뒤 기간 모두 음수이고 '더 싸게 산 비율'이 50%를 넘는 방식이 기본 몫 규칙 후보입니다.");
            sb.AppendLine();
        }
        if (r.Supply.Count > 0)
            Tables($"수급 이탈이 이어질 때 (S · 명분일 포함 모든 날 · 보유 {r.Horizons[bIdx]}거래일 · 기준선 = 아무 날)", r.Supply,
                new[] { "'수급이 다 빠질 때 사도 되나'를 보는 표입니다. S1·S2·S3(빠지는 중)와 S4(돌아서는 첫날)를 비교하세요.",
                        "S8 이 기준선보다 나쁘면 '연속 매도 중 하락일'은 떨어지는 칼이었다는 뜻입니다.",
                        "연속 신호는 같은 구간을 여러 번 셉니다 → 쿨다운 줄(S6·S7)로 확인하세요." });
        if (r.NoCatalyst.Count > 0)
            Tables($"명분 없는 날 눌림 (N · 보유 {r.Horizons[bIdx]}거래일 · 기준선 = 아무 날)", r.NoCatalyst,
                new[] { "명분 없는 날은 표본이 많아 명분일 표보다 덜 흔들립니다. 다만 연달아 나온 신호는 같은 구간을 여러 번 셉니다 → 쿨다운 줄(N11·N12)로 확인하세요.",
                        "N0(명분 없는 날 전부)보다 나은 줄이 '명분 없는 날에도 살 만한 자리' 후보입니다." });

        sb.AppendLine("지침 신호가 나온 날");
        var guide = r.Strategies[0];
        if (guide.Hits.Count == 0) sb.AppendLine("  없음");
        foreach (var hit in guide.Hits.OrderBy(x => x.Date))
            sb.AppendLine($"  {hit.Date:yyyy-MM-dd} {names.GetValueOrDefault(hit.Symbol, hit.Symbol),-8} {hit.Close,12:N0}  " +
                          string.Join("  ", r.Horizons.Select((h, i) => $"{h}일 {Pct(hit.Forward[i])}")) +
                          $"  · {hit.Catalyst}");
        sb.AppendLine();

        sb.AppendLine("알아둘 것");
        foreach (var n in r.Notes) sb.AppendLine("  · " + n);
        sb.AppendLine("  · 과거 성과가 앞으로를 보장하지 않습니다. 이 결과는 규칙을 정할 때 참고하는 용도입니다.");
        return sb.ToString();

        void Tables(string title, List<StrategyResult> list, string[] notes)
        {
            var baseAvg = Avg(baseline, bIdx);
            const int NW = 28;
            sb.AppendLine(title);
            sb.AppendLine($"  {PadW("변형", NW)}{LPadW("신호", 6)}{LPadW("5일 평균", 11)}{LPadW("10일 평균", 11)}{LPadW(r.Horizons[bIdx] + "일 평균", 11)}{LPadW("중앙값", 10)}{LPadW("승률", 7)}{LPadW("최악", 10)}{LPadW("기준선 대비", 13)}");
            foreach (var v in list)
            {
                if (v.Hits.Count == 0) { sb.AppendLine($"  {PadW(v.Name, NW)}{0,6}   신호 없음"); continue; }
                var last = v.Hits.Where(x => x.Forward[bIdx] is not null).Select(x => x.Forward[bIdx]!.Value).OrderBy(x => x).ToList();
                string med = last.Count == 0 ? "-" : Pct(last.Count % 2 == 1 ? last[last.Count / 2] : (last[last.Count / 2 - 1] + last[last.Count / 2]) / 2m);
                string win = last.Count == 0 ? "-" : (last.Count(x => x > 0) * 100 / last.Count) + "%";
                var avgLast = Avg(v, bIdx);
                sb.AppendLine($"  {PadW(v.Name, NW)}{v.Hits.Count,6}{Pct(Avg(v, 0)),11}{Pct(Avg(v, Math.Min(1, bIdx))),11}{Pct(avgLast),11}{med,10}{win,7}" +
                              $"{(last.Count == 0 ? "-" : Pct(last[0])),10}{(avgLast is { } a && baseAvg is { } b2 ? Pct(a - b2) + "p" : "-"),13}");
            }
            foreach (var v in list) sb.AppendLine($"    {v.Name}: {v.Rule}");
            foreach (var n in notes) sb.AppendLine("  · " + n);
            sb.AppendLine();

            if (r.SplitDate is not { } sd) return;
            string Cell((int n, decimal? avg, decimal? med, int? win, decimal? ex) x) =>
                x.n == 0 ? LPadW("신호 없음", 41)
                : $"{x.n,4}{Pct(x.avg),10}{Pct(x.med),10}{(x.win is { } w ? w + "%" : "-"),6}{(x.ex is { } e ? Pct(e) + "p" : "-"),11}";
            (int, decimal?, decimal?, int?, decimal?) Stat(IEnumerable<SignalHit> hits, decimal? bAvg)
            {
                var v = hits.Where(x => x.Forward[bIdx] is not null).Select(x => x.Forward[bIdx]!.Value).OrderBy(x => x).ToList();
                if (v.Count == 0) return (0, null, null, null, null);
                var med = v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2m;
                var avg = v.Average();
                return (v.Count, avg, med, v.Count(x => x > 0) * 100 / v.Count, bAvg is { } b ? avg - b : null);
            }
            var baseA = Stat(baseline.Hits.Where(h => h.Date < sd), null);
            var baseB = Stat(baseline.Hits.Where(h => h.Date >= sd), null);
            sb.AppendLine($"  └ 기간 나눠 보기 (앞 ~{sd.AddDays(-1):yyyy-MM} / 뒤 {sd:yyyy-MM}~)");
            sb.AppendLine($"  {PadW("", NW)}  {LPadW("── 앞 기간 ──", 41)}     {LPadW("── 뒤 기간 ──", 41)}");
            var head = $"{LPadW("신호", 4)}{LPadW("평균", 10)}{LPadW("중앙값", 10)}{LPadW("승률", 6)}{LPadW("기준선 대비", 11)}";
            sb.AppendLine($"  {PadW("변형", NW)}  {head}     {head}");
            sb.AppendLine($"  {PadW("아무 날 (기준선)", NW)}  {Cell(baseA)}     {Cell(baseB)}");
            foreach (var v in list)
                sb.AppendLine($"  {PadW(v.Name, NW)}  {Cell(Stat(v.Hits.Where(h => h.Date < sd), baseA.Item2))}     {Cell(Stat(v.Hits.Where(h => h.Date >= sd), baseB.Item2))}");
            sb.AppendLine("  · 앞·뒤 기간 모두 기준선 대비가 +이고 중앙값도 기준선보다 높아야 '한 시기의 운'이 아닐 가능성이 큽니다.");
            sb.AppendLine();
        }

        static decimal? Avg(StrategyResult s, int h)
        {
            var v = s.Hits.Where(x => x.Forward[h] is not null).Select(x => x.Forward[h]!.Value).ToList();
            return v.Count > 0 ? v.Average() : null;
        }
    }

    /// <summary>한글은 2칸으로 세어 표 칸을 맞춘다 (메모장 고정폭 글꼴 기준)</summary>
    private static int DisplayWidth(string s) => s.Sum(c => c >= '\u1100' && (c <= '\u115F' || (c >= '\u2E80' && c <= '\uA4CF') || (c >= '\uAC00' && c <= '\uD7A3') || (c >= '\uFF00' && c <= '\uFF60')) ? 2 : 1);
    private static string PadW(string s, int w) => s + new string(' ', Math.Max(1, w - DisplayWidth(s)));
    private static string LPadW(string s, int w) => new string(' ', Math.Max(1, w - DisplayWidth(s))) + s;

    /// <summary>신호별 원자료 CSV (엑셀로 열어 보기용)</summary>
    public static string ToCsv(BacktestReport r, IReadOnlyDictionary<string, string> names)
    {
        var sb = new StringBuilder();
        sb.AppendLine("전략,날짜,종목,종가," + string.Join(",", r.Horizons.Select(h => $"{h}일수익률%")) + ",명분");
        foreach (var s in r.Strategies.Take(r.Strategies.Count - 1).Concat(r.Variants).Concat(r.Splits).Concat(r.NoCatalyst).Concat(r.Supply)) // 기준선은 너무 길어서 제외
            foreach (var h in s.Hits.OrderBy(x => x.Date))
                sb.AppendLine(string.Join(",",
                    Csv(s.Name), h.Date.ToString("yyyy-MM-dd"), Csv(names.GetValueOrDefault(h.Symbol, h.Symbol)),
                    h.Close.ToString(CultureInfo.InvariantCulture),
                    string.Join(",", h.Forward.Select(f => f?.ToString("0.00", CultureInfo.InvariantCulture) ?? "")),
                    Csv(h.Catalyst ?? "")));
        return sb.ToString();

        static string Csv(string v) => v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
