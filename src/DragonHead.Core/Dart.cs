using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DragonHead.Core;

public sealed class DartException(string status, string message) : Exception($"DART [{status}] {message}")
{
    public string Status { get; } = status;
}

/// <summary>DART 공시 목록 한 건</summary>
public sealed record DartDisclosure(string CorpCode, string CorpName, string StockCode, string ReportName, string ReceiptNo, DateOnly ReceiptDate)
{
    public string ViewerUrl => $"https://dart.fss.or.kr/dsaf001/main.do?rcpNo={ReceiptNo}";
}

public enum CatalystKind { None, Earnings, InvestorRelations, Buyback, Cancellation, Dividend }

/// <summary>
/// 금감원 전자공시(DART) Open API. 조회만 한다.
/// https://opendart.fss.or.kr — 인증키 하루 20,000건 한도.
/// </summary>
public sealed class DartClient : IDisposable
{
    private const string Base = "https://opendart.fss.or.kr/api/";
    private readonly HttpClient _http;
    private readonly string _key;

    /// <summary>자주 쓰는 종목의 DART 고유번호. 전체 목록(corpCode.xml)을 못 받을 때만 쓴다.</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownCorpCodes = new Dictionary<string, string>
    {
        ["005930"] = "00126380", // 삼성전자
        ["000660"] = "00164779", // SK하이닉스
    };

    static DartClient()
    {
        // 공시 원문 중 EUC-KR 로 된 것이 있어 코드페이지 인코딩을 켠다 (.NET 기본 포함)
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public DartClient(string apiKey, HttpMessageHandler? handler = null)
    {
        _key = apiKey.Trim();
        // 리다이렉트는 직접 처리한다: 어디로 넘겨지는지 알아야 회사 방화벽 차단인지 구분할 수 있고,
        // .NET 은 https → http 로 넘기는 리다이렉트를 따라가지 않아 302 오류만 남기 때문.
        _http = handler is null
            ? new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false })
            : new HttpClient(handler);
        _http.BaseAddress = new Uri(Base);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) DragonHead/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json, application/zip, application/xml, */*");
    }

    /// <summary>
    /// GET 하나. 3xx 면 opendart 안에서의 이동만 따라가고(http 로 넘기면 https 로 바꿔서), 다른 곳으로 넘기면
    /// 차단 페이지로 보고 어디로 넘겼는지 담아 예외를 던진다.
    /// </summary>
    private async Task<byte[]> GetBytesAsync(string relative, CancellationToken ct)
    {
        var uri = new Uri(_http.BaseAddress!, relative);
        for (int hop = 0; hop < 4; hop++)
        {
            using var resp = await _http.GetAsync(uri, ct);
            var code = (int)resp.StatusCode;
            if (code is >= 300 and < 400)
            {
                var loc = resp.Headers.Location;
                if (loc is null) throw new DartException(code.ToString(), "DART 가 이동할 주소 없이 리다이렉트했습니다.");
                if (!loc.IsAbsoluteUri) loc = new Uri(uri, loc);

                if (loc.Host.EndsWith("fss.or.kr", StringComparison.OrdinalIgnoreCase))
                {
                    // 같은 기관 안의 이동. http 로 내리면 https 로 올려서 따라간다.
                    uri = loc.Scheme == Uri.UriSchemeHttp ? new UriBuilder(loc) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri : loc;
                    if (uri.AbsolutePath.Contains("error", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.Contains("main", StringComparison.OrdinalIgnoreCase))
                        throw new DartException(code.ToString(), $"DART 가 API 대신 웹페이지({Mask(uri)})로 보냈습니다. 인증키나 요청 주소를 확인하세요.");
                    continue;
                }
                throw new DartException(code.ToString(),
                    $"DART 대신 {loc.Host} 로 넘겨졌습니다. 회사 방화벽·보안 프로그램이 opendart.fss.or.kr 를 막는 것 같습니다. (이동한 주소: {Mask(loc)})");
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new DartException(code.ToString(), $"HTTP {code} {resp.ReasonPhrase}");

            // 차단 프로그램이 200 으로 경고 페이지를 돌려주는 경우
            var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 200)).TrimStart();
            if (head.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                throw new DartException("html", "DART 대신 웹페이지가 왔습니다. 회사 방화벽·보안 프로그램의 차단 페이지일 수 있습니다.");
            return bytes;
        }
        throw new DartException("redirect", "리다이렉트가 너무 많습니다.");
    }

    /// <summary>오류 메시지에 인증키가 찍히지 않게 가린다.</summary>
    private string Mask(Uri u) => _key.Length > 0 ? u.ToString().Replace(_key, "****") : u.ToString();

    public void Dispose() => _http.Dispose();

    /// <summary>JSON API 하나 (인증키는 여기서 붙인다). 재무·주식 수 조회용.</summary>
    public async Task<string> GetJsonAsync(string relative, CancellationToken ct = default) =>
        Encoding.UTF8.GetString(await GetBytesAsync($"{relative}&crtfc_key={_key}", ct));

    /// <summary>최근 <paramref name="years"/>년치 정기보고서 재무 (지난 보고서는 cache 에 넣고 다시 안 받음).</summary>
    public async Task<List<ReportRaw>> GetReportsAsync(string corpCode, int years, IDictionary<string, ReportRaw> cache, Action<string>? log = null, CancellationToken ct = default)
    {
        var list = new List<ReportRaw>();
        var now = DateTime.Now;
        for (int y = now.Year - years; y <= now.Year; y++)
        {
            foreach (var code in Reprt.InOrder)
            {
                var key = $"{corpCode}|{y}|{code}";
                if (cache.TryGetValue(key, out var hit)) { list.Add(hit); continue; }
                // 분기말 + 2주 전엔 보고서가 있을 수 없으니 묻지 않는다
                var end = new DateTime(y, Reprt.QuarterOf(code) * 3, 1).AddMonths(1);
                if (end.AddDays(14) > now) continue;
                try
                {
                    var r = await DartFinancials.FetchAsync(GetJsonAsync, corpCode, y, code, ct);
                    if (r is null) continue;
                    list.Add(r);
                    // 제출 기한(분기 45일·사업보고서 90일)이 충분히 지난 것만 영구 캐시 (정정 공시 반영 여지)
                    if (end.AddDays(120) < now) cache[key] = r;
                }
                catch (DartException ex) { log?.Invoke($"DART 재무 {y} {code}: {ex.Message}"); }
            }
        }
        return list;
    }

    private static string D(DateOnly d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>
    /// 종목코드(6자리) → DART 고유번호(8자리). 전체 회사 목록 zip(수 MB)을 받아 필요한 것만 돌려준다.
    /// 호출하는 쪽이 결과를 파일로 캐시해 두는 게 좋다.
    /// </summary>
    public async Task<Dictionary<string, string>> ResolveCorpCodesAsync(IEnumerable<string> stockCodes, CancellationToken ct = default)
    {
        var wanted = stockCodes.ToHashSet();
        var bytes = await GetBytesAsync($"corpCode.xml?crtfc_key={_key}", ct);
        if (!IsZip(bytes)) ThrowFromBody(bytes);

        using var zip = new ZipArchive(new MemoryStream(bytes));
        var entry = zip.Entries.First(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
        using var s = entry.Open();
        var doc = XDocument.Load(s);

        var map = new Dictionary<string, string>();
        foreach (var item in doc.Root!.Elements("list"))
        {
            var stock = item.Element("stock_code")?.Value.Trim() ?? "";
            if (stock.Length == 6 && wanted.Contains(stock))
                map[stock] = item.Element("corp_code")!.Value.Trim();
        }
        return map;
    }

    /// <summary>한 회사의 공시 목록 (최종본만, 접수일 오름차순). 기간이 길면 여러 페이지를 돈다.</summary>
    public async Task<List<DartDisclosure>> ListAsync(string corpCode, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var all = new List<DartDisclosure>();
        for (int page = 1; page <= 200; page++)
        {
            var url = $"list.json?crtfc_key={_key}&corp_code={corpCode}&bgn_de={D(from)}&end_de={D(to)}" +
                      $"&last_reprt_at=Y&sort=date&sort_mth=asc&page_no={page}&page_count=100";
            var text = Encoding.UTF8.GetString(await GetBytesAsync(url, ct));
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var status = root.GetProperty("status").GetString() ?? "";
            if (status == "013") break; // 조회된 데이터 없음
            if (status != "000")
                throw new DartException(status, root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "");

            foreach (var it in root.GetProperty("list").EnumerateArray())
            {
                all.Add(new DartDisclosure(
                    it.GetProperty("corp_code").GetString() ?? corpCode,
                    it.GetProperty("corp_name").GetString() ?? "",
                    it.TryGetProperty("stock_code", out var sc) ? sc.GetString() ?? "" : "",
                    it.GetProperty("report_nm").GetString()?.Trim() ?? "",
                    it.GetProperty("rcept_no").GetString() ?? "",
                    DateOnly.ParseExact(it.GetProperty("rcept_dt").GetString()!, "yyyyMMdd", CultureInfo.InvariantCulture)));
            }

            var totalPage = root.TryGetProperty("total_page", out var tp) ? tp.GetInt32() : 1;
            if (page >= totalPage) break;
        }
        return all;
    }

    /// <summary>공시 원문(zip 안 XML)을 태그를 걷어낸 글자로. IR 개최일을 찾는 데 쓴다.</summary>
    public async Task<string> GetDocumentTextAsync(string receiptNo, CancellationToken ct = default)
    {
        var bytes = await GetBytesAsync($"document.xml?crtfc_key={_key}&rcept_no={receiptNo}", ct);
        if (!IsZip(bytes)) ThrowFromBody(bytes);

        var sb = new StringBuilder();
        using var zip = new ZipArchive(new MemoryStream(bytes));
        foreach (var entry in zip.Entries)
        {
            using var s = entry.Open();
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms, ct);
            sb.AppendLine(StripTags(Decode(ms.ToArray())));
        }
        return sb.ToString();
    }

    private static bool IsZip(byte[] b) => b.Length > 4 && b[0] == 'P' && b[1] == 'K';

    private static void ThrowFromBody(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        var status = Regex.Match(text, @"<status>(\d+)</status>|""status""\s*:\s*""(\d+)""");
        var message = Regex.Match(text, @"<message>([^<]*)</message>|""message""\s*:\s*""([^""]*)""");
        throw new DartException(
            status.Success ? status.Groups[1].Value + status.Groups[2].Value : "?",
            message.Success ? message.Groups[1].Value + message.Groups[2].Value : text.Length > 200 ? text[..200] : text);
    }

    private static string Decode(byte[] bytes)
    {
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(200, bytes.Length));
        var m = Regex.Match(head, @"encoding\s*=\s*[""']([A-Za-z0-9_\-]+)[""']");
        try
        {
            return m.Success ? Encoding.GetEncoding(m.Groups[1].Value).GetString(bytes) : Encoding.UTF8.GetString(bytes);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    public static string StripTags(string xml)
    {
        var noTags = Regex.Replace(xml, "<[^>]+>", " ");
        noTags = WebUtility.HtmlDecode(noTags);
        return Regex.Replace(noTags, @"[ \t ]+", " ");
    }
}

/// <summary>공시 제목으로 '명분' 종류를 가른다.</summary>
public static class DartClassifier
{
    public static CatalystKind Classify(string reportName)
    {
        var n = reportName.Replace(" ", "");
        if (n.Contains("영업(잠정)실적")) return CatalystKind.Earnings;
        if (n.Contains("기업설명회")) return CatalystKind.InvestorRelations;
        if (n.Contains("자기주식취득") && !n.Contains("결과")) return CatalystKind.Buyback;   // '취득결과보고서'는 사후 보고라 제외
        if (n.Contains("주식소각결정")) return CatalystKind.Cancellation;
        if (n.Contains("배당결정")) return CatalystKind.Dividend;
        return CatalystKind.None;
    }

    public static string Describe(CatalystKind k) => k switch
    {
        CatalystKind.Earnings => "실적 공시",
        CatalystKind.InvestorRelations => "실적 발표(IR)",
        CatalystKind.Buyback => "자사주 취득 결정",
        CatalystKind.Cancellation => "주식 소각 결정",
        CatalystKind.Dividend => "배당 결정",
        _ => "",
    };

    private static readonly Regex DatePattern = new(
        @"(?<y>20\d{2})\s*(?:년|[.\-/])\s*(?<m>\d{1,2})\s*(?:월|[.\-/])\s*(?<d>\d{1,2})\s*일?",
        RegexOptions.Compiled);

    /// <summary>
    /// IR 개최 안내 공시 본문에서 개최일을 찾는다. '일시'·'개최' 근처의 날짜 중 접수일 이후 첫 날짜.
    /// 못 찾으면 null (그 경우 실적 공시 당일만 명분으로 잡힌다).
    /// </summary>
    public static DateOnly? FindIrDate(string text, DateOnly receiptDate)
    {
        foreach (var anchor in new[] { "개최일시", "일시", "개최일" })
        {
            var idx = text.IndexOf(anchor, StringComparison.Ordinal);
            while (idx >= 0)
            {
                var window = text.Substring(idx, Math.Min(160, text.Length - idx));
                foreach (Match m in DatePattern.Matches(window))
                {
                    if (TryDate(m, out var d) && d >= receiptDate && d <= receiptDate.AddDays(60))
                        return d;
                }
                idx = text.IndexOf(anchor, idx + anchor.Length, StringComparison.Ordinal);
            }
        }
        return null;
    }

    private static readonly Regex EarningsPattern = new(
        @"[1-4]\s*분기|1Q|2Q|3Q|4Q|반기\s*(경영)?실적|연간\s*(경영)?실적|결산\s*실적|경영\s*실적\s*발표|잠정\s*실적|earnings",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 실적 발표용 IR 인지. 해외 컨퍼런스·NDR 참가 같은 IR 안내도 '기업설명회'로 올라오는데, 그건 명분이 아니다.
    /// 분기·반기·연간 실적이나 earnings 를 언급하면 실적 IR 로 본다.
    /// </summary>
    public static bool IsEarningsIr(string text) => EarningsPattern.IsMatch(text);

    private static bool TryDate(Match m, out DateOnly d)
    {
        d = default;
        return int.TryParse(m.Groups["y"].Value, out var y) && int.TryParse(m.Groups["m"].Value, out var mo) &&
               int.TryParse(m.Groups["d"].Value, out var da) && mo is >= 1 and <= 12 && da is >= 1 and <= 31 &&
               DateOnly.TryParse($"{y:D4}-{mo:D2}-{da:D2}", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
    }
}

/// <summary>
/// DART 공시 → 명분 이벤트. 앱(실시간)과 백테스트(과거)가 같이 쓴다.
/// </summary>
public sealed class DartCatalystFinder
{
    private readonly DartClient _client;
    private readonly HashSet<CatalystKind> _kinds;

    /// <summary>IR 공시 본문에서 찾은 개최일 캐시 (접수번호 → 날짜). 앱이 파일로 저장해 재사용.</summary>
    public Dictionary<string, string> IrDateCache { get; } = new();

    public Action<string>? Log { get; set; }

    public DartCatalystFinder(DartClient client, IEnumerable<CatalystKind> kinds)
    {
        _client = client;
        _kinds = kinds.ToHashSet();
    }

    public async Task<List<CatalystEvent>> FindAsync(
        IReadOnlyDictionary<string, (string corpCode, string name)> symbols, DateOnly from, DateOnly to,
        bool resolveIrDates, CancellationToken ct = default)
    {
        var events = new List<CatalystEvent>();
        foreach (var (symbol, (corp, name)) in symbols)
        {
            var list = await _client.ListAsync(corp, from, to, ct);
            foreach (var d in list)
            {
                var kind = DartClassifier.Classify(d.ReportName);
                if (kind == CatalystKind.None || !_kinds.Contains(kind)) continue;

                var date = d.ReceiptDate;
                var title = $"{DartClassifier.Describe(kind)} · {d.ReportName}";

                if (kind == CatalystKind.InvestorRelations)
                {
                    // 안내 공시일이 아니라 실제 IR(실적 발표) 날짜가 명분일
                    // 캐시: "날짜|E"(실적 IR) · "날짜|N"(실적 아닌 IR) · ""(날짜 못 찾음). 예전 형식("날짜")은 본문을 다시 읽는다.
                    DateOnly? irDate = null;
                    bool earnings = false, known = false;
                    if (IrDateCache.TryGetValue(d.ReceiptNo, out var cached) && (cached.Length == 0 || cached.Contains('|')))
                    {
                        known = true;
                        var parts = cached.Split('|');
                        if (DateOnly.TryParse(parts[0], CultureInfo.InvariantCulture, out var c)) irDate = c;
                        earnings = parts.Length > 1 && parts[1] == "E";
                    }
                    if (!known && resolveIrDates)
                    {
                        try
                        {
                            var text = await _client.GetDocumentTextAsync(d.ReceiptNo, ct);
                            irDate = DartClassifier.FindIrDate(text, d.ReceiptDate);
                            earnings = DartClassifier.IsEarningsIr(text);
                            IrDateCache[d.ReceiptNo] = irDate is { } fd
                                ? fd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + (earnings ? "|E" : "|N")
                                : "";
                        }
                        catch (Exception ex) when (ex is DartException or HttpRequestException or InvalidDataException or TaskCanceledException)
                        {
                            Log?.Invoke($"IR 공시 본문 읽기 실패 {d.ReceiptNo}: {ex.Message}");
                        }
                    }
                    if (irDate is null) continue; // 날짜를 모르면 등록하지 않는다 (실적 공시가 당일에 따로 잡힘)
                    if (!earnings)
                    {
                        Log?.Invoke($"실적 아닌 IR 은 명분에서 뺌: {name} {irDate:yyyy-MM-dd} {d.ReportName} ({d.ReceiptNo})");
                        continue;
                    }
                    date = irDate.Value;
                    title = $"실적 발표(IR) · {name}";
                }

                events.Add(new CatalystEvent
                {
                    Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Symbol = symbol,
                    Title = title,
                    Source = "DART",
                    Url = d.ViewerUrl,
                });
            }
        }

        // 같은 종목·같은 날 여러 건이면 하나로 (실적 공시와 IR이 같은 날인 경우 등)
        return events
            .GroupBy(e => (e.Date, e.Symbol))
            .Select(g => g.OrderBy(e => e.Title.StartsWith("실적") ? 0 : 1).First())
            .OrderBy(e => e.Date)
            .ToList();
    }
}
