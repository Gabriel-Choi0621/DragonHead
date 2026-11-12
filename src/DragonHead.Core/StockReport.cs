using System.Globalization;
using System.Net;
using System.Text;

namespace DragonHead.Core;

/// <summary>종목 리포트에 들어가는 재료</summary>
public sealed class ReportData
{
    public required string Symbol { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset At { get; init; }
    public decimal Last { get; init; }
    public decimal PrevClose { get; init; }
    /// <summary>감시 종목이면 최신 판정. 아니면 null.</summary>
    public Evaluation? Eval { get; init; }
    /// <summary>일봉 (오래된 것 → 최신, 마지막이 오늘일 수 있음)</summary>
    public IReadOnlyList<Bar> Daily { get; init; } = Array.Empty<Bar>();
    /// <summary>오늘 정규장 1분봉</summary>
    public IReadOnlyList<Bar> Minute { get; init; } = Array.Empty<Bar>();
    /// <summary>투자자별 매매동향 (최신순)</summary>
    public IReadOnlyList<InvestorDay> Investors { get; init; } = Array.Empty<InvestorDay>();
    public IReadOnlyList<ProgramDay> Programs { get; init; } = Array.Empty<ProgramDay>();
    /// <summary>최근 공시 (최신순)</summary>
    public IReadOnlyList<DartDisclosure> Disclosures { get; init; } = Array.Empty<DartDisclosure>();
    public IReadOnlyList<CatalystEvent> Catalysts { get; init; } = Array.Empty<CatalystEvent>();
    public Holding? Holding { get; init; }
    public IReadOnlyList<DateOnly> BuyDays { get; init; } = Array.Empty<DateOnly>();
    public MarketContext? Market { get; init; }
    public string? DartNote { get; init; }
    /// <summary>'주가 기록' 대화형 차트용 긴 일봉 (없으면 Daily)</summary>
    public IReadOnlyList<Bar> HistoryDaily { get; init; } = Array.Empty<Bar>();
    /// <summary>'주가 기록' 대화형 차트용 여러 날 1분봉 (없으면 Minute)</summary>
    public IReadOnlyList<Bar> HistoryMinute { get; init; } = Array.Empty<Bar>();

    /// <summary>DART 정기보고서 분기 실적 (없으면 기업 정보 섹션 생략)</summary>
    public IReadOnlyList<QuarterFin> Quarters { get; init; } = Array.Empty<QuarterFin>();
    public string? FinNote { get; init; }

    public decimal? ChangePct => ChartMath.Pct(Last, PrevClose);
}

/// <summary>
/// 종목 리포트를 한 장짜리 HTML(차트는 SVG)로 만든다. 외부 파일 없이 혼자 열린다.
/// 앱이 저장하고 기본 브라우저로 연다.
/// </summary>
public static class HtmlReport
{
    private static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const string Up = "#f05454", Down = "#4c9aff", Ma20C = "#f0b040", Ma60C = "#4fd1c5", Green = "#2ecc71", Muted = "#969ca8";

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string N(decimal v) => v.ToString("N0", Ko);
    private static string S(decimal? v, string unit = "%") => v is { } x ? (x > 0 ? "+" : "") + x.ToString("#,0.##", Ko) + unit : "-";
    private static string Cls(decimal? v) => v is > 0 ? "up" : v is < 0 ? "down" : "";
    private static string F(double v) => v.ToString("0.#", Inv);

    public static string Build(ReportData d)
    {
        // 날짜·요일을 PC 언어와 상관없이 한국어로 (보간 문자열은 현재 문화권을 쓴다)
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = Ko;
        try { return BuildCore(d); }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    private static string BuildCore(ReportData d)
    {
        var trend = TrendSummary.From(d.Daily, d.Last);
        var today = DateOnly.FromDateTime(d.At.ToKst().DateTime);
        var sb = new StringBuilder();

        sb.Append("""
<!doctype html><html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<link rel="stylesheet" href="https://cdn.jsdelivr.net/gh/orioncactus/pretendard@v1.3.9/dist/web/static/pretendard.min.css">
<style>
:root{--bg:#181a1f;--card:#22252c;--text:#e8eaee;--muted:#969ca8;--line:#30343c;--up:#f05454;--down:#4c9aff;--green:#2ecc71;--amber:#f0b040;--purple:#a878ff}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.6 Pretendard,"Malgun Gothic",sans-serif}
main{max-width:960px;margin:0 auto;padding:28px 16px 60px}
h1{font-size:26px;margin:0}h2{font-size:18px;margin:34px 0 10px;padding-top:6px;border-top:1px solid var(--line)}
.sub{color:var(--muted);font-size:13px}.up{color:var(--up)}.down{color:var(--down)}.muted{color:var(--muted)}
.card{background:var(--card);border-radius:10px;padding:16px 18px;margin:10px 0}
.badge{display:inline-block;padding:3px 10px;border-radius:6px;font-weight:700;color:#fff;font-size:14px;vertical-align:middle}
.price{font-size:30px;font-weight:800;margin-right:8px}
table{width:100%;border-collapse:collapse;font-size:14px}th,td{padding:7px 8px;border-bottom:1px solid var(--line);text-align:right}
th{color:var(--muted);font-weight:600}td:first-child,th:first-child{text-align:left}
.checks td{text-align:left}.ok{color:var(--green)}.no{color:var(--up)}.na{color:var(--muted)}
.tags span{display:inline-block;margin:3px 6px 3px 0;padding:2px 9px;border-radius:12px;background:#2c3038;font-size:13px}
.tags .pos{color:var(--green)}.tags .neg{color:var(--amber)}
.help{background:#1d2026;border-left:3px solid var(--amber);padding:12px 16px;border-radius:6px;font-size:14px}
.help b{color:var(--amber)}
.legend span{margin-right:14px;font-size:13px;cursor:help}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:10px}
.grid .card{margin:0}.k{color:var(--muted);font-size:13px}.v{font-size:19px;font-weight:700}
svg text{font-family:Pretendard,"Malgun Gothic",sans-serif}
a{color:var(--down)}ul{margin:6px 0;padding-left:20px}
@media print{body{background:#fff;color:#000}.card{background:#f4f5f7}}
</style>
""");
        sb.Append($"<title>{E(d.Name)} 리포트 {d.At:yyyy-MM-dd HH:mm}</title></head><body><main>");

        // ── 머리
        sb.Append($"<h1>{E(d.Name)} <span class='sub'>{E(d.Symbol)}</span></h1>");
        sb.Append($"<div class='sub'>{d.At:yyyy-MM-dd (ddd) HH:mm} 기준 · 용머리 알리미 리포트 · 투자 판단은 본인 책임입니다</div>");
        var e = d.Eval;
        sb.Append("<div class='card'>");
        // 09:00 전에는 오늘 등락이 없으니 0% 대신 '장 시작 전 · 전 거래일 종가'로
        bool preOpen = d.At.ToKst().TimeOfDay < new TimeSpan(9, 0, 0) && d.ChangePct is null or 0m;
        if (preOpen)
            sb.Append($"<span class='price'>{N(d.PrevClose)}원</span><span class='sub' style='font-size:15px'>장 시작 전 · 전 거래일 KRX 종가(오늘 기준가)</span>");
        else
        {
            sb.Append($"<span class='price {Cls(d.ChangePct)}'>{N(d.Last)}원</span><span class='{Cls(d.ChangePct)}' style='font-size:18px;font-weight:700'>{S(d.ChangePct)}</span>");
            sb.Append($"<span class='sub'> · 기준가 {N(d.PrevClose)}원</span>");
        }
        if (e is not null)
            sb.Append($"<div style='margin-top:10px'><span class='badge' style='background:{VerdictColor(e.Verdict)}'>{E(SignalEngine.Describe(e.Verdict))}</span> {E(e.Headline)}</div>");
        else
            sb.Append("<div class='sub' style='margin-top:8px'>감시 종목이 아니라 용머리 판정은 없습니다. 시장 창에서 ☆ 를 누르면 판정이 붙어요.</div>");
        sb.Append("</div>");

        // ── 한눈에
        sb.Append("<h2>한눈에 보기</h2><ul>");
        foreach (var line in Summary(d, trend, today)) sb.Append($"<li>{line}</li>");
        sb.Append("</ul>");

        // ── 평소 판단 (명분 없는 날 · 기본 몫)
        if (e?.General is { } gen)
        {
            var gc = gen.Verdict switch { GeneralVerdict.Good => Green, GeneralVerdict.CheckFirst => "#f0b040", GeneralVerdict.NotToday => "#a878ff", _ => "#787e8a" };
            sb.Append("<h2>평소 판단 <span class='sub'>명분 없는 날 · 기본 몫 · 차트·수급·시장 종합</span></h2><div class='card'>");
            sb.Append($"<span class='badge' style='background:{gc}'>{E(GeneralView.Describe(gen.Verdict))}</span> <span style='font-size:16px'>{E(gen.Opinion)}</span>");
            sb.Append("<table class='checks' style='margin-top:8px'>");
            foreach (var c in gen.Checks)
            {
                var (mark, cls) = c.Passed switch { true => ("✓", "ok"), false => ("✗", "no"), null => ("–", "na") };
                sb.Append($"<tr><td style='width:36px' class='{cls}'><b>{mark}</b></td><td style='width:110px'><b>{E(c.Label)}</b></td><td>{E(c.Detail)}</td></tr>");
            }
            sb.Append("</table><div class='sub' style='margin-top:6px'>하락·보합 마감이면 '사도 괜찮음', 급등(+2%↑)·과열(20일선 +5%↑)·외국인 3일+ 연속 매도면 '기다려 봄'(창 마지막 날엔 삼), 공시 없이 시장보다 크게 빠지면 '뉴스 확인 후'. 추세 이탈은 주의 문구만 붙습니다. " +
                      "백테스트에서 명분 없는 날 조건들의 우위는 작았습니다 — 추격·과열을 피하는 상식 규칙으로 보세요.</div></div>");
        }

        // ── 판정 체크리스트
        if (e is not null)
        {
            sb.Append("<h2>명분 판단 체크리스트 <span class='sub'>명분일 눌림 규칙 · 기회 몫</span></h2><div class='card'><table class='checks'>");
            foreach (var c in e.Checks)
            {
                var (mark, cls) = c.Passed switch { true => ("✓", "ok"), false => ("✗", "no"), null => ("–", "na") };
                sb.Append($"<tr><td style='width:36px' class='{cls}'><b>{mark}</b></td><td style='width:110px'><b>{E(c.Label)}</b></td><td>{E(c.Detail)}<div class='sub'>{E(CheckHelp(c.Label))}</div></td></tr>");
            }
            sb.Append("</table></div>");
            if (e.Tags.Count > 0)
            {
                sb.Append("<div class='tags'>");
                foreach (var t in e.Tags)
                    sb.Append($"<span class='{(t.Tone == Tone.Positive ? "pos" : t.Tone == Tone.Negative ? "neg" : "")}'>#{E(t.Text)}</span>");
                sb.Append("</div>");
            }
        }

        // ── 일봉 차트
        // ── 주가 기록 (대화형: 1·5·10분·일·주·월·년)
        sb.Append(ReportChart.Section(
            d.HistoryDaily.Count > 0 ? d.HistoryDaily : d.Daily,
            d.HistoryMinute.Count > 0 ? d.HistoryMinute : d.Minute,
            d.Catalysts, d.BuyDays, d.PrevClose, d.Investors));

        sb.Append("<h2>일봉 차트 <span class='sub'>최근 약 6개월 · 기준가·평단 표시</span></h2>");
        sb.Append("<div class='legend sub'>");
        sb.Append($"<span title='{E(ChartHelp.Candle)}'>▮ 캔들 ⓘ</span>");
        sb.Append($"<span style='color:{Ma20C}' title='{E(ChartHelp.Ma20)}'>━ 20일선 ⓘ</span>");
        sb.Append($"<span style='color:{Ma60C}' title='{E(ChartHelp.Ma60)}'>━ 60일선 ⓘ</span>");
        sb.Append($"<span title='{E(ChartHelp.BaseLine)}'>┅ 기준가 ⓘ</span>");
        if (d.Holding is { Quantity: > 0 }) sb.Append($"<span style='color:{Green}' title='{E(ChartHelp.AvgPrice)}'>┅ 평단 ⓘ</span>");
        sb.Append($"<span style='color:{Green}' title='{E(ChartHelp.BuyMark)}'>▲ 매수 ⓘ</span>");
        sb.Append($"<span style='color:{Down}' title='{E(ChartHelp.CatalystMark)}'>◆ 명분 ⓘ</span>");
        sb.Append("<span>(ⓘ 에 마우스를 올리면 설명)</span></div>");
        sb.Append(DailySvg(d));
        sb.Append("<div class='help'><b>이 차트에서 볼 것</b><ul>");
        foreach (var f in ChartHelp.DailyFocus) sb.Append($"<li>{E(f)}</li>");
        sb.Append("</ul></div>");

        // ── 오늘 1분봉
        if (d.Minute.Count > 1)
        {
            sb.Append("<h2>오늘 흐름 <span class='sub'>1분봉 · 정규장</span></h2>");
            sb.Append(MinuteSvg(d));
            sb.Append("<div class='help'><b>오늘 차트에서 볼 것</b><ul>");
            foreach (var f in ChartHelp.MinuteFocus) sb.Append($"<li>{E(f)}</li>");
            sb.Append("</ul></div>");
        }

        // ── 추세 지표
        sb.Append($"<h2>추세 — {E(trend.Label)}</h2><p class='sub'>{E(trend.Explain)}</p><div class='grid'>");
        void Tile(string k, string v, string cls = "", string? tip = null) =>
            sb.Append($"<div class='card'{(tip is null ? "" : $" title='{E(tip)}'")}><div class='k'>{k}{(tip is null ? "" : " ⓘ")}</div><div class='v {cls}'>{v}</div></div>");
        Tile("20일선", trend.Ma20 is { } m20 ? $"{N(Math.Round(m20))} <span class='sub'>({S(ChartMath.Pct(d.Last, m20))})</span>" : "-", "", ChartHelp.Ma20);
        Tile("60일선", trend.Ma60 is { } m60 ? $"{N(Math.Round(m60))} <span class='sub'>({S(ChartMath.Pct(d.Last, m60))})</span>" : "-", "", ChartHelp.Ma60);
        Tile("20일선 기울기 (5일)", S(trend.Ma20Slope5), Cls(trend.Ma20Slope5), "20일선이 5거래일 전보다 얼마나 올랐나. +면 위를 향하는 중.");
        Tile("5일 수익률", S(trend.Ret5), Cls(trend.Ret5));
        Tile("20일 수익률", S(trend.Ret20), Cls(trend.Ret20));
        Tile("60일 수익률", S(trend.Ret60), Cls(trend.Ret60));
        Tile("20일 고점 대비", S(trend.FromHigh20), Cls(trend.FromHigh20), "최근 한 달 최고가에서 얼마나 내려왔나. -3~-10%면 건전한 조정 범위.");
        Tile("200일 고점 대비", S(trend.FromHigh200), Cls(trend.FromHigh200), "약 10개월 최고가 대비 위치. 0%에 가까우면 신고가 근처.");
        Tile("하루 변동폭 (20일)", trend.Volatility20 is { } v20 ? $"±{v20.ToString("0.##", Ko)}%" : "-", "", "최근 20일 동안 하루에 보통 이 정도 오르내렸다는 뜻. 클수록 출렁임이 큰 종목.");
        Tile("거래량 (20일 평균 대비)", trend.VolumeRatio is { } vr ? $"{vr.ToString("0.0#", Ko)}배" : "-", "", ChartHelp.Volume);
        sb.Append("</div>");

        // ── 수급
        sb.Append("<h2>수급 — 최근 10거래일 <span class='sub'>주식 수 · 오늘은 장중 잠정치</span></h2>");
        if (d.Investors.Count == 0) sb.Append("<p class='sub'>수급 데이터가 없습니다.</p>");
        else
        {
            var closeByDate = d.Daily.ToDictionary(b => DateOnly.FromDateTime(b.Time.DateTime), b => b.Close);
            sb.Append("<div class='card'><table><tr><th>날짜</th><th>종가</th><th>외국인</th><th>기관</th><th>개인</th><th>프로그램</th></tr>");
            foreach (var r in d.Investors.OrderByDescending(r => r.Date).Take(10))
            {
                var prog = d.Programs.FirstOrDefault(p => p.Date == r.Date);
                closeByDate.TryGetValue(r.Date, out var close);
                sb.Append($"<tr><td>{r.Date:MM.dd (ddd)}{(r.Date == today && r.IndividualNet is null ? " <span class='sub'>잠정</span>" : "")}</td>" +
                          $"<td>{(close > 0 ? N(close) : "-")}</td>" +
                          $"<td class='{Cls(r.ForeignerNet)}'>{S(r.ForeignerNet, "")}</td><td class='{Cls(r.InstitutionNet)}'>{S(r.InstitutionNet, "")}</td>" +
                          $"<td class='{Cls(r.IndividualNet)}'>{(r.IndividualNet is { } iv ? S(iv, "") : "-")}</td>" +
                          $"<td class='{Cls(prog?.TotalNet)}'>{(prog is null ? "-" : S(prog.TotalNet, ""))}</td></tr>");
            }
            sb.Append("</table></div>");
            sb.Append("<p class='sub'>하루짜리 외국인·기관 동반 매도는 백테스트에서 매수 성과가 나쁘지 않았지만, 외국인 순매도가 3일 넘게 이어지는 중엔 최근 기간 성과가 나빴습니다(평소 판단에 반영). 개인 수치는 저녁 확정 후에 나옵니다.</p>");
        }

        // ── 보유 · 예산 분할 (기본 몫 정기 분할 + 기회 몫 명분일 눌림)
        if (d.Holding is { Quantity: > 0 } || e?.Plan is not null)
        {
            sb.Append("<h2>보유 · 예산 분할</h2><div class='card'>");
            if (d.Holding is { Quantity: > 0 } h)
                sb.Append($"<div>보유 <b>{h.Quantity.ToString("#,0.######", Inv)}주</b> · 평단 <b>{N(h.AveragePrice)}원</b> · 평가 <b>{N(h.MarketValue)}원</b> · " +
                          $"손익 <b class='{Cls(h.ProfitLoss)}'>{S(h.ProfitLoss, "원")} ({S(h.ProfitPct)})</b></div>");
            if (e?.Plan is { } p)
            {
                sb.Append($"<div style='margin-top:6px'>예산 <b>{N(p.Budget)}원</b> · <b>{E(BudgetPresets.NameOf(p.CoreRatio))}</b> " +
                          $"(기본 몫 {p.CoreRatio * 100:0}% · 기회 몫 {(1 - p.CoreRatio) * 100:0}%)" +
                          (p.Estimated ? " · <span class='muted'>체결 내역이 없어 회차는 매입금액으로 추정</span>" : "") + "</div>");
                sb.Append("<table style='margin-top:6px'><tr><th>몫</th><th>예산</th><th>1회분</th><th>진행</th><th style='text-align:left'>상태</th></tr>");
                foreach (var part in new[] { p.Core, p.Opp })
                {
                    if (!part.Enabled) continue;
                    var cls = part.Ready == true ? "ok" : "muted";
                    sb.Append($"<tr><td>{E(part.Name)}</td><td>{N(part.Budget)}원</td><td>{N(part.PerTranche)}원</td><td><b>{part.Done}/{part.Total}</b></td>" +
                              $"<td style='text-align:left' class='{cls}'>{E(part.Reason)}" +
                              (part.Ready == true && part.Shares > 0 ? $" → <b>{part.Shares}주 ≈ {N(part.Amount)}원</b>" : "") + "</td></tr>");
                }
                sb.Append("</table><div class='sub' style='margin-top:6px'>기본 몫은 평소 판단이 괜찮은 날(최소 간격 지나서), 기회 몫은 명분일 눌림에서만 삽니다. " +
                          "과거 매수는 그날이 명분일이면 기회 몫으로 셉니다.</div>");
            }
            sb.Append("</div>");
        }

        // ── 명분 · 공시
        sb.Append("<h2>명분 · 공시</h2>");
        var upcoming = d.Catalysts.Where(c => c.ParsedDate >= today).OrderBy(c => c.Date).ToList();
        var past = d.Catalysts.Where(c => c.ParsedDate < today && c.ParsedDate >= today.AddDays(-120)).OrderByDescending(c => c.Date).ToList();
        sb.Append("<div class='card'><b>앞으로 남은 명분</b><ul>");
        if (upcoming.Count == 0) sb.Append("<li class='sub'>없음 — 명분일이 없어 기회 몫은 쉬고, 기본 몫은 평소 판단대로 삽니다.</li>");
        foreach (var c in upcoming)
            sb.Append($"<li>{c.ParsedDate:MM.dd (ddd)} · D-{c.ParsedDate!.Value.DayNumber - today.DayNumber} · {Link(c.Title, c.Url)}</li>");
        sb.Append("</ul>");
        if (past.Count > 0)
        {
            sb.Append("<b>최근 명분일과 그 뒤 반응</b><table><tr><th>날짜</th><th>내용</th><th>당일</th><th>5거래일 뒤</th></tr>");
            foreach (var c in past.Take(6))
            {
                var (dayChg, after5) = Reaction(d.Daily, c.ParsedDate!.Value);
                sb.Append($"<tr><td>{c.ParsedDate:yy.MM.dd}</td><td style='text-align:left'>{Link(c.Title, c.Url)}</td><td class='{Cls(dayChg)}'>{S(dayChg)}</td><td class='{Cls(after5)}'>{S(after5)}</td></tr>");
            }
            sb.Append("</table><div class='sub'>명분일에 오르고 5일 뒤에도 유지되면 시장이 그 소식을 좋게 받아들였다는 뜻입니다.</div>");
        }
        sb.Append("</div>");
        if (d.Disclosures.Count > 0)
        {
            sb.Append("<div class='card'><b>최근 주요 공시</b> <span class='sub'>(지분 보고 같은 정례 공시 제외, 최대 12건)</span><table>");
            foreach (var x in d.Disclosures.Where(x => !IsNoise(x.ReportName)).Take(12))
            {
                var kind = DartClassifier.Classify(x.ReportName);
                sb.Append($"<tr><td style='width:90px'>{x.ReceiptDate:yy.MM.dd}</td><td style='text-align:left'>" +
                          (kind == CatalystKind.None ? "" : $"<span class='badge' style='background:#2c3038;color:{Green};font-size:12px;padding:1px 6px'>{E(DartClassifier.Describe(kind))}</span> ") +
                          $"{Link(x.ReportName, x.ViewerUrl)}</td></tr>");
            }
            sb.Append("</table></div>");
        }
        else if (d.DartNote is not null)
        {
            sb.Append($"<p class='sub'>{E(d.DartNote)}</p>");
        }

        // ── 기업 정보 · 밸류에이션
        sb.Append(FundamentalSection(d, today));

        // ── 시장
        if (d.Market is { } m)
        {
            sb.Append("<h2>시장 분위기</h2><div class='card'>");
            sb.Append($"코스피 <b>{(m.KospiLast is { } kl ? kl.ToString("N2", Ko) : "-")}</b> <span class='{Cls(m.KospiChangePct)}'>{S(m.KospiChangePct)}</span>");
            if (m.TodayFlow is { } fl)
                sb.Append($" · 외국인 {Eok(fl.ForeignNet)} · 기관 {Eok(fl.InstitutionNet)} · 개인 {Eok(fl.IndividualNet)} <span class='sub'>(코스피 전체, 잠정)</span>");
            if (preOpen) sb.Append(" <span class='sub'>(장 시작 전 · 전 거래일 마감값)</span>");
            else if (d.ChangePct is { } c1 && m.KospiChangePct is { } k)
                sb.Append($"<div class='sub' style='margin-top:6px'>이 종목은 코스피보다 {S(Math.Round(c1 - k, 2), "%p")} {(c1 >= k ? "강했습니다" : "약했습니다")}.</div>");
            sb.Append("</div>");
        }

        // ── 용어
        sb.Append("<h2>용어 풀이</h2><div class='card' style='font-size:14px'><ul>");
        foreach (var (k, v) in Glossary) sb.Append($"<li><b>{E(k)}</b> — {E(v)}</li>");
        sb.Append("</ul></div>");
        sb.Append("<p class='sub'>이 리포트는 공개 데이터를 정해진 규칙으로 정리한 참고 자료입니다. 미래 수익을 보장하지 않으며, 매수·매도 판단과 결과는 본인 책임입니다.</p>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    // ───────────────────────── 기업 정보 ─────────────────────────

    private static string FundamentalSection(ReportData d, DateOnly today)
    {
        var sb = new StringBuilder();
        sb.Append("<h2>기업 정보 · 밸류에이션 <span class='sub'>DART 정기보고서 · 연결 기준 · 전망치·목표가 없음</span></h2>");
        var hist = d.HistoryDaily.Count > 0 ? d.HistoryDaily : d.Daily;
        var v = Valuation.Compute(d.Quarters, hist, d.Last);
        if (v is null)
        {
            sb.Append($"<p class='sub'>{E(d.FinNote ?? "분기 실적을 가져오지 못해 이 섹션은 비어 있습니다.")}</p>");
            return sb.ToString();
        }

        string Card(string k, string val, string sub) => $"<div class='card'><div class='k'>{k}</div><div class='v'>{val}</div><div class='sub'>{sub}</div></div>";
        sb.Append("<div class='grid'>");
        sb.Append(Card("시가총액", v.MarketCap is { } cap ? Valuation.Jo(cap) : "-", v.Shares is { } sh ? $"유통주식 {sh:N0}주 × 현재가" : "주식 수 없음"));
        sb.Append(Card("PER", v.Per is { } per ? $"{per:0.#}배" : "-", v.TtmNet is { } tn ? $"최근 4분기 순이익 {Valuation.Jo(tn)}" : "4분기 순이익 부족"));
        sb.Append(Card("PBR", v.Pbr is { } pbr ? $"{pbr:0.00}배" : "-",
            v.PbrPercentile is { } pc ? $"최근 {v.PbrYears:0.#}년 중 {pc}% 날보다 높음" : "과거 범위 계산 못 함"));
        sb.Append(Card("최근 4분기 영업이익", v.TtmOp is { } op ? Valuation.Jo(op) : "-",
            Valuation.Growth(v.TtmOp, v.TtmOpYearAgo) is { } g ? $"1년 전보다 <span class='{Cls(g)}'>{S(g)}</span>" : "1년 전 비교 불가"));
        sb.Append("</div>");

        if (v.PbrSeries.Count >= 120) sb.Append(PbrSvg(v));

        var facts = v.Facts().ToList();
        var next = d.Catalysts.Where(c => c.ParsedDate >= today && DartClassifier.Classify(c.Title) is CatalystKind.Earnings or CatalystKind.InvestorRelations)
            .OrderBy(c => c.Date).FirstOrDefault()
            ?? d.Catalysts.Where(c => c.ParsedDate >= today).OrderBy(c => c.Date).FirstOrDefault();
        if (next is not null) facts.Add($"다음 실적 이벤트: {next.ParsedDate:MM.dd (ddd)} {E(next.Title)} — 이 날 숫자가 바뀝니다.");
        if (facts.Count > 0)
        {
            sb.Append("<div class='card'><b>숫자로 확인되는 것</b><ul>");
            foreach (var f in facts) sb.Append($"<li>{f}</li>");
            sb.Append("</ul></div>");
        }

        var rows = v.Quarters.OrderByDescending(x => x.PeriodEnd).Take(8).ToList();
        if (rows.Count > 0)
        {
            sb.Append("<div class='card'><b>분기 실적</b> <span class='sub'>(4분기 = 연간 − 1~3분기)</span><table><tr><th>분기</th><th>매출</th><th>영업이익</th><th>이익률</th><th>순이익</th><th>영업이익 전년 대비</th></tr>");
            foreach (var r in rows)
            {
                var ya = v.Quarters.FirstOrDefault(x => x.Year == r.Year - 1 && x.Quarter == r.Quarter);
                var yoy = Valuation.Growth(r.OpIncome, ya?.OpIncome);
                sb.Append($"<tr><td>{r.Label}</td><td>{J(r.Revenue)}</td><td class='{(r.OpIncome < 0 ? "down" : "")}'>{J(r.OpIncome)}</td><td>{(r.OpMargin is { } om ? om.ToString("0.#") + "%" : "-")}</td>" +
                          $"<td>{J(r.NetIncome)}</td><td class='{Cls(yoy)}'>{S(yoy)}</td></tr>");
            }
            sb.Append("</table></div>");
        }

        sb.Append("<div class='help'><b>어떻게 보나</b><ul>" +
                  "<li><b>PER 은 반도체에선 거꾸로 읽힐 때가 많습니다.</b> 메모리는 이익이 크게 오르내려서, 이익이 정점일 때 PER 이 낮아 '싸 보이고' 바닥일 때 PER 이 높아 '비싸 보입니다'. PER 하나로 싸다·비싸다를 정하지 마세요.</li>" +
                  "<li><b>PBR 의 과거 범위 위치</b>가 이 업종에서 더 자주 쓰입니다. 범위 상단이면 이미 좋은 기대가 많이 반영된 구간, 하단이면 비관이 많이 반영된 구간이라는 뜻입니다. 단 범위는 과거 기준이라 새 고점·저점이 날 수 있습니다.</li>" +
                  "<li><b>이익의 방향</b>(4분기 합계가 1년 전보다 늘었나, 최근 분기가 전년보다 좋은가)을 같이 봅니다. 비싼 구간이라도 이익이 빠르게 늘면 PBR 이 따라 내려옵니다.</li>" +
                  "<li>이 섹션은 '지금이 비싼 구간인지'를 알려 주는 참고 자료입니다. 앱의 매수 판단(명분·평소 판단)에는 쓰지 않으며, 언제 살지는 그쪽 규칙을 따릅니다.</li></ul></div>");
        sb.Append("<div class='sub' style='margin-top:6px'>연결재무제표 기준. 순이익·자본은 비지배지분 포함(근사). 주식 수는 보통주+우선주 유통주식(자사주 제외)이라 우선주가 있는 회사는 시가총액이 조금 크게 잡힙니다. " +
                  "과거 PBR 은 그날 이전에 끝난 분기말 자본으로 계산했습니다(실제 공시는 분기말 뒤 45일 안팎). 증권사 전망치·목표가는 넣지 않았습니다.</div>");
        if (d.FinNote is not null) sb.Append($"<p class='sub'>{E(d.FinNote)}</p>");
        return sb.ToString();

        static string J(decimal? won) => won is { } w ? Valuation.Jo(w) : "-";
    }

    private static string PbrSvg(Valuation v)
    {
        const int W = 900, H = 200, L = 46, R = 70, T = 12, B = 26;
        var pts = v.PbrSeries;
        var lo = pts.Min(p => p.pbr); var hi = pts.Max(p => p.pbr);
        if (v.Pbr is { } now) { lo = Math.Min(lo, now); hi = Math.Max(hi, now); }
        var pad = (hi - lo) * 0.08m + 0.01m; lo -= pad; hi += pad;
        double X(int i) => L + (W - L - R) * (double)i / Math.Max(1, pts.Count - 1);
        double Y(decimal p) => T + (H - T - B) * (double)((hi - p) / (hi - lo));
        var sb = new StringBuilder();
        sb.Append($"<div class='card'><b>PBR 범위</b> <span class='sub'>최근 {v.PbrYears:0.#}년 · 일별</span><svg viewBox='0 0 {W} {H}' width='100%' style='display:block;margin-top:6px'>");
        foreach (var (val, name, col) in new[] { (v.PbrMax, "최고", Up), (v.PbrMedian, "중간", Muted), (v.PbrMin, "최저", Down) })
        {
            if (val is not { } y) continue;
            sb.Append($"<line x1='{L}' x2='{W - R}' y1='{F(Y(y))}' y2='{F(Y(y))}' stroke='{col}' stroke-dasharray='4 4' stroke-opacity='.6'/>");
            sb.Append($"<text x='{W - R + 6}' y='{F(Y(y) + 4)}' fill='{col}' font-size='12'>{name} {y:0.00}</text>");
        }
        sb.Append("<polyline fill='none' stroke='#e8eaee' stroke-width='1.4' points='");
        for (int i = 0; i < pts.Count; i++) sb.Append($"{F(X(i))},{F(Y(pts[i].pbr))} ");
        sb.Append("'/>");
        if (v.Pbr is { } cur)
        {
            sb.Append($"<circle cx='{F(X(pts.Count - 1))}' cy='{F(Y(cur))}' r='4.5' fill='{Ma20C}'/>");
            sb.Append($"<text x='{F(X(pts.Count - 1) - 6)}' y='{F(Y(cur) - 9)}' fill='{Ma20C}' font-size='12' text-anchor='end'>지금 {cur:0.00}</text>");
        }
        int lastYear = -1;
        for (int i = 0; i < pts.Count; i++)
        {
            var yr = pts[i].date.Year;
            if (yr == lastYear) continue;
            lastYear = yr;
            if (i == 0 && pts[0].date.Month > 2) continue;
            sb.Append($"<text x='{F(X(i))}' y='{H - 6}' fill='{Muted}' font-size='12' text-anchor='middle'>{yr}</text>");
        }
        sb.Append("</svg></div>");
        return sb.ToString();
    }

    // ───────────────────────── 문장 ─────────────────────────

    private static IEnumerable<string> Summary(ReportData d, TrendSummary t, DateOnly today)
    {
        yield return $"추세: <b>{E(t.Label)}</b> — {E(t.Explain)}";

        var inv = d.Investors.OrderByDescending(r => r.Date).ToList();
        int buy = 0, sell = 0;
        foreach (var r in inv) { if (r.ForeignerNet > 0) buy++; else break; }
        foreach (var r in inv) { if (r.ForeignerNet < 0) sell++; else break; }
        if (buy >= 2) yield return $"수급: 외국인 <b class='up'>{buy}일 연속 순매수</b> 중입니다.";
        else if (sell >= 2) yield return $"수급: 외국인 <b class='down'>{sell}일 연속 순매도</b> 중입니다.";
        else if (inv.Count > 0)
        {
            var when = inv[0].Date == today ? "오늘" : $"{inv[0].Date:M/d}(마지막 거래일)";
            yield return $"수급: 외국인 {when} {(inv[0].ForeignerNet > 0 ? "순매수" : "순매도")} — 뚜렷한 연속 흐름은 없습니다.";
        }

        var next = d.Catalysts.Where(c => c.ParsedDate >= today).OrderBy(c => c.Date).FirstOrDefault();
        yield return next is not null
            ? $"다음 명분: <b>{next.ParsedDate:MM.dd (ddd)}</b> (D-{next.ParsedDate!.Value.DayNumber - today.DayNumber}) {E(next.Title)}"
            : "다음 명분: 등록된 일정 없음";

        if (Valuation.Compute(d.Quarters, d.HistoryDaily.Count > 0 ? d.HistoryDaily : d.Daily, d.Last) is { Pbr: { } pb } val)
            yield return $"밸류에이션({val.Latest?.Label} 보고서까지): PBR {pb:0.00}배" + (val.PbrPercentile is { } pc ? $" — 최근 {val.PbrYears:0.#}년 중 {pc}% 날보다 높은 구간" : "") +
                         (Valuation.Growth(val.TtmOp, val.TtmOpYearAgo) is { } g ? $", 최근 4분기 영업이익은 1년 전보다 {S(g)}" : "") + " (아래 '기업 정보')";
        // 오늘 판단: 명분일엔 명분 판단, 아닌 날엔 평소 판단 (상황판 배지와 같은 기준)
        if (d.Eval is { } e)
        {
            if (e.General is { Verdict: not GeneralVerdict.Wait } g2)
                yield return $"오늘 판단(평소): <b>{E(GeneralView.Describe(g2.Verdict))}</b> — {E(g2.Opinion)} <span class='sub'>(명분 판단: {E(SignalEngine.Describe(e.Verdict))})</span>";
            else
                yield return $"오늘 판단(명분): <b>{E(SignalEngine.Describe(e.Verdict))}</b> — {E(e.Headline)}";
        }
        if (d.Eval?.Plan is { Complete: false } p)
        {
            var part = p.ReadyPart ?? (p.Opp.Enabled ? p.Opp : p.Core);
            yield return $"예산 분할: {E(p.Summary)} — {E(part.Name)} {(part.Ready == true ? "<b>매수 차례</b>" : "대기")}" +
                         (part.Ready == true && part.Shares > 0 ? $" ({part.Shares}주 ≈ {N(part.Amount)}원)" : "");
        }
    }

    private static string CheckHelp(string label) => label switch
    {
        "명분" => "실적 공시·실적 IR 같은 이벤트가 오늘 있나. 명분일만 이 체크리스트(기회 몫)로 보고, 명분 없는 날은 평소 판단(기본 몫)을 따릅니다.",
        "기준가 아래" => "현재가가 기준가(전일 종가)보다 낮은가. 백테스트에서 명분일에 하락 마감한 날 산 쪽이 20일 성과가 좋았습니다 (오른 날은 나빴음).",
        "쇼크 아님" => "-5% 이하로 급락했는데 코스피는 보합·상승이면 종목만의 악재(실적 쇼크)일 수 있어 공시를 먼저 확인합니다.",
        "강도" => "외국인·기관이 같이 팔았거나 거래량이 20일 평균의 1.5배 이상이면 '강한 후보'. 과거엔 이런 눌림이 더 좋았습니다. 필수 조건은 아닙니다.",
        "외국인 연속" => "참고용. 연속 순매수 조건은 명분일 성과를 높이지 못해 뺐습니다. (평소 판단에선 연속 순매도 3일 이상이면 '기다려 봄')",
        _ => "",
    };

    private static readonly (string, string)[] Glossary =
    {
        ("기준가", "전 거래일 종가. 등락률의 기준."),
        ("이동평균선(20일선·60일선)", "최근 N일 종가의 평균을 이은 선. 주가의 큰 흐름을 보여 줍니다."),
        ("정배열 / 역배열", "짧은 평균선이 긴 평균선 위에 있으면 정배열(상승 흐름), 아래면 역배열(하락 흐름)."),
        ("순매수 / 순매도", "산 양 − 판 양. +면 더 많이 샀고 −면 더 많이 팔았다는 뜻."),
        ("잠정치 / 확정치", "장중 수급은 추정치(잠정)이고 저녁에 확정됩니다. 장중 숫자는 바뀔 수 있어요."),
        ("프로그램 매매", "기관 등이 정해진 규칙으로 컴퓨터가 자동 주문하는 매매. 큰 물량이 한 번에 들어오거나 나갑니다."),
        ("종가 동시호가", "15:20~15:30. 주문을 모아 15:30에 한 가격으로 체결합니다. 판단이 '하락 마감' 기준이라 이 무렵 사는 게 판단과 가장 잘 맞습니다."),
        ("평소 판단 · 기본 몫", "명분 없는 날의 판단. 하락·보합 마감이면 '사도 괜찮음', 급등·과열·외국인 연속 매도면 '기다려 봄'. 예산의 기본 몫을 최소 간격(기본 1주)마다 나눠 삽니다."),
        ("명분 판단 · 기회 몫", "실적 공시·실적 IR 날의 판단. 기준가 아래로 마감하는 눌림이면 기회 몫을 삽니다."),
        ("매수 창", "기본 몫 최소 간격이 지난 뒤 열리는 며칠(기본 5거래일). 그 안에 괜찮은 날이 없으면 창 마지막 날 1회분을 삽니다 (계속 미루다 비싸게 사는 것 방지)."),
        ("명분일 눌림", "실적 공시·실적 IR 같은 명분이 있는 날 기준가 아래로 빠진 상태. 백테스트에서 이런 날 산 쪽이 20일 성과가 좋았습니다."),
        ("추격 금지", "명분일에 이미 오른 상태. 과거에는 이런 날 산 쪽이 20일 성과가 나빴습니다."),
    };

    private static (decimal? day, decimal? after5) Reaction(IReadOnlyList<Bar> daily, DateOnly date)
    {
        var idx = -1;
        for (int i = 0; i < daily.Count; i++)
            if (DateOnly.FromDateTime(daily[i].Time.DateTime) >= date) { idx = i; break; }
        if (idx <= 0) return (null, null);
        var day = ChartMath.Pct(daily[idx].Close, daily[idx - 1].Close);
        var after = idx + 5 < daily.Count ? ChartMath.Pct(daily[idx + 5].Close, daily[idx].Close) : null;
        return (day, after);
    }

    private static readonly string[] Noise =
    {
        "소유상황보고서", "대량보유상황보고서", "대규모기업집단", "특수관계인", "공정거래", "증권발행실적", "투자설명서",
        "일괄신고", "채무증권", "임원ㆍ주요주주", "최대주주등소유주식", "기업지배구조",
    };

    public static bool IsNoise(string reportName) => Noise.Any(n => reportName.Replace(" ", "").Contains(n));

    private static string Link(string text, string? url) =>
        url is null ? E(text) : $"<a href='{E(url)}' target='_blank' rel='noopener'>{E(text)}</a>";

    private static string Eok(decimal won) => S(Math.Round(won / 100_000_000m), "억");

    private static string VerdictColor(Verdict v) => v switch
    {
        Verdict.GapTrap or Verdict.ShockWarn => Up,
        Verdict.NoChase => Ma20C,
        Verdict.DipCandidate => Down,
        Verdict.DipBuy => Green,
        Verdict.Avoid => "#a878ff",
        _ => "#787e8a",
    };

    // ───────────────────────── SVG 차트 ─────────────────────────

    /// <summary>일봉 캔들 + 20/60일선 + 거래량 + 기준가·평단 점선 + 매수▲·명분◆ (각 요소에 마우스를 올리면 설명)</summary>
    private static string DailySvg(ReportData d)
    {
        var all = d.Daily.ToList();
        if (all.Count < 2) return "<p class='sub'>일봉 데이터가 없습니다.</p>";
        var closes = all.Select(b => b.Close).ToList();
        var ma20 = ChartMath.Ma(closes, 20);
        var ma60 = ChartMath.Ma(closes, 60);
        int take = Math.Min(130, all.Count), start = all.Count - take;
        var bars = all.Skip(start).ToList();

        const double W = 920, PH = 300, VH = 70, L = 8, R = 100, T = 14, Gap = 18;
        double plotW = W - L - R;
        double step = plotW / (bars.Count + 3), body = Math.Max(1.5, step * 0.62);   // 오른쪽에 빈칸 3개 (마지막 봉이 가격표에 붙지 않게)

        var hi = bars.Max(b => b.High);
        var lo = bars.Min(b => b.Low);
        foreach (var v in new[] { d.PrevClose, d.Holding?.AveragePrice ?? 0 })
            if (v > 0) { hi = Math.Max(hi, v); lo = Math.Min(lo, v); }
        for (int i = start; i < all.Count; i++)
        {
            if (ma20[i] is { } a) { hi = Math.Max(hi, a); lo = Math.Min(lo, a); }
            if (ma60[i] is { } b) { hi = Math.Max(hi, b); lo = Math.Min(lo, b); }
        }
        var pad = (hi - lo) * 0.06m;
        hi += pad; lo -= pad;
        double Y(decimal p) => T + (double)((hi - p) / (hi - lo)) * PH;
        double X(int i) => L + step * i + step / 2;
        var maxVol = Math.Max(1m, bars.Max(b => b.Volume));
        double vTop = T + PH + Gap;

        var sb = new StringBuilder();
        sb.Append($"<svg viewBox='0 0 {W} {T + PH + Gap + VH + 24}' width='100%' style='background:#1d2026;border-radius:10px'>");

        // 가격 눈금 (글자는 맨 끝에 점선 가격표와 겹치지 않게 그린다)
        var guides = new List<(decimal price, string color, string label, string help)>();
        if (d.PrevClose > 0) guides.Add((d.PrevClose, "#9aa0ac", "기준 " + N(d.PrevClose), ChartHelp.BaseLine));
        if (d.Holding is { Quantity: > 0 } h2) guides.Add((h2.AveragePrice, Green, "평단 " + N(h2.AveragePrice), ChartHelp.AvgPrice));
        var stepP = ChartMath.NiceStep(hi - lo);
        var ticks = new List<decimal>();
        for (var p = Math.Ceiling(lo / stepP) * stepP; p <= hi; p += stepP)
        {
            sb.Append($"<line x1='{L}' x2='{W - R}' y1='{F(Y(p))}' y2='{F(Y(p))}' stroke='#2a2e36'/>");
            ticks.Add(p);
        }
        // 월 구분
        int lastMonth = -1;
        for (int i = 0; i < bars.Count; i++)
        {
            var dt = bars[i].Time;
            if (dt.Month != lastMonth && i > 0)
            {
                sb.Append($"<line x1='{F(X(i) - step / 2)}' x2='{F(X(i) - step / 2)}' y1='{T}' y2='{F(vTop + VH)}' stroke='#2a2e36'/>");
                sb.Append($"<text x='{F(X(i) - step / 2 + 3)}' y='{F(vTop + VH + 16)}' fill='{Muted}' font-size='11'>{dt:yy.M}</text>");
            }
            lastMonth = dt.Month;
        }

        // 거래량
        sb.Append($"<g><title>{E(ChartHelp.Volume)}</title>");
        for (int i = 0; i < bars.Count; i++)
        {
            var h = (double)(bars[i].Volume / maxVol) * VH;
            var up = bars[i].Close >= bars[i].Open;
            sb.Append($"<rect x='{F(X(i) - body / 2)}' y='{F(vTop + VH - h)}' width='{F(body)}' height='{F(Math.Max(0.5, h))}' fill='{(up ? Up : Down)}' opacity='.45'/>");
        }
        sb.Append($"<text x='{L + 2}' y='{F(vTop + 10)}' fill='{Muted}' font-size='11'>거래량</text></g>");

        // 캔들
        for (int i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            var up = b.Close >= b.Open;
            var col = up ? Up : Down;
            var yo = Y(b.Open); var yc = Y(b.Close);
            var prev = start + i > 0 ? all[start + i - 1].Close : b.Open;
            sb.Append($"<g><title>{b.Time:yyyy-MM-dd (ddd)}\n시가 {N(b.Open)} · 고가 {N(b.High)}\n저가 {N(b.Low)} · 종가 {N(b.Close)} ({S(ChartMath.Pct(b.Close, prev))})\n거래량 {N(b.Volume)}</title>");
            sb.Append($"<line x1='{F(X(i))}' x2='{F(X(i))}' y1='{F(Y(b.High))}' y2='{F(Y(b.Low))}' stroke='{col}'/>");
            sb.Append($"<rect x='{F(X(i) - body / 2)}' y='{F(Math.Min(yo, yc))}' width='{F(body)}' height='{F(Math.Max(1, Math.Abs(yo - yc)))}' fill='{col}'/></g>");
        }

        // 이동평균
        string Path(decimal?[] ma)
        {
            var pts = new StringBuilder();
            bool started = false;
            for (int i = 0; i < bars.Count; i++)
            {
                if (ma[start + i] is not { } v) { started = false; continue; }
                pts.Append(started ? " L" : " M").Append(F(X(i))).Append(',').Append(F(Y(v)));
                started = true;
            }
            return pts.ToString();
        }
        sb.Append($"<path d='{Path(ma60)}' fill='none' stroke='{Ma60C}' stroke-width='1.8'><title>{E(ChartHelp.Ma60)}</title></path>");
        sb.Append($"<path d='{Path(ma20)}' fill='none' stroke='{Ma20C}' stroke-width='1.8'><title>{E(ChartHelp.Ma20)}</title></path>");

        // 점선 가이드 + 오른쪽 가격 글자
        AxisAndGuides(sb, guides, ticks, Y, L, W, R, T, T + PH);

        // 매수 ▲ · 명분 ◆
        var dateIdx = new Dictionary<DateOnly, int>();
        for (int i = 0; i < bars.Count; i++) dateIdx[DateOnly.FromDateTime(bars[i].Time.DateTime)] = i;
        foreach (var bd in d.BuyDays)
            if (dateIdx.TryGetValue(bd, out var i))
                sb.Append($"<text x='{F(X(i))}' y='{F(Y(bars[i].Low) + 16)}' fill='{Green}' font-size='13' text-anchor='middle'>▲<title>{bd:MM.dd} 매수 — {E(ChartHelp.BuyMark)}</title></text>");
        foreach (var c in d.Catalysts)
            if (c.ParsedDate is { } cd && dateIdx.TryGetValue(cd, out var i))
                sb.Append($"<text x='{F(X(i))}' y='{F(Y(bars[i].High) - 6)}' fill='{Down}' font-size='12' text-anchor='middle'>◆<title>{cd:MM.dd} {E(c.Title)}\n{E(ChartHelp.CatalystMark)}</title></text>");

        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>
    /// 점선 가이드(기준가·평단·고가)와 오른쪽 가격 글자. 가이드 가격표끼리 가까우면 위아래로 벌리고,
    /// 가격표와 겹치는 눈금 글자는 뺀다 (기준 1,294,000 과 고가 1,297,000 이 한 줄에 겹치던 문제).
    /// </summary>
    private static void AxisAndGuides(StringBuilder sb, List<(decimal price, string color, string label, string help)> guides,
        List<decimal> ticks, Func<decimal, double> Y, double L, double W, double R, double top, double bottom)
    {
        const double H = 17, Gap = 2;
        var placed = guides.Where(g => g.price > 0)
            .Select(g => (g, line: Y(g.price), box: Y(g.price)))
            .Where(x => x.line >= top - 1 && x.line <= bottom + 1)
            .OrderBy(x => x.line).ToList();
        // 아래로 밀어 겹침 풀기 → 바닥을 넘으면 위로 되밀기
        for (int i = 1; i < placed.Count; i++)
            if (placed[i].box - placed[i - 1].box < H + Gap)
                placed[i] = placed[i] with { box = placed[i - 1].box + H + Gap };
        for (int i = placed.Count - 1; i >= 0; i--)
        {
            var maxBox = i == placed.Count - 1 ? bottom - H / 2 : placed[i + 1].box - H - Gap;
            if (placed[i].box > maxBox) placed[i] = placed[i] with { box = maxBox };
        }

        foreach (var p in ticks)
        {
            var y = Y(p);
            if (placed.Any(g => Math.Abs(g.box - y) < H)) continue;
            sb.Append($"<text x='{F(W - R + 6)}' y='{F(y + 4)}' fill='{Muted}' font-size='11'>{N(p)}</text>");
        }
        foreach (var (g, line, box) in placed)
        {
            sb.Append($"<g><title>{E(g.help)}</title><line x1='{F(L)}' x2='{F(W - R)}' y1='{F(line)}' y2='{F(line)}' stroke='{g.color}' stroke-dasharray='5 4' stroke-width='1.2'/>");
            if (Math.Abs(box - line) > 1)   // 가격표가 밀려났으면 선 끝과 가격표를 잇는 짧은 선
                sb.Append($"<line x1='{F(W - R)}' x2='{F(W - R + 2)}' y1='{F(line)}' y2='{F(box)}' stroke='{g.color}'/>");
            sb.Append($"<rect x='{F(W - R + 2)}' y='{F(box - 9)}' width='{F(R - 4)}' height='{H}' rx='3' fill='{g.color}'/>");
            sb.Append($"<text x='{F(W - R + 6)}' y='{F(box + 4)}' fill='#111' font-size='11' font-weight='700'>{E(g.label)}</text></g>");
        }
    }

    /// <summary>오늘 1분봉 선 차트 + 기준가·당일 고가 점선 + 13시 이후 음영</summary>
    private static string MinuteSvg(ReportData d)
    {
        var bars = d.Minute.ToList();
        const double W = 920, H = 230, L = 8, R = 100, T = 12, B = 22;
        var hi = Math.Max(bars.Max(b => b.High), d.PrevClose);
        var lo = Math.Min(bars.Min(b => b.Low), d.PrevClose > 0 ? d.PrevClose : bars.Min(b => b.Low));
        var pad = Math.Max(1m, (hi - lo) * 0.08m);
        hi += pad; lo -= pad;
        var day = DateOnly.FromDateTime(bars[0].Time.DateTime);
        var open = Kst.At(day, 9, 0);
        const double minutes = 390; // 09:00~15:30
        double X(DateTimeOffset t) => L + (t - open).TotalMinutes / minutes * (W - L - R);
        double Y(decimal p) => T + (double)((hi - p) / (hi - lo)) * (H - T - B);

        var sb = new StringBuilder();
        sb.Append($"<svg viewBox='0 0 {W} {H}' width='100%' style='background:#1d2026;border-radius:10px'>");
        var x13 = X(Kst.At(day, 13, 0));
        sb.Append($"<rect x='{F(x13)}' y='{T}' width='{F(W - R - x13)}' height='{F(H - T - B)}' fill='#ffffff' opacity='.05'><title>{E(ChartHelp.Afternoon)}</title></rect>");
        for (int h = 9; h <= 15; h++)
        {
            var x = X(Kst.At(day, h, 0));
            sb.Append($"<line x1='{F(x)}' x2='{F(x)}' y1='{T}' y2='{F(H - B)}' stroke='#2a2e36'/><text x='{F(x + 3)}' y='{F(H - 6)}' fill='{Muted}' font-size='11'>{h}시</text>");
        }
        var stepP = ChartMath.NiceStep(hi - lo, 4);
        var ticks = new List<decimal>();
        for (var p = Math.Ceiling(lo / stepP) * stepP; p <= hi; p += stepP) ticks.Add(p);

        var last = bars[^1].Close;
        var col = d.PrevClose > 0 && last < d.PrevClose ? Down : Up;
        var path = string.Join(" ", bars.Select((b, i) => (i == 0 ? "M" : "L") + F(X(b.Time)) + "," + F(Y(b.Close))));
        sb.Append($"<path d='{path}' fill='none' stroke='{col}' stroke-width='1.8'/>");

        // 지금 위치: 선 끝에 점 + 시각·가격 (오른쪽 빈 곳은 아직 안 온 시간)
        var lb = bars[^1];
        double lx = X(lb.Time), ly = Y(lb.Close);
        sb.Append($"<circle cx='{F(lx)}' cy='{F(ly)}' r='4' fill='{col}'><title>마지막 체결 {lb.Time:HH:mm} · {N(lb.Close)}</title></circle>");
        var close = Kst.At(day, 15, 30);
        if (lb.Time < close.AddMinutes(-10))
        {
            var tx = lx + 8;
            var anchor = "start";
            if (tx > W - R - 90) { tx = lx - 8; anchor = "end"; }
            sb.Append($"<text x='{F(tx)}' y='{F(ly - 8)}' fill='{col}' font-size='11' font-weight='700' text-anchor='{anchor}'>{lb.Time:HH:mm} {N(lb.Close)}</text>");
            var rest = X(close) - lx;
            if (rest > 90)
            {
                // 선 끝이 아래쪽이면 위에, 위쪽이면 아래에 적는다 (선·점과 겹치지 않게)
                var midY = (T + H - B) / 2;
                var ty = ly > midY ? (T + ly) / 2 : (ly + H - B) / 2 + 4;
                sb.Append($"<line x1='{F(lx)}' x2='{F(lx)}' y1='{T}' y2='{F(H - B)}' stroke='{Muted}' stroke-dasharray='2 4' opacity='.6'/>");
                sb.Append($"<text x='{F(lx + 14 + (rest - 14) / 2)}' y='{F(ty)}' fill='{Muted}' font-size='11' text-anchor='middle' opacity='.8'>남은 장 시간</text>");
            }
        }

        var guides = new List<(decimal price, string color, string label, string help)>();
        if (d.PrevClose > 0) guides.Add((d.PrevClose, "#9aa0ac", "기준 " + N(d.PrevClose), ChartHelp.BaseLine));
        var dayHigh = bars.Max(b => b.High);
        guides.Add((dayHigh, Ma20C, "고가 " + N(dayHigh), ChartHelp.DayHigh));
        AxisAndGuides(sb, guides, ticks, Y, L, W, R, T, H - B);
        sb.Append("</svg>");
        return sb.ToString();
    }
}
