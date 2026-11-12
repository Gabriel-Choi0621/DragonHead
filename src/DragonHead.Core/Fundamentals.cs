using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DragonHead.Core;

/// <summary>한 분기 실적 (연결 기준, 원). 손익은 그 분기 3개월치, 자본·주식 수는 분기말 시점.</summary>
public sealed record QuarterFin(int Year, int Quarter, decimal? Revenue, decimal? OpIncome, decimal? NetIncome, decimal? Equity, long? Shares)
{
    public DateOnly PeriodEnd => Quarter switch
    {
        1 => new DateOnly(Year, 3, 31),
        2 => new DateOnly(Year, 6, 30),
        3 => new DateOnly(Year, 9, 30),
        _ => new DateOnly(Year, 12, 31),
    };
    public string Label => $"{Year % 100:00}.{Quarter}Q";
    public decimal? OpMargin => Revenue is > 0 && OpIncome is { } o ? Math.Round(o / Revenue.Value * 100m, 1) : null;
}

/// <summary>정기보고서 하나에서 뽑은 원재료. reprt_code 별로 캐시한다 (지난 보고서는 바뀌지 않음).</summary>
public sealed record ReportRaw(int Year, string ReprtCode, decimal? Revenue, decimal? OpIncome, decimal? NetIncome, decimal? Equity, long? Shares);

public static class Reprt
{
    public const string Q1 = "11013", H1 = "11012", Q3 = "11014", Annual = "11011";
    public static readonly string[] InOrder = { Q1, H1, Q3, Annual };
    public static int QuarterOf(string code) => Array.IndexOf(InOrder, code) + 1;
}

public static class DartFinancials
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// 단일회사 주요계정(fnlttSinglAcnt) + 주식의 총수 현황(stockTotqySttus) 한 보고서치.
    /// 아직 안 나온 보고서면 null (DART 013).
    /// </summary>
    public static async Task<ReportRaw?> FetchAsync(Func<string, CancellationToken, Task<string>> getJson, string corpCode, int year, string reprtCode, CancellationToken ct = default)
    {
        var acc = await getJson($"fnlttSinglAcnt.json?corp_code={corpCode}&bsns_year={year}&reprt_code={reprtCode}", ct);
        var (rev, op, net, eq) = ParseAccounts(acc, out bool none);
        if (none) return null;
        long? shares = null;
        try { shares = ParseShares(await getJson($"stockTotqySttus.json?corp_code={corpCode}&bsns_year={year}&reprt_code={reprtCode}", ct)); }
        catch (DartException) { }
        return new ReportRaw(year, reprtCode, rev, op, net, eq, shares);
    }

    /// <summary>연결(CFS) 우선, 없으면 개별(OFS). 손익은 thstrm_amount (분기·반기 보고서는 그 3개월, 사업보고서는 1년).</summary>
    public static (decimal? rev, decimal? op, decimal? net, decimal? eq) ParseAccounts(string json, out bool none)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var status = root.GetProperty("status").GetString();
        none = status == "013";
        if (none) return default;
        if (status != "000") throw new DartException(status ?? "?", root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "");

        var rows = root.GetProperty("list").EnumerateArray()
            .Select(it => (fs: Str(it, "fs_div"), name: Str(it, "account_nm").Replace(" ", ""), amt: Num(Str(it, "thstrm_amount"))))
            .ToList();
        var fs = rows.Any(r => r.fs == "CFS") ? "CFS" : "OFS";
        decimal? Find(params string[] names)
        {
            foreach (var n in names)
            {
                var hit = rows.FirstOrDefault(r => r.fs == fs && r.name.StartsWith(n, StringComparison.Ordinal) && r.amt is not null);
                if (hit.amt is not null) return hit.amt;
            }
            return null;
        }
        return (Find("매출액", "수익(매출액)", "영업수익"), Find("영업이익"), Find("당기순이익", "분기순이익", "반기순이익"), Find("자본총계"));
    }

    /// <summary>발행주식 총수 - 자기주식 = 유통주식 (보통주+우선주 '합계' 행, 없으면 보통주).</summary>
    public static long? ParseShares(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("status").GetString() != "000") return null;
        long? Pick(string se)
        {
            foreach (var it in root.GetProperty("list").EnumerateArray())
            {
                if (!Str(it, "se").Replace(" ", "").StartsWith(se, StringComparison.Ordinal)) continue;
                var distb = Num(Str(it, "distb_stock_co"));
                if (distb is > 0) return (long)distb.Value;
                var total = Num(Str(it, "istc_totqy"));
                var tre = Num(Str(it, "tesstk_co")) ?? 0;
                if (total is > 0) return (long)(total.Value - tre);
            }
            return null;
        }
        return Pick("합계") ?? Pick("보통주");
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static decimal? Num(string s)
    {
        s = s.Replace(",", "").Trim();
        if (s is "" or "-") return null;
        return decimal.TryParse(s, NumberStyles.Number, Inv, out var v) ? v : null;
    }

    /// <summary>보고서들 → 분기 실적. 4분기 = 연간 - (1+2+3분기).</summary>
    public static List<QuarterFin> ToQuarters(IEnumerable<ReportRaw> reports)
    {
        var byKey = reports.ToDictionary(r => (r.Year, Reprt.QuarterOf(r.ReprtCode)));
        var list = new List<QuarterFin>();
        foreach (var ((year, q), r) in byKey.OrderBy(k => k.Key.Year).ThenBy(k => k.Key.Item2))
        {
            if (q < 4) { list.Add(new QuarterFin(year, q, r.Revenue, r.OpIncome, r.NetIncome, r.Equity, r.Shares)); continue; }
            byKey.TryGetValue((year, 1), out var a); byKey.TryGetValue((year, 2), out var b); byKey.TryGetValue((year, 3), out var c);
            decimal? Q4(decimal? annual, Func<ReportRaw, decimal?> f) =>
                annual is { } y && a is not null && b is not null && c is not null && f(a) is { } x1 && f(b) is { } x2 && f(c) is { } x3 ? y - x1 - x2 - x3 : null;
            list.Add(new QuarterFin(year, 4, Q4(r.Revenue, x => x.Revenue), Q4(r.OpIncome, x => x.OpIncome), Q4(r.NetIncome, x => x.NetIncome), r.Equity, r.Shares));
        }
        return list;
    }
}

/// <summary>리포트용 밸류에이션 계산. 근거가 분명한 숫자만 만든다 (전망치·목표가 없음).</summary>
public sealed class Valuation
{
    public required IReadOnlyList<QuarterFin> Quarters { get; init; }
    public decimal Price { get; init; }
    public long? Shares { get; init; }
    public decimal? MarketCap { get; init; }
    public decimal? TtmNet { get; init; }
    public decimal? TtmOp { get; init; }
    public decimal? TtmOpYearAgo { get; init; }
    public decimal? Per { get; init; }
    public decimal? Pbr { get; init; }
    public QuarterFin? Latest { get; init; }
    public QuarterFin? LatestYearAgo { get; init; }
    /// <summary>최근 N년 일별 PBR 분포 (그날 기준 가장 최근 분기말 자본)</summary>
    public decimal? PbrMin { get; init; }
    public decimal? PbrMedian { get; init; }
    public decimal? PbrMax { get; init; }
    /// <summary>오늘 PBR 이 과거 N년 중 몇 % 날보다 높은가 (0~100)</summary>
    public int? PbrPercentile { get; init; }
    public double PbrYears { get; init; }
    public int PbrDays { get; init; }
    public IReadOnlyList<(DateOnly date, decimal pbr)> PbrSeries { get; init; } = Array.Empty<(DateOnly, decimal)>();

    public static Valuation? Compute(IReadOnlyList<QuarterFin> quarters, IReadOnlyList<Bar> daily, decimal price, int years = 5)
    {
        var q = quarters.OrderBy(x => x.PeriodEnd).ToList();
        if (q.Count == 0 || price <= 0) return null;
        var latest = q.LastOrDefault(x => x.OpIncome is not null) ?? q[^1];
        var latestBs = q.LastOrDefault(x => x.Equity is > 0 && x.Shares is > 0);
        long? shares = latestBs?.Shares ?? q.LastOrDefault(x => x.Shares is > 0)?.Shares;
        decimal? cap = shares is { } sh ? price * sh : null;

        decimal? Ttm(Func<QuarterFin, decimal?> f, int skip)
        {
            var last4 = q.Where(x => f(x) is not null).Reverse().Skip(skip).Take(4).ToList();
            if (last4.Count < 4) return null;
            // 연속된 4분기인지 확인 (빠진 분기가 있으면 합계를 믿지 않음)
            var first = last4[^1];
            var expect = first.Year * 4 + first.Quarter - 1 + 3;
            return last4[0].Year * 4 + last4[0].Quarter - 1 == expect ? last4.Sum(x => f(x)!.Value) : null;
        }
        var ttmNet = Ttm(x => x.NetIncome, 0);
        var ttmOp = Ttm(x => x.OpIncome, 0);
        var ttmOpAgo = Ttm(x => x.OpIncome, 4);
        var yearAgo = q.FirstOrDefault(x => x.Year == latest.Year - 1 && x.Quarter == latest.Quarter);

        // 일별 PBR: 그날 이전에 끝난 가장 최근 분기의 자본·주식 수
        var bs = q.Where(x => x.Equity is > 0 && x.Shares is > 0).ToList();
        var series = new List<(DateOnly, decimal)>();
        var today = daily.Count > 0 ? DateOnly.FromDateTime(daily[^1].Time.DateTime) : DateOnly.MinValue;
        var from = today.AddYears(-years);
        foreach (var b in daily)
        {
            var d = DateOnly.FromDateTime(b.Time.DateTime);
            if (d < from) continue;
            var src = bs.LastOrDefault(x => x.PeriodEnd < d);
            if (src is null) continue;
            series.Add((d, Math.Round(b.Close * src.Shares!.Value / src.Equity!.Value, 3)));
        }
        decimal? pbr = latestBs is not null && cap is not null ? Math.Round(cap.Value / latestBs.Equity!.Value, 2) : null;
        int? pct = null; decimal? min = null, med = null, max = null;
        if (series.Count >= 120 && pbr is { } p)
        {
            var sorted = series.Select(x => x.Item2).OrderBy(x => x).ToList();
            min = sorted[0]; max = sorted[^1]; med = sorted[sorted.Count / 2];
            pct = (int)Math.Round(sorted.Count(x => x < p) * 100.0 / sorted.Count);
        }

        return new Valuation
        {
            Quarters = q, Price = price, Shares = shares, MarketCap = cap,
            TtmNet = ttmNet, TtmOp = ttmOp, TtmOpYearAgo = ttmOpAgo,
            Per = cap is { } c && ttmNet is > 0 ? Math.Round(c / ttmNet.Value, 1) : null,
            Pbr = pbr, Latest = latest, LatestYearAgo = yearAgo,
            PbrMin = min, PbrMedian = med, PbrMax = max, PbrPercentile = pct,
            PbrYears = series.Count > 0 ? Math.Round((series[^1].Item1.DayNumber - series[0].Item1.DayNumber) / 365.25, 1) : 0,
            PbrDays = series.Count, PbrSeries = series,
        };
    }

    public static decimal? Growth(decimal? now, decimal? before) =>
        now is { } n && before is { } b && b > 0 ? Math.Round((n / b - 1m) * 100m, 1) : null;

    /// <summary>
    /// 근거가 분명한 문장만. 사라/팔라는 말은 하지 않는다.
    /// </summary>
    public IEnumerable<string> Facts()
    {
        if (Pbr is { } p && PbrPercentile is { } pc && PbrMin is { } lo && PbrMax is { } hi)
        {
            string zone = pc >= 80 ? "상단" : pc >= 60 ? "중상단" : pc >= 40 ? "중간" : pc >= 20 ? "중하단" : "하단";
            yield return $"PBR {p:0.00}배 — 최근 {PbrYears:0.#}년 범위({lo:0.00}~{hi:0.00}배) 중 <b>{zone}</b>. 지난 {PbrYears:0.#}년 거래일의 {pc}%가 지금보다 낮았습니다." +
                         (PbrYears < 3 ? " (비교 기간이 3년이 안 돼 범위는 약한 참고만)" : "");
        }
        else if (Pbr is { } p2) yield return $"PBR {p2:0.00}배 (과거 범위를 낼 일봉·재무가 부족)";

        if (TtmOp is { } op && TtmOpYearAgo is { } ago)
        {
            var g = Growth(op, ago);
            yield return $"최근 4분기 영업이익 {Jo(op)} — 1년 전 4분기 합계({Jo(ago)})보다 {(g is { } x ? (x >= 0 ? $"<b class='up'>+{x:0.#}%</b>" : $"<b class='down'>{x:0.#}%</b>") : "-")}.";
        }
        if (Latest is { OpIncome: { } lop } l && LatestYearAgo is { OpIncome: { } yop } y)
        {
            var g = Growth(lop, yop);
            var qoq = Quarters.OrderBy(x => x.PeriodEnd).TakeWhile(x => x != l).LastOrDefault();
            var gq = Growth(lop, qoq?.OpIncome);
            yield return $"최근 분기({l.Label}) 영업이익 {Jo(lop)} · 영업이익률 {l.OpMargin:0.#}% — 전년 같은 분기 대비 {Signed(g)}, 직전 분기 대비 {Signed(gq)}.";
        }
        if (Per is { } per) yield return $"PER {per:0.#}배 (최근 4분기 순이익 기준). 이익이 크게 늘거나 줄고 있는 중이면 PER 숫자만으로 싸다·비싸다를 말하기 어렵습니다.";
        else if (TtmNet is <= 0) yield return "최근 4분기 순이익이 적자라 PER 은 의미가 없습니다.";
    }

    private static string Signed(decimal? v) => v is { } x ? (x >= 0 ? $"+{x:0.#}%" : $"{x:0.#}%") : "-";

    public static string Jo(decimal won)
    {
        var jo = won / 1_000_000_000_000m;
        return Math.Abs(jo) >= 1 ? $"{jo:#,0.0}조" : $"{won / 100_000_000m:#,0}억";
    }
}
