using System.Globalization;
using System.Text.Json;
using DragonHead.Core;

namespace DragonHead.App;

/// <summary>
/// DART 공시로 명분 이벤트를 자동 등록한다.
/// 고유번호·IR 개최일·찾은 이벤트는 %APPDATA%\DragonHead 에 캐시해서 재시작해도 바로 쓴다.
/// </summary>
internal sealed class DartSync : IDisposable
{
    private readonly AppSettings _settings;
    private readonly DartClient _client;
    private readonly DartCatalystFinder _finder;
    private Dictionary<string, string> _corp = new();
    private DateTimeOffset _lastRun = DateTimeOffset.MinValue;
    private bool _running;

    public string? LastError { get; private set; }

    /// <summary>처음 보는 (오늘 이후) 명분이 생기면 알린다.</summary>
    public event Action<CatalystEvent>? NewUpcoming;

    private static string CorpPath => Path.Combine(SettingsStore.Folder, "dart-corp.json");
    private static string IrPath => Path.Combine(SettingsStore.Folder, "dart-ir.json");
    private static string EventsPath => Path.Combine(SettingsStore.Folder, "events-auto.json");

    public DartSync(AppSettings settings)
    {
        _settings = settings;
        _client = new DartClient(settings.DartApiKey);
        _finder = new DartCatalystFinder(_client, settings.DartKinds) { Log = Log.Write };

        _corp = ReadJson<Dictionary<string, string>>(CorpPath) ?? new();
        foreach (var (k, v) in ReadJson<Dictionary<string, string>>(IrPath) ?? new()) _finder.IrDateCache[k] = v;
        _settings.AutoEvents = ReadJson<List<CatalystEvent>>(EventsPath) ?? new();
    }

    public DartClient Client => _client;
    public DartCatalystFinder Finder => _finder;

    /// <summary>평일 07:30~18:30 은 30분마다, 그 외 3시간마다.</summary>
    public bool Due(DateTimeOffset now)
    {
        var t = TimeOnly.FromDateTime(now.DateTime);
        bool weekday = now.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        var every = weekday && t >= new TimeOnly(7, 30) && t < new TimeOnly(18, 30) ? TimeSpan.FromMinutes(30) : TimeSpan.FromHours(3);
        return !_running && now - _lastRun >= every;
    }

    public async Task<Dictionary<string, (string corpCode, string name)>> SymbolsAsync(CancellationToken ct = default)
    {
        var need = _settings.Watch.Select(w => w.Symbol).Where(s => !_corp.ContainsKey(s)).ToList();
        if (need.Count > 0)
        {
            try
            {
                foreach (var (k, v) in await _client.ResolveCorpCodesAsync(need, ct)) _corp[k] = v;
                WriteJson(CorpPath, _corp);
            }
            catch (Exception ex) when (ex is DartException or HttpRequestException or InvalidDataException or TaskCanceledException)
            {
                Log.Write("DART 고유번호 목록 실패, 내장 값 사용: " + ex.Message);
                foreach (var s in need)
                    if (DartClient.KnownCorpCodes.TryGetValue(s, out var c)) _corp[s] = c;
            }
        }

        return _settings.Watch
            .Where(w => _corp.ContainsKey(w.Symbol))
            .ToDictionary(w => w.Symbol, w => (_corp[w.Symbol], w.Name));
    }

    /// <summary>최근 45일 공시를 훑어 명분을 갱신한다. (IR 안내 공시는 보통 1~2주 전에 올라온다)</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        if (_running) return;
        _running = true;
        _lastRun = Kst.Now;
        try
        {
            var today = Kst.Today;
            var symbols = await SymbolsAsync(ct);
            var found = await _finder.FindAsync(symbols, today.AddDays(-45), today, resolveIrDates: true, ct);

            var known = _settings.AutoEvents.Select(Key).ToHashSet();
            var fresh = found.Where(e => !known.Contains(Key(e)) && e.ParsedDate >= today).ToList();

            _settings.AutoEvents = found;
            WriteJson(EventsPath, found);
            WriteJson(IrPath, _finder.IrDateCache);
            LastError = null;

            // 첫 실행(캐시가 비어 있던 경우)에도 오늘 이후 것은 알려 준다
            foreach (var e in fresh) NewUpcoming?.Invoke(e);
        }
        catch (DartException ex) when (ex.Status is "010" or "011" or "901")
        {
            LastError = "DART 인증키를 확인하세요: " + ex.Message;
            Log.Write(LastError);
        }
        catch (Exception ex) when (ex is DartException or HttpRequestException or TaskCanceledException or JsonException)
        {
            LastError = "DART 조회 실패: " + ex.Message;
            Log.Write(LastError);
        }
        finally
        {
            _running = false;
        }

        static string Key(CatalystEvent e) => $"{e.Date}|{e.Symbol}|{e.Title}";
    }

    private readonly Dictionary<string, (DateTimeOffset at, List<DartDisclosure> list)> _recent = new();

    /// <summary>한 종목의 최근 공시 전체 (최신순). 10분 캐시.</summary>
    public async Task<List<DartDisclosure>> GetRecentAsync(string symbol, int days = 90, bool force = false, CancellationToken ct = default)
    {
        if (!force && _recent.TryGetValue(symbol, out var c) && Kst.Now - c.at < TimeSpan.FromMinutes(10))
            return c.list;

        var symbols = await SymbolsAsync(ct);
        if (!symbols.TryGetValue(symbol, out var corp))
            throw new DartException("corp", "이 종목의 DART 고유번호를 찾지 못했습니다.");

        var today = Kst.Today;
        var list = await _client.ListAsync(corp.corpCode, today.AddDays(-days), today, ct);
        list.Reverse(); // 최신이 위로
        _recent[symbol] = (Kst.Now, list);
        return list;
    }

    private static string FinPath => Path.Combine(SettingsStore.Folder, "dart-financials.json");
    private Dictionary<string, ReportRaw>? _fin;

    /// <summary>리포트용 최근 6년 분기 실적. 지난 보고서는 파일 캐시라 두 번째부터는 1~2건만 묻는다.</summary>
    public async Task<List<QuarterFin>> GetQuartersAsync(string symbol, CancellationToken ct = default)
    {
        _fin ??= ReadJson<Dictionary<string, ReportRaw>>(FinPath) ?? new();
        var symbols = await SymbolsAsync(ct);
        string? corp = symbols.TryGetValue(symbol, out var c) ? c.corpCode
            : _corp.TryGetValue(symbol, out var c2) ? c2 : DartClient.KnownCorpCodes.GetValueOrDefault(symbol);
        if (corp is null) throw new DartException("corp", "이 종목의 DART 고유번호를 찾지 못했습니다.");
        int before = _fin.Count;
        var reports = await _client.GetReportsAsync(corp, 6, _fin, Log.Write, ct);
        if (_fin.Count != before) WriteJson(FinPath, _fin);
        return DartFinancials.ToQuarters(reports);
    }

    private static T? ReadJson<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default; }
        catch (Exception ex) { Log.Write($"{Path.GetFileName(path)} 읽기 실패: {ex.Message}"); return default; }
    }

    private static void WriteJson<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
            }));
        }
        catch (Exception ex) { Log.Write($"{Path.GetFileName(path)} 저장 실패: {ex.Message}"); }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>트레이 메뉴의 '백테스트 실행'. 결과를 txt·csv 로 저장하고 메모장으로 연다.</summary>
internal static class BacktestRunner
{
    public static async Task<string> RunAsync(AppSettings settings, TossClient toss, DartSync? dart, int years, IProgress<string> progress)
    {
        var from = Kst.Today.AddYears(-years);
        var data = new Dictionary<string, List<DayRow>>();
        foreach (var w in settings.Watch)
        {
            progress.Report($"{w.Name} 과거 데이터 받는 중…");
            data[w.Symbol] = await Backtester.LoadAsync(toss, w.Symbol, from, null);
        }

        var catalysts = new List<CatalystEvent>(settings.Events);
        if (dart is not null)
        {
            progress.Report("DART 과거 공시 받는 중… (IR 본문을 읽느라 처음엔 몇 분 걸릴 수 있음)");
            try
            {
                var symbols = await dart.SymbolsAsync();
                catalysts.AddRange(await dart.Finder.FindAsync(symbols, from, Kst.Today, resolveIrDates: true));
            }
            catch (Exception ex) when (ex is DartException or HttpRequestException or TaskCanceledException)
            {
                Log.Write("백테스트 DART 조회 실패: " + ex.Message);
                progress.Report("DART 조회 실패 — 설정 파일의 Events 만으로 계산합니다.");
            }
        }

        Dictionary<DateOnly, decimal>? market = null;
        try
        {
            progress.Report("코스피 일봉 받는 중…");
            market = await Backtester.LoadMarketChangesAsync(toss, from);
        }
        catch (Exception ex) when (ex is TossApiException or HttpRequestException or TaskCanceledException)
        {
            Log.Write("백테스트 코스피 조회 실패: " + ex.Message);
        }

        progress.Report("계산 중…");
        var report = Backtester.Run(data, catalysts, settings.Strategy, null, market);
        var names = settings.Watch.ToDictionary(w => w.Symbol, w => w.Name);

        var stamp = Kst.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        var txt = Path.Combine(SettingsStore.Folder, $"backtest-{stamp}.txt");
        var csv = Path.Combine(SettingsStore.Folder, $"backtest-{stamp}.csv");
        File.WriteAllText(txt, Backtester.Format(report, names), new System.Text.UTF8Encoding(true));
        File.WriteAllText(csv, Backtester.ToCsv(report, names), new System.Text.UTF8Encoding(true)); // BOM: 엑셀 한글
        return txt;
    }
}
