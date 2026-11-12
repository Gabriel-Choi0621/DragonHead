namespace DragonHead.Core;

public enum Tone { Positive, Negative, Neutral }

/// <summary>숫자 근거가 있는 짧은 상황 태그. 판정에는 쓰지 않고 '왜 이렇게 움직이나'를 보여주는 용도.</summary>
public sealed record Tag(string Text, Tone Tone);

/// <summary>
/// 규칙 기반 태그. 토스 앱의 AI 요약 태그 중 수급·가격으로 설명되는 것들을 숫자로 재현한다.
/// 뉴스로만 알 수 있는 이유(예: 'AI 투자심리 회복')는 만들지 않는다.
/// </summary>
public static class TagEngine
{
    private const decimal StrongMovePct = 2.0m;      // '강세/약세'로 볼 등락률
    private const decimal RelativeGapPct = 2.0m;     // 코스피 대비 강·약세 차이 (%p)
    private const decimal ProgramMinWon = 10_000_000_000m;   // 프로그램 순매수 태그 최소 금액 (100억)
    private const decimal MarketFlowMinWon = 100_000_000_000m; // 시장 전체 수급 태그 최소 금액 (1,000억)

    /// <summary>감시 종목 전체에 대해 태그를 만든다. 업종 동반 태그는 여러 종목을 함께 봐야 해서 한 번에 처리.</summary>
    public static Dictionary<string, List<Tag>> BuildAll(IReadOnlyList<Snapshot> snaps, IReadOnlyList<Evaluation> evals)
    {
        var map = new Dictionary<string, List<Tag>>();
        foreach (var e in evals)
        {
            var s = snaps.FirstOrDefault(x => x.Symbol == e.Symbol);
            map[e.Symbol] = s is null ? new() : ForStock(s, e);
        }

        // 삼성전자·SK하이닉스가 둘 다 감시 중이고 같은 방향으로 크게 움직이면 반도체 동반 태그 (두 종목에만)
        var semis = evals.Where(e => e.Symbol is "005930" or "000660" && e.ChangePct is not null).ToList();
        if (semis.Count == 2)
        {
            Tag? sector = semis.All(e => e.ChangePct >= StrongMovePct) ? new("반도체 동반 강세", Tone.Positive)
                        : semis.All(e => e.ChangePct <= -StrongMovePct) ? new("반도체 동반 약세", Tone.Negative)
                        : null;
            if (sector is not null)
                foreach (var e in semis) map[e.Symbol].Add(sector);
        }

        return map;
    }

    public static List<Tag> ForStock(Snapshot s, Evaluation e)
    {
        var tags = new List<Tag>();
        var today = DateOnly.FromDateTime(s.Now.ToKst().DateTime);

        // ── 명분 반응 (공시·실적 이후 가격이 어떻게 반응했나)
        if (s.CatalystToday is not null && e.ChangePct is { } c0 && e.Phase is not (Phase.PreMarket or Phase.MorningTrap))
        {
            if (c0 >= StrongMovePct) tags.Add(new("명분일 강세 · 호재 반응?", Tone.Positive));
            else if (c0 <= -1.0m) tags.Add(new("명분일 약세 · 실망 반응?", Tone.Negative));
            else tags.Add(new("명분일 반응 미지근", Tone.Neutral));
        }
        if (s.CatalystPrevDay is not null && e.ChangePct is { } c1)
        {
            if (c1 > 0) tags.Add(new("명분 다음날도 상승", Tone.Positive));
            else if (c1 < -1.0m) tags.Add(new("명분 다음날 되돌림", Tone.Negative));
        }

        // ── 외국인 연속 매매 (오늘 잠정치 포함)
        var inv = s.Investors.OrderByDescending(r => r.Date).ToList();
        int buyStreak = 0, sellStreak = 0;
        foreach (var r in inv) { if (r.ForeignerNet > 0) buyStreak++; else break; }
        foreach (var r in inv) { if (r.ForeignerNet < 0) sellStreak++; else break; }
        bool brokeToday = e.ForeignTodayShares is <= 0 && e.PrevForeignStreak >= 2;
        if (buyStreak >= 2) tags.Add(new($"외국인 {buyStreak}일 연속 순매수", Tone.Positive));
        else if (brokeToday)
            tags.Add(new(e.ForeignTodayConfirmed || e.Phase == Phase.Closed
                ? $"외국인 {e.PrevForeignStreak}일 연속 매수 끊김"
                : $"외국인 {e.PrevForeignStreak}일 연속 매수 끊길 위기", Tone.Negative));
        else if (sellStreak >= 2) tags.Add(new($"외국인 {sellStreak}일 연속 순매도", Tone.Negative));

        // ── 오늘 외국인·기관 조합
        if (e.ForeignTodayShares is { } f && e.InstitutionTodayShares is { } i)
        {
            if (f > 0 && i > 0) tags.Add(new("외국인·기관 동반 순매수", Tone.Positive));
            else if (f < 0 && i < 0) tags.Add(new("외국인·기관 동반 순매도", Tone.Negative));
            else if (i > 0 && f < 0) tags.Add(new("기관만 매수 · 외국인 이탈", Tone.Negative));
            else if (f > 0 && i < 0) tags.Add(new("외국인 매수 · 기관 차익실현", Tone.Neutral));
        }

        // ── 프로그램매매 (오늘)
        var prog = s.Programs.FirstOrDefault(p => p.Date == today);
        if (prog is not null && s.Last > 0)
        {
            var won = prog.TotalNet * s.Last;
            if (won >= ProgramMinWon) tags.Add(new("프로그램 순매수 유입", Tone.Positive));
            else if (won <= -ProgramMinWon) tags.Add(new("프로그램 순매도 출회", Tone.Negative));
        }

        // ── 코스피 대비
        if (e.ChangePct is { } chg && s.MarketContext?.KospiChangePct is { } k)
        {
            var diff = chg - k;
            if (diff >= RelativeGapPct) tags.Add(new($"코스피 대비 강세 ({SignalEngine.Signed(Math.Round(diff, 1))}%p)", Tone.Positive));
            else if (diff <= -RelativeGapPct) tags.Add(new($"코스피 대비 약세 ({SignalEngine.Signed(Math.Round(diff, 1))}%p)", Tone.Negative));
        }

        // ── 가격 위치
        if (s.High200 is { } h200 && s.DayHigh is { } dh && Math.Max(dh, s.Last) > h200)
            tags.Add(new("200일 신고가 돌파", Tone.Positive));

        if (e.GapPct is { } gap && gap >= 1.5m && s.DayOpen is { } open && s.Last < open)
            tags.Add(new("갭상승 후 시가 아래로 밀림", Tone.Negative));
        else if (e.FromHighPct is { } fh && fh <= -2.0m && e.ChangePct is > 0)
            tags.Add(new($"고점에서 {Math.Abs(fh):0.#}% 밀림", Tone.Negative));
        else if (e.FromHighPct is { } fh2 && fh2 >= -0.3m && e.ChangePct is >= StrongMovePct)
            tags.Add(new("고가 부근 유지", Tone.Positive));

        return tags;
    }

    /// <summary>시장 전체 태그 (코스피 수급·방향)</summary>
    public static List<Tag> ForMarket(MarketContext? m)
    {
        var tags = new List<Tag>();
        if (m is null) return tags;

        if (m.KospiChangePct is { } k)
        {
            if (k >= 1.5m) tags.Add(new("코스피 강세장", Tone.Positive));
            else if (k <= -1.5m) tags.Add(new("코스피 약세장", Tone.Negative));
        }

        if (m.TodayFlow is { } f)
        {
            bool fBuy = f.ForeignNet >= MarketFlowMinWon, fSell = f.ForeignNet <= -MarketFlowMinWon;
            bool iBuy = f.InstitutionNet >= MarketFlowMinWon, iSell = f.InstitutionNet <= -MarketFlowMinWon;
            if (fBuy && iBuy) tags.Add(new("외국인·기관 순매수", Tone.Positive));
            else if (fSell && iSell) tags.Add(new("외국인·기관 순매도", Tone.Negative));
            else if (fBuy) tags.Add(new("외국인 매수 주도", Tone.Positive));
            else if (fSell) tags.Add(new("외국인 매도 우위", Tone.Negative));
            else if (iBuy) tags.Add(new("기관 매수 주도", Tone.Neutral));

            if (f.IndividualNet >= MarketFlowMinWon * 3 && (fSell || iSell))
                tags.Add(new("개인이 물량 받는 중", Tone.Negative));
        }

        return tags;
    }
}
