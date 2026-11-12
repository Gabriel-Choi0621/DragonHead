namespace DragonHead.Core;

/// <summary>
/// KRX 가격 규칙. 토스 API는 '전일 종가' 필드를 주지 않고, 일봉 종가는 NXT 애프터마켓(~20:00) 마지막 체결가가
/// 섞여 들어온다. 대신 당일 상·하한가는 KRX 기준가(전일 KRX 종가)로 계산되므로 거기서 기준가를 역산한다.
///   상한가 = 기준가 × 1.3 을 (상한가 가격대의) 호가단위로 절사
///   하한가 = 기준가 × 0.7 을 (하한가 가격대의) 호가단위로 절상
/// </summary>
public static class KrxPrice
{
    /// <summary>KRX 주식 호가단위 (2023년 개편 기준, 코스피·코스닥 공통)</summary>
    public static decimal Tick(decimal price) => price switch
    {
        < 2_000m => 1m,
        < 5_000m => 5m,
        < 20_000m => 10m,
        < 50_000m => 50m,
        < 200_000m => 100m,
        < 500_000m => 500m,
        _ => 1_000m,
    };

    public static decimal UpperLimit(decimal basePrice)
    {
        var raw = basePrice * 1.3m;
        var tick = Tick(raw);
        return Math.Floor(raw / tick) * tick;
    }

    public static decimal LowerLimit(decimal basePrice)
    {
        var raw = basePrice * 0.7m;
        var tick = Tick(raw);
        return Math.Ceiling(raw / tick) * tick;
    }

    /// <summary>
    /// 호가단위 표를 쓰지 않는 근사 역산. 상한가/1.3 과 하한가/0.7 사이에 기준가가 있으므로 그 가운데 값.
    /// 오차는 호가 1~2칸 (대형주 기준 0.1% 안팎).
    /// </summary>
    public static decimal? ApproxBaseFromLimits(decimal upper, decimal lower)
    {
        if (upper <= 0 || lower <= 0) return null;
        var hi = upper / 1.3m;
        var lo = lower / 0.7m;
        if (Math.Abs(hi - lo) / hi > 0.01m) return null; // 상·하한가 쌍이 이상하면 포기
        return Math.Round((hi + lo) / 2m);
    }

    /// <summary>상·하한가가 둘 다 정확히 맞는 기준가를 찾는다. 못 찾으면 null.</summary>
    public static decimal? BaseFromLimits(decimal upper, decimal lower)
    {
        if (upper <= 0 || lower <= 0) return null;

        var guess = upper / 1.3m;
        var tick = Tick(guess);
        var start = Math.Floor(guess / tick) * tick;

        // 추정치 주변 호가 몇 칸을 확인 (절사 오차는 1~2호가 이내)
        decimal? best = null;
        for (int i = -4; i <= 4; i++)
        {
            var candidate = start + i * tick;
            if (candidate <= 0 || candidate % Tick(candidate) != 0) continue;
            if (UpperLimit(candidate) == upper && LowerLimit(candidate) == lower)
            {
                // 둘 다 맞는 후보가 여러 개면 (아주 드묾) 추정치에 가장 가까운 것
                if (best is null || Math.Abs(candidate - guess) < Math.Abs(best.Value - guess))
                    best = candidate;
            }
        }
        return best;
    }
}
