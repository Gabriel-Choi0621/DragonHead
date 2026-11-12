namespace DragonHead.Core;

public sealed record Bar(DateTimeOffset Time, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

/// <summary>체결 한 건 (틱)</summary>
public sealed record Trade(DateTimeOffset Time, decimal Price, decimal Volume);

/// <summary>
/// 일별 투자자 매매동향 (주식 수 기준).
/// 당일 기록은 장중 잠정치이며 외국인·기관 합계만 제공된다. 금융투자 세부(FinancialInvestmentNet)는 당일 저녁 확정 후에만 값이 있다.
/// </summary>
public sealed record InvestorDay(
    DateOnly Date,
    long ForeignerNet,
    long InstitutionNet,
    long? FinancialInvestmentNet,
    long? IndividualNet,
    DateTimeOffset UpdatedAt);

/// <summary>종목 일별 프로그램매매 (KRX만, 주식 수). 당일 기록은 장중 갱신.</summary>
public sealed record ProgramDay(DateOnly Date, long NonArbitrageNet, long ArbitrageNet)
{
    public long TotalNet => NonArbitrageNet + ArbitrageNet;
}

/// <summary>코스피 전체 투자자별 순매수 금액(원). 당일은 잠정치.</summary>
public sealed record MarketFlow(DateOnly Date, decimal ForeignNet, decimal InstitutionNet, decimal IndividualNet, DateTimeOffset UpdatedAt);

/// <summary>시장 전체 맥락: 코스피 지수와 투자자별 매매대금.</summary>
public sealed class MarketContext
{
    public decimal? KospiLast { get; init; }
    public decimal? KospiPrevClose { get; init; }
    public MarketFlow? TodayFlow { get; init; }
    /// <summary>오늘 한국장 직전에 끝난 미국 정규장 (반도체 ETF·마이크론·엔비디아)</summary>
    public IReadOnlyList<UsMove> UsOvernight { get; init; } = Array.Empty<UsMove>();
    /// <summary>지금 열려 있는 미국장 (한국 밤 시간)</summary>
    public IReadOnlyList<UsMove> UsLive { get; init; } = Array.Empty<UsMove>();

    public decimal? KospiChangePct =>
        KospiLast is { } l && KospiPrevClose is > 0 ? Math.Round((l / KospiPrevClose.Value - 1m) * 100m, 2) : null;
}

public sealed record MarketDay(
    DateOnly Date,
    bool IsOpen,
    DateTimeOffset? RegularStart,
    DateTimeOffset? ClosingAuctionStart,
    DateTimeOffset? RegularEnd,
    DateTimeOffset? AfterMarketStart = null);

/// <summary>보유 종목 1건. 금액은 거래 통화(Currency) 기준. 수익률은 소수비율이 아니라 % 값.</summary>
/// <summary>체결된 주문 한 건 (부분 체결 포함)</summary>
public sealed record Fill(string Symbol, bool IsBuy, decimal Quantity, decimal Price, DateTimeOffset FilledAt)
{
    public DateOnly Date => DateOnly.FromDateTime(FilledAt.ToKst().DateTime);
}

public sealed record Holding(
    string Symbol,
    string Name,
    string Currency,
    decimal Quantity,
    decimal AveragePrice,
    decimal LastPrice,
    decimal PurchaseAmount,
    decimal MarketValue,
    decimal ProfitLoss,
    decimal ProfitPct,
    decimal DailyProfitLoss,
    decimal DailyPct)
{
    public bool IsUsd => Currency == "USD";
}

/// <summary>통화별 합산 금액. 원화·달러를 섞어 더하지 않는다 (토스 API 규칙과 동일).</summary>
public sealed record Money(decimal Krw, decimal? Usd)
{
    public decimal ToKrw(decimal? usdKrw) => Krw + (Usd ?? 0m) * (usdKrw ?? 0m);
}

/// <summary>계좌 요약. 비중·총자산은 원화 환산 기준.</summary>
public sealed class AccountSummary
{
    public required DateTimeOffset At { get; init; }
    public required decimal CashKrw { get; init; }
    /// <summary>1달러 = ?원 (매매기준율). 달러 종목이 없거나 조회 실패면 null.</summary>
    public decimal? UsdKrw { get; init; }
    public required Money MarketValue { get; init; }
    public required Money ProfitLoss { get; init; }
    public required Money DailyProfitLoss { get; init; }
    /// <summary>전체 손익률 (%) — 토스가 원화 환산으로 계산해 준 값</summary>
    public required decimal ProfitPct { get; init; }
    public required decimal DailyPct { get; init; }
    public required IReadOnlyList<Holding> Items { get; init; }

    public bool HasUsd => Items.Any(i => i.IsUsd);

    /// <summary>달러 종목이 있는데 환율이 없으면 원화 환산값을 믿을 수 없다.</summary>
    public bool KrwConversionReliable => !HasUsd || UsdKrw is > 0;

    public decimal StocksKrw => MarketValue.ToKrw(UsdKrw);
    public decimal TotalKrw => CashKrw + StocksKrw;

    public decimal ItemKrw(Holding h) => h.IsUsd ? h.MarketValue * (UsdKrw ?? 0m) : h.MarketValue;

    /// <summary>총자산(예수금 포함) 대비 비중 %</summary>
    public decimal WeightPct(Holding h) => TotalKrw > 0 ? ItemKrw(h) / TotalKrw * 100m : 0m;

    public decimal CashWeightPct => TotalKrw > 0 ? CashKrw / TotalKrw * 100m : 0m;
}

/// <summary>한 종목에 대한 판정 입력 묶음.</summary>
public sealed class Snapshot
{
    public required string Symbol { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset Now { get; init; }

    /// <summary>판정용 가격. 장중엔 실시간, 15:30 이후엔 KRX 종가.</summary>
    public decimal Last { get; init; }
    /// <summary>KRX 기준가 (전일 KRX 종가)</summary>
    public decimal PrevClose { get; init; }

    /// <summary>KRX 정규장 밖(NXT 프리·애프터마켓) 실시간 가격. 참고 표시용, 판정에는 안 씀.</summary>
    public decimal? ExtendedPrice { get; init; }

    /// <summary>09:00 이후 첫 1분봉 시가. 아직 장 시작 전이면 null.</summary>
    public decimal? DayOpen { get; init; }
    public decimal? DayHigh { get; init; }
    public decimal? DayLow { get; init; }

    /// <summary>13:00 이후 저가. 13시 전이면 null.</summary>
    public decimal? LowSince13 { get; init; }

    /// <summary>최신순 투자자 동향. 첫 항목이 오늘일 수도, 아닐 수도 있다.</summary>
    public IReadOnlyList<InvestorDay> Investors { get; init; } = Array.Empty<InvestorDay>();

    /// <summary>오늘 이 종목에 걸린 '명분' 이벤트 (실적 발표 등). 사용자가 설정 파일에 등록한다.</summary>
    public string? CatalystToday { get; init; }
    /// <summary>이 종목의 명분일 전부 (과거 매수가 기본 몫인지 기회 몫인지 가르고, 다음 명분일을 알려 주는 데 쓴다)</summary>
    public IReadOnlyCollection<DateOnly> CatalystDays { get; init; } = Array.Empty<DateOnly>();
    /// <summary>이 종목 예산 (원). null 이면 Strategy.TotalBudget.</summary>
    public decimal? Budget { get; init; }

    /// <summary>직전 거래일의 명분 이벤트 (다음날 반응 태그용)</summary>
    public string? CatalystPrevDay { get; init; }

    /// <summary>최신순 프로그램매매</summary>
    public IReadOnlyList<ProgramDay> Programs { get; init; } = Array.Empty<ProgramDay>();

    /// <summary>오늘을 제외한 최근 200거래일 최고가 (일봉 기준)</summary>
    public decimal? High200 { get; init; }

    /// <summary>오늘을 뺀 최근 일봉 (오래된 것 → 최신). 이동평균·추세 판단용.</summary>
    public IReadOnlyList<Bar> DailyBars { get; init; } = Array.Empty<Bar>();

    /// <summary>이 종목의 체결 내역 (오래된 것 → 최신). 분할 매수 회차 계산용. 계좌 조회를 끄면 비어 있음.</summary>
    public IReadOnlyList<Fill> Fills { get; init; } = Array.Empty<Fill>();

    /// <summary>오늘 KRX 정규장 1분봉 (시간순). 카드 미니 차트용.</summary>
    public IReadOnlyList<Bar> MinuteBars { get; init; } = Array.Empty<Bar>();

    public MarketContext? MarketContext { get; init; }

    public MarketDay? Market { get; init; }
    public Holding? Holding { get; init; }
    public decimal? CashBuyingPower { get; init; }
}
