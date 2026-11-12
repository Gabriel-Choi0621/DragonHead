using System.Globalization;

namespace DragonHead.Core;

public enum Phase
{
    Holiday,
    PreMarket,      // ~09:00
    MorningTrap,    // 09:00~10:00  용의 꼬리. 구경만.
    Midday,         // 10:00~13:00
    Afternoon,      // 13:00~14:50
    Decision,       // 14:50~종가단일가 시작  판정 구간
    ClosingAuction, // 15:20~15:30  종가 동시호가
    Closed,
}

/// <summary>
/// 판정. 2026-10 백테스트(삼성·하이닉스 5년, 명분일 51일)로 규칙을 바꿨다:
/// 명분일에 오른 날 사는 것보다 '명분일에 하락 마감한 날' 사는 쪽이 앞·뒤 기간 모두 기준선보다 좋았다.
/// </summary>
public enum Verdict
{
    /// <summary>판단 보류. 명분 없는 날이거나 시간이 아님.</summary>
    Wait,
    /// <summary>시초가 갭상승 (09~10시 정보용). 매수 금지.</summary>
    GapTrap,
    /// <summary>명분일인데 오르는 중 / 상승 마감. 추격 매수 금지.</summary>
    NoChase,
    /// <summary>명분일 장중(10:00~14:50) 기준가 아래. 종가까지 이 상태면 매수 후보.</summary>
    DipCandidate,
    /// <summary>명분일 판정 시각(14:50~) 기준가 아래. 1차 분할 매수 후보.</summary>
    DipBuy,
    /// <summary>명분일 -5% 이하 급락인데 코스피는 보합·상승. 실적 쇼크일 수 있어 공시 확인 후 판단.</summary>
    ShockWarn,
    /// <summary>명분 없는 날, 하락 중이고 외국인·기관이 같이 빠져나감 (참고용). 분할 추가 매수도 보류.</summary>
    Avoid,
}

public sealed record Check(string Label, bool? Passed, string Detail);

public sealed class Evaluation
{
    public required string Symbol { get; init; }
    public required string Name { get; init; }
    public required Phase Phase { get; init; }
    public required Verdict Verdict { get; init; }
    public required string Headline { get; init; }
    public required IReadOnlyList<Check> Checks { get; init; }

    public decimal Last { get; init; }
    public decimal? ChangePct { get; init; }
    /// <summary>NXT 프리·애프터마켓 가격과 기준가(전일 KRX 종가) 대비 % — 토스 앱 등락률과 같은 기준</summary>
    public decimal? ExtendedPrice { get; init; }
    public decimal? ExtendedChangePct { get; init; }
    /// <summary>프리·애프터마켓 가격과 판정 가격(장 마감 후 KRX 종가, 장 전엔 전일 종가) 대비 % — 다음 갭 미리보기</summary>
    public decimal? ExtendedVsLastPct { get; init; }
    public decimal PrevClose { get; init; }
    public decimal? GapPct { get; init; }
    public decimal? FromHighPct { get; init; }
    public decimal? AfternoonHoldPct { get; init; }

    /// <summary>오늘 외국인 순매수(잠정, 주). 오늘 기록이 아직 없으면 null.</summary>
    public long? ForeignTodayShares { get; init; }
    /// <summary>오늘 외국인 순매수 추정 금액(원) = 주식수 × 현재가. 토스는 거래대금을 주지 않으므로 근사치.</summary>
    public decimal? ForeignTodayAmountApprox { get; init; }
    public long? InstitutionTodayShares { get; init; }
    public int ForeignStreak { get; init; }
    /// <summary>어제까지(오늘 제외) 외국인 연속 순매수 일수</summary>
    public int PrevForeignStreak { get; init; }
    /// <summary>오늘 수급이 저녁 확정치로 바뀌었는지 (확정 전엔 장중 잠정치)</summary>
    public bool ForeignTodayConfirmed { get; init; }

    public TrancheSuggestion? Tranche { get; init; }
    public Holding? Holding { get; init; }

    /// <summary>규칙 기반 상황 태그 (TagEngine). 판정과 무관한 참고 정보.</summary>
    public IReadOnlyList<Tag> Tags { get; set; } = Array.Empty<Tag>();

    /// <summary>평소 판단 (명분 없는 날 기본 몫용): 차트·수급·시장 종합 의견</summary>
    public GeneralJudgement? General { get; init; }

    /// <summary>보유 중일 때 분할 매수 진행·다음 회차 상태. 보유가 없으면 null.</summary>
    public TrancheStatus? Plan { get; set; }

    /// <summary>오늘 정규장 1분봉 (미니 차트·리포트용)</summary>
    public IReadOnlyList<Bar> Intraday { get; init; } = Array.Empty<Bar>();
    public decimal? DayHigh { get; init; }
}

public sealed record TrancheSuggestion(decimal Budget, long Shares, decimal Amount, string Note);

public static class SignalEngine
{
    public static Phase GetPhase(DateTimeOffset now, MarketDay? market)
    {
        now = now.ToKst();
        var today = DateOnly.FromDateTime(now.DateTime);
        if (market is not null && market.Date == today && !market.IsOpen)
            return Phase.Holiday;
        if (market is null && now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return Phase.Holiday;

        var t = TimeOnly.FromDateTime(now.DateTime);
        var auction = market?.ClosingAuctionStart is { } a && DateOnly.FromDateTime(a.DateTime) == today
            ? TimeOnly.FromDateTime(a.DateTime)
            : new TimeOnly(15, 20);

        if (t < new TimeOnly(9, 0)) return Phase.PreMarket;
        if (t < new TimeOnly(10, 0)) return Phase.MorningTrap;
        if (t < new TimeOnly(13, 0)) return Phase.Midday;
        if (t < new TimeOnly(14, 50)) return Phase.Afternoon;
        if (t < auction) return Phase.Decision;
        if (t < new TimeOnly(15, 30)) return Phase.ClosingAuction;
        return Phase.Closed;
    }

    /// <summary>오늘 잠정치 포함, 최신일부터 외국인 순매수(>0)가 이어진 일수.</summary>
    public static int ForeignStreak(IReadOnlyList<InvestorDay> recordsNewestFirst)
    {
        int n = 0;
        foreach (var r in recordsNewestFirst.OrderByDescending(r => r.Date))
        {
            if (r.ForeignerNet > 0) n++;
            else break;
        }
        return n;
    }

    /// <summary>
    /// '외국인 연속' 체크의 설명. 장중 잠정치는 오후에 뒤집힐 수 있어서,
    /// 오늘 순매도면 장중엔 '끊길 위기', 장 마감 뒤엔 '끊김(잠정)', 저녁 확정 뒤엔 '끊김'으로 나눠 말한다.
    /// </summary>
    private static string StreakDetail(IReadOnlyList<InvestorDay> inv, InvestorDay? todayRec, int prevStreak, int streak,
                                       bool confirmed, Phase phase, int min)
    {
        var basis = $"(기준 {min}일)";
        if (todayRec is null)
            return prevStreak > 0
                ? $"어제까지 {prevStreak}일 연속 순매수 · 오늘 잠정치 아직 없음 {basis}"
                : $"연속 순매수 없음 · 오늘 잠정치 아직 없음 {basis}";

        if (todayRec.ForeignerNet > 0)
            return $"오늘 포함 {streak}일 연속 순매수{(confirmed ? "" : " (오늘은 잠정)")} {basis}";

        bool afterClose = phase is Phase.Closed;
        if (prevStreak > 0)
        {
            if (confirmed) return $"오늘 순매도로 끊김 · 어제까지 {prevStreak}일 {basis}";
            if (afterClose) return $"오늘 순매도로 끊김 · 어제까지 {prevStreak}일 (잠정, 저녁에 확정) {basis}";
            return $"끊길 위기 · 어제까지 {prevStreak}일, 오늘 잠정 순매도 (순매수로 돌면 {prevStreak + 1}일) {basis}";
        }

        int sell = 0;
        foreach (var r in inv.OrderByDescending(r => r.Date)) { if (r.ForeignerNet < 0) sell++; else break; }
        return sell >= 2
            ? $"{sell}일 연속 순매도 중{(confirmed ? "" : " (오늘은 잠정)")} {basis}"
            : $"연속 순매수 없음 {basis}";
    }

    /// <summary>
    /// 회피 조건: -2% 이상 하락 + 외국인 순매도 + (기관도 순매도 또는 외국인 2일 이상 연속 순매도).
    /// 해당하면 이유 문장, 아니면 null.
    /// </summary>
    private static string? IsAvoid(decimal? change, long? foreignToday, long? instToday, IReadOnlyList<InvestorDay> inv, StrategySettings cfg)
    {
        if (change is not { } c || c > -cfg.AvoidDropPct || foreignToday is not < 0) return null;

        int sellStreak = 0;
        foreach (var r in inv.OrderByDescending(r => r.Date)) { if (r.ForeignerNet < 0) sellStreak++; else break; }

        if (instToday is < 0)
            return $"{Signed(c)}% 하락에 외국인·기관이 같이 팔고 있습니다.";
        if (sellStreak >= 2)
            return $"{Signed(c)}% 하락, 외국인 {sellStreak}일 연속 순매도.";
        return null;
    }

    public static Evaluation Evaluate(Snapshot s, StrategySettings cfg)
    {
        var now = s.Now.ToKst();
        var today = DateOnly.FromDateTime(now.DateTime);
        var phase = GetPhase(now, s.Market);

        decimal? Pct(decimal? a, decimal? b) =>
            a is { } x && b is { } y && y != 0 ? Math.Round((x / y - 1m) * 100m, 2) : null;

        var change = Pct(s.Last, s.PrevClose);
        var gap = Pct(s.DayOpen, s.PrevClose);
        var high = s.DayHigh is { } h ? Math.Max(h, s.Last) : (decimal?)null;
        var fromHigh = Pct(s.Last, high);
        var afternoonHold = Pct(s.LowSince13, high);

        var todayRec = s.Investors.FirstOrDefault(r => r.Date == today);
        long? foreignToday = todayRec?.ForeignerNet;
        long? instToday = todayRec?.InstitutionNet;
        // 오늘 기록이 없으면 연속 일수는 '어제까지 확정' 기준. 오늘 잠정치가 있으면 포함.
        int streak = ForeignStreak(s.Investors);
        int prevStreak = ForeignStreak(s.Investors.Where(r => r.Date < today).ToList());
        // 확정치가 반영되면 개인 값이 채워진다 (장중 잠정 기록에는 개인이 없음)
        bool confirmed = todayRec?.IndividualNet is not null;
        decimal? foreignAmt = foreignToday is { } f ? f * s.Last : null;
        var streakDetail = StreakDetail(s.Investors, todayRec, prevStreak, streak, confirmed, phase, cfg.MinForeignStreak);

        // 거래량: 오늘 정규장 분봉 합 ÷ 직전 20거래일 평균 (일봉 거래량에는 넥스트레이드가 섞일 수 있어 근사치)
        decimal? volRatio = null;
        var pastDaily = s.DailyBars.Where(b => DateOnly.FromDateTime(b.Time.DateTime) < today).TakeLast(20).ToList();
        var todayVol = s.MinuteBars.Sum(b => b.Volume);
        if (pastDaily.Count >= 10 && todayVol > 0 && pastDaily.Average(b => b.Volume) is var avgVol && avgVol > 0)
            volRatio = Math.Round(todayVol / avgVol, 2);
        var kospi = s.MarketContext?.KospiChangePct;

        bool catalyst = s.CatalystToday is not null;
        bool dip = change is < 0;
        bool shock = change is { } cs && cs <= -cfg.ShockDropPct && kospi is >= 0;
        bool bothSelling = foreignToday is < 0 && instToday is < 0;
        bool heavyVolume = volRatio is { } vr && vr >= cfg.StrongVolumeRatio;
        bool strong = dip && (bothSelling || heavyVolume);

        var strongParts = new List<string>();
        if (bothSelling) strongParts.Add("외국인·기관 동반 매도");
        if (heavyVolume) strongParts.Add($"거래량 {volRatio:0.#}배");

        var checks = new List<Check>
        {
            new("명분",
                catalyst,
                s.CatalystToday ?? "등록된 명분 없음 (실적 공시·실적 IR 등)"),
            new("기준가 아래",
                !catalyst || change is null ? null : dip,
                change is null ? "데이터 없음" : $"전일 대비 {Signed(change.Value)}%"),
            new("쇼크 아님",
                !catalyst || change is null ? null : !shock,
                shock ? $"{Signed(change!.Value)}% 급락인데 코스피는 {Signed(kospi!.Value)}% · 실적 쇼크일 수 있음"
                      : kospi is { } kk ? $"코스피 {Signed(kk)}%" : "코스피 데이터 없음"),
            new("강도",
                catalyst && dip ? (strong ? true : null) : null,
                strongParts.Count > 0 ? string.Join(" · ", strongParts) + (catalyst && dip ? " → 강한 후보" : " (명분일이었다면 강한 후보)")
                    : $"외국인 {(foreignToday is { } f1 ? Signed(f1) : "-")} · 기관 {(instToday is { } i1 ? Signed(i1) : "-")} · 거래량 {(volRatio is { } v1 ? v1.ToString("0.#") + "배" : "-")} ({(confirmed ? "확정" : "잠정")})"),
            new("외국인 연속",
                null,
                streakDetail + " · 참고용 (판정에 안 씀)"),
        };

        Verdict verdict;
        string headline;
        string strongText = strong ? $" 강한 후보 ({string.Join(" · ", strongParts)})." : "";

        switch (phase)
        {
            case Phase.Holiday:
                verdict = Verdict.Wait;
                headline = "휴장일. 쉬는 날입니다.";
                break;

            case Phase.PreMarket:
                verdict = Verdict.Wait;
                headline = catalyst
                    ? $"오늘 명분일: {s.CatalystToday}. 오르면 추격 금지, 기준가 아래로 마감하면 기회 몫 매수 후보."
                    : "장 시작 전. 9~10시는 구경만.";
                break;

            case Phase.MorningTrap:
                if (gap is { } g && g >= cfg.GapWarnPct)
                {
                    verdict = Verdict.GapTrap;
                    headline = $"시초가 갭상승 {Signed(g)}%. 아침 급등은 꼬리일 확률이 높습니다. 매수 금지.";
                }
                else
                {
                    verdict = Verdict.Wait;
                    headline = catalyst ? "명분일 09~10시. 방향이 정해질 때까지 구경만." : "09~10시 관망 구간.";
                }
                break;

            default:
                bool decision = phase is Phase.Decision or Phase.ClosingAuction or Phase.Closed;
                if (!catalyst)
                {
                    if (IsAvoid(change, foreignToday, instToday, s.Investors, cfg) is { } why)
                    {
                        verdict = Verdict.Avoid;
                        headline = $"약세 경계. {why} 명분 없는 날이라 기회 몫(명분일 눌림) 대상은 아니에요. 기본 몫은 평소 판단을 따릅니다.";
                    }
                    else
                    {
                        verdict = Verdict.Wait;
                        headline = "명분 없는 날. 기회 몫 대상은 아니고, 기본 몫은 평소 판단을 따릅니다.";
                    }
                }
                else if (change is null)
                {
                    verdict = Verdict.Wait;
                    headline = "명분일. 가격 데이터를 기다리는 중.";
                }
                else if (shock)
                {
                    verdict = Verdict.ShockWarn;
                    headline = $"명분일 {Signed(change.Value)}% 급락인데 코스피는 {Signed(kospi!.Value)}%. 실적 쇼크일 수 있습니다. 공시 내용 확인 후 판단.";
                }
                else if (!dip)
                {
                    verdict = Verdict.NoChase;
                    headline = $"명분일 {Signed(change.Value)}%. 오른 명분일은 과거에 20일 성과가 기준선보다 나빴습니다. 추격 금지.";
                }
                else if (decision)
                {
                    verdict = Verdict.DipBuy;
                    headline = phase == Phase.Closed
                        ? $"명분일 {Signed(change.Value)}% 하락 마감. 기회 몫 매수 후보였던 날입니다.{strongText}"
                        : $"명분일 {Signed(change.Value)}% 눌림. 기회 몫 매수 후보 (종가 동시호가 15:20~15:30).{strongText}";
                }
                else
                {
                    verdict = Verdict.DipCandidate;
                    headline = $"명분일 {Signed(change.Value)}% 눌림 중. 종가까지 기준가 아래면 기회 몫 매수 후보.{strongText}";
                }
                break;
        }

        var evaluation = new Evaluation
        {
            Symbol = s.Symbol,
            Name = s.Name,
            Phase = phase,
            Verdict = verdict,
            Headline = headline,
            Checks = checks,
            Last = s.Last,
            ChangePct = change,
            ExtendedPrice = s.ExtendedPrice,
            ExtendedChangePct = Pct(s.ExtendedPrice, s.PrevClose),
            ExtendedVsLastPct = Pct(s.ExtendedPrice, s.Last),
            PrevClose = s.PrevClose,
            GapPct = gap,
            FromHighPct = fromHigh,
            AfternoonHoldPct = afternoonHold,
            ForeignTodayShares = foreignToday,
            ForeignTodayAmountApprox = foreignAmt,
            InstitutionTodayShares = instToday,
            ForeignStreak = streak,
            PrevForeignStreak = prevStreak,
            ForeignTodayConfirmed = confirmed,
            Intraday = s.MinuteBars,
            DayHigh = high,
            // 이미 보유 중이면 '1차 분할' 대신 분할 매수 상태(Plan)로 안내한다
            Tranche = null,   // 예전 '1차 정찰병'. 지금은 Plan(기본 몫·기회 몫)으로 안내한다.
            General = GeneralView.Judge(s, phase, change, foreignToday, instToday, confirmed),
            Holding = s.Holding,
        };
        evaluation.Plan = TrancheEngine.Compute(s, evaluation, cfg);
        return evaluation;
    }

    public static TrancheSuggestion? Tranche(Snapshot s, StrategySettings cfg)
    {
        if (s.Last <= 0) return null;
        var budget = Math.Floor(cfg.TotalBudget * cfg.TrancheRatio);
        string note = "";
        if (s.CashBuyingPower is { } cash && cash < budget)
        {
            note = $"매수가능금액 {Won(cash)}이 1회분보다 적어 그 안에서 계산함. ";
            budget = cash;
        }
        var shares = (long)Math.Floor(budget / s.Last);
        if (shares == 0)
            note += $"1주({Won(s.Last)})가 1회분 예산({Won(budget)})보다 비쌉니다.";
        return new TrancheSuggestion(budget, shares, shares * s.Last, note.Trim());
    }

    // ───────────── 표시용 포맷 ─────────────

    private static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");

    public static string Signed(decimal v) => (v > 0 ? "+" : "") + v.ToString("0.##", Ko);
    public static string Signed(long v) => (v > 0 ? "+" : "") + v.ToString("N0", Ko);
    public static string Won(decimal v) => v.ToString("N0", Ko) + "원";

    public static string Eok(decimal? won)
    {
        if (won is null) return "-";
        var eok = won.Value / 100_000_000m;
        return (eok > 0 ? "+" : "") + eok.ToString(Math.Abs(eok) >= 10 ? "N0" : "0.#", Ko) + "억";
    }

    public static string Describe(Phase p) => p switch
    {
        Phase.Holiday => "휴장",
        Phase.PreMarket => "장 시작 전",
        Phase.MorningTrap => "09~10시 · 아침 흔들림 (판단 안 함)",
        Phase.Midday => "오전~점심 · 관찰",
        Phase.Afternoon => "13시~ · 오후 (최종 판단은 14:50)",
        Phase.Decision => "판정 구간",
        Phase.ClosingAuction => "종가 동시호가",
        Phase.Closed => "장 마감",
        _ => p.ToString(),
    };

    public static string Describe(Verdict v) => v switch
    {
        Verdict.Wait => "관망",
        Verdict.GapTrap => "갭상승 경계",
        Verdict.NoChase => "추격 금지",
        Verdict.DipCandidate => "눌림 후보",
        Verdict.DipBuy => "눌림 매수",
        Verdict.ShockWarn => "쇼크 의심",
        Verdict.Avoid => "약세 경계",
        _ => v.ToString(),
    };
}
