using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DragonHead.Core;

public sealed class TossApiException : Exception
{
    public HttpStatusCode Status { get; }
    public string Code { get; }

    public TossApiException(HttpStatusCode status, string code, string message)
        : base($"[{(int)status} {code}] {message}")
    {
        Status = status;
        Code = code;
    }
}

/// <summary>
/// 토스증권 Open API 조회 전용 클라이언트.
/// 의도적으로 주문·정정·취소·조건주문 메서드를 두지 않는다. 이 앱은 알림만 한다.
/// </summary>
public sealed class TossClient : IDisposable
{
    private const string BaseUrl = "https://openapi.tossinvest.com";

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _token;
    private DateTimeOffset _tokenExpires;

    public TossClient(string clientId, string clientSecret, HttpMessageHandler? handler = null)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _http = handler is null
            ? new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
            : new HttpClient(handler);
        _http.BaseAddress = new Uri(BaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public void Dispose()
    {
        _http.Dispose();
        _tokenLock.Dispose();
    }

    // ───────────────────────── 인증 ─────────────────────────

    private async Task<string> GetTokenAsync(bool force, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!force && _token is not null && DateTimeOffset.UtcNow < _tokenExpires)
                return _token;

            using var body = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
            });
            using var resp = await _http.PostAsync("/oauth2/token", body, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                // OAuth 표준 에러: { "error": "invalid_client", "error_description": "..." }
                string code = "oauth-error", msg = text;
                try
                {
                    using var d = JsonDocument.Parse(text);
                    if (d.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                    {
                        code = e.GetString() ?? code;
                        msg = d.RootElement.TryGetProperty("error_description", out var ed) ? ed.GetString() ?? "" : "";
                    }
                }
                catch (JsonException) { }
                throw new TossApiException(resp.StatusCode, code, Hint(resp.StatusCode, code, msg));
            }

            using var doc = JsonDocument.Parse(text);
            _token = doc.RootElement.GetProperty("access_token").GetString();
            var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt64();
            // 만료 1분 전에 미리 갱신
            _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));
            return _token!;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // ───────────────────────── 공통 GET ─────────────────────────

    private async Task<JsonElement> GetAsync(string pathAndQuery, long? accountSeq, CancellationToken ct)
    {
        bool reissued = false;
        for (int attempt = 0; ; attempt++)
        {
            var token = await GetTokenAsync(force: false, ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (accountSeq is not null)
                req.Headers.Add("X-Tossinvest-Account", accountSeq.Value.ToString(CultureInfo.InvariantCulture));

            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.GetProperty("result").Clone();
            }

            var (code, message) = ParseError(text);

            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1 << attempt);
                await Task.Delay(wait + TimeSpan.FromMilliseconds(Random.Shared.Next(100, 400)), ct);
                continue;
            }

            if (resp.StatusCode == HttpStatusCode.Unauthorized && !reissued)
            {
                // 다른 곳에서 토큰을 재발급하면 기존 토큰이 즉시 무효화된다 (client 당 유효 토큰 1개).
                reissued = true;
                await GetTokenAsync(force: true, ct);
                continue;
            }

            throw new TossApiException(resp.StatusCode, code, Hint(resp.StatusCode, code, message));
        }
    }

    private static (string code, string message) ParseError(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
                return (e.GetProperty("code").GetString() ?? "unknown",
                        e.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "");
        }
        catch (JsonException) { }
        return ("unknown", text.Length > 200 ? text[..200] : text);
    }

    private static string Hint(HttpStatusCode status, string code, string message)
    {
        if (status == HttpStatusCode.Forbidden)
            return $"{message} — 이 PC의 공인 IP가 토스증권 WTS > 설정 > Open API > 허용 IP 관리에 등록돼 있는지 확인하세요.";
        if (code == "invalid_client")
            return $"{message} — client_id / client_secret 이 맞는지 확인하세요.";
        return message;
    }

    // ───────────────────────── 파서 ─────────────────────────

    private static decimal Dec(JsonElement e) =>
        e.ValueKind switch
        {
            JsonValueKind.String => decimal.Parse(e.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture),
            JsonValueKind.Number => e.GetDecimal(),
            _ => 0m,
        };

    private static decimal Dec(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) ? Dec(v) : 0m;

    private static long? NetOrNull(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object
            ? (long)Dec(v, "netBuyVolume")
            : null;

    private static string Enc(string s) => Uri.EscapeDataString(s);

    // ───────────────────────── 시세 ─────────────────────────

    public async Task<Dictionary<string, decimal>> GetPricesAsync(IEnumerable<string> symbols, CancellationToken ct = default)
    {
        var r = await GetAsync($"/api/v1/prices?symbols={Enc(string.Join(',', symbols))}", null, ct);
        var map = new Dictionary<string, decimal>();
        foreach (var item in r.EnumerateArray())
            map[item.GetProperty("symbol").GetString()!] = Dec(item, "lastPrice");
        return map;
    }

    public async Task<(List<Bar> bars, DateTimeOffset? nextBefore)> GetCandlesAsync(
        string symbol, string interval, int count, DateTimeOffset? before, CancellationToken ct = default)
    {
        var q = $"/api/v1/candles?symbol={Enc(symbol)}&interval={interval}&count={count}";
        if (before is not null)
            q += "&before=" + Enc(before.Value.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
        var r = await GetAsync(q, null, ct);

        var bars = new List<Bar>();
        foreach (var c in r.GetProperty("candles").EnumerateArray())
        {
            bars.Add(new Bar(
                DateTimeOffset.Parse(c.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture).ToKst(),
                Dec(c, "openPrice"), Dec(c, "highPrice"), Dec(c, "lowPrice"), Dec(c, "closePrice"), Dec(c, "volume")));
        }

        DateTimeOffset? next = null;
        if (r.TryGetProperty("nextBefore", out var nb) && nb.ValueKind == JsonValueKind.String)
            next = DateTimeOffset.Parse(nb.GetString()!, CultureInfo.InvariantCulture);
        return (bars, next);
    }

    /// <summary>당일 최근 체결 (최대 50건, 최신이 먼저). 틱 차트용.</summary>
    public async Task<List<Trade>> GetTradesAsync(string symbol, int count = 50, CancellationToken ct = default)
    {
        var r = await GetAsync($"/api/v1/trades?symbol={Enc(symbol)}&count={Math.Clamp(count, 1, 50)}", null, ct);
        var list = new List<Trade>();
        if (r.ValueKind != JsonValueKind.Array) return list;
        foreach (var t in r.EnumerateArray())
        {
            if (!t.TryGetProperty("timestamp", out var ts) || ts.ValueKind != JsonValueKind.String) continue;
            list.Add(new Trade(DateTimeOffset.Parse(ts.GetString()!, CultureInfo.InvariantCulture).ToKst(), Dec(t, "price"), Dec(t, "volume")));
        }
        return list;
    }

    /// <summary>한 시장의 거래 가능한 전체 종목 (하루 1회 받아 캐시 권장)</summary>
    public async Task<List<StockInfo>> GetAllStocksAsync(string market, CancellationToken ct = default)
    {
        var r = await GetAsync($"/api/v1/stocks/all?market={Enc(market)}", null, ct);
        var list = new List<StockInfo>();
        foreach (var it in r.EnumerateArray())
        {
            list.Add(new StockInfo(
                it.GetProperty("symbol").GetString()!,
                it.GetProperty("name").GetString() ?? "",
                market,
                it.TryGetProperty("securityType", out var st) ? st.GetString() ?? "" : "",
                it.TryGetProperty("isCommonShare", out var cs) && cs.ValueKind == JsonValueKind.True));
        }
        return list;
    }

    /// <summary>
    /// 국내 주식 순위 (최대 100). type: MARKET_TRADING_AMOUNT, MARKET_TRADING_VOLUME, TOP_GAINERS, TOP_LOSERS,
    /// TOSS_SECURITIES_TRADING_AMOUNT, TOSS_SECURITIES_TRADING_VOLUME. duration: realtime, 1d, 1w, 1mo, 3mo, 6mo, 1y
    /// (급상승·급하락은 realtime 미지원).
    /// </summary>
    public async Task<(List<RankItem> items, DateTimeOffset? rankedAt)> GetRankingsAsync(
        string type, string duration, int count = 100, bool excludeCaution = false, CancellationToken ct = default, string country = "KR")
    {
        var q = $"/api/v1/rankings?type={Enc(type)}&marketCountry={Enc(country)}&duration={Enc(duration)}&count={count}";
        if (excludeCaution) q += "&excludeInvestmentCaution=true";
        var r = await GetAsync(q, null, ct);
        var list = new List<RankItem>();
        foreach (var it in r.GetProperty("rankings").EnumerateArray())
        {
            var price = it.GetProperty("price");
            decimal? rate = price.TryGetProperty("changeRate", out var cr) && cr.ValueKind != JsonValueKind.Null
                ? Math.Round(Dec(cr) * 100m, 2)
                : null;
            list.Add(new RankItem(
                it.GetProperty("rank").GetInt32(),
                it.GetProperty("symbol").GetString()!,
                Dec(price, "lastPrice"),
                Dec(price, "basePrice"),
                rate,
                Dec(it, "tradingVolume"),
                Dec(it, "tradingAmount")));
        }
        DateTimeOffset? at = r.TryGetProperty("rankedAt", out var ra) && ra.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(ra.GetString()!, CultureInfo.InvariantCulture).ToKst()
            : null;
        return (list, at);
    }

    /// <summary>
    /// 랭킹 응답의 전일 기준가 (price.basePrice). 거래대금 상위 100위 안에 있는 종목만 나온다.
    /// TOP_GAINERS/LOSERS 가 아닌 타입은 항상 전일 기준가라고 명세에 적혀 있다.
    /// </summary>
    public async Task<Dictionary<string, decimal>> GetBasePricesFromRankingAsync(string type = "MARKET_TRADING_AMOUNT", CancellationToken ct = default)
    {
        var r = await GetAsync($"/api/v1/rankings?type={type}&marketCountry=KR&duration=1d&count=100", null, ct);
        var map = new Dictionary<string, decimal>();
        foreach (var item in r.GetProperty("rankings").EnumerateArray())
        {
            var bp = Dec(item.GetProperty("price"), "basePrice");
            if (bp > 0) map[item.GetProperty("symbol").GetString()!] = bp;
        }
        return map;
    }

    /// <summary>진단용: 응답 원문(JSON) 그대로. 조회 전용 GET 만.</summary>
    public async Task<string> GetRawAsync(string pathAndQuery, CancellationToken ct = default)
    {
        var result = await GetAsync(pathAndQuery, null, ct);
        return result.GetRawText();
    }

    /// <summary>당일 상·하한가. 가격제한이 없는 종목은 null.</summary>
    public async Task<(decimal? upper, decimal? lower)> GetPriceLimitsAsync(string symbol, CancellationToken ct = default)
    {
        var r = await GetAsync($"/api/v1/price-limits?symbol={Enc(symbol)}", null, ct);
        static decimal? N(JsonElement o, string n) =>
            o.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null ? Dec(v) : null;
        return (N(r, "upperLimitPrice"), N(r, "lowerLimitPrice"));
    }

    /// <summary>오늘 09:00 이후의 1분봉 전체 (시간순). 최대 3페이지(600분)까지 거슬러 올라간다.</summary>
    public async Task<List<Bar>> GetTodayMinuteBarsAsync(string symbol, DateOnly today, CancellationToken ct = default)
    {
        var open = Kst.At(today, 9, 0);
        var all = new List<Bar>();
        DateTimeOffset? before = null;
        for (int page = 0; page < 3; page++)
        {
            var (bars, next) = await GetCandlesAsync(symbol, "1m", 200, before, ct);
            all.AddRange(bars);
            if (bars.Count == 0 || next is null || bars.Min(b => b.Time) < open)
                break;
            before = next;
        }
        return all
            .Where(b => b.Time >= open && DateOnly.FromDateTime(b.Time.DateTime) == today)
            .GroupBy(b => b.Time).Select(g => g.First())
            .OrderBy(b => b.Time)
            .ToList();
    }

    // ───────────────────────── 수급 ─────────────────────────

    public async Task<List<InvestorDay>> GetInvestorTradingAsync(string symbol, int count = 10, CancellationToken ct = default) =>
        (await GetInvestorTradingPageAsync(symbol, count, null, ct)).records;

    /// <summary>투자자 매매동향 한 페이지 (최신순). nextUntil 이 null 이면 마지막 페이지.</summary>
    public async Task<(List<InvestorDay> records, DateOnly? nextUntil)> GetInvestorTradingPageAsync(
        string symbol, int count, DateOnly? until, CancellationToken ct = default)
    {
        var q = $"/api/v1/stocks/{Enc(symbol)}/investor-trading?count={count}";
        if (until is { } u) q += "&until=" + u.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var r = await GetAsync(q, null, ct);
        var list = new List<InvestorDay>();
        foreach (var rec in r.GetProperty("records").EnumerateArray())
        {
            var inst = rec.GetProperty("institution");
            long? finInv = null;
            if (inst.TryGetProperty("breakdown", out var bd) && bd.ValueKind == JsonValueKind.Object)
                finInv = NetOrNull(bd, "financialInvestment");

            list.Add(new InvestorDay(
                DateOnly.Parse(rec.GetProperty("date").GetString()!, CultureInfo.InvariantCulture),
                (long)Dec(rec.GetProperty("foreigner"), "netBuyVolume"),
                (long)Dec(inst, "netBuyVolume"),
                finInv,
                NetOrNull(rec, "individual"),
                DateTimeOffset.Parse(rec.GetProperty("updatedAt").GetString()!, CultureInfo.InvariantCulture).ToKst()));
        }
        DateOnly? next = r.TryGetProperty("nextUntil", out var nu) && nu.ValueKind == JsonValueKind.String
            ? DateOnly.Parse(nu.GetString()!, CultureInfo.InvariantCulture)
            : null;
        return (list, next);
    }

    public async Task<List<ProgramDay>> GetProgramTradesAsync(string symbol, int count = 5, CancellationToken ct = default)
    {
        var r = await GetAsync($"/api/v1/stocks/{Enc(symbol)}/program-trades?count={count}", null, ct);
        var list = new List<ProgramDay>();
        foreach (var rec in r.GetProperty("records").EnumerateArray())
        {
            list.Add(new ProgramDay(
                DateOnly.Parse(rec.GetProperty("date").GetString()!, CultureInfo.InvariantCulture),
                (long)Dec(rec.GetProperty("nonArbitrage"), "netBuyVolume"),
                (long)Dec(rec.GetProperty("arbitrage"), "netBuyVolume")));
        }
        return list;
    }

    // ───────────────────────── 시장 지표 ─────────────────────────

    public async Task<decimal?> GetIndicatorLastAsync(string symbol, CancellationToken ct = default)
    {
        var r = await GetAsync($"/api/v1/market-indicators/prices?symbols={Enc(symbol)}", null, ct);
        foreach (var item in r.EnumerateArray())
            if (item.GetProperty("symbol").GetString() == symbol)
                return Dec(item, "lastPrice");
        return null;
    }

    /// <summary>지수 일봉 (최신순 아님 — 응답 순서 그대로). 지수는 애프터마켓이 없어 일봉 종가를 그대로 쓴다.</summary>
    public async Task<List<Bar>> GetIndicatorDailyAsync(string symbol, int count = 3, CancellationToken ct = default)
    {
        var r = await GetAsync($"/api/v1/market-indicators/{Enc(symbol)}/candles?interval=1d&count={count}", null, ct);
        var bars = new List<Bar>();
        foreach (var c in r.GetProperty("candles").EnumerateArray())
        {
            bars.Add(new Bar(
                DateTimeOffset.Parse(c.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture).ToKst(),
                Dec(c, "openPrice"), Dec(c, "highPrice"), Dec(c, "lowPrice"), Dec(c, "closePrice"),
                c.TryGetProperty("volume", out var v) && v.ValueKind != JsonValueKind.Null ? Dec(v) : 0m));
        }
        return bars;
    }

    /// <summary>지수 일봉 한 페이지 (과거로 넘기기용). 백테스트에서 코스피 등락을 받는 데 쓴다.</summary>
    public async Task<(List<Bar> bars, DateTimeOffset? nextBefore)> GetIndicatorCandlesPageAsync(
        string symbol, DateTimeOffset? before, CancellationToken ct = default)
    {
        var q = $"/api/v1/market-indicators/{Enc(symbol)}/candles?interval=1d&count=200";
        if (before is not null)
            q += "&before=" + Enc(before.Value.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));
        var r = await GetAsync(q, null, ct);
        var bars = new List<Bar>();
        foreach (var c in r.GetProperty("candles").EnumerateArray())
            bars.Add(new Bar(
                DateTimeOffset.Parse(c.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture).ToKst(),
                Dec(c, "openPrice"), Dec(c, "highPrice"), Dec(c, "lowPrice"), Dec(c, "closePrice"), 0m));
        DateTimeOffset? next = null;
        if (r.TryGetProperty("nextBefore", out var nb) && nb.ValueKind == JsonValueKind.String)
            next = DateTimeOffset.Parse(nb.GetString()!, CultureInfo.InvariantCulture);
        return (bars, next);
    }

    /// <summary>코스피·코스닥 투자자별 순매수 금액 (일별, 최신순)</summary>
    public async Task<List<MarketFlow>> GetMarketFlowAsync(string symbol = "KOSPI", int count = 2, CancellationToken ct = default)
    {
        var r = await GetAsync($"/api/v1/market-indicators/{Enc(symbol)}/investor-trading?interval=1d&count={count}", null, ct);
        static decimal Net(JsonElement o) => Dec(o, "buyAmount") - Dec(o, "sellAmount");
        var list = new List<MarketFlow>();
        foreach (var rec in r.GetProperty("records").EnumerateArray())
        {
            list.Add(new MarketFlow(
                DateOnly.Parse(rec.GetProperty("date").GetString()!, CultureInfo.InvariantCulture),
                Net(rec.GetProperty("foreigner")),
                Net(rec.GetProperty("institution")),
                Net(rec.GetProperty("individual")),
                DateTimeOffset.Parse(rec.GetProperty("updatedAt").GetString()!, CultureInfo.InvariantCulture).ToKst()));
        }
        return list;
    }

    // ───────────────────────── 장 운영 ─────────────────────────

    public async Task<MarketDay> GetMarketDayAsync(CancellationToken ct = default)
    {
        var r = await GetAsync("/api/v1/market-calendar/KR", null, ct);
        var today = r.GetProperty("today");
        var date = DateOnly.Parse(today.GetProperty("date").GetString()!, CultureInfo.InvariantCulture);

        if (!today.TryGetProperty("integrated", out var integ) || integ.ValueKind != JsonValueKind.Object
            || !integ.TryGetProperty("regularMarket", out var reg) || reg.ValueKind != JsonValueKind.Object)
            return new MarketDay(date, false, null, null, null);

        static DateTimeOffset? T(JsonElement o, string n) =>
            o.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String
                ? DateTimeOffset.Parse(v.GetString()!, CultureInfo.InvariantCulture).ToKst()
                : null;

        DateTimeOffset? afterStart = integ.TryGetProperty("afterMarket", out var am) && am.ValueKind == JsonValueKind.Object
            ? T(am, "startTime")
            : null;
        return new MarketDay(date, true, T(reg, "startTime"), T(reg, "singlePriceAuctionStartTime"), T(reg, "endTime"), afterStart);
    }

    // ───────────────────────── 계좌 (조회만) ─────────────────────────

    public async Task<long?> GetBrokerageAccountSeqAsync(CancellationToken ct = default)
    {
        var r = await GetAsync("/api/v1/accounts", null, ct);
        foreach (var a in r.EnumerateArray())
            if (a.GetProperty("accountType").GetString() == "BROKERAGE")
                return a.GetProperty("accountSeq").GetInt64();
        return null;
    }

    public async Task<decimal> GetCashBuyingPowerKrwAsync(long accountSeq, CancellationToken ct = default)
    {
        var r = await GetAsync("/api/v1/buying-power?currency=KRW", accountSeq, ct);
        return Dec(r, "cashBuyingPower");
    }

    /// <summary>
    /// 한 종목의 체결 내역 (조회만). 종료된 주문 중 체결 수량이 있는 것만, 오래된 것 → 최신.
    /// 부분 체결 후 취소된 주문도 체결분은 포함한다.
    /// </summary>
    public async Task<List<Fill>> GetFillsAsync(long accountSeq, string symbol, DateOnly from, CancellationToken ct = default)
    {
        var fills = new List<Fill>();
        string? cursor = null;
        for (int page = 0; page < 10; page++)
        {
            var q = $"/api/v1/orders?status=CLOSED&symbol={Enc(symbol)}&from={from:yyyy-MM-dd}&limit=100";
            if (cursor is not null) q += "&cursor=" + Enc(cursor);
            var r = await GetAsync(q, accountSeq, ct);
            foreach (var o in r.GetProperty("orders").EnumerateArray())
            {
                var ex = o.GetProperty("execution");
                var qty = Dec(ex, "filledQuantity");
                if (qty <= 0) continue;
                var at = ex.TryGetProperty("filledAt", out var fa) && fa.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(fa.GetString()!, CultureInfo.InvariantCulture)
                    : DateTimeOffset.Parse(o.GetProperty("orderedAt").GetString()!, CultureInfo.InvariantCulture);
                var price = ex.TryGetProperty("averageFilledPrice", out var ap) && ap.ValueKind != JsonValueKind.Null ? Dec(ap) : 0m;
                fills.Add(new Fill(symbol, o.GetProperty("side").GetString() == "BUY", qty, price, at.ToKst()));
            }
            if (!(r.TryGetProperty("hasNext", out var hn) && hn.ValueKind == JsonValueKind.True)) break;
            cursor = r.TryGetProperty("nextCursor", out var nc) && nc.ValueKind == JsonValueKind.String ? nc.GetString() : null;
            if (cursor is null) break;
        }
        return fills.OrderBy(f => f.FilledAt).ToList();
    }

    /// <summary>1달러 = ?원 (매매기준율). 참고용 표시 환율.</summary>
    public async Task<decimal> GetUsdKrwAsync(CancellationToken ct = default)
    {
        var r = await GetAsync("/api/v1/exchange-rate?baseCurrency=USD&quoteCurrency=KRW", null, ct);
        var mid = Dec(r, "midRate");
        return mid > 0 ? mid : Dec(r, "rate");
    }

    /// <summary>보유 주식 전체와 요약. 예수금·환율은 따로 채운다.</summary>
    public async Task<(List<Holding> items, Money marketValue, Money profitLoss, Money daily, decimal profitPct, decimal dailyPct)>
        GetHoldingsOverviewAsync(long accountSeq, CancellationToken ct = default)
    {
        var r = await GetAsync("/api/v1/holdings", accountSeq, ct);

        static Money M(JsonElement price) => new(
            Dec(price, "krw"),
            price.TryGetProperty("usd", out var u) && u.ValueKind != JsonValueKind.Null ? Dec(u) : null);

        // 손익률은 소수비율(0.1077 = 10.77%)로 온다 → % 로 바꾼다
        static decimal Pct(JsonElement obj, string name) => Math.Round(Dec(obj, name) * 100m, 2);

        var items = new List<Holding>();
        foreach (var i in r.GetProperty("items").EnumerateArray())
        {
            var mv = i.GetProperty("marketValue");
            var pl = i.GetProperty("profitLoss");
            var dpl = i.GetProperty("dailyProfitLoss");
            items.Add(new Holding(
                i.GetProperty("symbol").GetString()!,
                i.GetProperty("name").GetString() ?? "",
                i.TryGetProperty("currency", out var cur) ? cur.GetString() ?? "KRW" : "KRW",
                Dec(i, "quantity"),
                Dec(i, "averagePurchasePrice"),
                Dec(i, "lastPrice"),
                Dec(mv, "purchaseAmount"),
                Dec(mv, "amount"),
                Dec(pl, "amount"),
                Pct(pl, "rate"),
                Dec(dpl, "amount"),
                Pct(dpl, "rate")));
        }

        var omv = r.GetProperty("marketValue");
        var opl = r.GetProperty("profitLoss");
        var odpl = r.GetProperty("dailyProfitLoss");
        return (items,
                M(omv.GetProperty("amount")),
                M(opl.GetProperty("amount")),
                M(odpl.GetProperty("amount")),
                Pct(opl, "rate"),
                Pct(odpl, "rate"));
    }
}
