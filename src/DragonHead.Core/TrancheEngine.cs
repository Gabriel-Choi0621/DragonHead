namespace DragonHead.Core;

/// <summary>예산의 한 몫 (기본 몫 또는 기회 몫)의 진행 상태</summary>
public sealed class PlanPart
{
    /// <summary>"기본 몫" / "기회 몫"</summary>
    public required string Name { get; init; }
    public required int Done { get; init; }
    public required int Total { get; init; }
    /// <summary>이 몫 전체 예산과 1회분</summary>
    public decimal Budget { get; init; }
    public decimal PerTranche { get; init; }
    public DateOnly? LastBuy { get; init; }
    public int? TradingDaysSince { get; init; }
    /// <summary>true = 지금 이 몫을 살 차례, false = 아님, null = 판단 불가</summary>
    public bool? Ready { get; init; }
    public required string Reason { get; init; }
    public long Shares { get; init; }
    public decimal Amount { get; init; }
    /// <summary>예수금(매수가능금액)이 회당 금액보다 적어 Shares 가 줄었거나 0이면 그 안내. 판정·차례 자체는 그대로.</summary>
    public string? CashNote { get; init; }

    /// <summary>회당 주식 수 (몫 예산을 주가로 나눠 회차에 고르게). 0이면 예산으로 1주도 못 삼.</summary>
    public long SharesPerTranche { get; init; }
    /// <summary>설정한 회차 수 (주가가 비싸 실제 회차가 줄었으면 Total 보다 큼)</summary>
    public int ConfiguredTotal { get; init; }

    public bool Enabled => Total > 0 && Budget > 0;
    /// <summary>설정은 여러 회차인데 주가가 비싸 2회 미만으로 줄어 사실상 한 번에 다 사게 되는 경우</summary>
    public bool NoSplit => Enabled && ConfiguredTotal >= 2 && Total < 2;
    public string? NoSplitWarning => NoSplit
        ? $"분할이 안 됩니다 — {Name} 예산이 {SharesPerTranche}주분이라 한 번에 다 씁니다. 예산을 늘리거나 비율을 바꾸세요 (예산·분할 설정)"
        : null;
    public bool Complete => !Enabled || Done >= Total;
    public int NextIndex => Done + 1;
}

/// <summary>
/// 종목 예산을 두 몫으로 나눈 분할 매수 계획.
///   기본 몫: 명분 없는 날 '평소 판단'(GeneralView)이 괜찮다고 하고, 지난 기본 몫 매수 뒤 CoreIntervalDays 거래일이 지났을 때 1회분
///   기회 몫: 명분일 눌림(DipBuy) 판정이 뜬 날에만 1회분
/// 과거 매수는 그날이 명분일이면 기회 몫, 아니면 기본 몫으로 센다.
/// </summary>
public sealed class TrancheStatus
{
    public required decimal Budget { get; init; }
    /// <summary>종목별 예산을 안 정해서 공통 기본값(Strategy.TotalBudget)을 쓰는 중</summary>
    public bool BudgetIsDefault { get; init; }
    public required decimal CoreRatio { get; init; }
    public required PlanPart Core { get; init; }
    public required PlanPart Opp { get; init; }
    /// <summary>체결 내역이 없어 매입금액으로 회차를 추정했는지 (전부 기본 몫으로 봄)</summary>
    public bool Estimated { get; init; }
    /// <summary>이번 보유 구간의 매수일 (차트 표시용)</summary>
    public IReadOnlyList<DateOnly> BuyDays { get; init; } = Array.Empty<DateOnly>();

    public bool Complete => Core.Complete && Opp.Complete;
    /// <summary>지금 살 차례인 몫 (없으면 null). 기회 몫이 먼저.</summary>
    public PlanPart? ReadyPart => Opp is { Ready: true, Complete: false } ? Opp : Core is { Ready: true, Complete: false } ? Core : null;
    public string Summary => $"{BudgetPresets.NameOf(CoreRatio)} · 기본 {Core.Done}/{Core.Total} · 기회 {Opp.Done}/{Opp.Total}";
}

public static class TrancheEngine
{
    private static string Won(decimal v) => v >= 10_000 ? (v / 10_000m).ToString("#,0.#") + "만원" : v.ToString("N0") + "원";

    /// <summary>이번 보유 구간(직전에 전량 매도한 뒤부터)의 매수일 목록</summary>
    public static List<DateOnly> CurrentCycleBuyDays(IReadOnlyList<Fill> fills)
    {
        decimal position = 0;
        var days = new List<DateOnly>();
        foreach (var f in fills.OrderBy(f => f.FilledAt))
        {
            if (f.IsBuy)
            {
                if (position <= 0) days.Clear(); // 새 보유 구간 시작
                position += f.Quantity;
                if (!days.Contains(f.Date)) days.Add(f.Date);
            }
            else
            {
                position -= f.Quantity;
                if (position <= 0) { position = 0; days.Clear(); }
            }
        }
        return days;
    }

    public static TrancheStatus? Compute(Snapshot s, Evaluation e, StrategySettings cfg)
    {
        var budget = s.Budget ?? cfg.TotalBudget;
        if (budget <= 0) return null;
        var price = s.Last > 0 ? s.Last : s.PrevClose;
        var today = DateOnly.FromDateTime(s.Now.ToKst().DateTime);

        var coreRatio = Math.Clamp(cfg.CoreRatio, 0m, 1m);
        var coreBudget = Math.Floor(budget * coreRatio);
        var oppBudget = budget - coreBudget;
        // 금액이 아니라 '주식 수'로 나눈다: 몫 예산으로 살 수 있는 주식 수를 회차에 고르게.
        // 주가가 비싸 주식 수가 회차보다 적으면 회차를 줄인다 (예: 122만원 종목, 몫 200만 → 1주 · 1회).
        (int total, long perShares, decimal perWon) Split(decimal partBudget, int configured)
        {
            if (partBudget <= 0 || configured <= 0) return (0, 0, 0);
            if (price <= 0) return (configured, 0, Math.Floor(partBudget / configured));
            long shares = (long)Math.Floor(partBudget / price);
            if (shares == 0) return (configured, 0, Math.Floor(partBudget / configured));
            int n = (int)Math.Min(configured, shares);
            long per = shares / n;
            return (n, per, per * price);
        }
        var (coreTotal, coreShares, corePer) = Split(coreBudget, Math.Max(1, cfg.CoreTranches));
        var (oppTotal, oppShares, oppPer) = Split(oppBudget, Math.Max(1, cfg.OppTranches));

        bool holding = s.Holding is { Quantity: > 0 };
        var buyDays = holding ? CurrentCycleBuyDays(s.Fills) : new List<DateOnly>();
        bool estimated = holding && buyDays.Count == 0;

        var oppDays = buyDays.Where(d => s.CatalystDays.Contains(d)).ToList();
        var coreDays = buyDays.Where(d => !s.CatalystDays.Contains(d)).ToList();
        int coreDone = estimated
            ? Math.Clamp((int)Math.Round(s.Holding!.PurchaseAmount / Math.Max(1m, corePer > 0 ? corePer : oppPer)), 1, Math.Max(1, coreTotal))
            : Math.Min(coreDays.Count, coreTotal);
        int oppDone = Math.Min(oppDays.Count, oppTotal);

        int? Since(DateOnly? last)
        {
            if (last is not { } lb) return null;
            int n = s.DailyBars.Count(b => DateOnly.FromDateTime(b.Time.DateTime) > lb);
            bool todayIsSession = s.Market is { IsOpen: true } m && m.Date == today;
            if (todayIsSession && today > lb && s.DailyBars.All(b => DateOnly.FromDateTime(b.Time.DateTime) < today)) n++;
            return n;
        }

        long SharesFor(long perShares)
        {
            if (perShares <= 0 || price <= 0) return 0;
            if (s.CashBuyingPower is { } cash && cash < perShares * price) return (long)Math.Floor(cash / price);
            return perShares;
        }
        string? CashNote(long perShares)
        {
            if (perShares <= 0 || price <= 0 || s.CashBuyingPower is not { } cash || cash >= perShares * price) return null;
            long can = (long)Math.Floor(cash / price);
            return can == 0
                ? $"예수금 부족 · 매수가능 {Won(cash)} (회당 {perShares}주 ≈ {Won(perShares * price)} 필요) · 입금하면 그대로 사면 됩니다"
                : $"예수금이 모자라 {can}주만 계산 · 매수가능 {Won(cash)} (원래 회당 {perShares}주)";
        }
        string TooPricey(string name, decimal partBudget) =>
            $"{name} {Won(partBudget)}으로는 1주({Won(price)})도 안 됩니다 · 예산이나 비율을 조정하세요";

        // ── 기본 몫: '평소 판단'이 괜찮다고 할 때. 간격(CoreIntervalDays)은 너무 자주 사지 않게 막는 최소 간격.
        DateOnly? coreLast = coreDays.Count > 0 ? coreDays.Max() : null;
        int? coreSince = Since(coreLast);
        bool? coreReady;
        string coreReason;
        var g = e.General;
        string gText = g is null ? "" : $"{GeneralView.Describe(g.Verdict)} · {g.Opinion}";
        if (coreTotal == 0) { coreReady = false; coreReason = "기본 몫 없음 (비율 0%)"; }
        else if (coreShares == 0) { coreReady = false; coreReason = TooPricey("기본 몫", coreBudget); }
        else if (coreDone >= coreTotal) { coreReady = false; coreReason = $"기본 몫 {coreTotal}회 완료"; }
        else if (s.CatalystDays.Contains(today)) { coreReady = false; coreReason = $"기본 몫 {coreDone + 1}차 · 명분일이라 명분 판단을 따름"; }
        else if (!estimated && coreSince is { } cs && cs < cfg.CoreIntervalDays)
        {
            coreReady = false;
            coreReason = $"기본 몫 {coreDone + 1}차 · 최소 간격 {cfg.CoreIntervalDays - cs}거래일 남음 (마지막 {coreLast:M/d})";
        }
        else if (g is null || g.Verdict == GeneralVerdict.Wait)
        {
            coreReady = null;
            coreReason = $"기본 몫 {coreDone + 1}차 · {(g?.Opinion ?? "판단 대기")}";
        }
        else if (g.Verdict is GeneralVerdict.NotToday or GeneralVerdict.Later)
        {
            // 매수 창: 최소 간격이 지난 날부터 CoreWindowDays 거래일. 창 마지막 날이면 기다리지 않고 산다
            // (백테스트에서 오른 날을 계속 피하면 상승장에서 오히려 비싸게 샀다).
            int? dayInWindow = coreSince is { } cs2 ? cs2 - cfg.CoreIntervalDays + 1 : null;
            int window = Math.Max(1, cfg.CoreWindowDays);
            if (dayInWindow is { } dw && dw >= window)
            {
                coreReady = true;
                coreReason = $"기본 몫 {coreDone + 1}차 · 매수 창 마지막 날이라 삽니다 ({window}거래일 기다림) — {g.Opinion}";
            }
            else
            {
                coreReady = false;
                var left = dayInWindow is { } d2 ? $" · 창 {window - d2}거래일 남음" : estimated ? " · 체결 내역이 없어 하락·보합 날을 기다림" : coreLast is null ? " · 첫 회는 괜찮은 날을 기다림" : "";
                coreReason = $"기본 몫 {coreDone + 1}차 · {GeneralView.Describe(g.Verdict)}{left} — {g.Opinion}";
            }
        }
        else
        {
            // Good / CheckFirst: 살 차례 (CheckFirst 는 '확인 후'를 붙인다)
            coreReady = true;
            coreReason = $"기본 몫 {coreDone + 1}차 · {(g.Verdict == GeneralVerdict.CheckFirst ? "확인 후 매수" : "매수 괜찮음")} — {g.Opinion}" +
                         (estimated ? $" (체결 내역이 없어 간격 {cfg.CoreIntervalDays}거래일은 직접 확인)" : coreLast is null ? " (첫 회)" : "");
        }

        // ── 기회 몫: 명분일 눌림 판정이 뜬 날만
        DateOnly? oppLast = oppDays.Count > 0 ? oppDays.Max() : null;
        var nextCat = s.CatalystDays.Where(d => d > today).OrderBy(d => d).Cast<DateOnly?>().FirstOrDefault();
        bool? oppReady;
        string oppReason;
        if (oppTotal == 0) { oppReady = false; oppReason = "기회 몫 없음 (비율 100% 기본)"; }
        else if (oppShares == 0) { oppReady = false; oppReason = TooPricey("기회 몫", oppBudget); }
        else if (oppDone >= oppTotal) { oppReady = false; oppReason = $"기회 몫 {oppTotal}회 완료"; }
        else if (e.Verdict == Verdict.DipBuy)
        {
            oppReady = true;
            oppReason = $"기회 몫 {oppDone + 1}차 매수 차례 · 명분일 눌림";
        }
        else
        {
            oppReady = false;
            oppReason = $"기회 몫 {oppDone + 1}차 · " + e.Verdict switch
            {
                Verdict.DipCandidate => "명분일 눌림 후보 (종가까지 지켜보기)",
                Verdict.NoChase => "명분일인데 올라서 대기",
                Verdict.ShockWarn => "쇼크 의심 · 공시 확인 후",
                _ => nextCat is { } nc ? $"다음 명분 {nc:M/d} 눌림 대기" : "명분일 눌림 대기",
            };
        }

        return new TrancheStatus
        {
            Budget = budget,
            BudgetIsDefault = s.Budget is null,
            CoreRatio = coreRatio,
            Estimated = estimated,
            BuyDays = buyDays,
            Core = new PlanPart
            {
                Name = "기본 몫", Done = coreDone, Total = coreTotal, Budget = coreBudget, PerTranche = corePer,
                SharesPerTranche = coreShares, ConfiguredTotal = Math.Max(1, cfg.CoreTranches),
                LastBuy = coreLast, TradingDaysSince = coreSince, Ready = coreReady, Reason = coreReason,
                Shares = SharesFor(coreShares), Amount = SharesFor(coreShares) * price, CashNote = CashNote(coreShares),
            },
            Opp = new PlanPart
            {
                Name = "기회 몫", Done = oppDone, Total = oppTotal, Budget = oppBudget, PerTranche = oppPer,
                SharesPerTranche = oppShares, ConfiguredTotal = Math.Max(1, cfg.OppTranches),
                LastBuy = oppLast, TradingDaysSince = Since(oppLast), Ready = oppReady, Reason = oppReason,
                Shares = SharesFor(oppShares), Amount = SharesFor(oppShares) * price, CashNote = CashNote(oppShares),
            },
        };
    }
}
