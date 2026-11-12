using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DragonHead.Core;

/// <summary>상장 종목 한 건 (검색·이름 표시용)</summary>
public sealed record StockInfo(string Symbol, string Name, string Market, string SecurityType, bool IsCommon)
{
    public bool IsEtfLike => SecurityType is "ETF" or "ETN" or "FOREIGN_ETF";

    public string TypeLabel => SecurityType switch
    {
        "STOCK" => IsCommon ? "주식" : "우선주",
        "ETF" or "FOREIGN_ETF" => "ETF",
        "ETN" => "ETN",
        "REIT" => "리츠",
        "INFRASTRUCTURE_FUND" => "인프라펀드",
        "DEPOSITARY_RECEIPT" => "DR",
        "FOREIGN_STOCK" => "외국주식",
        "STOCK_WARRANTS" => "신주인수권",
        _ => SecurityType,
    };

    public string MarketLabel => Market switch { "KOSPI" => "코스피", "KOSDAQ" => "코스닥", "NASDAQ" => "나스닥", "NYSE" => "뉴욕", "AMEX" => "아멕스", _ => "기타" };
    public bool IsUs => Market is "NASDAQ" or "NYSE" or "AMEX" or "US_ETC";
}

/// <summary>순위 한 줄</summary>
public sealed record RankItem(int Rank, string Symbol, decimal LastPrice, decimal BasePrice, decimal? ChangePct, decimal Volume, decimal Amount);

/// <summary>
/// 코스피·코스닥 전체 종목 목록. 하루 한 번 받아 파일로 캐시하고, 검색은 메모리에서 한다 (약 3천 건).
/// 검색: 정확히 일치 → 앞글자 일치 → 포함 → 초성(ㅅㅅㅈㅈ) 순. 같은 점수면 보통주 → 짧은 이름.
/// </summary>
public sealed class StockDirectory
{
    private readonly string[] _markets;

    /// <summary>markets: 국장은 KOSPI·KOSDAQ (기본), 미장은 NASDAQ·NYSE·AMEX</summary>
    public StockDirectory(params string[] markets) => _markets = markets.Length > 0 ? markets : new[] { "KOSPI", "KOSDAQ" };

    public static readonly string[] UsMarkets = { "NASDAQ", "NYSE", "AMEX" };

    private List<StockInfo> _all = new();
    private Dictionary<string, StockInfo> _bySymbol = new();
    private Dictionary<string, string> _choseong = new();

    public DateOnly? LoadedOn { get; private set; }
    public int Count => _all.Count;

    public StockInfo? Find(string symbol) => _bySymbol.GetValueOrDefault(symbol);

    public void Set(IEnumerable<StockInfo> items, DateOnly day)
    {
        _all = items.GroupBy(i => i.Symbol).Select(g => g.First()).ToList();
        _bySymbol = _all.ToDictionary(i => i.Symbol);
        _choseong = _all.ToDictionary(i => i.Symbol, i => Choseong(Normalize(i.Name)));
        LoadedOn = day;
    }

    /// <summary>캐시 파일이 오늘 것이면 그걸 쓰고, 아니면 토스에서 새로 받는다.</summary>
    public async Task LoadAsync(TossClient toss, string cachePath, CancellationToken ct = default)
    {
        var today = Kst.Today;
        try
        {
            if (File.Exists(cachePath))
            {
                var cached = JsonSerializer.Deserialize<Cache>(await File.ReadAllTextAsync(cachePath, ct));
                if (cached is not null && cached.Day == today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) && cached.Items.Count > 0)
                {
                    Set(cached.Items, today);
                    return;
                }
                if (cached is { Items.Count: > 0 } && _all.Count == 0)
                    Set(cached.Items, DateOnly.Parse(cached.Day, CultureInfo.InvariantCulture)); // 받는 동안이라도 어제 목록으로
            }
        }
        catch (JsonException) { /* 캐시가 깨졌으면 새로 받음 */ }

        var items = new List<StockInfo>();
        foreach (var market in _markets)
            items.AddRange(await toss.GetAllStocksAsync(market, ct));
        Set(items, today);

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(new Cache
        {
            Day = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Items = _all,
        }), ct);
    }

    private sealed class Cache
    {
        public string Day { get; set; } = "";
        public List<StockInfo> Items { get; set; } = new();
    }

    public List<StockInfo> Search(string query, int max = 15)
    {
        var q = Normalize(query);
        if (q.Length == 0) return new();
        bool jamoOnly = q.All(IsChoseongJamo);

        var scored = new List<(StockInfo s, int score)>();
        foreach (var s in _all)
        {
            var name = Normalize(s.Name);
            int score;
            if (name == q || s.Symbol == q) score = 0;
            else if (name.StartsWith(q, StringComparison.Ordinal) || s.Symbol.StartsWith(q, StringComparison.Ordinal)) score = 1;
            else if (name.Contains(q, StringComparison.Ordinal)) score = 2;
            else if (jamoOnly && _choseong[s.Symbol].StartsWith(q, StringComparison.Ordinal)) score = 3;
            else if (jamoOnly && _choseong[s.Symbol].Contains(q, StringComparison.Ordinal)) score = 4;
            else continue;

            // 보통주 먼저, 그다음 우선주·ETF 등
            if (!(s.SecurityType == "STOCK" && s.IsCommon)) score += 5;
            scored.Add((s, score));
        }

        return scored
            .OrderBy(x => x.score)
            .ThenBy(x => x.s.Name.Length)
            .ThenBy(x => x.s.Symbol, StringComparer.Ordinal)
            .Take(max)
            .Select(x => x.s)
            .ToList();
    }

    public static string Normalize(string s) => s.Replace(" ", "").ToLowerInvariant();

    private const string Initials = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";

    private static bool IsChoseongJamo(char c) => Initials.IndexOf(c) >= 0;

    /// <summary>'삼성전자' → 'ㅅㅅㅈㅈ'. 한글이 아닌 글자는 그대로.</summary>
    public static string Choseong(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c is >= '가' and <= '힣' ? Initials[(c - '가') / 588] : c);
        return sb.ToString();
    }
}
