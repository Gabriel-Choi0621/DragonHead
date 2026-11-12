namespace DragonHead.Core;

/// <summary>차트·리포트 공통 계산</summary>
public static class ChartMath
{
    /// <summary>단순 이동평균. 데이터가 모자란 앞부분은 null.</summary>
    public static decimal?[] Ma(IReadOnlyList<decimal> closes, int n)
    {
        var r = new decimal?[closes.Count];
        decimal sum = 0;
        for (int i = 0; i < closes.Count; i++)
        {
            sum += closes[i];
            if (i >= n) sum -= closes[i - n];
            if (i >= n - 1) r[i] = sum / n;
        }
        return r;
    }

    public static decimal? Pct(decimal? a, decimal? b) =>
        a is { } x && b is { } y && y != 0 ? Math.Round((x / y - 1m) * 100m, 2) : null;

    /// <summary>최근 n일 일간 등락률의 표준편차 (%)</summary>
    public static decimal? Volatility(IReadOnlyList<decimal> closes, int n = 20)
    {
        if (closes.Count < n + 1) return null;
        var rets = new List<double>();
        for (int i = closes.Count - n; i < closes.Count; i++)
            rets.Add((double)(closes[i] / closes[i - 1] - 1m) * 100);
        var avg = rets.Average();
        return (decimal)Math.Round(Math.Sqrt(rets.Sum(x => (x - avg) * (x - avg)) / rets.Count), 2);
    }

    /// <summary>보기 좋은 눈금 간격 (1·2·5 × 10^k)</summary>
    public static decimal NiceStep(decimal range, int targetTicks = 5)
    {
        if (range <= 0) return 1;
        var raw = (double)range / targetTicks;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var norm = raw / mag;
        var step = norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10;
        return (decimal)(step * mag);
    }
}

/// <summary>추세 요약 (리포트·차트 공통)</summary>
public sealed class TrendSummary
{
    public decimal Last { get; init; }
    public decimal? Ma20 { get; init; }
    public decimal? Ma60 { get; init; }
    public decimal? Ma20Slope5 { get; init; }      // 20일선의 5거래일 변화율 %
    public decimal? Ret5 { get; init; }
    public decimal? Ret20 { get; init; }
    public decimal? Ret60 { get; init; }
    public decimal? FromHigh20 { get; init; }
    public decimal? FromHigh200 { get; init; }
    public decimal? Volatility20 { get; init; }
    public decimal? VolumeRatio { get; init; }     // 오늘(마지막 봉) 거래량 ÷ 이전 20일 평균
    public required string Label { get; init; }
    public required string Explain { get; init; }

    public static TrendSummary From(IReadOnlyList<Bar> dailyOldToNew, decimal last)
    {
        var closes = dailyOldToNew.Select(b => b.Close).ToList();
        if (closes.Count > 0) closes[^1] = last > 0 ? last : closes[^1];
        var ma20 = ChartMath.Ma(closes, 20);
        var ma60 = ChartMath.Ma(closes, 60);
        decimal? m20 = ma20.Length > 0 ? ma20[^1] : null;
        decimal? m60 = ma60.Length > 0 ? ma60[^1] : null;
        decimal? m20p = ma20.Length > 5 ? ma20[^6] : null;
        decimal? Ret(int n) => closes.Count > n ? ChartMath.Pct(closes[^1], closes[^(n + 1)]) : null;
        decimal? FromHigh(int n) => closes.Count >= 2
            ? ChartMath.Pct(closes[^1], dailyOldToNew.Skip(Math.Max(0, dailyOldToNew.Count - n)).Max(b => b.High))
            : null;
        decimal? volRatio = null;
        if (dailyOldToNew.Count > 21)
        {
            var avg = dailyOldToNew.Skip(dailyOldToNew.Count - 21).Take(20).Average(b => b.Volume);
            if (avg > 0) volRatio = Math.Round(dailyOldToNew[^1].Volume / avg, 2);
        }

        var slope = ChartMath.Pct(m20, m20p);
        string label, explain;
        if (m20 is null || m60 is null)
        {
            label = "판단 보류";
            explain = "일봉이 60개보다 적어 이동평균을 다 계산하지 못했습니다.";
        }
        else if (last >= m20 && m20 >= m60 && slope > 0)
        {
            label = "상승 추세";
            explain = "주가가 20일선 위, 20일선이 60일선 위에 있고 20일선이 올라가는 중입니다. 정배열이라고 부르는 모양이에요.";
        }
        else if (last < m20 && last >= m60)
        {
            label = "조정 중";
            explain = "단기 평균(20일선) 아래로 내려왔지만 중기 평균(60일선) 위는 지키고 있습니다. 추세가 꺾였다기보다 쉬어 가는 구간일 수 있어요.";
        }
        else if (last < m60 && m20 < m60)
        {
            label = "하락 추세";
            explain = "주가가 60일선 아래이고 20일선도 60일선 아래입니다. 역배열이라고 부르며, 바닥을 알 수 없는 구간이라 회차를 나눠 천천히 사는 게 맞아요 (매수를 막지는 않음).";
        }
        else
        {
            label = "방향 탐색";
            explain = "선들이 엉켜 있어 뚜렷한 방향이 없습니다. 20일선이 어느 쪽으로 기우는지 지켜보세요.";
        }

        return new TrendSummary
        {
            Last = last, Ma20 = m20, Ma60 = m60, Ma20Slope5 = slope,
            Ret5 = Ret(5), Ret20 = Ret(20), Ret60 = Ret(60),
            FromHigh20 = FromHigh(20), FromHigh200 = FromHigh(200),
            Volatility20 = ChartMath.Volatility(closes), VolumeRatio = volRatio,
            Label = label, Explain = explain,
        };
    }
}

/// <summary>
/// 차트 초보자용 설명. 앱 차트의 마우스 설명과 리포트의 '차트 읽는 법'이 같은 문장을 쓴다.
/// </summary>
public static class ChartHelp
{
    public const string Candle =
        "캔들(봉) 하나가 하루입니다. 빨간 봉은 시작가보다 끝난 가격이 높은 날(상승), 파란 봉은 낮은 날(하락)이에요. " +
        "굵은 몸통은 시가~종가, 위아래 가는 꼬리는 그날의 최고가·최저가입니다. 위꼬리가 길면 올랐다가 밀린 날이에요.";

    public const string Ma20 =
        "20일선: 최근 20거래일(약 한 달) 종가의 평균. 주가가 이 선 위에 있고 선이 위로 향하면 단기 상승 추세입니다. " +
        "평소 판단의 '추세' 체크와 과열(20일선 +5% 이상) 기준이 이 선이에요.";

    public const string Ma60 =
        "60일선: 약 3개월 평균. 중기 추세의 바닥 역할을 합니다. 주가가 20일선 아래로 내려와도 이 선 위면 '건전한 조정', " +
        "이 선마저 깨지면 '추세 이탈'이지만, 매수를 막지는 않고 회차를 나눠 천천히 사라는 주의만 붙입니다 (회복을 기다리면 한참 오른 뒤에 사게 됨).";

    public const string BaseLine =
        "기준가(점선): 전 거래일 종가. 오늘 등락률(+/−%)은 이 가격과 비교한 값입니다.";

    public const string AvgPrice =
        "평단(점선): 내가 산 평균 가격. 주가가 이 선 위면 수익, 아래면 손실 구간입니다.";

    public const string Volume =
        "거래량: 그날 거래된 주식 수. 거래량이 크게 늘면서 오르면 힘이 실린 상승, 거래량 없이 오르면 쉽게 꺾일 수 있는 상승으로 봅니다.";

    public const string BuyMark = "▲ 매수: 내가 산 날 (토스 체결 내역 기준).";

    public const string CatalystMark = "◆ 명분: 실적 발표·IR·자사주 같은 명분이 있던 날. 그날과 그 뒤 며칠의 거래량·방향을 보면 시장이 그 소식을 어떻게 받아들였는지 알 수 있어요.";

    public const string Afternoon =
        "회색 영역: 13시 이후. 최종 판단(14:50)이 가까워지는 구간이에요. 명분일이면 기준가 아래 마감인지, 명분 없는 날이면 하락·보합 마감인지가 판단을 가릅니다.";

    public const string DayHigh = "당일 고가(점선): 오늘 가장 높았던 가격. 고가에서 얼마나 밀렸는지 보는 기준이에요.";

    /// <summary>'?' 버튼: 일봉에서 중점으로 볼 것</summary>
    public static readonly string[] DailyFocus =
    {
        "① 주가가 20일선 위에 있나, 20일선이 위를 향하나 → 단기 추세 (20일선보다 5% 넘게 높으면 과열 → 기다려 봄)",
        "② 60일선을 지키고 있나 → 깨지면 추세 이탈. 매수를 막진 않지만 회차를 나눠 천천히",
        "③ ◆ 명분일에 거래량이 터졌나, 그 뒤로 오르나 → 시장이 소식을 좋게 받았는지",
        "④ 20일선보다 10% 넘게 아래면 급락 구간 → 사더라도 1회분만, 다음 회차는 간격을 두고",
    };

    /// <summary>'?' 버튼: 주·월·년봉에서 볼 것</summary>
    public static readonly string[] LongFocus =
    {
        "① 지금 가격이 긴 이동평균선(20·60) 위인가 → 몇 달~몇 년 단위의 큰 흐름이 살아 있는지",
        "② 예전 고점 근처인가 → 처음 가는 가격대면 위에 팔 사람이 적고, 예전 고점 바로 아래면 매물이 나올 수 있어요",
        "③ 큰 하락 뒤 얼마나 회복했나 → 반도체는 업황(사이클)을 타서 몇 년 주기로 크게 오르내립니다",
        "④ 분할 매수 판단은 일봉·분봉으로 하고, 이 차트는 '지금이 비싼 구간인지' 감을 잡는 용도로 보세요",
    };

    /// <summary>'?' 버튼: 틱 차트에서 볼 것</summary>
    public static readonly string[] TickFocus =
    {
        "① 봉 하나 = 체결 10건. 시간이 아니라 거래가 쌓일 때마다 봉이 생겨서, 거래가 몰리면 봉이 빨리 늘어요",
        "② 거래량 막대가 갑자기 커지며 오르면 큰 매수 체결, 커지며 내리면 큰 매도 체결",
        "③ 14:50 전후에 기준가 위·아래 어디서 체결이 이어지나 → 종가가 하락 마감일지 가늠하는 용도",
        "④ 토스 API 는 최근 체결 50건만 줘서, 창을 열어 둔 동안만 쌓이고 체결이 아주 몰리면 일부가 빠집니다",
    };

    /// <summary>'?' 버튼: 1분봉에서 중점으로 볼 것</summary>
    public static readonly string[] MinuteFocus =
    {
        "① 9~10시 급등·급락은 무시 → 방향이 정해지기 전 구간",
        "② 명분일이면 기준가 점선 아래냐 위냐가 핵심 → 아래로 마감하면 기회 몫 매수, 위면 추격 금지. 명분 없는 날은 하락·보합 마감이면 기본 몫 '사도 괜찮음'",
        "③ 빠지면서 거래량이 크게 늘면 '강한 후보' (과거에 더 좋았던 눌림)",
        "④ 코스피는 오르는데 혼자 -5% 넘게 빠지면 쇼크 의심 → 공시부터 확인",
    };
}
