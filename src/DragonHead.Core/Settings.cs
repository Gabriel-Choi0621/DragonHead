using System.Text.Json.Serialization;

namespace DragonHead.Core;

public sealed class AppSettings
{
    /// <summary>토스증권 WTS > 설정 > Open API 에서 발급받은 client_id</summary>
    public string ClientId { get; set; } = "";

    /// <summary>평문 client_secret. 앱이 처음 읽을 때 암호화해서 ProtectedSecret 으로 옮기고 이 칸은 비운다.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>Windows DPAPI(현재 사용자)로 암호화된 client_secret. 직접 편집하지 말 것.</summary>
    public string ProtectedSecret { get; set; } = "";

    /// <summary>계좌 조회(예수금·보유 종목) 사용 여부. 끄면 시세·수급만 사용한다.</summary>
    public bool UseAccount { get; set; } = true;

    /// <summary>상황판을 열 때 계좌 금액을 가린 상태로 시작할지. 카드의 '보기/숨기기'로 언제든 바꿀 수 있다.</summary>
    public bool HideAmounts { get; set; } = false;

    public List<WatchItem> Watch { get; set; } = new()
    {
        new WatchItem { Symbol = "005930", Name = "삼성전자" },
        new WatchItem { Symbol = "000660", Name = "SK하이닉스" },
    };

    /// <summary>
    /// '명분' 이벤트 목록. 실적 발표, 대규모 주주환원, HBM 공급 오피셜 등.
    /// 이 날짜가 아닌 날에는 신규 매수 판정(눌림 매수)을 내리지 않는다.
    /// </summary>
    public List<CatalystEvent> Events { get; set; } = new();

    /// <summary>
    /// DART(금감원 전자공시) 인증키. opendart.fss.or.kr 에서 무료 발급.
    /// 넣으면 실적·IR·자사주·소각 공시를 명분 이벤트로 자동 등록하고, 백테스트의 과거 명분일도 여기서 뽑는다.
    /// </summary>
    public string DartApiKey { get; set; } = "";

    /// <summary>자동 등록할 공시 종류. Earnings, InvestorRelations, Buyback, Cancellation, Dividend</summary>
    public List<CatalystKind> DartKinds { get; set; } = new()
    {
        CatalystKind.Earnings, CatalystKind.InvestorRelations, CatalystKind.Buyback, CatalystKind.Cancellation,
    };

    /// <summary>DART 에서 자동으로 찾은 명분 (저장하지 않음, 앱이 따로 캐시)</summary>
    [JsonIgnore]
    public List<CatalystEvent> AutoEvents { get; set; } = new();

    /// <summary>수동 + 자동. 같은 날·같은 종목이면 수동이 먼저 매칭된다.</summary>
    [JsonIgnore]
    public IEnumerable<CatalystEvent> AllEvents => Events.Concat(AutoEvents);

    public StrategySettings Strategy { get; set; } = new();

    public NotifySettings Notify { get; set; } = new();
}

/// <summary>상황판 창 상태. settings.json 과 섞이지 않게 window.json 에 앱이 따로 저장한다.</summary>
public sealed class WindowSettings
{
    /// <summary>항상 위에 고정</summary>
    public bool Pinned { get; set; } = true;
    /// <summary>불투명도 (0.4 ~ 1.0)</summary>
    public double Opacity { get; set; } = 1.0;
    /// <summary>마지막 위치·크기. 비어 있으면 화면 오른쪽 아래.</summary>
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    /// <summary>상황판의 '내 계좌' 카드를 한 줄로 접어 둘지</summary>
    public bool AccountCollapsed { get; set; }
    /// <summary>상황판 시장 카드 탭: KR(국장) / US(미장)</summary>
    public string MarketTab { get; set; } = "KR";
    /// <summary>종목 창 크기 (마지막으로 바꾼 크기를 다음에도)</summary>
    public int? StockWidth { get; set; }
    public int? StockHeight { get; set; }
    /// <summary>종목 창 차트의 마지막 봉 단위 (Min1, Min5, Min10, Day, Week, Month, Year)</summary>
    public string? ChartInterval { get; set; }
}

public sealed class WatchItem
{
    public string Symbol { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>이 종목에 넣을 총예산 (원). 비우면 Strategy.TotalBudget, 0이면 매수 안내 안 함.</summary>
    public decimal? Budget { get; set; }
}

/// <summary>예산을 '기본 몫(정기 분할)'과 '기회 몫(명분일 눌림)'으로 나누는 프리셋</summary>
public static class BudgetPresets
{
    public sealed record Preset(string Name, decimal CoreRatio, string Upside, string Downside, string Fits);

    public static readonly Preset[] All =
    {
        new("수비형", 0.25m, "많이 놓침", "현금이 많아 덜 아픔", "물리는 게 제일 싫은 사람"),
        new("균형형", 0.50m, "절반은 따라감", "절반만 노출", "아직 잘 모르겠는 사람"),
        new("공격형", 0.75m, "거의 따라감", "거의 다 맞음", "반도체 장기 상승을 믿는 사람"),
    };

    public const string Question = "현금 들고 기다리다가 30% 올라가 버리면 vs 다 사놨는데 30% 빠지면, 어느 쪽이 더 괴로운가? " +
                                   "앞쪽이면 공격형, 뒤쪽이면 수비형, 비슷하면 균형형.";

    public static string NameOf(decimal coreRatio) =>
        All.FirstOrDefault(p => p.CoreRatio == coreRatio)?.Name ?? $"직접 설정 (기본 {coreRatio * 100:0}%)";
}

public sealed class CatalystEvent
{
    /// <summary>yyyy-MM-dd</summary>
    public string Date { get; set; } = "";
    /// <summary>종목 코드. 비우거나 "*" 이면 감시 종목 전체에 적용.</summary>
    public string Symbol { get; set; } = "*";
    public string Title { get; set; } = "";

    /// <summary>"DART" 면 자동 등록. 비어 있으면 직접 입력.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }

    /// <summary>공시 원문 링크 (DART 자동 등록일 때)</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonIgnore]
    public DateOnly? ParsedDate => DateOnly.TryParse(Date, out var d) ? d : null;

    public bool Matches(string symbol, DateOnly day) =>
        ParsedDate == day && (string.IsNullOrWhiteSpace(Symbol) || Symbol == "*" || Symbol == symbol);
}

public sealed class StrategySettings
{
    /// <summary>총 투자 예정 금액 (원)</summary>
    public decimal TotalBudget { get; set; } = 4_000_000m;

    /// <summary>(예전 설정) 1회 투입 비율. 지금은 CoreTranches·OppTranches 를 쓴다.</summary>
    public decimal TrancheRatio { get; set; } = 0.25m;

    /// <summary>예산 중 기본 몫(정기 분할) 비율. 나머지는 기회 몫(명분일 눌림). 0.25 수비형 / 0.5 균형형 / 0.75 공격형</summary>
    public decimal CoreRatio { get; set; } = 0.5m;
    /// <summary>기본 몫 최소 간격 (거래일). 이 간격이 지나고 '평소 판단'이 괜찮다고 할 때 산다. 5 = 1주, 10 = 2주, 20 = 한 달</summary>
    public int CoreIntervalDays { get; set; } = 5;
    /// <summary>기본 몫 매수 창 (거래일). 최소 간격이 지난 뒤 이 기간 안에 하락 마감일을 기다리고, 창 마지막 날엔 판단과 상관없이 산다.</summary>
    public int CoreWindowDays { get; set; } = 5;
    /// <summary>기본 몫을 몇 번에 나눠 살지</summary>
    public int CoreTranches { get; set; } = 4;
    /// <summary>기회 몫을 몇 번에 나눠 살지</summary>
    public int OppTranches { get; set; } = 4;

    /// <summary>시초가 갭상승 경계 기준 (전일 종가 대비 %)</summary>
    public decimal GapWarnPct { get; set; } = 1.5m;

    /// <summary>'급등'으로 보는 당일 상승률 (%)</summary>
    public decimal RallyPct { get; set; } = 2.0m;

    /// <summary>현재가가 당일 고가 대비 이 % 이내면 '고점 부근'</summary>
    public decimal NearHighPct { get; set; } = 1.0m;

    /// <summary>13시 이후 저가가 당일 고가 대비 이 % 이내로 유지되면 '오후에 밀리지 않음'</summary>
    public decimal AfternoonHoldPct { get; set; } = 2.0m;

    /// <summary>외국인 연속 순매수 최소 일수 (오늘 잠정치 포함)</summary>
    public int MinForeignStreak { get; set; } = 3;

    /// <summary>'회피'로 볼 하락폭 (%). 이만큼 이상 빠지고 외국인이 팔면 관망보다 한 단계 아래.</summary>
    public decimal AvoidDropPct { get; set; } = 2.0m;
    /// <summary>명분일 이만큼(%) 이상 빠졌는데 코스피는 보합·상승이면 '쇼크 의심'</summary>
    public decimal ShockDropPct { get; set; } = 5.0m;
    /// <summary>오늘 거래량이 20일 평균의 이 배수 이상이면 눌림 '강한 후보'</summary>
    public decimal StrongVolumeRatio { get; set; } = 1.5m;
}

public sealed class NotifySettings
{
    /// <summary>장 시작 전 브레이크 알림 (HH:mm)</summary>
    public string MorningBrake { get; set; } = "08:55";
    /// <summary>1차 판정 알림</summary>
    public string FirstVerdict { get; set; } = "14:50";
    /// <summary>최종 판정 알림 (종가 동시호가 직전)</summary>
    public string FinalVerdict { get; set; } = "15:15";
    /// <summary>
    /// 장중 중간 점검 간격(분). 0 이면 끔. CheckFrom~CheckTo 사이에 이 간격마다 종목별 현재 상태(등락·수급·판정·태그)를 알린다.
    /// 14:50·15:15 판정은 그대로 따로 뜬다.
    /// </summary>
    public int CheckEveryMinutes { get; set; } = 30;
    public string CheckFrom { get; set; } = "10:00";
    public string CheckTo { get; set; } = "14:30";
    /// <summary>토스트 자동 닫힘 (초). '눌림 매수'·'쇼크 의심' 알림은 클릭할 때까지 유지된다.</summary>
    public int ToastSeconds { get; set; } = 20;

    public static TimeOnly Parse(string hhmm, TimeOnly fallback) =>
        TimeOnly.TryParse(hhmm, out var t) ? t : fallback;
}
