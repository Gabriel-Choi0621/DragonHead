// 판정 엔진 시나리오 점검. 외부 패키지 없이 돌아가도록 간단한 assert 만 쓴다.
// 실행: dotnet run --project tests/DragonHead.Checks
using DragonHead.Core;

var cfg = new StrategySettings();
var day = new DateOnly(2026, 10, 22); // 목요일
var open = new MarketDay(day, true, Kst.At(day, 9, 0), Kst.At(day, 15, 20), Kst.At(day, 15, 30));
int failures = 0;

InvestorDay Inv(int daysAgo, long foreign, long inst = 0) =>
    new(day.AddDays(-daysAgo), foreign, inst, null, null, Kst.At(day, 15, 0));

Snapshot Snap(int h, int m, decimal last, decimal prev = 100_000m, decimal? dayOpen = 100_000m,
              decimal? high = null, decimal? low13 = null, string? catalyst = null,
              IReadOnlyList<InvestorDay>? inv = null, decimal? cash = null, MarketDay? market = null,
              decimal? kospiPct = null, decimal volumeX = 0) => new()
{
    Symbol = "000660",
    Name = "SK하이닉스",
    Now = Kst.At(day, h, m),
    Last = last,
    PrevClose = prev,
    DayOpen = dayOpen,
    DayHigh = high ?? last,
    DayLow = Math.Min(prev, last),
    LowSince13 = low13,
    Investors = inv ?? new[] { Inv(0, 50_000), Inv(1, 30_000), Inv(2, 20_000), Inv(3, 10_000) },
    CatalystToday = catalyst,
    Market = market ?? open,
    CashBuyingPower = cash,
    MarketContext = kospiPct is { } kp ? new MarketContext { KospiPrevClose = 1000m, KospiLast = 1000m * (1 + kp / 100m) } : null,
    // volumeX > 0 이면: 지난 20일 거래량 1000, 오늘 분봉 합 = 1000 × volumeX
    DailyBars = volumeX > 0 ? Enumerable.Range(1, 20).Select(i => new Bar(Kst.At(day.AddDays(-i), 9, 0), prev, prev, prev, prev, 1000)).Reverse().ToList() : Array.Empty<Bar>(),
    MinuteBars = volumeX > 0 ? new[] { new Bar(Kst.At(day, 9, 0), prev, prev, prev, prev, 1000 * volumeX) } : Array.Empty<Bar>(),
};

void Expect(string name, Snapshot s, Verdict expected, Action<Evaluation>? more = null)
{
    var e = SignalEngine.Evaluate(s, cfg);
    var ok = e.Verdict == expected;
    try { more?.Invoke(e); }
    catch (Exception ex) { ok = false; Console.WriteLine($"   추가 검증 실패: {ex.Message}"); }
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
    Console.WriteLine($"      → [{SignalEngine.Describe(e.Phase)}] {SignalEngine.Describe(e.Verdict)}: {e.Headline}");
    if (!ok) { failures++; Console.WriteLine($"      기대값: {expected}"); }
}

void Assert(bool cond, string msg) { if (!cond) throw new Exception(msg); }

// 1. 아침 갭상승: 9시 20분, 시초가 +3%
Expect("09:20 시초가 갭상승 +3% → 매수 금지",
    Snap(9, 20, 103_500m, dayOpen: 103_000m, catalyst: "3Q 실적"),
    Verdict.GapTrap,
    e => Assert(e.GapPct == 3.00m, $"갭 {e.GapPct}"));

// 2. 아침인데 갭이 작으면 그냥 관망
Expect("09:40 갭 +0.5% → 관망",
    Snap(9, 40, 100_800m, dayOpen: 100_500m),
    Verdict.Wait);

// 3. 명분 없는 날 오후 급등 → 신규 매수 대상 아님
Expect("13:30 +4% 급등, 명분 없음 → 관망",
    Snap(13, 30, 104_000m, inv: new[] { Inv(0, -80_000, 120_000), Inv(1, 30_000), Inv(2, 20_000) }),
    Verdict.Wait,
    e => Assert(e.Headline.Contains("명분 없는 날"), e.Headline));

// 4. 명분일 상승 → 추격 금지 (수급이 좋아도)
Expect("14:00 명분일 +3%, 외인·기관 매수 → 추격 금지",
    Snap(14, 0, 103_000m, catalyst: "3Q 실적", inv: new[] { Inv(0, 40_000, 30_000), Inv(1, 10_000) }),
    Verdict.NoChase);

// 5. 명분일 장중 기준가 아래 → 눌림 후보 (판정 시간 아님)
Expect("14:10 명분일 -1.5% → 눌림 후보",
    Snap(14, 10, 98_500m, catalyst: "SK하이닉스 3Q 실적", kospiPct: -0.8m),
    Verdict.DipCandidate);

// 6. 14:55 명분일 눌림 → 눌림 매수 + 기회 몫 1회분 (예산 400만 × 기회 50% ÷ 4회 = 50만 → 9.8만원에 5주)
Expect("14:55 명분일 -2% → 눌림 매수 + 기회 몫 1차 5주",
    Snap(14, 55, 98_000m, catalyst: "SK하이닉스 3Q 실적", kospiPct: -1m),
    Verdict.DipBuy,
    e =>
    {
        Assert(e.Plan?.ReadyPart == e.Plan?.Opp && e.Plan!.Opp.Ready == true, "기회 몫 차례가 아님");
        Assert(e.Plan.Opp.Shares == 5 && e.Plan.Opp.SharesPerTranche == 5 && e.Plan.Opp.PerTranche == 490_000m, $"수량 {e.Plan.Opp.Shares} / 1회분 {e.Plan.Opp.PerTranche}");
        Assert(!e.Headline.Contains("강한 후보"), "강도 조건 없는데 강한 후보");
    });

// 7. 외국인·기관 동반 매도 + 거래량 2배 → 강한 후보 (회피 판정 아님)
Expect("14:55 명분일 -3%, 외인·기관 동반 매도, 거래량 2배 → 눌림 매수(강한 후보)",
    Snap(14, 55, 97_000m, catalyst: "3Q 실적", kospiPct: -1.2m, volumeX: 2,
         inv: new[] { Inv(0, -90_000, -40_000), Inv(1, -10_000) }),
    Verdict.DipBuy,
    e => Assert(e.Headline.Contains("강한 후보") && e.Headline.Contains("동반 매도") && e.Headline.Contains("거래량 2배"), e.Headline));

// 8. 혼자 -6% 급락 (코스피 +0.5%) → 쇼크 의심
Expect("14:55 명분일 -6%, 코스피 +0.5% → 쇼크 의심",
    Snap(14, 55, 94_000m, catalyst: "3Q 실적", kospiPct: 0.5m),
    Verdict.ShockWarn,
    e => Assert(e.Plan?.Opp.Ready != true && e.Plan?.Core.Ready != true, "쇼크 의심인데 매수 차례"));

// 9. 코스피도 같이 -6% 급락이면 쇼크 아님 → 눌림 매수
Expect("15:21 명분일 -6%, 코스피도 -3% → 눌림 매수",
    Snap(15, 21, 94_000m, catalyst: "3Q 실적", kospiPct: -3m),
    Verdict.DipBuy,
    e => Assert(e.Phase == Phase.ClosingAuction, $"단계 {e.Phase}"));

// 10. 예수금이 1회분보다 적을 때 예수금 한도로 계산
Expect("14:55 예수금 30만원 → 예수금 한도로 3주",
    Snap(14, 55, 98_000m, catalyst: "3Q 실적", cash: 300_000m),
    Verdict.DipBuy,
    e => Assert(e.Plan!.Opp.Shares == 3 && e.Plan.Opp.CashNote!.Contains("3주만"), $"{e.Plan.Opp.Shares} {e.Plan.Opp.CashNote}"));

// 10-1. 예수금 0원이어도 판정·차례는 그대로, 수량만 0 + 입금 안내
Expect("14:55 예수금 0원 → 눌림 매수 판정은 그대로",
    Snap(14, 55, 98_000m, catalyst: "3Q 실적", cash: 0m),
    Verdict.DipBuy,
    e => Assert(e.Plan!.Opp is { Ready: true, Shares: 0 } && e.Plan.Opp.CashNote!.Contains("예수금 부족"), $"{e.Plan.Opp.Shares} {e.Plan.Opp.CashNote}"));

// 11. 휴장일
Expect("휴장일 → 휴장",
    Snap(14, 55, 104_000m, market: new MarketDay(day, false, null, null, null)),
    Verdict.Wait,
    e => Assert(e.Phase == Phase.Holiday, $"단계 {e.Phase}"));

// 12. 오늘 잠정치가 아직 없으면 어제까지 확정으로 연속 일수 계산
Expect("10:30 오늘 수급 기록 없음 → 어제까지 3일 연속으로 계산, 관망",
    Snap(10, 30, 100_500m, inv: new[] { Inv(1, 30_000), Inv(2, 20_000), Inv(3, 10_000), Inv(4, -5_000) }),
    Verdict.Wait,
    e =>
    {
        Assert(e.ForeignTodayShares is null, "오늘 잠정치가 있으면 안 됨");
        Assert(e.ForeignStreak == 3, $"연속 {e.ForeignStreak}");
    });

// 13. 시간 경계
Assert(SignalEngine.GetPhase(Kst.At(day, 8, 59), open) == Phase.PreMarket, "08:59");
Assert(SignalEngine.GetPhase(Kst.At(day, 9, 59), open) == Phase.MorningTrap, "09:59");
Assert(SignalEngine.GetPhase(Kst.At(day, 14, 49), open) == Phase.Afternoon, "14:49");
Assert(SignalEngine.GetPhase(Kst.At(day, 14, 50), open) == Phase.Decision, "14:50");
Assert(SignalEngine.GetPhase(Kst.At(day, 15, 20), open) == Phase.ClosingAuction, "15:20");
Assert(SignalEngine.GetPhase(Kst.At(day, 15, 30), open) == Phase.Closed, "15:30");
Console.WriteLine("PASS  시간대 경계 (08:59 / 09:59 / 14:49 / 14:50 / 15:20 / 15:30)");

// 14. 표시 포맷
Assert(SignalEngine.Eok(1_234_000_000m) == "+12억", SignalEngine.Eok(1_234_000_000m));
Assert(SignalEngine.Eok(-350_000_000m) == "-3.5억", SignalEngine.Eok(-350_000_000m));
Console.WriteLine("PASS  억 단위 표시");

// 16. KRX 기준가 역산: 오늘 실제 상·하한가로 확인 (삼성 211,500 / 하이닉스 1,223,000)
{
    var cases = new (decimal basePrice, string name)[]
    {
        (211_500m, "삼성전자"), (1_223_000m, "SK하이닉스"), (55_800m, "5만원대"), (4_995m, "5천원 미만"), (199_900m, "호가 경계"),
    };
    foreach (var (bp, name) in cases)
    {
        var up = KrxPrice.UpperLimit(bp);
        var lo = KrxPrice.LowerLimit(bp);
        var back = KrxPrice.BaseFromLimits(up, lo);
        Assert(back == bp, $"{name}: 기준가 {bp} → 상한 {up} 하한 {lo} → 역산 {back}");
    }
    Assert(KrxPrice.UpperLimit(211_500m) == 274_500m && KrxPrice.LowerLimit(211_500m) == 148_100m, "삼성 상하한");
    Assert(KrxPrice.UpperLimit(1_223_000m) == 1_589_000m && KrxPrice.LowerLimit(1_223_000m) == 857_000m, "하이닉스 상하한");
    // 오늘 토스 앱 숫자와 일치하는지: 224,500 → +6.14%, 1,296,000 → +5.96% (앱은 소수 둘째 자리 절사)
    Assert(Math.Floor((224_500m / 211_500m - 1m) * 10000m) / 100m == 6.14m, "삼성 등락률");
    Assert(Math.Floor((1_296_000m / 1_223_000m - 1m) * 10000m) / 100m == 5.96m, "하이닉스 등락률");
    // 호가단위 표가 달라도 근사 역산은 0.2% 안에 들어와야 한다
    foreach (var bp in new[] { 211_500m, 211_250m, 1_223_000m, 55_800m })
    {
        var approx = KrxPrice.ApproxBaseFromLimits(KrxPrice.UpperLimit(bp), KrxPrice.LowerLimit(bp))!.Value;
        Assert(Math.Abs(approx / bp - 1m) < 0.002m, $"근사 역산 {bp} → {approx}");
    }
    Console.WriteLine("PASS  상·하한가 → KRX 기준가 역산 (삼성 211,500 / 하이닉스 1,223,000, 토스 앱 등락률 일치)");
}

// 17. 태그: 오늘(10/21) 실제 숫자로
{
    var t = new DateOnly(2026, 10, 21);
    var mkt = new MarketContext
    {
        KospiLast = 5735.66m, KospiPrevClose = 5552.84m,   // +3.29%
        TodayFlow = new MarketFlow(t, 1_200_000_000_000m, 340_000_000_000m, -1_500_000_000_000m, Kst.At(t, 15, 30)),
    };
    InvestorDay D(int ago, long f, long i) => new(t.AddDays(-ago), f, i, null, null, Kst.At(t, 15, 0));
    Snapshot S(string sym, string name, decimal last, decimal prev, decimal high, IReadOnlyList<InvestorDay> inv, string? cat = null, long prog = 0) => new()
    {
        Symbol = sym, Name = name, Now = Kst.At(t, 14, 55), Last = last, PrevClose = prev, DayOpen = prev * 1.03m,
        DayHigh = high, LowSince13 = last * 0.97m, Investors = inv, CatalystToday = cat,
        Programs = new[] { new ProgramDay(t, prog, 0) }, High200 = 230_000m, MarketContext = mkt,
        Market = new MarketDay(t, true, Kst.At(t, 9, 0), Kst.At(t, 15, 20), Kst.At(t, 15, 30)),
    };
    var sam = S("005930", "삼성전자", 224_500m, 211_500m, 228_500m,
        new[] { D(0, 2_109_554, 318_198), D(1, 1_000_000, 0), D(2, 800_000, 0), D(3, 500_000, 0), D(4, -100, 0) },
        cat: "3Q 확정 실적", prog: 300_000);
    var hyn = S("000660", "SK하이닉스", 1_296_000m, 1_223_000m, 1_318_000m,
        new[] { D(0, 157_591, -13_898), D(1, -50_000, 0) });
    var snaps = new[] { sam, hyn };
    var evs = snaps.Select(x => SignalEngine.Evaluate(x, cfg)).ToList();
    var tags = TagEngine.BuildAll(snaps, evs);
    string Show(string sym) => string.Join(" ", tags[sym].Select(x => $"#{x.Text}({x.Tone.ToString()[0]})"));
    try
    {
        var st = tags["005930"].Select(x => x.Text).ToList();
        var ht = tags["000660"].Select(x => x.Text).ToList();
        Assert(st.Contains("외국인 4일 연속 순매수"), "삼성 연속: " + Show("005930"));
        Assert(st.Contains("외국인·기관 동반 순매수"), "삼성 동반");
        Assert(st.Any(x => x.StartsWith("코스피 대비 강세")), "삼성 코스피 대비");
        Assert(st.Contains("명분일 강세 · 호재 반응?"), "삼성 명분 반응");
        Assert(st.Contains("프로그램 순매수 유입"), "삼성 프로그램 (30만주×22만원=673억)");
        Assert(ht.Contains("외국인 매수 · 기관 차익실현"), "하이닉스 조합: " + Show("000660"));
        Assert(st.Contains("반도체 동반 강세") && ht.Contains("반도체 동반 강세"), "업종 동반");
        Assert(!ht.Any(x => x.StartsWith("외국인") && x.Contains("연속")), "하이닉스는 1일이라 연속 태그 없음");
        var mt = TagEngine.ForMarket(mkt).Select(x => x.Text).ToList();
        Assert(mt.Contains("코스피 강세장") && mt.Contains("외국인·기관 순매수"), "시장 태그: " + string.Join(",", mt));
        Console.WriteLine("PASS  태그 (오늘 숫자)");
        Console.WriteLine("      삼성: " + Show("005930"));
        Console.WriteLine("      하닉: " + Show("000660"));
        Console.WriteLine("      시장: " + string.Join(" ", mt.Select(x => "#" + x)));
    }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL  태그: " + ex.Message); }
}

// 18. DART: 공시 분류 · IR 개최일 · 목록 페이지 · 명분 이벤트 만들기
{
    try
    {
        Assert(DartClassifier.Classify("연결재무제표기준영업(잠정)실적(공정공시)") == CatalystKind.Earnings, "잠정실적");
        Assert(DartClassifier.Classify("기업설명회(IR)개최(안내공시)") == CatalystKind.InvestorRelations, "IR");
        Assert(DartClassifier.Classify("주요사항보고서(자기주식취득결정)") == CatalystKind.Buyback, "자사주");
        Assert(DartClassifier.Classify("자기주식취득결과보고서") == CatalystKind.None, "취득결과는 제외");
        Assert(DartClassifier.Classify("주식소각결정") == CatalystKind.Cancellation, "소각");
        Assert(DartClassifier.Classify("임원ㆍ주요주주특정증권등소유상황보고서") == CatalystKind.None, "지분공시 제외");

        var ir1 = "1. 일시 및 장소 개최일시 2026-10-28 09:00 장소 컨퍼런스콜 2. 대상 국내외 투자자";
        var ir2 = "기업설명회 개최 1. 개최일시 : 2026년 10월 29일 10시 00분 2. 개최장소: 온라인";
        var ir3 = "공시일 2026.10.15 개최 예정 일시 2026.10.28 (수) 오전 9시";
        Assert(DartClassifier.FindIrDate(ir1, new DateOnly(2026, 10, 15)) == new DateOnly(2026, 10, 28), "IR 날짜1");
        Assert(DartClassifier.FindIrDate(ir2, new DateOnly(2026, 10, 15)) == new DateOnly(2026, 10, 29), "IR 날짜2");
        Assert(DartClassifier.FindIrDate(ir3, new DateOnly(2026, 10, 15)) == new DateOnly(2026, 10, 28), "IR 날짜3 (공시일 건너뜀)");
        Assert(DartClassifier.FindIrDate("내용 없음", new DateOnly(2026, 10, 15)) is null, "IR 날짜 없음");

        // 목록 2페이지 + IR 원문 zip
        string Page(int no, int total, params (string nm, string no, string dt)[] items) =>
            "{\"status\":\"000\",\"message\":\"정상\",\"page_no\":" + no + ",\"page_count\":100,\"total_count\":9,\"total_page\":" + total + ",\"list\":[" +
            string.Join(",", items.Select(i => $"{{\"corp_cls\":\"Y\",\"corp_name\":\"SK하이닉스\",\"corp_code\":\"00164779\",\"stock_code\":\"000660\",\"report_nm\":\"{i.nm}\",\"rcept_no\":\"{i.no}\",\"flr_nm\":\"SK하이닉스\",\"rcept_dt\":\"{i.dt}\",\"rm\":\"유\"}}")) + "]}";
        var zip = new MemoryStream();
        using (var za = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, true))
        using (var w = new StreamWriter(za.CreateEntry("20261015000123.xml").Open(), System.Text.Encoding.UTF8))
            w.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?><DOC><TABLE><TR><TD>1. 일시</TD><TD>2026-10-28 09:00</TD></TR><TR><TD>2. 주요내용</TD><TD>2026년 3분기 경영실적 발표</TD></TR></TABLE></DOC>");
        var zipBytes = zip.ToArray();
        int listCalls = 0, docCalls = 0;
        var handler = new RouteHandler(req =>
        {
            var q = req.RequestUri!.Query;
            if (req.RequestUri.AbsolutePath.EndsWith("list.json"))
            {
                listCalls++;
                return q.Contains("page_no=1&")
                    ? Page(1, 2, ("기업설명회(IR)개최(안내공시)", "20261015000123", "20261015"), ("임원ㆍ주요주주특정증권등소유상황보고서", "20261016000001", "20261016"))
                    : Page(2, 2, ("연결재무제표기준영업(잠정)실적(공정공시)", "20261028000456", "20261028"), ("주요사항보고서(자기주식취득결정)", "20261028000789", "20261028"));
            }
            if (req.RequestUri.AbsolutePath.EndsWith("document.xml")) { docCalls++; return zipBytes; }
            throw new Exception("예상 못한 DART 경로 " + req.RequestUri);
        });
        using var dc = new DartClient("k".PadRight(40, 'x'), handler);
        var finder = new DartCatalystFinder(dc, new[] { CatalystKind.Earnings, CatalystKind.InvestorRelations, CatalystKind.Buyback });
        var evs = await finder.FindAsync(new Dictionary<string, (string, string)> { ["000660"] = ("00164779", "SK하이닉스") },
            new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 28), resolveIrDates: true);
        Assert(listCalls == 2, $"페이지 {listCalls}번");
        Assert(docCalls == 1, "IR 원문 1번");
        Assert(evs.Count == 1, "같은 날 3건(IR·실적·자사주)이 하루로 합쳐져야 함: " + string.Join(" / ", evs.Select(e => e.Date + " " + e.Title)));
        Assert(evs[0].Date == "2026-10-28" && evs[0].Title.StartsWith("실적") && evs[0].Source == "DART", evs[0].Title);
        Assert(finder.IrDateCache["20261015000123"] == "2026-10-28|E", "IR 캐시 " + finder.IrDateCache["20261015000123"]);

        // 인증키 오류
        var bad = new RouteHandler(_ => "{\"status\":\"010\",\"message\":\"등록되지 않은 키입니다.\"}");
        using var dc2 = new DartClient("bad", bad);
        try { await dc2.ListAsync("00164779", new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1)); Assert(false, "예외가 나야 함"); }
        catch (DartException ex) { Assert(ex.Status == "010", ex.Message); }

        // 리다이렉트: (1) 방화벽 호스트로 302 (2) DART 가 http 로 내림 → https 로 따라감 (3) 200 으로 차단 페이지
        HttpResponseMessage Redirect(string to) { var m = new HttpResponseMessage(System.Net.HttpStatusCode.Found); m.Headers.Location = new Uri(to); return m; }
        using (var fw = new DartClient("secretkey", new RouteHandler(_ => Redirect("http://block.company.local/deny?u=opendart"))))
        {
            try { await fw.ListAsync("00164779", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2)); Assert(false, "예외 필요"); }
            catch (DartException ex) { Assert(ex.Message.Contains("block.company.local") && ex.Message.Contains("방화벽") && !ex.Message.Contains("secretkey"), ex.Message); }
        }
        int hops = 0;
        using (var down = new DartClient("k", new RouteHandler(req =>
        {
            hops++;
            if (req.RequestUri!.Scheme == "https" && hops == 1) return Redirect("http://opendart.fss.or.kr" + req.RequestUri.PathAndQuery);
            Assert(req.RequestUri.Scheme == "https", "https 로 다시 요청해야 함");
            return "{\"status\":\"013\",\"message\":\"조회된 데이타가 없습니다.\"}";
        })))
        {
            var empty = await down.ListAsync("00164779", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2));
            Assert(empty.Count == 0 && hops == 2, $"http→https 재시도 {hops}");
        }
        using (var page = new DartClient("k", new RouteHandler(_ => "<!DOCTYPE html><html><body>접근이 차단되었습니다</body></html>")))
        {
            try { await page.ListAsync("00164779", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2)); Assert(false, "예외 필요"); }
            catch (DartException ex) { Assert(ex.Status == "html", ex.Message); }
        }

        Console.WriteLine("PASS  DART (분류 · IR 날짜 · 2페이지 목록 · 원문 zip · 같은 날 합치기 · 인증키 오류 · 리다이렉트 3종)");
    }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL  DART: " + ex.Message); }
}

// 19. 백테스트 계산: 손으로 검산 가능한 작은 데이터
{
    try
    {
        var d0 = new DateOnly(2026, 1, 5);
        var rows = new List<DayRow>();
        // 30일: 종가 100 → 매일 +1, 고가 = 종가 (항상 고가 마감), 외국인 매일 순매수
        for (int i = 0; i < 30; i++)
            rows.Add(new DayRow(d0.AddDays(i), 100 + i, 100 + i, 99 + i, 100 + i, 1000));
        // 10번째 날(인덱스 10)은 고가에서 밀려 마감 (고가 대비 -5%)
        rows[10] = rows[10] with { High = rows[10].Close * 1.05m };
        var cats = new List<CatalystEvent>
        {
            new() { Date = d0.AddDays(10).ToString("yyyy-MM-dd"), Symbol = "X", Title = "밀린 날 명분" },
            new() { Date = d0.AddDays(5).ToString("yyyy-MM-dd"), Symbol = "X", Title = "좋은 날 명분" },
        };
        var rep = Backtester.Run(new Dictionary<string, List<DayRow>> { ["X"] = rows }, cats, cfg);
        var g = rep.Strategies[0]; var co = rep.Strategies[1]; var so = rep.Strategies[2]; var any = rep.Strategies[3];
        Assert(co.Hits.Count == 2, $"명분일 무조건 {co.Hits.Count}");
        Assert(g.Hits.Count == 1 && g.Hits[0].Date == d0.AddDays(5), "지침은 좋은 날 하나만: " + string.Join(",", g.Hits.Select(h => h.Date)));
        // 인덱스 5: 종가 105, 5일 뒤 110 → +4.7619%
        Assert(Math.Round(g.Hits[0].Forward[0]!.Value, 2) == 4.76m, $"5일 수익률 {g.Hits[0].Forward[0]}");
        // 연속 3일 조건: 인덱스 2부터 가능, 인덱스 10 제외 → 30 - 2 - 1 = 27
        Assert(so.Hits.Count == 27, $"수급만 {so.Hits.Count}");
        Assert(any.Hits.Count == 29, $"아무 날 {any.Hits.Count}");
        Assert(g.Hits[0].Forward[2] is not null, "5번째 날은 20일 뒤(25번째)가 있음");
        Assert(so.Hits[^1].Date == d0.AddDays(29) && so.Hits[^1].Forward.All(f => f is null), "마지막 날은 앞으로의 수익률이 비어야 함");
        if (Environment.GetEnvironmentVariable("SHOW_REPORT") == "1") Console.WriteLine(Backtester.Format(rep, new Dictionary<string, string> { ["X"] = "테스트" }));
        var text = Backtester.Format(rep, new Dictionary<string, string> { ["X"] = "테스트" });
        Assert(text.Contains("옛 지침 (명분 + 차트 + 수급, 지금은 안 씀)  —  신호 1번") && text.Contains("기준선(아무 날) 대비"), "보고서 형식");
        var csv = Backtester.ToCsv(rep, new Dictionary<string, string> { ["X"] = "테스트" });
        Assert(csv.Split('\n')[0].StartsWith("전략,날짜,종목,종가,5일수익률%"), "CSV 머리줄");
        Console.WriteLine("PASS  백테스트 계산 (명분·고가 마감·외국인 연속·N일 수익률 검산)");
    }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL  백테스트: " + ex.Message); }
}

// 20. 외국인 연속 끊김 문구: 오늘(10/22) 삼성 상황 — 어제까지 5일, 오늘 잠정 -117,222주
{
    try
    {
        var t = new DateOnly(2026, 10, 22);
        InvestorDay R(int ago, long f, long? indiv = null) => new(t.AddDays(-ago), f, 0, null, indiv, Kst.At(t, 10, 0));
        var inv = new[] { R(0, -117_222), R(1, 2_109_554, 1), R(2, 1_000_000, 1), R(3, 900_000, 1), R(4, 800_000, 1), R(5, 700_000, 1), R(6, -5, 1) };
        var mk = new MarketDay(t, true, Kst.At(t, 9, 0), Kst.At(t, 15, 20), Kst.At(t, 15, 30));
        Snapshot At(int h, int m, IReadOnlyList<InvestorDay> v) => new()
        {
            Symbol = "005930", Name = "삼성전자", Now = Kst.At(t, h, m), Last = 222_000m, PrevClose = 223_500m, DayOpen = 223_000m,
            DayHigh = 226_000m, Investors = v, Market = mk,
        };
        string Detail(Evaluation e) => e.Checks.Single(c => c.Label == "외국인 연속").Detail;

        var morning = SignalEngine.Evaluate(At(10, 0, inv), cfg);
        Assert(Detail(morning).Contains("끊길 위기") && Detail(morning).Contains("어제까지 5일") && Detail(morning).Contains("순매수로 돌면 6일"), Detail(morning));
        Assert(morning.Checks.Single(c => c.Label == "외국인 연속").Passed is null, "외국인 연속은 참고용 (판정에 안 씀)");
        var mTags = TagEngine.ForStock(At(10, 0, inv), morning).Select(x => x.Text).ToList();
        Assert(mTags.Contains("외국인 5일 연속 매수 끊길 위기"), string.Join(",", mTags));

        var evening = SignalEngine.Evaluate(At(16, 0, inv), cfg);
        Assert(Detail(evening).Contains("끊김") && Detail(evening).Contains("잠정, 저녁에 확정"), Detail(evening));

        var confirmedInv = inv.Select((r, i) => i == 0 ? r with { IndividualNet = 50_000 } : r).ToArray();
        var night = SignalEngine.Evaluate(At(19, 0, confirmedInv), cfg);
        Assert(Detail(night).StartsWith("오늘 순매도로 끊김 · 어제까지 5일") && !Detail(night).Contains("잠정"), Detail(night));
        Assert(TagEngine.ForStock(At(19, 0, confirmedInv), night).Any(x => x.Text == "외국인 5일 연속 매수 끊김"), "확정 후 태그");

        var flipped = inv.Select((r, i) => i == 0 ? r with { ForeignerNet = 30_000 } : r).ToArray();
        var afternoon = SignalEngine.Evaluate(At(14, 0, flipped), cfg);
        Assert(Detail(afternoon).StartsWith("오늘 포함 6일 연속 순매수 (오늘은 잠정)"), Detail(afternoon));
        Assert(afternoon.Checks.Single(c => c.Label == "외국인 연속").Passed is null, "참고용");

        Console.WriteLine("PASS  외국인 연속 문구 (장중 끊길 위기 → 마감 후 끊김(잠정) → 저녁 확정 · 오후 반전 시 6일)");
        Console.WriteLine("      10:00 " + Detail(morning));
    }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL  외국인 연속 문구: " + ex.Message); }
}

// 21. 종목 검색 · 초성 · 순위 파싱
{
    try
    {
        var dir = new StockDirectory();
        dir.Set(new[]
        {
            new StockInfo("005930", "삼성전자", "KOSPI", "STOCK", true),
            new StockInfo("005935", "삼성전자우", "KOSPI", "STOCK", false),
            new StockInfo("000660", "SK하이닉스", "KOSPI", "STOCK", true),
            new StockInfo("009150", "삼성전기", "KOSPI", "STOCK", true),
            new StockInfo("069500", "KODEX 200", "KOSPI", "ETF", false),
            new StockInfo("091160", "KODEX 반도체", "KOSPI", "ETF", false),
            new StockInfo("247540", "에코프로비엠", "KOSDAQ", "STOCK", true),
            new StockInfo("028260", "삼성물산", "KOSPI", "STOCK", true),
        }, new DateOnly(2026, 10, 22));
        string Names(string q) => string.Join(",", dir.Search(q).Select(x => x.Name));

        Assert(StockDirectory.Choseong("삼성전자") == "ㅅㅅㅈㅈ", StockDirectory.Choseong("삼성전자"));
        Assert(dir.Search("삼성전자")[0].Symbol == "005930", "정확히 일치가 맨 위: " + Names("삼성전자"));
        Assert(dir.Search("삼성")[0].Name.StartsWith("삼성") && Names("삼성").IndexOf("삼성전자우") > Names("삼성").IndexOf("삼성물산"), "보통주 먼저, 우선주 뒤: " + Names("삼성"));
        Assert(dir.Search("하이")[0].Symbol == "000660", "포함 검색: " + Names("하이"));
        Assert(dir.Search("ㅅㅅㅈㅈ")[0].Symbol == "005930", "초성: " + Names("ㅅㅅㅈㅈ"));
        Assert(dir.Search("ㅎㅇㄴㅅ")[0].Symbol == "000660", "초성 포함(SK하이닉스): " + Names("ㅎㅇㄴㅅ"));
        Assert(dir.Search("000660")[0].Name == "SK하이닉스" && dir.Search("0006")[0].Symbol == "000660", "코드 검색");
        Assert(dir.Search("kodex반도체")[0].Symbol == "091160", "대소문자·공백 무시: " + Names("kodex반도체"));
        Assert(dir.Search("없는종목").Count == 0 && dir.Search("  ").Count == 0, "빈 결과");
        Assert(dir.Find("069500")!.IsEtfLike && dir.Find("005935")!.TypeLabel == "우선주" && dir.Find("247540")!.MarketLabel == "코스닥", "라벨");

        // 순위 응답 → 항목 (changeRate 소수비율 → %)
        var rankJson = """
        {"result":{"rankedAt":"2026-10-22T11:30:00+09:00","rankings":[
          {"rank":1,"symbol":"005930","currency":"KRW","price":{"lastPrice":"216500","basePrice":"223500","changeRate":"-0.03132"},"tradingVolume":"18500000","tradingAmount":"4010000000000"},
          {"rank":2,"symbol":"000660","currency":"KRW","price":{"lastPrice":"1248000","basePrice":"1294000","changeRate":null},"tradingVolume":"2100000","tradingAmount":"2630000000000"}
        ]}}
        """;
        using var tc = new TossClient("id", "secret", new FakeHandler(path => path switch
        {
            "/oauth2/token" => """{"access_token":"t","token_type":"Bearer","expires_in":3600}""",
            "/api/v1/rankings" => rankJson,
            _ => throw new Exception("예상 못한 경로 " + path),
        }));
        var (items, at) = await tc.GetRankingsAsync("MARKET_TRADING_AMOUNT", "realtime");
        Assert(items.Count == 2 && items[0].ChangePct == -3.13m && items[1].ChangePct is null, $"순위 파싱 {items[0].ChangePct}");
        Assert(items[0].Amount == 4_010_000_000_000m && at!.Value.Hour == 11, "거래대금·기준시각");
        Console.WriteLine("PASS  종목 검색 (정확·앞글자·포함·초성·코드·보통주 우선) · 순위 파싱");
        Console.WriteLine("      '삼성' → " + Names("삼성"));
    }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL  종목 검색: " + ex.Message); }
}

// 22. 회피 (관망보다 아래): 오늘 11시 숫자
{
    try
    {
        var t = new DateOnly(2026, 10, 22);
        InvestorDay R(int ago, long f, long i) => new(t.AddDays(-ago), f, i, null, null, Kst.At(t, 11, 0));
        var mk = new MarketDay(t, true, Kst.At(t, 9, 0), Kst.At(t, 15, 20), Kst.At(t, 15, 30));
        Snapshot S(decimal last, decimal prev, IReadOnlyList<InvestorDay> inv) => new()
        {
            Symbol = "005930", Name = "삼성전자", Now = Kst.At(t, 11, 8), Last = last, PrevClose = prev, DayOpen = prev,
            DayHigh = prev * 1.01m, Investors = inv, Market = mk,
        };
        // 삼성: -3.13%, 외국인 -685,374 · 기관 -314,000 → 회피
        var sam = SignalEngine.Evaluate(S(216_500m, 223_500m, new[] { R(0, -685_374, -314_000), R(1, 2_109_554, 0) }), cfg);
        Assert(sam.Verdict == Verdict.Avoid && sam.Headline.Contains("외국인·기관이 같이"), sam.Verdict + " " + sam.Headline);
        // 하락 + 외국인 2일 연속 순매도 (기관은 매수) → 회피
        var streak = SignalEngine.Evaluate(S(216_500m, 223_500m, new[] { R(0, -100, 50), R(1, -100, 0), R(2, 500, 0) }), cfg);
        Assert(streak.Verdict == Verdict.Avoid && streak.Headline.Contains("2일 연속 순매도"), streak.Headline);
        // 하락이지만 외국인은 순매수 → 관망 (회피 아님)
        var buy = SignalEngine.Evaluate(S(216_500m, 223_500m, new[] { R(0, 1000, -10), R(1, 100, 0) }), cfg);
        Assert(buy.Verdict == Verdict.Wait, "외국인 매수면 회피 아님: " + buy.Verdict);
        // -1% 하락에 동반 매도 → 관망 (하락폭 부족)
        var small = SignalEngine.Evaluate(S(221_250m, 223_500m, new[] { R(0, -1000, -10), R(1, 100, 0) }), cfg);
        Assert(small.Verdict == Verdict.Wait, "-1%는 관망: " + small.Verdict);
        Console.WriteLine("PASS  회피 (-2%↓ + 외국인 매도 + 기관 동반/외국인 연속 매도) · 관망과 구분");
        Console.WriteLine("      " + sam.Headline);
    }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL  회피: " + ex.Message); }
}

// 23. 분할 매수 2~4차
{
    try
    {
        var t = new DateOnly(2026, 10, 22);
        var mk = new MarketDay(t, true, Kst.At(t, 9, 0), Kst.At(t, 15, 20), Kst.At(t, 15, 30));
        // 오늘 이전 거래일 80개 (주말 제외)
        var days = new List<DateOnly>();
        for (var d = t.AddDays(-1); days.Count < 80; d = d.AddDays(-1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days.Insert(0, d);
        List<Bar> Series(Func<int, decimal> close) => days.Select((d, i) =>
            new Bar(Kst.At(d, 9, 0), close(i), close(i) * 1.005m, close(i) * 0.995m, close(i), 1000)).ToList();
        Fill Buy(DateOnly d, decimal q) => new("005930", true, q, 200_000m, Kst.At(d, 15, 25));
        Fill Sell(DateOnly d, decimal q) => new("005930", false, q, 210_000m, Kst.At(d, 10, 0));
        var hold = new Holding("005930", "삼성전자", "KRW", 4, 200_000m, 0, 800_000m, 0, 0, 0, 0, 0);
        InvestorDay R(int ago, long f, long i) => new(t.AddDays(-ago), f, i, null, null, Kst.At(t, 14, 50));
        Evaluation E(List<Bar> bars, decimal last, IReadOnlyList<Fill> fills, Holding? h = null, IReadOnlyList<InvestorDay>? inv = null,
                     string? catalystToday = null, IReadOnlyCollection<DateOnly>? catDays = null, decimal? budget = null, StrategySettings? c2 = null) =>
            SignalEngine.Evaluate(new Snapshot
            {
                Symbol = "005930", Name = "삼성전자", Now = Kst.At(t, 14, 55), Last = last, PrevClose = bars[^1].Close,
                DayOpen = bars[^1].Close, DayHigh = last, LowSince13 = last, Investors = inv ?? new[] { R(0, 100, 10), R(1, 100, 10) },
                Market = mk, Holding = h ?? hold, DailyBars = bars, Fills = fills, CashBuyingPower = 3_000_000m,
                CatalystToday = catalystToday, CatalystDays = catDays ?? Array.Empty<DateOnly>(), Budget = budget,
            }, c2 ?? cfg);

        // 회차 복원: 이전 구간(매수·전량 매도) 무시, 같은 날 두 번 체결은 1회
        var cycle = TrancheEngine.CurrentCycleBuyDays(new[]
        {
            Buy(days[40], 3), Sell(days[45], 3),
            Buy(days[70], 2), Buy(days[70], 2), Buy(days[76], 1),
        });
        Assert(cycle.Count == 2 && cycle[0] == days[70] && cycle[1] == days[76], "회차 복원: " + string.Join(",", cycle));

        // 예산 400만, 균형형(기본 50%·기회 50%), 기본 몫 2주(10거래일)마다 4회, 기회 몫 4회 → 1회분 각 50만
        var rising = Series(i => 180_000m + i * 500m);
        var up = E(rising, 220_000m, new[] { Buy(days[70], 4) });  // 명분일 아닌 날 매수 → 기본 몫 1회. 9거래일 + 오늘 = 10
        Assert(up.Plan is { } upP && upP.Core is { Done: 1, Ready: true, TradingDaysSince: 10, Shares: 2, PerTranche: 440_000m } && upP.Opp is { Done: 0, Ready: false },
            $"기본 몫 {up.Plan?.Core.Reason} / 기회 {up.Plan?.Opp.Reason}");
        Assert(up.Plan!.ReadyPart == up.Plan.Core && up.Plan.Summary.StartsWith("균형형"), up.Plan.Summary);

        // 조금 오른 날(+0.23%)은 '기다려 봄'인데, 최소 간격 5 + 창 5 를 넘겼으므로(10거래일) 창 마지막 날 규칙으로 산다
        Assert(up.General is { Verdict: GeneralVerdict.Later } && up.Plan.Core.Reason.Contains("매수 창 마지막 날"), up.General?.Opinion + " / " + up.Plan.Core.Reason);
        // 창 중간(7거래일 = 창 3일째)엔 기다림
        var midWin = E(rising, 220_000m, new[] { Buy(days[73], 4) });
        Assert(midWin.Plan!.Core is { Ready: false } && midWin.Plan.Core.Reason.Contains("창 2거래일 남음"), midWin.Plan.Core.Reason);
        // 하락 마감한 날은 창 중간이어도 '사도 괜찮음'
        var dipNoCat = E(rising, rising[^1].Close * 0.995m, new[] { Buy(days[73], 4) });
        Assert(dipNoCat.General!.Verdict == GeneralVerdict.Good && dipNoCat.Plan!.Core is { Ready: true } && dipNoCat.Plan.Core.Reason.Contains("매수 괜찮음"), dipNoCat.Plan!.Core.Reason);

        var tooSoon = E(rising, 220_000m, new[] { Buy(days[77], 4) });   // 2거래일 + 오늘 = 3 → 최소 간격 5까지 2거래일
        Assert(tooSoon.Plan!.Core is { Ready: false } && tooSoon.Plan.Core.Reason.Contains("2거래일 남음"), tooSoon.Plan.Core.Reason);

        // 평소 판단: +3% 급등이면 '오늘은 아님' (추격) → 기본 몫도 안 삼
        var chaseDay = E(rising, rising[^1].Close * 1.03m, new[] { Buy(days[75], 4) });   // 창 첫날
        Assert(chaseDay.General!.Verdict == GeneralVerdict.Later && chaseDay.General.Opinion.Contains("따라 사기보다") && chaseDay.Plan!.Core.Ready == false, chaseDay.General.Opinion);
        // 공시 없는데 외인·기관 동반 매도로 혼자 -3% → '확인 후 괜찮음' (뉴스 확인) → 기본 몫은 확인 후 매수
        var newsDay = E(rising, rising[^1].Close * 0.97m, new[] { Buy(days[70], 4) }, inv: new[] { R(0, -500, -300), R(1, 100, 10) });
        Assert(newsDay.General!.Verdict == GeneralVerdict.CheckFirst && newsDay.General.Opinion.Contains("뉴스") && newsDay.Plan!.Core is { Ready: true } && newsDay.Plan.Core.Reason.Contains("확인 후"),
            newsDay.General.Opinion + " / " + newsDay.Plan!.Core.Reason);
        // 추세 이탈 (60일선 아래, 20일선 < 60일선) → 오늘은 아님
        var falling = Series(i => 260_000m - i * 800m);
        var brokenDay = E(falling, falling[^1].Close * 0.995m, new[] { Buy(days[60], 4) });
        // 추세 이탈이어도 매수를 막지 않는다 (60일선 회복을 기다리면 한참 오른 뒤에 사게 됨) — 주의 문구만
        Assert(brokenDay.General!.Verdict == GeneralVerdict.Good && brokenDay.General.Opinion.Contains("추세는 꺾여") && brokenDay.Plan!.Core.Ready == true, brokenDay.General.Opinion);

        // 명분일에 산 건 기회 몫으로 센다 → 기본 몫은 아직 첫 회
        var catBuy = E(rising, 220_000m, new[] { Buy(days[70], 4) }, catDays: new[] { days[70], t.AddDays(7) });
        Assert(catBuy.Plan!.Opp.Done == 1 && catBuy.Plan.Core is { Done: 0, Ready: false } && catBuy.Plan.Core.Reason.Contains("첫 회"), catBuy.Plan.Core.Reason);
        Assert(catBuy.Plan.Opp.Reason.Contains($"다음 명분 {t.AddDays(7):M/d}"), catBuy.Plan.Opp.Reason);

        // 명분일 눌림 → 기회 몫 차례 (기본 몫보다 먼저)
        var dipDay = E(rising, 210_000m, new[] { Buy(days[70], 4) }, catalystToday: "3Q 실적", catDays: new[] { t });
        Assert(dipDay.Verdict == Verdict.DipBuy && dipDay.Plan!.ReadyPart == dipDay.Plan.Opp, $"{dipDay.Verdict} {dipDay.Plan?.Opp.Reason}");

        // 명분일에 오르면 기본 몫도 하루 미룬다
        var chase = E(rising, 230_000m, new[] { Buy(days[70], 4) }, catalystToday: "3Q 실적", catDays: new[] { t });
        Assert(chase.Verdict == Verdict.NoChase && chase.Plan!.Core.Ready == false && chase.Plan.Core.Reason.Contains("명분 판단을 따름") && chase.General!.Verdict == GeneralVerdict.Wait, chase.Plan!.Core.Reason);

        // 기본 몫 4회 완료
        var full = E(rising, 220_000m, new[] { Buy(days[50], 1), Buy(days[56], 1), Buy(days[63], 1), Buy(days[70], 1) });
        Assert(full.Plan!.Core is { Done: 4, Complete: true } && !full.Plan.Complete, "기본 몫 완료, 기회 몫 남음");

        // 체결 내역 없음 → 매입금액(100만)÷기본 1회분(50만)=2회로 추정, 간격은 확인 불가
        var est = E(rising, 220_000m, Array.Empty<Fill>(), hold with { PurchaseAmount = 1_000_000m });
        Assert(est.Plan is { Estimated: true } && est.Plan.Core is { Done: 2, Ready: false } && est.Plan.Core.Reason.Contains("체결 내역이 없어"), $"추정 {est.Plan?.Core.Done} {est.Plan?.Core.Ready} {est.Plan?.Core.Reason}");

        // 보유 없음 → 기본 몫 첫 회 차례 / 예산 0 → 계획 없음
        var none = E(rising, 220_000m, Array.Empty<Fill>(), hold with { Quantity = 0 });
        Assert(none.Plan is { } nP && nP.Core is { Done: 0, Ready: false } && nP.Core.Reason.Contains("첫 회는 괜찮은 날"), "보유 없음 · 첫 회는 하락·보합 날을 기다림: " + none.Plan?.Core.Reason);
        Assert(E(rising, 220_000m, Array.Empty<Fill>(), budget: 0).Plan is null, "예산 0");


        // 공격형(기본 75%): 기본 300만/4 = 75만, 기회 100만/4 = 25만 · 비율 100%면 기회 몫 없음
        var aggr = E(rising, 220_000m, Array.Empty<Fill>(), hold with { Quantity = 0 }, c2: new StrategySettings { CoreRatio = 0.75m });
        Assert(aggr.Plan!.Core.SharesPerTranche == 3 && aggr.Plan.Opp.SharesPerTranche == 1 && aggr.Plan.Summary.StartsWith("공격형"), aggr.Plan.Summary);
        var allCore = E(rising, 220_000m, Array.Empty<Fill>(), hold with { Quantity = 0 }, c2: new StrategySettings { CoreRatio = 1m });
        Assert(!allCore.Plan!.Opp.Enabled && allCore.Plan.Core.SharesPerTranche == 4, "기본 100%");

        // 비싼 종목: 1주 122만, 예산 400만 균형형 → 몫마다 200만 = 1주 → 회차 1회로 줄어듦
        var pricey = Series(i => 1_100_000m + i * 1_000m);
        var px = E(pricey, 1_220_000m, Array.Empty<Fill>(), hold with { Quantity = 0 });
        Assert(px.Plan!.Core is { Total: 1, ConfiguredTotal: 4, SharesPerTranche: 1, Shares: 1 } && px.Plan.Opp.Total == 1, $"비싼 종목 {px.Plan.Core.Total}회 {px.Plan.Core.SharesPerTranche}주");
        Assert(px.Plan.Core.NoSplit && px.Plan.Core.NoSplitWarning!.Contains("분할이 안 됩니다"), "분할 안 됨 경고");

        // 오늘 하이닉스 재현: 장 마감 뒤, -5.72%, 코스피 -3.52%, 20일선보다 한참 아래 → '크게 빠진 날 · 시장보다 더 · 급락 구간 · 종가 기준으로는'
        var crash = Series(i => i < 60 ? 1_400_000m + i * 4_000m : 1_640_000m - (i - 59) * 18_000m);
        var sk = SignalEngine.Evaluate(new Snapshot
        {
            Symbol = "000660", Name = "SK하이닉스", Now = Kst.At(t, 16, 20), Last = 1_220_000m, PrevClose = 1_294_000m,
            DayOpen = 1_294_000m, DayHigh = 1_297_000m, Investors = new[] { R(0, 35_241, -212_153), R(1, 100, 10) },
            Market = mk, DailyBars = crash, Holding = hold with { Quantity = 0 },
            MarketContext = new MarketContext { KospiPrevClose = 1000m, KospiLast = 964.8m },
        }, cfg);
        var op = sk.General!.Opinion;
        Assert(sk.General.Verdict == GeneralVerdict.Good && op.Contains("크게 빠진 날") && op.Contains("시장보다 더") && op.Contains("급락 구간") && op.Contains("종가 기준으로는"), op);
        Console.WriteLine("      하이닉스 오늘: " + op);

        // 오늘 삼성 재현: 시장과 똑같이 -3.58% (코스피 -3.52%), 외국인·기관 동반 매도 → 시장 탓, '사도 괜찮음'
        Snapshot Day(decimal last, decimal prevC, decimal kospiPct, long f, long ins) => new()
        {
            Symbol = "005930", Name = "삼성전자", Now = Kst.At(t, 16, 20), Last = last, PrevClose = prevC, DayOpen = prevC, DayHigh = prevC,
            Investors = new[] { R(0, f, ins), R(1, 100, 10) }, Market = mk, DailyBars = crash, Holding = hold with { Quantity = 0 },
            MarketContext = new MarketContext { KospiPrevClose = 1000m, KospiLast = 1000m * (1 + kospiPct / 100m) },
        };
        var sam = SignalEngine.Evaluate(Day(1_220_000m * 0.9642m / 0.9428m, 1_220_000m / 0.9428m, -3.52m, -580_267, -392_994), cfg).General!;
        Assert(sam.Verdict == GeneralVerdict.Good && sam.Opinion.Contains("시장 전체가 빠진 날이라 종목만의 문제로 보긴 어려워요"), sam.Opinion);
        // 종목만 크게 빠짐: 코스피 -1%, 종목 -5% → 뉴스 확인
        var solo = SignalEngine.Evaluate(Day(1_220_000m, 1_284_210m, -1m, -100, -100), cfg).General!;
        Assert(solo.Verdict == GeneralVerdict.CheckFirst && solo.Opinion.Contains("시장보다 크게 빠졌어요"), solo.Opinion);
        Console.WriteLine("      삼성 오늘: " + sam.Opinion);

        // 외국인 연속 매도: 어제까지 4일 + 오늘 = 5일째, 시장과 같이 빠진 날 → '기다려 봄' (창 마지막 날엔 삼)
        Snapshot Streak(IReadOnlyList<InvestorDay> inv, decimal last) => new()
        {
            Symbol = "005930", Name = "삼성전자", Now = Kst.At(t, 14, 55), Last = last, PrevClose = 1_000_000m, DayOpen = 1_000_000m, DayHigh = 1_000_000m,
            Investors = inv, Market = mk, DailyBars = Series(i => 1_000_000m), Holding = hold with { Quantity = 0 },
            MarketContext = new MarketContext { KospiPrevClose = 1000m, KospiLast = 990m },
        };
        var longSell = SignalEngine.Evaluate(Streak(new[] { R(0, -300, -100), R(1, -100, 10), R(2, -100, 10), R(3, -100, 10), R(4, -100, 10), R(5, 100, 10) }, 990_000m), cfg).General!;
        Assert(longSell.Verdict == GeneralVerdict.Later && longSell.Opinion.Contains("5일째 팔고") && longSell.Checks.Any(c => c.Label == "외인 연속" && c.Passed == false), longSell.Opinion);
        Console.WriteLine("      외인 5일째 매도: " + longSell.Opinion);
        // 3일째부터 걸림 / 2일째면 아직 평소대로 '사도 괜찮음'
        var three = SignalEngine.Evaluate(Streak(new[] { R(0, -300, -100), R(1, -100, 10), R(2, -100, 10), R(3, 100, 10) }, 990_000m), cfg).General!;
        Assert(three.Verdict == GeneralVerdict.Later && three.Opinion.Contains("3일째 팔고"), three.Opinion);
        var shortSell = SignalEngine.Evaluate(Streak(new[] { R(0, -300, -100), R(1, -100, 10), R(2, 100, 10) }, 990_000m), cfg).General!;
        Assert(shortSell.Verdict == GeneralVerdict.Good, shortSell.Opinion);
        // 3일 매도 끝나고 오늘 순매수 + 하락 마감 → 사도 괜찮음 + 전환 문구
        var turn = SignalEngine.Evaluate(Streak(new[] { R(0, 300, -100), R(1, -100, 10), R(2, -100, 10), R(3, -100, 10), R(4, 100, 10) }, 990_000m), cfg).General!;
        Assert(turn.Verdict == GeneralVerdict.Good && turn.Opinion.Contains("3일 매도를 끝내고"), turn.Opinion);

        // 예산 100만이면 몫 50만 → 1주도 안 됨
        var poor = E(pricey, 1_220_000m, Array.Empty<Fill>(), hold with { Quantity = 0 }, budget: 1_000_000m);
        Assert(poor.Plan!.Core is { Ready: false, SharesPerTranche: 0 } && poor.Plan.Core.Reason.Contains("1주"), poor.Plan.Core.Reason);

        Console.WriteLine("PASS  예산 분할 (기본 몫 간격 · 명분일 매수는 기회 몫 · 눌림 차례 · 추격 금지일 미룸 · 완료 · 추정 · 프리셋)");
        Console.WriteLine("      " + up.Plan.Summary + " · " + up.Plan.Core.Reason + " → " + up.Plan.Core.Shares + "주");
    }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL  분할 매수: " + ex.Message); }
}

// 24. 리포트 HTML (샘플 생성 — MAKE_REPORT=경로 면 파일로 저장)
{
    try
    {
        var t = new DateOnly(2026, 10, 22);
        var rnd = new Random(7);
        var days = new List<DateOnly>();
        for (var d = t; days.Count < 200; d = d.AddDays(-1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days.Insert(0, d);
        var daily = new List<Bar>();
        decimal px = 150_000m;
        foreach (var d in days)
        {
            var drift = d < new DateOnly(2026, 9, 25) ? 0.0025m : d < new DateOnly(2026, 10, 15) ? -0.006m : 0.012m;
            var o = px;
            var c = Math.Round(o * (1 + drift + (decimal)(rnd.NextDouble() - 0.5) * 0.04m) / 250) * 250;
            var h = Math.Max(o, c) * (1 + (decimal)rnd.NextDouble() * 0.015m);
            var l = Math.Min(o, c) * (1 - (decimal)rnd.NextDouble() * 0.015m);
            daily.Add(new Bar(Kst.At(d, 9, 0), o, Math.Round(h / 250) * 250, Math.Round(l / 250) * 250, c, 10_000_000 + rnd.Next(0, 15_000_000)));
            px = c;
        }
        var prev = daily[^2].Close;
        var minute = new List<Bar>();
        decimal mp = prev * 1.02m;
        for (var tm = Kst.At(t, 9, 0); tm < Kst.At(t, 14, 0); tm = tm.AddMinutes(1))
        {
            mp = Math.Round(mp * (1 + (decimal)(rnd.NextDouble() - 0.52) * 0.003m) / 250) * 250;
            minute.Add(new Bar(tm, mp, mp + 250, mp - 250, mp, 50_000));
        }
        var inv = Enumerable.Range(0, 12).Select(i => new InvestorDay(days[^(i + 1)], i == 0 ? -685_374 : (i < 5 ? 1_000_000 - i * 50_000 : -300_000), i == 0 ? -314_000 : 120_000, null, i == 0 ? null : 50_000, Kst.At(t, 11, 0))).ToList();
        var snap = new Snapshot
        {
            Symbol = "005930", Name = "삼성전자", Now = Kst.At(t, 13, 59), Last = minute[^1].Close, PrevClose = prev,
            DayOpen = minute[0].Open, DayHigh = minute.Max(b => b.High), LowSince13 = minute.Where(b => b.Time.Hour >= 13).Min(b => b.Low),
            Investors = inv, Market = new MarketDay(t, true, Kst.At(t, 9, 0), Kst.At(t, 15, 20), Kst.At(t, 15, 30)),
            DailyBars = daily.Take(daily.Count - 1).ToList(), MinuteBars = minute,
            Holding = new Holding("005930", "삼성전자", "KRW", 4, 210_000m, minute[^1].Close, 840_000m, 4 * minute[^1].Close, 4 * minute[^1].Close - 840_000m, Math.Round((minute[^1].Close / 210_000m - 1) * 100, 2), 0, 0),
            Fills = new[] { new Fill("005930", true, 4, 210_000m, Kst.At(days[^12], 15, 25)) },
            CatalystToday = null, CashBuyingPower = 3_160_000m,
            MarketContext = new MarketContext { KospiLast = 5_695.22m, KospiPrevClose = 5_735.66m, TodayFlow = new MarketFlow(t, -676_000_000_000m, -949_000_000_000m, 1_493_000_000_000m, Kst.At(t, 10, 0)) },
        };
        var ev = SignalEngine.Evaluate(snap, cfg);
        ev.Tags = TagEngine.ForStock(snap, ev);
        var data = new ReportData
        {
            Symbol = "005930", Name = "삼성전자", At = Kst.At(t, 13, 59), Last = snap.Last, PrevClose = prev, Eval = ev,
            Daily = daily, Minute = minute, Investors = inv, Programs = new[] { new ProgramDay(t, -400_000, 10_000) },
            Disclosures = new[]
            {
                new DartDisclosure("00126380", "삼성전자", "005930", "기업설명회(IR)개최(안내공시)", "20261012000123", new DateOnly(2026, 10, 12)),
                new DartDisclosure("00126380", "삼성전자", "005930", "임원ㆍ주요주주특정증권등소유상황보고서", "20261010000001", new DateOnly(2026, 10, 10)),
                new DartDisclosure("00126380", "삼성전자", "005930", "연결재무제표기준영업(잠정)실적(공정공시)", "20261008000456", new DateOnly(2026, 10, 8)),
                new DartDisclosure("00126380", "삼성전자", "005930", "주요사항보고서(자기주식취득결정)", "20260821000789", new DateOnly(2026, 8, 21)),
            },
            Catalysts = new[]
            {
                new CatalystEvent { Date = "2026-10-29", Symbol = "005930", Title = "실적 발표(IR) · 삼성전자", Source = "DART", Url = "https://dart.fss.or.kr/dsaf001/main.do?rcpNo=20261012000123" },
                new CatalystEvent { Date = "2026-10-08", Symbol = "005930", Title = "실적 공시 · 연결재무제표기준영업(잠정)실적", Source = "DART" },
                new CatalystEvent { Date = "2026-08-21", Symbol = "005930", Title = "자사주 취득 결정", Source = "DART" },
            },
            Holding = snap.Holding, BuyDays = ev.Plan?.BuyDays ?? Array.Empty<DateOnly>(), Market = snap.MarketContext,
            // 분기 실적: 2025 1Q ~ 2026 2Q (조 단위 대략값, 이익 증가 중)
            Quarters = Enumerable.Range(0, 6).Select(i =>
            {
                int y = 2025 + (i / 4), q = i % 4 + 1;
                decimal rev = (75m + i * 4m) * 1_000_000_000_000m, op = (6m + i * 3m) * 1_000_000_000_000m;
                return new QuarterFin(y, q, rev, op, op * 0.9m, (380m + i * 8m) * 1_000_000_000_000m, 5_900_000_000);
            }).ToList(),
        };
        var html = HtmlReport.Build(data);
        Assert(html.Contains("기업 정보 · 밸류에이션") && html.Contains("PBR 범위") && html.Contains("숫자로 확인되는 것") && html.Contains("분기 실적") && html.Contains("거꾸로 읽힐"), "기업 정보 섹션");
        Assert(html.Contains("<svg") && html.Contains("명분 판단 체크리스트") && html.Contains("평소 판단") && html.Contains("이 차트에서 볼 것") && html.Contains("보유 · 예산 분할") && html.Contains("기본 몫"), "리포트 구성");
        Assert(!html.Contains("임원ㆍ주요주주특정증권등소유상황보고서</a>"), "정례 공시는 주요 공시에서 빠져야 함");
        var outPath = Environment.GetEnvironmentVariable("MAKE_REPORT");
        if (!string.IsNullOrEmpty(outPath)) File.WriteAllText(outPath, html);
        Console.WriteLine($"PASS  리포트 HTML ({html.Length / 1024}KB · 판정 {SignalEngine.Describe(ev.Verdict)} · 추세 {TrendSummary.From(daily, snap.Last).Label})");
    }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL  리포트: " + ex); }
}

// 24b. DART 재무 파싱 · 4분기 계산 · 밸류에이션
{
    try
    {
        string Acc(string fs, params (string name, string amt)[] rows) =>
            "{\"status\":\"000\",\"message\":\"정상\",\"list\":[" + string.Join(",", rows.Select(r =>
                $"{{\"fs_div\":\"{fs}\",\"account_nm\":\"{r.name}\",\"thstrm_amount\":\"{r.amt}\"}}")) + "]}";
        var json = Acc("CFS", ("매출액", "86,061,747,000,000"), ("영업이익", "12,161,000,000,000"), ("당기순이익(손실)", "10,000,000,000,000"), ("자본총계", "420,000,000,000,000"));
        var (rev, op, net, eq) = DartFinancials.ParseAccounts(json, out var none);
        Assert(!none && rev == 86_061_747_000_000m && op == 12_161_000_000_000m && net == 10_000_000_000_000m && eq == 420_000_000_000_000m, "주요계정 파싱");
        DartFinancials.ParseAccounts("{\"status\":\"013\",\"message\":\"조회된 데이타가 없습니다.\"}", out var none2);
        Assert(none2, "013 = 아직 없음");
        var sharesJson = "{\"status\":\"000\",\"list\":[{\"se\":\"보통주\",\"istc_totqy\":\"5,919,637,922\",\"tesstk_co\":\"100,000,000\",\"distb_stock_co\":\"5,819,637,922\"}," +
                         "{\"se\":\"우선주\",\"istc_totqy\":\"815,974,664\",\"tesstk_co\":\"0\",\"distb_stock_co\":\"815,974,664\"}," +
                         "{\"se\":\"합계\",\"istc_totqy\":\"6,735,612,586\",\"tesstk_co\":\"100,000,000\",\"distb_stock_co\":\"6,635,612,586\"}]}";
        Assert(DartFinancials.ParseShares(sharesJson) == 6_635_612_586, "유통주식 합계");

        // 4분기 = 연간 - 1~3분기
        decimal T(decimal jo) => jo * 1_000_000_000_000m;
        var raws = new List<ReportRaw>();
        foreach (var y in new[] { 2024, 2025 })
        {
            raws.Add(new ReportRaw(y, Reprt.Q1, T(70), T(5), T(4), T(360 + (y - 2024) * 20), 6_000_000_000));
            raws.Add(new ReportRaw(y, Reprt.H1, T(72), T(6), T(5), T(365 + (y - 2024) * 20), 6_000_000_000));
            raws.Add(new ReportRaw(y, Reprt.Q3, T(78), T(9), T(8), T(370 + (y - 2024) * 20), 6_000_000_000));
            raws.Add(new ReportRaw(y, Reprt.Annual, T(300), T(30 + (y - 2024) * 10), T(25), T(375 + (y - 2024) * 20), 6_000_000_000));
        }
        var qs = DartFinancials.ToQuarters(raws);
        Assert(qs.Count == 8 && qs[3] is { Quarter: 4, Revenue: var r4, OpIncome: var o4 } && r4 == T(80) && o4 == T(10), $"4분기 계산 {qs[3].Revenue} {qs[3].OpIncome}");
        Assert(qs[7].OpIncome == T(20), "2025 4Q");

        // 일봉: 2024-01 ~ 2026-10, 가격 60,000 고정 → PBR = 60000*6e9/자본
        var bars = new List<Bar>();
        for (var d = new DateOnly(2024, 1, 2); d <= new DateOnly(2026, 10, 22); d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) bars.Add(new Bar(Kst.At(d, 9, 0), 60_000m, 60_000m, 60_000m, 60_000m, 1));
        var val = Valuation.Compute(qs, bars, 60_000m)!;
        Assert(val.MarketCap == 360_000_000_000_000m && val.Pbr == Math.Round(360m / 395m, 2), $"시총·PBR {val.MarketCap} {val.Pbr}");
        Assert(val.TtmOp == T(40) && val.TtmOpYearAgo == T(30) && Valuation.Growth(val.TtmOp, val.TtmOpYearAgo) == 33.3m, "4분기 합계 비교");
        Assert(val.Per == Math.Round(360m / 25m, 1), $"PER {val.Per}");   // 2025 순이익 4+5+8+(25-17)=25
        Assert(val.PbrPercentile == 0, $"자본이 계속 늘어 지금 PBR 이 가장 낮음 {val.PbrPercentile}");
        var facts = val.Facts().ToList();
        Assert(facts.Any(f => f.Contains("하단")) && facts.Any(f => f.Contains("+33.3%")), string.Join(" / ", facts));
        Assert(!facts.Any(f => f.Contains("매수") || f.Contains("매도")), "사라·팔라는 말 없음");
        Console.WriteLine("PASS  DART 재무 · 밸류에이션 (" + facts[0].Replace("<b>", "").Replace("</b>", "") + ")");
    }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL  재무: " + ex); }
}

// 24c. 미국 반도체 밤사이 · 판단 기록
{
    try
    {
        // 서머타임: 10/22 은 EDT(-4), 12/1 은 EST(-5)
        Assert(UsSemis.ToEt(new DateTimeOffset(2026, 10, 22, 13, 30, 0, TimeSpan.Zero)).Offset == TimeSpan.FromHours(-4), "EDT");
        Assert(UsSemis.ToEt(new DateTimeOffset(2026, 12, 1, 15, 0, 0, TimeSpan.Zero)).Offset == TimeSpan.FromHours(-5), "EST");
        Assert(UsSemis.ToEt(new DateTimeOffset(2026, 3, 8, 6, 59, 0, TimeSpan.Zero)).Offset == TimeSpan.FromHours(-5)
            && UsSemis.ToEt(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero)).Offset == TimeSpan.FromHours(-4), "서머타임 시작 경계");

        // 미국 일봉 (날짜 00:00 ET 로 찍힌다고 가정 → KST 로 변환돼 들어옴)
        Bar Us(int day, decimal close) => new(new DateTimeOffset(2026, 10, day, 0, 0, 0, TimeSpan.FromHours(-4)).ToKst(), close, close, close, close, 1);
        var soxx = new List<Bar> { Us(19, 300m), Us(20, 306m), Us(21, 312m), Us(22, 300m) };
        // 한국 10/22 23:00 (미국 10/22 장중): 밤사이 = 10/21 세션 (+1.96%)
        var night = Kst.At(new DateOnly(2026, 10, 22), 23, 0);
        var o1 = UsSemis.Overnight("SOXX", soxx, new DateOnly(2026, 10, 22), night)!;
        Assert(o1.SessionDate == new DateOnly(2026, 10, 21) && o1.ChangePct == 1.96m, $"밤사이 {o1.SessionDate} {o1.ChangePct}");
        Assert(UsSemis.IsOpen(night) && UsSemis.Live("SOXX", soxx, 297m, night)!.ChangePct == -4.81m, "지금 미국장 (직전 마감 312 대비)");
        // 한국 10/23 00:30 — 날짜는 23일이지만 미국 10/22 장은 아직 진행 중 → 여전히 10/21 세션
        var o2 = UsSemis.Overnight("SOXX", soxx, new DateOnly(2026, 10, 23), Kst.At(new DateOnly(2026, 10, 23), 0, 30))!;
        Assert(o2.SessionDate == new DateOnly(2026, 10, 21), "미완료 세션은 빼야 함");
        // 한국 10/23 09:00 — 10/22 장 끝남 → -3.85%
        var o3 = UsSemis.Overnight("SOXX", soxx, new DateOnly(2026, 10, 23), Kst.At(new DateOnly(2026, 10, 23), 9, 0))!;
        Assert(o3.SessionDate == new DateOnly(2026, 10, 22) && o3.ChangePct == -3.85m, $"다음 날 아침 {o3.ChangePct}");

        // 봉 시각이 '한국 날짜 00:00' 으로 찍혀 동부 날짜가 하루 밀리는 경우 → 주말이 생기므로 자동 보정
        var kstStamped = new[] { 16, 19, 20, 21, 22 }.Select(d => new Bar(new DateTimeOffset(2026, 10, d, 0, 0, 0, TimeSpan.FromHours(9)), 1, 1, 1, 100m + d, 1)).ToList();
        var ses = UsSemis.Sessions(kstStamped);
        Assert(ses.Select(x => x.date.Day).SequenceEqual(new[] { 16, 19, 20, 21, 22 }), "날짜 보정: " + string.Join(",", ses.Select(x => x.date)));

        // 판단 기록
        var t = new DateOnly(2026, 10, 22);
        var mk = new MarketDay(t, true, Kst.At(t, 9, 0), Kst.At(t, 15, 20), Kst.At(t, 15, 30));
        var days = new List<DateOnly>();
        for (var d = t.AddDays(-1); days.Count < 80; d = d.AddDays(-1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days.Insert(0, d);
        var bars = days.Select((d, i) => new Bar(Kst.At(d, 9, 0), 100_000m + i * 100, 100_000m + i * 100, 100_000m + i * 100, 100_000m + i * 100, 1000)).ToList();
        Snapshot JSnap(DateTimeOffset now, decimal last, IReadOnlyList<Fill>? fills = null) => new()
        {
            Symbol = "005930", Name = "삼성전자", Now = now, Last = last, PrevClose = bars[^1].Close, DayOpen = bars[^1].Close, DayHigh = last,
            Investors = new[] { new InvestorDay(t, -100, -50, null, null, now) }, Market = mk, DailyBars = bars, Fills = fills ?? Array.Empty<Fill>(),
            MarketContext = new MarketContext { KospiPrevClose = 1000m, KospiLast = 990m, UsOvernight = new[] { new UsMove("SOXX", "반도체ETF", new DateOnly(2026, 10, 21), 312m, -3.4m), new UsMove("MU", "마이크론", new DateOnly(2026, 10, 21), 880m, -4.1m) } },
        };
        var jr = new List<JournalEntry>();
        var s0 = JSnap(Kst.At(t, 9, 40), bars[^1].Close * 0.99m);
        Assert(!Journal.Upsert(jr, s0, SignalEngine.Evaluate(s0, cfg), s0.Now) && jr.Count == 0, "10시 전엔 기록 안 함");
        var s1 = JSnap(Kst.At(t, 11, 0), bars[^1].Close * 0.99m);
        Assert(Journal.Upsert(jr, s1, SignalEngine.Evaluate(s1, cfg), s1.Now) && jr.Count == 1 && jr[0].UsSox == -3.4m && jr[0].General == "사도 괜찮음", $"기록 {jr[0].General}");
        var s2 = JSnap(Kst.At(t, 14, 55), bars[^1].Close * 1.03m);
        Journal.Upsert(jr, s2, SignalEngine.Evaluate(s2, cfg), s2.Now);
        Assert(jr.Count == 1 && jr[0].General == "기다려 봄" && jr[0].At == "14:55", "같은 날은 덮어씀");
        var s3 = JSnap(Kst.At(t, 16, 0), bars[^1].Close * 0.95m, new[] { new Fill("005930", true, 1, 100_000m, Kst.At(t, 15, 25)) });
        Journal.Upsert(jr, s3, SignalEngine.Evaluate(s3, cfg), s3.Now);
        Assert(jr[0].General == "기다려 봄" && jr[0].Bought, "15:40 뒤엔 판단 고정, 매수 표시는 반영");

        // 성적: 과거 기록 30일 + 일봉으로 5·20일 수익률
        var hist = new List<JournalEntry>();
        for (int i = 40; i < 79; i++)
            hist.Add(new JournalEntry { Date = days[i], Symbol = "005930", Name = "삼성전자", Price = bars[i].Close, ChangePct = i % 3 == 0 ? -1.2m : 0.8m,
                Verdict = "관망", General = i % 3 == 0 ? "사도 괜찮음" : "기다려 봄", CoreReady = i % 6 == 0, Us = new() { ["SOXX"] = i % 4 == 0 ? -3.5m : 0.5m }, UsDate = days[i] });
        var scored = Journal.Score(hist, new Dictionary<string, IReadOnlyList<Bar>> { ["005930"] = bars });
        var first = scored[0];
        Assert(first.R5 == Math.Round((bars[45].Close / bars[40].Close - 1) * 100, 2) && scored[^1].R5 is null, "5일 수익률 · 최근은 대기");
        var html = Journal.Html(scored, Kst.At(t, 20, 0));
        Assert(html.Contains("평소 판단 (명분 없는 날)") && html.Contains("SOXX -3% 이하") && html.Contains("최근 기록") && html.Contains("대기"), "성적표 구성");
        var csv = Journal.Csv(scored);
        Assert(csv.Split('\n')[0].Contains("us_SOXX") && csv.Contains("사도 괜찮음"), "CSV");
        var jpath = Environment.GetEnvironmentVariable("MAKE_JOURNAL");
        if (!string.IsNullOrEmpty(jpath)) File.WriteAllText(jpath, html);
        // JSON 왕복
        var json = System.Text.Json.JsonSerializer.Serialize(jr);
        var back = System.Text.Json.JsonSerializer.Deserialize<List<JournalEntry>>(json)!;
        Assert(back[0].Us["SOXX"] == -3.4m && back[0].Date == t && back[0].Bought, "JSON 왕복");
        Console.WriteLine("PASS  미국 반도체 밤사이 · 판단 기록 · 성적표");
    }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL  미국·판단 기록: " + ex); }
}

// 25. 백테스트 변형 비교 · 조건별 통과 수
{
    try
    {
        var d0 = new DateOnly(2026, 1, 5);
        var rows = new List<DayRow>();
        for (int i = 0; i < 40; i++)
            rows.Add(new DayRow(d0.AddDays(i), 100 + i, 100 + i, 99 + i, 100 + i, 1000, 500));
        rows[10] = rows[10] with { High = rows[10].Close * 1.015m };               // 고가 -1.5% 마감 (-1% 실패, -2% 통과)
        rows[15] = rows[15] with { ForeignNet = -1000 };                           // 외국인 순매도
        rows[20] = rows[20] with { Close = rows[19].Close * 0.97m, High = rows[19].Close, ForeignNet = -1000, InstitutionNet = -500 }; // 회피
        rows[25] = rows[25] with { ForeignNet = 1000 };                            // 정상
        rows[23] = rows[23] with { ForeignNet = -10 };                             // 25일은 연속 2일뿐 (24, 25)
        var cats = new[] { 5, 10, 15, 20, 25 }.Select(i => new CatalystEvent { Date = d0.AddDays(i).ToString("yyyy-MM-dd"), Symbol = "X", Title = $"명분{i}" }).ToList();
        var rep = Backtester.Run(new Dictionary<string, List<DayRow>> { ["X"] = rows }, cats, cfg);
        int Hits(string prefix) => rep.Variants.First(v => v.Name.StartsWith(prefix)).Hits.Count;
        int Count(string label) => rep.Conditions.First(c => c.Label.StartsWith(label)).Passed;

        Assert(rep.Conditions[0].Total == 5, "명분일 5일");
        Assert(Count("상승 마감") == 4 && Count("외국인 당일") == 3 && Count("회피 아님") == 4, $"통과 수 {string.Join(",", rep.Conditions.Select(c => c.Label + c.Passed))}");
        Assert(Hits("①") == 1, $"현행 {Hits("①")} (5일만)");           // 10: 고가, 15: 외인, 20: 하락, 25: 연속 2일
        Assert(Hits("②") == 2, $"연속 뺌 {Hits("②")} (5, 25)");
        Assert(Hits("③") == 3, $"고가 -2% {Hits("③")} (5, 10, 25)");
        Assert(Hits("⑦") == 4, $"상승만 {Hits("⑦")}");
        Assert(Hits("⑧") == 4, $"회피만 아니면 {Hits("⑧")}");
        Assert(Hits("⓪") == 5 && Hits("⑨") == 1 && Hits("⑩") == 1 && Hits("⑪") == 5 && Hits("⑫") == 1, $"싸게 사기 변형 {Hits("⓪")},{Hits("⑨")},{Hits("⑩")},{Hits("⑪")},{Hits("⑫")}");
        var nextHit = rep.Variants.First(v => v.Name.StartsWith("⑫")).Hits[0];
        Assert(nextHit.Date == d0.AddDays(21) && nextHit.Close == rows[21].Close, $"다음날 매수일 {nextHit.Date}");
        Assert(rep.SplitDate is not null, "기간 분할일");

        // 쪼개 보기: 하락폭·코스피 동반·수급·거래량·눌림 진입
        var r2 = new List<DayRow>();
        for (int i = 0; i < 60; i++) r2.Add(new DayRow(d0.AddDays(i), 100, 101, 99, 100, 10, 10, 1000));
        r2[30] = r2[30] with { Close = 99, ForeignNet = -50, InstitutionNet = -50 };           // -1%, 동반 매도
        r2[35] = r2[35] with { Close = 94, Low = 93, Volume = 3000 };                            // -6%, 거래량 3배
        r2[40] = r2[40] with { Close = 103, High = 103, ForeignNet = 100, InstitutionNet = 100 }; // 상승, 수급 강도 20%
        r2[42] = r2[42] with { Close = 99.5m };                                                 // 40일 종가 대비 -3.4% → U6 진입
        var mkt = new Dictionary<DateOnly, decimal> { [d0.AddDays(30)] = -1m, [d0.AddDays(35)] = 0.5m, [d0.AddDays(40)] = 1m };
        var cats2 = new[] { 30, 35, 40 }.Select(i => new CatalystEvent { Date = d0.AddDays(i).ToString("yyyy-MM-dd"), Symbol = "Y", Title = $"명분{i}" }).ToList();
        var rep2 = Backtester.Run(new Dictionary<string, List<DayRow>> { ["Y"] = r2 }, cats2, cfg, null, mkt);
        int S(string prefix) => rep2.Splits.First(v => v.Name.StartsWith(prefix + " ")).Hits.Count;
        Assert(S("D1") == 1 && S("D3") == 1 && S("D4") == 1 && S("D5") == 1 && S("D6") == 1 && S("D8") == 1 && S("D10") == 1,
            $"하락 쪼개기 D1{S("D1")} D3{S("D3")} D4{S("D4")} D5{S("D5")} D6{S("D6")} D8{S("D8")} D10{S("D10")}");
        int N(string prefix) => rep2.NoCatalyst.First(v => v.Name.StartsWith(prefix + " ")).Hits.Count;
        Assert(N("N1") == 2 && N("N2") == 1 && N("N3") == 0 && N("N0") == 60 - 3 - 21, $"명분 없는 날 N0{N("N0")} N1{N("N1")} N2{N("N2")} N3{N("N3")}");
        Assert(S("U3") == 1 && S("U4") == 1 && S("U6") == 1 && rep2.Splits.First(v => v.Name.StartsWith("U6")).Hits[0].Date == d0.AddDays(42), "상승 쪼개기·눌림 진입");
        var text = Backtester.Format(rep, new Dictionary<string, string> { ["X"] = "테스트" });
        Assert(text.Contains("조건별 통과") && text.Contains("지침 변형 비교") && text.Contains("기간 나눠 보기"), "보고서 섹션");
        if (Environment.GetEnvironmentVariable("SHOW_VARIANTS") == "1")
            Console.WriteLine(text[text.IndexOf("명분일 5일 중")..text.IndexOf("지침 신호가 나온 날")]);
        Console.WriteLine("PASS  백테스트 변형 비교 · 조건별 통과 수");
    }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL  변형 비교: " + ex.Message); }
}

// 15. 계좌: 토스 응답 형식 그대로 파싱 → 원화 환산·비중 계산
{
    var holdingsJson = """
    {"result":{
      "totalPurchaseAmount":{"krw":"2000000","usd":"500"},
      "marketValue":{"amount":{"krw":"2240000","usd":"550"},"amountAfterCost":{"krw":"2230000","usd":"549"}},
      "profitLoss":{"amount":{"krw":"240000","usd":"50"},"amountAfterCost":{"krw":"230000","usd":"49"},"rate":"0.1150","rateAfterCost":"0.11"},
      "dailyProfitLoss":{"amount":{"krw":"150000","usd":"-5"},"rate":"0.0550"},
      "items":[
        {"symbol":"005930","name":"삼성전자","marketCountry":"KR","currency":"KRW","quantity":"10","lastPrice":"224000","averagePurchasePrice":"200000",
         "marketValue":{"purchaseAmount":"2000000","amount":"2240000","amountAfterCost":"2230000"},
         "profitLoss":{"amount":"240000","amountAfterCost":"230000","rate":"0.12","rateAfterCost":"0.115"},
         "dailyProfitLoss":{"amount":"150000","rate":"0.0718"},"cost":{"commission":"300","tax":null}},
        {"symbol":"VOO","name":"뱅가드 S&P500","marketCountry":"US","currency":"USD","quantity":"0.912345","lastPrice":"602.85","averagePurchasePrice":"548.03",
         "marketValue":{"purchaseAmount":"500","amount":"550","amountAfterCost":"549"},
         "profitLoss":{"amount":"50","amountAfterCost":"49","rate":"0.10","rateAfterCost":"0.098"},
         "dailyProfitLoss":{"amount":"-5","rate":"-0.0090"},"cost":{"commission":"0.25","tax":null}}
      ]}}
    """;
    using var client = new TossClient("id", "secret", new FakeHandler(path => path switch
    {
        "/oauth2/token" => """{"access_token":"t","token_type":"Bearer","expires_in":3600}""",
        "/api/v1/holdings" => holdingsJson,
        _ => throw new Exception("예상 못한 경로 " + path),
    }));
    var (items, mv, pl, daily, plPct, dailyPct) = await client.GetHoldingsOverviewAsync(1);
    var acc = new AccountSummary
    {
        At = Kst.Now, CashKrw = 1_000_000m, UsdKrw = 1400m,
        MarketValue = mv, ProfitLoss = pl, DailyProfitLoss = daily, ProfitPct = plPct, DailyPct = dailyPct, Items = items,
    };
    var sam = items.Single(i => i.Symbol == "005930");
    var voo = items.Single(i => i.Symbol == "VOO");
    try
    {
        Assert(items.Count == 2, "종목 수");
        Assert(sam.ProfitPct == 12.00m && voo.ProfitPct == 10.00m, $"수익률 {sam.ProfitPct} {voo.ProfitPct}");
        Assert(voo.IsUsd && voo.Quantity == 0.912345m, "소수점 수량");
        Assert(plPct == 11.50m && dailyPct == 5.50m, $"전체 수익률 {plPct} {dailyPct}");
        // 총자산 = 예수금 100만 + 삼성 224만 + VOO 550달러 × 1400 = 77만 → 401만
        Assert(acc.TotalKrw == 4_010_000m, $"총자산 {acc.TotalKrw}");
        Assert(Math.Round(acc.WeightPct(sam), 1) == 55.9m, $"삼성 비중 {acc.WeightPct(sam)}");
        Assert(Math.Round(acc.WeightPct(voo), 1) == 19.2m, $"VOO 비중 {acc.WeightPct(voo)}");
        Assert(Math.Round(acc.CashWeightPct, 1) == 24.9m, $"현금 비중 {acc.CashWeightPct}");
        var noFx = new AccountSummary
        {
            At = Kst.Now, CashKrw = 0, UsdKrw = null, MarketValue = mv, ProfitLoss = pl, DailyProfitLoss = daily,
            ProfitPct = plPct, DailyPct = dailyPct, Items = items,
        };
        Assert(!noFx.KrwConversionReliable, "환율 없으면 환산 불가로 표시해야 함");
        Console.WriteLine("PASS  계좌 파싱 · 원화 환산 · 비중 (총자산 401만, 삼성 55.9% / VOO 19.2% / 현금 24.9%)");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine("FAIL  계좌 파싱: " + ex.Message);
    }
}

// 26. 차트 봉 합치기 (5·10분, 주·월·년) · 정규장 필터
{
    try
    {
        var cday = new DateOnly(2026, 10, 21);
        var mins = Enumerable.Range(0, 30).Select(i => new Bar(Kst.At(cday, 9, 0).AddMinutes(i), 100 + i, 101 + i, 99 + i, 100.5m + i, 10)).ToList();
        var m5 = ChartIntervals.Aggregate(mins, ChartInterval.Min5);
        Assert(m5.Count == 6 && m5[0].Time == Kst.At(cday, 9, 0) && m5[1].Time == Kst.At(cday, 9, 5), $"5분봉 {m5.Count}");
        Assert(m5[0].Open == 100 && m5[0].Close == 104.5m && m5[0].High == 105 && m5[0].Low == 99 && m5[0].Volume == 50, "5분봉 OHLCV");
        Assert(ChartIntervals.Aggregate(mins, ChartInterval.Min10).Count == 3, "10분봉");

        var days = new List<Bar>();
        for (var d = new DateOnly(2024, 12, 2); d <= new DateOnly(2026, 2, 27); d = d.AddDays(1))
            if (d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
                days.Add(new Bar(Kst.At(d, 0, 0), 10, 12, 9, 11, 1));
        var wk = ChartIntervals.Aggregate(days, ChartInterval.Week);
        Assert(wk.All(b => b.Time.DayOfWeek == DayOfWeek.Monday) && wk[0].Volume == 5, "주봉은 월요일 시작, 5일 합");
        var mo = ChartIntervals.Aggregate(days, ChartInterval.Month);
        Assert(mo.Count == 15 && mo[0].Time.Day == 1, $"월봉 {mo.Count}");
        var yr = ChartIntervals.Aggregate(days, ChartInterval.Year);
        Assert(yr.Count == 3 && yr[1].Time.Year == 2025, $"년봉 {yr.Count}");

        var hist = new CandleHistory("1m");
        hist.Merge(new[] { new Bar(Kst.At(cday, 8, 30), 1, 1, 1, 1, 1), new Bar(Kst.At(cday, 9, 0), 1, 1, 1, 1, 1),
                           new Bar(Kst.At(cday, 15, 30), 1, 1, 1, 1, 1), new Bar(Kst.At(cday, 16, 0), 1, 1, 1, 1, 1) });
        Assert(hist.Count == 2, $"정규장만 {hist.Count}");
        var t0 = Kst.At(cday, 14, 50);
        var buf = new TickBuffer();
        var first = Enumerable.Range(0, 25).Select(i => new Trade(t0.AddMilliseconds(i * 300), 200000 + i * 100, 10)).Reverse().ToList();
        Assert(buf.Add(first) == 25, "첫 체결 25건");
        var second = first.Take(5).Concat(Enumerable.Range(25, 5).Select(i => new Trade(t0.AddMilliseconds(i * 300), 202000, 1))).ToList();
        Assert(buf.Add(second) == 5 && buf.Ticks.Count == 30 && buf.Gaps == 0, $"겹치는 체결은 한 번만 {buf.Ticks.Count}");
        var tb = ChartIntervals.FromTicks(buf.Ticks, ChartIntervals.TicksPerBar);
        Assert(tb.Count == 3 && tb[0].Open == 200000 && tb[0].Close == 200900 && tb[0].Volume == 100 && tb[2].Close == 202000, "10틱봉");

        Assert(DartClassifier.IsEarningsIr("2026년 3분기 경영실적 발표 및 컨퍼런스콜") && DartClassifier.IsEarningsIr("Q3 2026 Earnings Release"), "실적 IR");
        Assert(!DartClassifier.IsEarningsIr("해외 기관투자자 대상 기업설명회 (NDR) 참가 · 회사 현황 설명"), "실적 아닌 IR");
        Console.WriteLine("PASS  차트 봉 합치기 · 정규장 필터 · 틱 · 실적 IR 구분");
    }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL  봉 합치기: " + ex.Message); }
}

Console.WriteLine(failures == 0 ? "\n전체 통과" : $"\n실패 {failures}건");
return failures == 0 ? 0 : 1;

sealed class FakeHandler(Func<string, string> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(respond(request.RequestUri!.AbsolutePath), System.Text.Encoding.UTF8, "application/json"),
        });
}

sealed class RouteHandler(Func<HttpRequestMessage, object> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = respond(request);
        if (body is HttpResponseMessage direct) return Task.FromResult(direct);
        HttpContent content = body is byte[] b ? new ByteArrayContent(b) : new StringContent((string)body, System.Text.Encoding.UTF8, "application/json");
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
    }
}
