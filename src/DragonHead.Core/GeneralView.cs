namespace DragonHead.Core;

/// <summary>평소 판단 (명분 없는 날, 기본 몫용) 결과 단계</summary>
public enum GeneralVerdict
{
    /// <summary>볼 시간이 아니거나 데이터 부족, 또는 명분일(명분 판단이 맡음)</summary>
    Wait,
    /// <summary>사도 괜찮아요</summary>
    Good,
    /// <summary>뉴스·공시 확인하고 별일 없으면 사도 괜찮아요</summary>
    CheckFirst,
    /// <summary>오늘은 아니에요 (추격·과열·추세 이탈)</summary>
    NotToday,
    /// <summary>조금 오른 날 — 하락 마감하는 날을 기다려 봐도 됨 (매수 창 마지막 날이면 그냥 산다)</summary>
    Later,
}

public sealed class GeneralJudgement
{
    public required GeneralVerdict Verdict { get; init; }
    /// <summary>한두 문장짜리 의견 ("공시는 없는데 외국인·기관이 같이 팔아요. 뉴스 보고 별일 없으면 사도 괜찮아요.")</summary>
    public required string Opinion { get; init; }
    public required IReadOnlyList<Check> Checks { get; init; }
}

/// <summary>
/// 평소 판단: 명분이 없는 날 '오늘 기본 몫을 사도 괜찮은가'를 차트·수급·시장을 종합해서 말로 알려 준다.
///   추세   : 20일선·60일선 위치 (추세 유지 / 건전한 조정 / 추세 이탈)
///   오늘   : 등락률 (+2% 이상이면 추격)
///   과열   : 20일선보다 5% 이상 높으면 과열
///   수급   : 외국인·기관 오늘 잠정 (동반 매도면 이유 확인 필요)
///   시장   : 코스피 등락 (같이 빠졌으면 시장 탓, 혼자 빠졌으면 종목 이슈일 수 있음)
/// 명분일에는 명분 판단(Verdict)이 맡으므로 여기선 Wait.
/// 백테스트(기본 몫 타이밍): 한 주 안에서 언제 사느냐는 매수가 차이가 ±0.5% 안팎으로 작았고,
/// 두 기간 모두 조금이라도 싸게 산 건 '창 안 첫 하락 마감일'(T1)뿐이었다. 오른 날을 계속 피하면 상승장에서 오히려 비싸게 샀다(T8).
/// 그래서: 하락·보합 날 = 사도 괜찮음, 오른 날 = 기다려 봄(창 마지막 날엔 그냥 삼), 이유 모를 급락 = 뉴스 확인.
/// </summary>
public static class GeneralView
{
    public const decimal ChaseUpPct = 2.0m;
    public const decimal OverheatPct = 5.0m;
    public const decimal SharpDropPct = -3.0m;
    public const int LongSellStreak = 3;

    public static GeneralJudgement Judge(Snapshot s, Phase phase, decimal? change, long? foreignToday, long? instToday, bool confirmed = false)
    {
        var today = DateOnly.FromDateTime(s.Now.ToKst().DateTime);
        var closes = s.DailyBars.Where(b => DateOnly.FromDateTime(b.Time.DateTime) < today).OrderBy(b => b.Time).Select(b => b.Close).ToList();
        if (s.Last > 0) closes.Add(s.Last);
        decimal? Ma(int n, int back = 0) => closes.Count >= n + back ? closes.Skip(closes.Count - n - back).Take(n).Average() : null;
        var ma20 = Ma(20); var ma20Prev = Ma(20, 5); var ma60 = Ma(60);
        decimal? overMa20 = ma20 is > 0 ? Math.Round((s.Last / ma20.Value - 1m) * 100m, 1) : null;
        var kospi = s.MarketContext?.KospiChangePct;

        string trend; bool? trendOk; bool broken = false;
        if (ma20 is null || ma60 is null) { trend = "일봉이 부족해 추세 계산 못 함"; trendOk = null; }
        else if (s.Last >= ma20 && ma20 >= ma20Prev) { trend = $"추세 유지 · 20일선({ma20:N0}) 위, 20일선 상승 중"; trendOk = true; }
        else if (s.Last >= ma60) { trend = $"건전한 조정 · 20일선 아래지만 60일선({ma60:N0}) 위"; trendOk = true; }
        else if (ma20 < ma60) { trend = $"추세 이탈 · 60일선({ma60:N0}) 아래, 20일선도 60일선 아래 (참고 · 매수를 막진 않음)"; trendOk = false; broken = true; }
        else { trend = $"60일선({ma60:N0}) 아래로 막 내려옴"; trendOk = null; }

        bool chase = change is { } c1 && c1 >= ChaseUpPct;
        bool overheat = overMa20 is { } o && o >= OverheatPct;
        bool bothSelling = foreignToday is < 0 && instToday is < 0;
        // 외국인 연속 순매도 (어제까지 + 오늘 잠정). 백테스트 S표: 최근 기간 3일째 매도 중(S1) -3.6%p, 5일째(S2) -5.5%p (앞 기간엔 +0.8%p 안팎),
        // 매도가 끊기고 처음 순매수로 돌아선 날(S4)은 S표 중 가장 나았다. 하루짜리 동반 매도는 문제 없었다.
        int prevStreak = 0;
        foreach (var d in s.Investors.Where(x => x.Date < today).OrderByDescending(x => x.Date))
        {
            if (d.ForeignerNet < 0) prevStreak++; else break;
        }
        int sellStreak = foreignToday is < 0 ? prevStreak + 1 : foreignToday is null ? prevStreak : 0;
        bool longSelling = sellStreak >= LongSellStreak;
        bool turnDay = prevStreak >= 3 && foreignToday is > 0;
        bool marketDown = kospi is { } k && k <= -0.7m;
        bool aloneDrop = change is { } c2 && c2 <= -2m && (kospi is null || kospi > -0.7m);
        bool sharpDrop = change is { } c3 && c3 <= SharpDropPct;

        string flow = foreignToday is null && instToday is null
            ? "오늘 수급 아직 없음"
            : $"외국인 {(foreignToday is { } f ? SignalEngine.Signed(f) : "-")} · 기관 {(instToday is { } i ? SignalEngine.Signed(i) : "-")} ({(confirmed ? "확정" : "잠정")})";

        var checks = new List<Check>
        {
            new("추세", trendOk, trend),
            new("오늘 등락", change is null ? null : !chase,
                change is null ? "데이터 없음" : chase ? $"{SignalEngine.Signed(change.Value)}% 급등 → 따라 사면 추격" : $"{SignalEngine.Signed(change.Value)}%"),
            new("과열", overMa20 is null ? null : !overheat,
                overMa20 is null ? "20일선 계산 못 함" : $"20일선 대비 {SignalEngine.Signed(overMa20.Value)}%" + (overheat ? " → 과열" : "")),
            new("수급", foreignToday is null && instToday is null ? null : !bothSelling,
                flow + (bothSelling ? " → 둘 다 팔고 있음" : "")),
            new("시장", kospi is null ? null : !aloneDrop,
                kospi is null ? "코스피 데이터 없음"
                : aloneDrop ? $"코스피 {SignalEngine.Signed(kospi.Value)}%인데 혼자 빠짐 → 종목 이슈일 수 있음"
                : marketDown ? $"코스피 {SignalEngine.Signed(kospi.Value)}% · 시장 전체가 빠지는 날"
                : $"코스피 {SignalEngine.Signed(kospi.Value)}%"),
            new("미국 반도체", null,
                s.MarketContext?.UsOvernight is { Count: > 0 } usAll && UsSemis.Semis(usAll) is { Count: > 0 } us
                    ? $"밤사이({UsSemis.D(us[0].SessionDate)}) {UsSemis.Line(us)} · 참고용 (판단 기록에 쌓아 효과 확인 중)"
                    : "미국 시세 없음"),
            new("외인 연속", s.Investors.Count == 0 ? null : !longSelling,
                s.Investors.Count == 0 ? "일별 수급 없음"
                : turnDay ? $"{prevStreak}일 연속 매도 끝 → 오늘 순매수로 돌아섬"
                : sellStreak > 0 ? $"{sellStreak}일째 순매도{(foreignToday is null ? " (오늘 제외)" : "")}{(longSelling ? " → 매도가 길게 이어지는 중" : "")}"
                : "연속 매도 아님"),
        };

        GeneralJudgement Make(GeneralVerdict v, string opinion) => new() { Verdict = v, Opinion = opinion, Checks = checks };

        if (phase is Phase.Holiday) return Make(GeneralVerdict.Wait, "휴장일입니다.");
        if (s.CatalystToday is not null) return Make(GeneralVerdict.Wait, "오늘은 명분일이라 명분 판단을 따릅니다.");
        if (phase is Phase.PreMarket or Phase.MorningTrap) return Make(GeneralVerdict.Wait, "10시 이후에 판단합니다. 아침 움직임은 흔들리기 쉬워요.");
        if (change is null) return Make(GeneralVerdict.Wait, "가격 데이터를 기다리는 중입니다.");

        string when = phase is Phase.Midday or Phase.Afternoon ? " (지금 기준 · 최종은 14:50 이후)" : "";
        bool closed = phase is Phase.Closed;
        // 장 마감 뒤에는 '오늘 사도 괜찮아요' 대신 지난 시점으로 말한다
        string BuyOk() => closed ? "오늘 종가 기준으로는 사도 괜찮은 날이었어요. 내일 장중에 다시 판단해요." : "오늘 사도 괜찮아요.";
        string NewsOk() => closed ? "내일 장 전에 뉴스 한 번 보고 별일 없으면 사도 괜찮아요." : "뉴스 한 번 보고 별일 없으면 사도 괜찮아요.";
        decimal? rel = change is { } cr && kospi is { } kr ? cr - kr : null;   // 시장 대비 (음수면 시장보다 더 빠짐)
        bool deepDrop = overMa20 is { } od && od <= -10m;

        // 추세 이탈은 '사지 마라'가 아니라 주의 문구만 붙인다. 60일선 회복을 기다리면 크게 오른 뒤에야 사게 되고,
        // 백테스트에서도 추세 필터가 매수가를 낮추지 못했다.
        string caution = broken ? " 추세는 꺾여 있어요(60일선 아래) — 바닥은 아무도 모르니 회차를 나눠 천천히 사는 게 맞아요." : "";
        // 급등·과열도 '오늘은 아님'이 아니라 '기다려 봄': 며칠 안에 하락 마감하는 날을 기다리고, 매수 창 마지막 날엔 그냥 산다
        // (오른 날을 계속 피하면 상승장에서 오히려 비싸게 샀다).
        if (chase)
            return Make(GeneralVerdict.Later, $"오늘 {SignalEngine.Signed(change.Value)}% 올랐어요. 따라 사기보다 며칠 안에 하락 마감하는 날을 기다려요. 안 빠지면 창 마지막 날 사요.{when}");
        if (overheat)
            return Make(GeneralVerdict.Later, $"20일선보다 {overMa20:0.#}% 높아 단기 과열이에요. 하락 마감하는 날을 기다려 보고, 안 빠지면 창 마지막 날 사요.{when}");
        string depthNote = deepDrop
            ? $" 20일선보다 {Math.Abs(overMa20!.Value):0.#}% 아래까지 짧은 기간에 크게 내려온 급락 구간이라, 사더라도 1회분만 사고 다음 회차는 간격을 두세요."
            : "";
        // 뉴스 확인이 필요한 건 '종목만' 이상하게 빠질 때다. 시장 전체가 무너진 날의 동반 매도는 시장 탓으로 본다.
        //   ① 코스피는 거의 안 빠졌는데(-0.7% 초과) 종목만 -3% 이하
        //   ② 코스피보다 3%p 넘게 더 빠짐 (반도체 대형주는 평소 코스피의 1.5배쯤 움직여서 2%p 안팎은 보통 범위)
        //   ③ 코스피 데이터가 없을 땐 -3% 이하 + 외국인·기관 동반 매도
        bool stockOnly = (aloneDrop && sharpDrop) || (rel is { } rx && rx <= -3m && change <= -2m) || (kospi is null && sharpDrop && bothSelling);
        if (stockOnly)
            return Make(GeneralVerdict.CheckFirst,
                $"공시는 없는데 시장보다 크게 빠졌어요 ({SignalEngine.Signed(change.Value)}%{(kospi is { } kk2 ? $", 코스피 {SignalEngine.Signed(kk2)}%" : "")})" +
                $"{(bothSelling ? " · 외국인·기관도 같이 팔았어요" : "")}{(longSelling ? $" · 외국인은 {sellStreak}일째 매도 중" : "")}. {NewsOk()}{(longSelling ? " 매도가 길게 이어지는 중이라 사더라도 1회분만." : "")}{depthNote}{(deepDrop ? "" : caution)}{when}");
        if (change <= 0)
        {
            // 하락 폭·시장 대비·급락 구간에 따라 말을 바꾼다 (-5.7% 를 '쉬어가는 날'이라 부르지 않게)
            var cv = change.Value;
            string what = cv <= -3m ? "크게 빠진 날이에요" : cv <= -1m ? "빠진 날이에요" : "쉬어가는 날이에요";
            string market = kospi is { } kv
                ? rel is { } rv && rv <= -1.5m ? $" 시장보다 더 빠졌어요 (코스피 {SignalEngine.Signed(kv)}%)."
                  : marketDown ? $" 시장 전체가 같이 빠졌어요 (코스피 {SignalEngine.Signed(kv)}%)." : ""
                : "";
            string depth = deepDrop ? depthNote : trendOk == true ? " 추세는 살아 있어요." : "";
            // 시장 전체가 빠진 날의 동반 매도는 따로 말해 준다 (종목 문제로 보진 않음)
            if (bothSelling && marketDown && !longSelling) market += " 외국인·기관도 같이 팔았지만 시장 전체가 빠진 날이라 종목만의 문제로 보긴 어려워요.";
            // 하루 매도는 괜찮지만, 외국인 매도가 5일 넘게 이어지는 중의 하락일은 '떨어지는 칼'이었다 → 순매수 전환을 기다린다
            if (longSelling)
                return Make(GeneralVerdict.Later,
                    $"{what} ({SignalEngine.Signed(cv)}%).{market} 다만 외국인이 {sellStreak}일째 팔고 있어요. 매도가 길게 이어질 땐 더 내려가는 경우가 많았어요(백테스트). " +
                    $"외국인이 순매수로 돌아서는 날을 기다려 보고, 창 마지막 날까지 안 돌아서면 그때 1회분만 사요.{when}");
            if (turnDay) market += $" 외국인이 {prevStreak}일 매도를 끝내고 오늘 순매수로 돌아섰어요.";
            return Make(GeneralVerdict.Good, $"{what} ({SignalEngine.Signed(cv)}%).{market}{depth} {BuyOk()}{(deepDrop ? "" : caution)}{when}");
        }
        string turnNote = turnDay ? $" 외국인이 {prevStreak}일 매도를 끝내고 순매수로 돌아선 건 좋은 신호예요." : "";
        return Make(GeneralVerdict.Later, $"조금 오른 날이에요({SignalEngine.Signed(change.Value)}%).{turnNote} 하락 마감하는 날을 기다려 봐요. 며칠 안에 안 빠지면 그냥 사도 돼요.{caution}{when}");
    }

    public static string Describe(GeneralVerdict v) => v switch
    {
        GeneralVerdict.Good => "사도 괜찮음",
        GeneralVerdict.CheckFirst => "확인 후 괜찮음",
        GeneralVerdict.NotToday => "오늘은 아님",
        GeneralVerdict.Later => "기다려 봄",
        _ => "대기",
    };
}
