namespace DragonHead.Core;

/// <summary>
/// 감시 종목들의 Snapshot 을 만든다. 호출량을 줄이려고 데이터마다 갱신 주기를 다르게 둔다.
/// - 현재가: 매번
/// - 1분봉: 매번 (장중에만)
/// - 투자자 동향: 5분
/// - 일봉(전일 종가), 장 운영, 계좌: 30분
/// </summary>
public sealed class MarketDataService
{
    private readonly TossClient _client;
    private readonly AppSettings _settings;

    private MarketDay? _market;
    private DateTimeOffset _marketAt;

    private readonly Dictionary<string, decimal> _prevClose = new();
    /// <summary>마지막으로 받은 실시간 가격 (한 번 응답에 빠져도 줄이 깜빡이지 않게)</summary>
    private readonly Dictionary<string, decimal> _lastLive = new();
    private DateOnly _prevCloseDay;
    // 오늘 이미 기준가를 시도한 종목 (실패해도 1초마다 다시 부르지 않도록)
    private readonly HashSet<string> _prevCloseTried = new();

    private readonly Dictionary<string, List<InvestorDay>> _investors = new();
    private readonly Dictionary<string, List<ProgramDay>> _programs = new();
    private readonly Dictionary<string, decimal> _high200 = new();
    private DateOnly _high200Day;
    private readonly HashSet<string> _high200Tried = new();

    private MarketContext? _marketCtx;
    private decimal? _kospiPrev;
    private DateOnly _kospiPrevDay;
    private MarketFlow? _flow;
    private DateTimeOffset _marketCtxAt, _flowAt;
    private readonly Dictionary<string, List<Bar>> _usBars = new();
    private readonly Dictionary<string, decimal> _usPrices = new();
    private DateTimeOffset _usBarsAt, _usPricesAt;
    private readonly HashSet<string> _usLogged = new();
    private DateTimeOffset _investorsAt;

    private long? _accountSeq;
    private bool _accountResolved;
    // 분봉은 1분에 한 번만 새로 받는다. 현재가는 매 갱신(장중 5초)마다.
    private readonly Dictionary<string, (DateTimeOffset at, List<Bar> bars)> _minutes = new();

    private AccountSummary? _account;
    private readonly Dictionary<string, List<Bar>> _daily = new();
    private readonly Dictionary<string, List<Fill>> _fills = new();
    private DateTimeOffset _accountAt;

    public string? LastAccountError { get; private set; }

    /// <summary>기준가를 정확히 구하지 못했을 때의 안내. 정상이면 null.</summary>
    public string? LastBaseNote { get; private set; }

    public MarketDataService(TossClient client, AppSettings settings)
    {
        _client = client;
        _settings = settings;
    }

    public async Task<List<Snapshot>> RefreshAsync(CancellationToken ct = default)
    {
        var now = Kst.Now;
        var today = DateOnly.FromDateTime(now.DateTime);

        if (_market is null || _market.Date != today || now - _marketAt > TimeSpan.FromMinutes(30))
        {
            _market = await _client.GetMarketDayAsync(ct);
            _marketAt = now;
        }

        var symbols = _settings.Watch.Select(w => w.Symbol).ToList();
        var prices = await _client.GetPricesAsync(symbols, ct);

        // KRX 기준가(전일 KRX 종가). 일봉 종가는 NXT 애프터마켓 마지막 체결가라 쓰면 안 된다.
        // 당일 상·하한가에서 역산하고, 실패할 때만 일봉으로 대신한다.
        if (_prevCloseDay != today)
        {
            _prevClose.Clear();
            _prevCloseTried.Clear();
            LastBaseNote = null;
            _prevCloseDay = today;
        }
        // 자정~09:00 에는 토스 랭킹·상하한가가 아직 '어제 장' 기준(=그저께 종가)이라 그대로 쓰면 하루 밀린 기준가가 하루 종일 남는다.
        // 그 시간엔 전 거래일 1분봉의 15:30 종가(KRX 종가)를 쓰고, 09:00 이 지나면 한 번 다시 구한다.
        bool beforeOpen = now < Kst.At(today, 9, 0);
        if (!beforeOpen && _baseFromPreOpen.Count > 0)
        {
            foreach (var sym in _baseFromPreOpen) _prevCloseTried.Remove(sym);
            _baseFromPreOpen.Clear();
        }
        var needBase = symbols.Where(x => !_prevCloseTried.Contains(x)).ToList();
        if (needBase.Count > 0)
        {
            // (새로 추가한 종목만 여기로 온다)

            // 1순위: 랭킹 API의 전일 기준가 (토스 앱 등락률과 같은 기준)
            var ranked = new Dictionary<string, decimal>();
            foreach (var type in new[] { "MARKET_TRADING_AMOUNT", "MARKET_TRADING_VOLUME" })
            {
                if (needBase.All(ranked.ContainsKey)) break;
                try
                {
                    foreach (var (k, v) in await _client.GetBasePricesFromRankingAsync(type, ct))
                        ranked.TryAdd(k, v);
                }
                catch (TossApiException ex)
                {
                    LastBaseNote = "랭킹 조회 실패: " + ex.Message;
                }
            }

            foreach (var sym in needBase)
            {
                _prevCloseTried.Add(sym);
                decimal? basePrice = null;
                if (beforeOpen)
                {
                    try { basePrice = await KrxCloseBeforeAsync(sym, today, ct); }
                    catch (Exception ex) when (ex is TossApiException or HttpRequestException) { Log?.Invoke($"{sym} 전일 KRX 종가 조회 실패: " + ex.Message); }
                    _baseFromPreOpen.Add(sym);
                }
                basePrice ??= ranked.TryGetValue(sym, out var rb) ? rb : null;

                // 2순위: 상·하한가에서 역산 (정확 → 근사)
                if (basePrice is null)
                {
                    try
                    {
                        var (upper, lower) = await _client.GetPriceLimitsAsync(sym, ct);
                        if (upper is { } u && lower is { } l)
                            basePrice = KrxPrice.BaseFromLimits(u, l) ?? KrxPrice.ApproxBaseFromLimits(u, l);
                        if (basePrice is not null)
                            LastBaseNote = "일부 종목 기준가는 상·하한가에서 역산 (오차 0.1% 안팎)";
                    }
                    catch (TossApiException ex)
                    {
                        LastBaseNote = "상·하한가 조회 실패: " + ex.Message;
                    }
                }

                // 3순위: 일봉 (애프터마켓 포함이라 부정확)
                if (basePrice is null)
                {
                    var (bars, _) = await _client.GetCandlesAsync(sym, "1d", 5, null, ct);
                    var prev = bars.Where(b => DateOnly.FromDateTime(b.Time.DateTime) < today)
                                   .OrderByDescending(b => b.Time).FirstOrDefault();
                    basePrice = prev?.Close;
                    LastBaseNote = "기준가를 일봉으로 대신함 (애프터마켓 포함이라 등락률이 앱과 다를 수 있음)";
                }

                if (basePrice is { } b) _prevClose[sym] = b;
            }
        }

        bool newSymbol = symbols.Any(x => !_investors.ContainsKey(x));
        if (now - _investorsAt > TimeSpan.FromMinutes(5) || newSymbol)
        {
            foreach (var sym in newSymbol && now - _investorsAt <= TimeSpan.FromMinutes(5)
                         ? symbols.Where(x => !_investors.ContainsKey(x)).ToList()   // 새 종목만
                         : symbols)
            {
                var fresh = await _client.GetInvestorTradingAsync(sym, 10, ct);
                // 장 마감 뒤 확정치가 나오기 전까지 토스가 오늘 잠정 기록을 빼고 줄 때가 있다 → 마지막으로 본 오늘 잠정치를 유지
                if (fresh.All(r => r.Date != today) && _investors.GetValueOrDefault(sym)?.FirstOrDefault(r => r.Date == today) is { } kept)
                {
                    fresh.Insert(0, kept);
                    Log?.Invoke($"{sym} 오늘 수급 기록이 응답에서 빠져 마지막 잠정치({kept.UpdatedAt:HH:mm})를 유지");
                }
                _investors[sym] = fresh;
                try { _programs[sym] = await _client.GetProgramTradesAsync(sym, 3, ct); }
                catch (TossApiException ex) { Log?.Invoke("프로그램매매 조회 실패: " + ex.Message); }
            }
            _investorsAt = now;
        }

        await RefreshMarketContextAsync(now, today, ct);

        // 200일 최고가는 하루 한 번 (새로 추가한 종목은 그때 한 번)
        if (_high200Day != today) { _high200Tried.Clear(); _high200Day = today; }
        foreach (var sym in symbols.Where(x => !_high200Tried.Contains(x)).ToList())
        {
            _high200Tried.Add(sym);
            {
                try
                {
                    var (bars, _) = await _client.GetCandlesAsync(sym, "1d", 200, null, ct);
                    var past = bars.Where(b => DateOnly.FromDateTime(b.Time.DateTime) < today).ToList();
                    if (past.Count > 0) _high200[sym] = past.Max(b => b.High);
                    _daily[sym] = past.OrderBy(b => b.Time).ToList();
                }
                catch (TossApiException ex) { Log?.Invoke("일봉 200개 조회 실패: " + ex.Message); }
            }
        }

        // 장중에는 5분, 그 외에는 30분마다 계좌 갱신 (ACCOUNT 그룹 한도가 초당 1회라 넉넉함)
        var t = TimeOnly.FromDateTime(now.DateTime);
        var accountEvery = _market.IsOpen && t >= new TimeOnly(9, 0) && t < new TimeOnly(15, 35)
            ? TimeSpan.FromMinutes(5)
            : TimeSpan.FromMinutes(30);

        if (_settings.UseAccount && now - _accountAt > accountEvery)
        {
            try
            {
                if (!_accountResolved)
                {
                    _accountSeq = await _client.GetBrokerageAccountSeqAsync(ct);
                    _accountResolved = true;
                }
                if (_accountSeq is { } seq)
                {
                    var cash = await _client.GetCashBuyingPowerKrwAsync(seq, ct);

                    // 분할 매수 회차용 체결 내역 (최근 1년). 실패해도 나머지는 계속.
                    foreach (var sym in symbols)
                    {
                        try { _fills[sym] = await _client.GetFillsAsync(seq, sym, today.AddYears(-1), ct); }
                        catch (TossApiException ex) { Log?.Invoke($"체결 내역 조회 실패 {sym}: {ex.Message}"); }
                    }
                    var (items, mv, pl, daily, plPct, dailyPct) = await _client.GetHoldingsOverviewAsync(seq, ct);

                    decimal? usdKrw = _account?.UsdKrw;
                    if (items.Any(i => i.IsUsd))
                    {
                        try { usdKrw = await _client.GetUsdKrwAsync(ct); }
                        catch (TossApiException) { /* 직전 환율 유지. 없으면 화면에서 환산 불가 표시 */ }
                    }

                    _account = new AccountSummary
                    {
                        At = now,
                        CashKrw = cash,
                        UsdKrw = usdKrw,
                        MarketValue = mv,
                        ProfitLoss = pl,
                        DailyProfitLoss = daily,
                        ProfitPct = plPct,
                        DailyPct = dailyPct,
                        Items = items,
                    };
                }
                LastAccountError = null;
            }
            catch (TossApiException ex)
            {
                // 계좌 권한이 없어도 시세·수급 알림은 계속 돌아가야 한다.
                LastAccountError = ex.Message;
            }
            _accountAt = now;
        }

        bool sessionStarted = now >= Kst.At(today, 9, 0) && _market.IsOpen;
        var result = new List<Snapshot>();

        foreach (var w in _settings.Watch)
        {
            List<Bar> mins;
            if (!sessionStarted)
            {
                mins = new List<Bar>();
            }
            else if (_minutes.TryGetValue(w.Symbol, out var cached)
                     && DateOnly.FromDateTime(cached.at.DateTime) == today
                     && now - cached.at < TimeSpan.FromSeconds(55)
                     && !ForceBars)
            {
                mins = cached.bars;
            }
            else
            {
                mins = await _client.GetTodayMinuteBarsAsync(w.Symbol, today, ct);
                _minutes[w.Symbol] = (now, mins);
            }

            // KRX 정규장 분봉만 (NXT 애프터마켓 시작 전까지). 고가·저가·지지 판정이 애프터마켓에 흔들리지 않게.
            var afterStart = _market.AfterMarketStart is { } am && DateOnly.FromDateTime(am.DateTime) == today
                ? am
                : Kst.At(today, 15, 40);
            mins = mins.Where(b => b.Time < afterStart).ToList();
            var since13 = mins.Where(b => b.Time >= Kst.At(today, 13, 0)).ToList();

            // 15:30 종가 확정 이후에는 KRX 종가로 판정하고, 실시간 가격은 애프터마켓 참고값으로 따로 둔다.
            var live = prices.GetValueOrDefault(w.Symbol);
            if (live > 0) _lastLive[w.Symbol] = live;
            else live = _lastLive.GetValueOrDefault(w.Symbol);
            bool krxClosed = now >= Kst.At(today, 15, 30) && mins.Count > 0;
            bool preOpen = now < Kst.At(today, 9, 0);
            decimal last = krxClosed ? mins[^1].Close : live;
            // 장 전·장 마감 뒤에는 프리·애프터마켓 줄을 항상 보여 준다. 가격이 KRX 종가와 같아도(0원 · 0%) 줄을 없애지 않는다
            // — 같을 때 줄을 지우면 카드 모양이 바뀌어 1초마다 나타났다 사라지는 것처럼 보였다.
            decimal? extended = (krxClosed || preOpen) && live > 0 ? live : null;
            if (preOpen) last = _prevClose.GetValueOrDefault(w.Symbol, live);

            // 장중에는 분봉(최대 1분 묵음)에 5초 실시간 가격을 겹쳐서 고가·13시 이후 저가를 갱신한다.
            bool inSession = sessionStarted && !krxClosed && live > 0;
            decimal? dayHigh = mins.Count > 0 ? mins.Max(b => b.High) : null;
            decimal? dayLow = mins.Count > 0 ? mins.Min(b => b.Low) : null;
            decimal? low13 = since13.Count > 0 ? since13.Min(b => b.Low) : null;
            if (inSession)
            {
                dayHigh = dayHigh is { } h ? Math.Max(h, live) : live;
                dayLow = dayLow is { } l ? Math.Min(l, live) : live;
                if (now >= Kst.At(today, 13, 0))
                    low13 = low13 is { } l13 ? Math.Min(l13, live) : live;
            }

            var catalyst = _settings.AllEvents.FirstOrDefault(e => e.Matches(w.Symbol, today));
            var prevDay = _market.Date == today ? PreviousBusinessDay(today) : today.AddDays(-1);
            var catalystPrev = _settings.AllEvents.FirstOrDefault(e => e.Matches(w.Symbol, prevDay));

            result.Add(new Snapshot
            {
                Symbol = w.Symbol,
                Name = w.Name,
                Now = now,
                Last = last,
                ExtendedPrice = extended,
                PrevClose = _prevClose.GetValueOrDefault(w.Symbol),
                DayOpen = mins.Count > 0 ? mins[0].Open : null,
                DayHigh = dayHigh,
                DayLow = dayLow,
                LowSince13 = low13,
                MinuteBars = mins,
                Investors = _investors.GetValueOrDefault(w.Symbol) ?? new List<InvestorDay>(),
                CatalystToday = catalyst?.Title,
                CatalystDays = _settings.AllEvents.Where(e => e.ParsedDate is not null && (string.IsNullOrWhiteSpace(e.Symbol) || e.Symbol == "*" || e.Symbol == w.Symbol))
                    .Select(e => e.ParsedDate!.Value).ToHashSet(),
                Budget = w.Budget,
                CatalystPrevDay = catalystPrev?.Title,
                Programs = _programs.GetValueOrDefault(w.Symbol) ?? new List<ProgramDay>(),
                High200 = _high200.TryGetValue(w.Symbol, out var h200) ? h200 : null,
                DailyBars = _daily.GetValueOrDefault(w.Symbol) ?? new List<Bar>(),
                Fills = _fills.GetValueOrDefault(w.Symbol) ?? new List<Fill>(),
                MarketContext = _marketCtx,
                Market = _market,
                Holding = _account?.Items.FirstOrDefault(h => h.Symbol == w.Symbol),
                CashBuyingPower = _account?.CashKrw,
            });
        }

        ForceBars = false;
        return result;
    }

    /// <summary>진단용 로그 콜백 (앱이 연결)</summary>
    public Action<string>? Log { get; set; }

    public MarketContext? MarketContext => _marketCtx;

    /// <summary>코스피 지수는 1분, 시장 투자자별 매매대금은 5분마다.</summary>
    private async Task RefreshMarketContextAsync(DateTimeOffset now, DateOnly today, CancellationToken ct)
    {
        if (now - _marketCtxAt < TimeSpan.FromMinutes(1) && _marketCtx is not null) return;
        _marketCtxAt = now;
        try
        {
            if (_kospiPrevDay != today)
            {
                var bars = await _client.GetIndicatorDailyAsync("KOSPI", 3, ct);
                _kospiPrev = bars.Where(b => DateOnly.FromDateTime(b.Time.DateTime) < today)
                                 .OrderByDescending(b => b.Time).FirstOrDefault()?.Close;
                _kospiPrevDay = today;
            }
            var last = await _client.GetIndicatorLastAsync("KOSPI", ct);

            if (now - _flowAt > TimeSpan.FromMinutes(5) || _flow is null)
            {
                var flows = await _client.GetMarketFlowAsync("KOSPI", 2, ct);
                _flow = flows.FirstOrDefault(f => f.Date == today);
                _flowAt = now;
            }

            await RefreshUsAsync(now, TimeSpan.FromMinutes(5), ct);
            _marketCtx = WithUs(new MarketContext { KospiLast = last, KospiPrevClose = _kospiPrev, TodayFlow = _flow }, now);
        }
        catch (TossApiException ex)
        {
            // 시장 맥락은 태그용이라 실패해도 판정은 계속 돈다
            Log?.Invoke("시장 지표 조회 실패: " + ex.Message);
        }
    }

    private readonly HashSet<string> _baseFromPreOpen = new();

    /// <summary>today 이전 마지막 거래일의 KRX 종가 = 그날 1분봉 중 15:30 이하 마지막 봉 (애프터마켓 제외).</summary>
    private async Task<decimal?> KrxCloseBeforeAsync(string sym, DateOnly today, CancellationToken ct)
    {
        var (recent, _) = await _client.GetCandlesAsync(sym, "1m", 200, Kst.At(today, 0, 0), ct);
        if (recent.Count == 0) return null;
        var day = recent.Max(b => DateOnly.FromDateTime(b.Time.DateTime));
        var regular = recent.Where(b => DateOnly.FromDateTime(b.Time.DateTime) == day && ChartIntervals.IsRegularSession(b.Time)).ToList();
        if (regular.Count == 0)
        {
            // 애프터마켓 봉이 200개를 넘으면 15:30 근처를 따로 받는다
            (regular, _) = await _client.GetCandlesAsync(sym, "1m", 30, Kst.At(day, 15, 30), ct);
            regular = regular.Where(b => DateOnly.FromDateTime(b.Time.DateTime) == day && ChartIntervals.IsRegularSession(b.Time)).ToList();
        }
        var close = regular.OrderBy(b => b.Time).LastOrDefault()?.Close;
        if (close is not null) Log?.Invoke($"{sym} 장 전 기준가 = {day:MM/dd} KRX 종가 {close:N0}");
        return close;
    }

    /// <summary>미국 반도체 일봉은 30분마다(장 마감 직후엔 바로), 열린 동안 현재가는 5분마다. 실패해도 나머지는 계속.</summary>
    private MarketContext WithUs(MarketContext kr, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.ToKst().DateTime);
        return new MarketContext
        {
            KospiLast = kr.KospiLast, KospiPrevClose = kr.KospiPrevClose, TodayFlow = kr.TodayFlow,
            UsOvernight = UsSemis.All.Select(x => _usBars.TryGetValue(x.Symbol, out var b) ? UsSemis.Overnight(x.Symbol, b, today, now) : null).OfType<UsMove>().ToList(),
            UsLive = UsSemis.All.Select(x => _usBars.TryGetValue(x.Symbol, out var b) ? UsSemis.Live(x.Symbol, b, _usPrices.TryGetValue(x.Symbol, out var p) ? p : null, now) : null).OfType<UsMove>().ToList(),
        };
    }

    /// <summary>미국장이 열려 있는 동안 미장 시세만 따로 갱신 (국장 갱신 주기와 무관). 한국 지표는 마지막 값 유지.</summary>
    public async Task RefreshUsLiveAsync(CancellationToken ct = default)
    {
        var now = Kst.Now;
        await RefreshUsAsync(now, TimeSpan.Zero, ct);
        _marketCtx = WithUs(_marketCtx ?? new MarketContext(), now);
    }

    private async Task RefreshUsAsync(DateTimeOffset now, TimeSpan pricesEvery, CancellationToken ct)
    {
        bool open = UsSemis.IsOpen(now);
        bool justClosed = !open && _usBarsAt < now && UsSemis.IsOpen(_usBarsAt);
        if (now - _usBarsAt > TimeSpan.FromMinutes(30) || justClosed || _usBars.Count == 0)
        {
            _usBarsAt = now;
            foreach (var (sym, _) in UsSemis.All)
            {
                try
                {
                    var bars = (await _client.GetCandlesAsync(sym, "1d", 10, null, ct)).bars;
                    _usBars[sym] = bars;
                    // 미국 봉 시각 기준이 문서에 없어 하루 한 번 로그로 남긴다 (날짜가 맞는지 확인용)
                    if (bars.Count > 0 && _usLogged.Add($"{sym}|{Kst.Today}"))
                    {
                        var ses = UsSemis.Sessions(bars);
                        Log?.Invoke($"미국 {sym} 일봉: 최신 봉 시각 {bars.Max(b => b.Time):yyyy-MM-dd HH:mm zzz} → 거래일 {ses[^1].date:yyyy-MM-dd} 종가 {ses[^1].bar.Close}" +
                                    (ses.Count > 1 ? $" · 전 거래일 {ses[^2].date:yyyy-MM-dd} 종가 {ses[^2].bar.Close}" : ""));
                    }
                }
                catch (Exception ex) when (ex is TossApiException or HttpRequestException or System.Text.Json.JsonException or FormatException)
                { Log?.Invoke($"미국 {sym} 일봉 조회 실패: {ex.Message}"); }
            }
        }
        if (open && now - _usPricesAt >= pricesEvery)
        {
            _usPricesAt = now;
            try
            {
                foreach (var (k, v) in await _client.GetPricesAsync(UsSemis.All.Select(x => x.Symbol), ct))
                    if (v > 0) _usPrices[k] = v;
            }
            catch (Exception ex) when (ex is TossApiException or HttpRequestException or System.Text.Json.JsonException)
            { Log?.Invoke("미국 현재가 조회 실패: " + ex.Message); }
        }
    }

    /// <summary>주말만 건너뛴 직전 평일. 공휴일은 장 운영 API로 따로 거르지 않는다 (태그용이라 충분).</summary>
    private static DateOnly PreviousBusinessDay(DateOnly d)
    {
        var p = d.AddDays(-1);
        while (p.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) p = p.AddDays(-1);
        return p;
    }

    /// <summary>'지금 새로고침' 시 분봉 캐시를 무시하고 새로 받는다.</summary>
    public bool ForceBars { get; set; }

    public MarketDay? Market => _market;

    /// <summary>마지막으로 조회한 원화 매수가능금액. 계좌 조회를 끄거나 실패하면 null.</summary>
    public decimal? CashBuyingPower => _account?.CashKrw;

    /// <summary>마지막으로 조회한 계좌 요약. 계좌 조회를 끄거나 한 번도 성공하지 못했으면 null.</summary>
    public AccountSummary? Account => _account;

    /// <summary>다음 갱신 때 계좌를 바로 다시 조회하게 한다 ('지금 새로고침' 버튼용).</summary>
    public void ExpireAccount() => _accountAt = DateTimeOffset.MinValue;
}
