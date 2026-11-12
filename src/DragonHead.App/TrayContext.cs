using System.Diagnostics;
using DragonHead.Core;

namespace DragonHead.App;

/// <summary>
/// 트레이에 상주하면서 주기적으로 데이터를 갱신하고, 정해진 시각과 상황 변화에 맞춰 토스트를 띄운다.
/// 주문 기능은 없다.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly TossClient _client;
    private readonly DartSync? _dart;
    private readonly StockDirectory _directory = new();
    private readonly StockDirectory _usDirectory = new(StockDirectory.UsMarkets);
    private MarketForm? _marketForm;
    private bool _backtesting;
    private readonly MarketDataService _data;
    private readonly NotifyIcon _tray;
    private readonly DashboardForm _dashboard;
    private readonly System.Windows.Forms.Timer _timer;

    private List<Evaluation> _evals = new();
    private DateTimeOffset? _updatedAt;
    private DateTimeOffset _lastRefreshAttempt = DateTimeOffset.MinValue;
    private string? _error;
    private bool _refreshing;
    private readonly JournalStore _journal = new();
    private DateTimeOffset _lastUsTick = DateTimeOffset.MinValue;
    private bool _usRefreshing;

    private DateOnly _flagsDay;
    private readonly HashSet<string> _fired = new();

    private readonly TimeOnly _brakeAt, _firstAt, _finalAt;

    public TrayContext(AppSettingsHolder holder)
    {
        _settings = holder.Settings;
        _client = new TossClient(_settings.ClientId, holder.Secret);
        _data = new MarketDataService(_client, _settings) { Log = Log.Write };
        if (!string.IsNullOrWhiteSpace(_settings.DartApiKey))
        {
            _dart = new DartSync(_settings);
            _dart.NewUpcoming += OnNewCatalyst;
        }

        _brakeAt = NotifySettings.Parse(_settings.Notify.MorningBrake, new TimeOnly(8, 55));
        _firstAt = NotifySettings.Parse(_settings.Notify.FirstVerdict, new TimeOnly(14, 50));
        _finalAt = NotifySettings.Parse(_settings.Notify.FinalVerdict, new TimeOnly(15, 15));

        var window = WindowStore.Load();
        _dashboard = new DashboardForm(window, () => WindowStore.Save(window)) { HideAmounts = _settings.HideAmounts };
        _dashboard.RefreshRequested += async (_, _) => { _data.ExpireAccount(); _data.ForceBars = true; await RefreshAsync(force: true); };
        _dashboard.SettingsRequested += (_, _) => OpenSettings();
        _dashboard.StockClicked += OpenDisclosures;
        _dashboard.AlertsRequested += ShowAlerts;
        _dashboard.JudgeNowRequested += async () => await JudgeNowAsync();
        _dashboard.MarketRequested += ShowMarket;
        _dashboard.BudgetRequested += OpenBudgetSetup;
        _dashboard.UnwatchRequested += (symbol, name) => SetWatched(new StockInfo(symbol, name, "", "", true), false);
        _alertLog.Changed += UpdateUnread;
        _dashboard.SetUnread(_alertLog.Unread);
        _dashboard.LookChanged += () =>
        {
            // 공시·시장·알림 창도 상황판 투명도·고정을 그대로 따라간다
            var popups = new List<Form?> { _marketForm, _alertsForm };
            popups.AddRange(_disclosureForms.Values);
            foreach (var f in popups.OfType<Form>().Where(f => !f.IsDisposed))
            {
                f.Opacity = _dashboard.Opacity;
                f.TopMost = _dashboard.TopMost;
            }
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("대시보드 열기", null, (_, _) => ShowDashboard());
        menu.Items.Add("지금 새로고침", null, async (_, _) => { _data.ExpireAccount(); _data.ForceBars = true; await RefreshAsync(force: true); });
        menu.Items.Add("시장 · 종목 검색", null, (_, _) => ShowMarket());
        menu.Items.Add("알림 기록", null, (_, _) => ShowAlerts());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("예산 · 분할 설정", null, (_, _) => OpenBudgetSetup());
        menu.Items.Add("API 키 설정", null, (_, _) => OpenKeySetup());
        menu.Items.Add("설정 파일 열기 (수정 후 재시작)", null, (_, _) => OpenSettings());
        menu.Items.Add("로그 폴더 열기", null, (_, _) => OpenFolder());
        menu.Items.Add("진단 정보 저장", null, async (_, _) => await SaveDiagnosticsAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("명분 공시 지금 확인 (DART)", null, async (_, _) => await CheckDartNowAsync());
        menu.Items.Add("백테스트 실행 (최근 5년)", null, async (_, _) => await RunBacktestAsync(5));
        menu.Items.Add("판단 기록 · 성적표", null, async (_, _) => await OpenJournalAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = Theme.TrayIcon(Theme.Gray),
            Text = "용머리 알리미 · 준비 중",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleDashboard(); };

        _timer = new System.Windows.Forms.Timer { Interval = 1_000 };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();

        _ = TickAsync();
        _ = EnsureDirectoryAsync();   // 검색용 종목 목록 (하루 한 번)
    }

    // ───────────────────────── 주기 처리 ─────────────────────────

    private async Task TickAsync()
    {
        var now = Kst.Now;
        var today = DateOnly.FromDateTime(now.DateTime);
        if (_flagsDay != today)
        {
            _flagsDay = today;
            _fired.Clear();
            _dropSince.Clear();
        }

        if (_dart is not null && _dart.Due(now))
            _ = _dart.RunAsync();               // 공시 확인은 따로 돈다 (시세 갱신을 막지 않게)

        if (now - _lastRefreshAttempt >= RefreshInterval(now))
            await RefreshAsync(force: false);   // 끝나면 상황판도 갱신
        else if (_dashboard.Visible && now.Second == 0)
            RenderDashboard();                  // 장 밖에서도 시계·남은 시간은 분 단위로 갱신

        // 미장: 열려 있는 동안 상황판을 보고 있으면 2초, 아니면 5분마다 따로 갱신
        if (UsSemis.IsOpen(now) && !_usRefreshing && now - _lastUsTick >= (_dashboard.Visible ? TimeSpan.FromSeconds(2) : TimeSpan.FromMinutes(5)))
        {
            _usRefreshing = true;
            _lastUsTick = now;
            try
            {
                await _data.RefreshUsLiveAsync();
                if (_dashboard.Visible) RenderDashboard();
            }
            catch (Exception ex) { Log.Write("미장 갱신 실패: " + ex.Message); }
            finally { _usRefreshing = false; }
        }

        RunScheduledToasts(now);
    }

    private TimeSpan RefreshInterval(DateTimeOffset now)
    {
        var t = TimeOnly.FromDateTime(now.DateTime);
        bool weekend = now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        bool holiday = _data.Market is { IsOpen: false } m && m.Date == DateOnly.FromDateTime(now.DateTime);

        if (weekend || holiday) return TimeSpan.FromHours(3);
        // 장중 현재가: 상황판이 열려 있으면 1초, 닫혀 있으면 5초.
        // MARKET_DATA 한도(초당 15회) 대비 초당 1회 / 0.2회. 분봉·수급·계좌는 서비스 안에서 각자 주기.
        if (t >= new TimeOnly(9, 0) && t < new TimeOnly(15, 31))
            return _dashboard.Visible ? TimeSpan.FromSeconds(0.9) : TimeSpan.FromSeconds(4.9);
        // NXT 프리마켓(08:00~09:00)·애프터마켓(15:30~20:00): 상황판을 보고 있으면 1초, 아니면 30분.
        // 애프터마켓 시간대의 30분 주기는 저녁 확정 수급 반영도 겸한다.
        bool nxt = (t >= new TimeOnly(8, 0) && t < new TimeOnly(9, 0)) || (t >= new TimeOnly(15, 31) && t < new TimeOnly(20, 0));
        if (nxt && _dashboard.Visible) return TimeSpan.FromSeconds(0.9);
        if (t >= new TimeOnly(8, 50) && t < new TimeOnly(9, 0)) return TimeSpan.FromSeconds(60);
        if (nxt) return TimeSpan.FromMinutes(30);
        return TimeSpan.FromHours(2);
    }

    private async Task RefreshAsync(bool force)
    {
        if (_refreshing) return;
        _refreshing = true;
        _lastRefreshAttempt = Kst.Now;
        try
        {
            var snaps = await _data.RefreshAsync();
            var previous = _evals;
            _evals = snaps.Select(s => SignalEngine.Evaluate(s, _settings.Strategy)).ToList();
            var tags = TagEngine.BuildAll(snaps, _evals);
            foreach (var e in _evals)
                e.Tags = tags.GetValueOrDefault(e.Symbol) ?? new List<Tag>();
            _updatedAt = Kst.Now;
            _error = null;
            _journal.Record(snaps, _evals);
            OnEvaluations(previous, _evals);
        }
        catch (Exception ex) when (ex is TossApiException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _error = ex is TaskCanceledException ? "토스 서버 응답 시간 초과" : ex.Message;
            Log.Write("갱신 실패: " + ex);

            if (ex is TossApiException { Status: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized } api
                && _fired.Add("auth-error"))
            {
                Toast("토스 API 연결 실패", api.Message, Theme.Red, sticky: true);
            }
        }
        catch (Exception ex)
        {
            _error = "알 수 없는 오류: " + ex.Message;
            Log.Write("갱신 실패(예상 밖): " + ex);
        }
        finally
        {
            _refreshing = false;
            UpdateTray();
            if (_dashboard.Visible || force) RenderDashboard();
        }
    }

    // ───────────────────────── 알림 규칙 ─────────────────────────

    /// <summary>상황이 바뀔 때 뜨는 알림 (하루 종목당 1번).</summary>
    private void OnEvaluations(IReadOnlyList<Evaluation> previous, IReadOnlyList<Evaluation> current)
    {
        foreach (var e in current)
        {
            switch (e.Verdict)
            {
                case Verdict.GapTrap when _fired.Add($"gap:{e.Symbol}"):
                    Toast($"{e.Name} 시초가 갭상승 {SignalEngine.Signed(e.GapPct ?? 0)}%",
                        e.Headline + $"\n09~10시는 구경만. {_firstAt:HH:mm}에 다시 알려드릴게요.",
                        Theme.Red);
                    break;

                case Verdict.NoChase when _fired.Add($"nochase:{e.Symbol}"):
                    Toast($"{e.Name} 명분일 상승 · 추격 금지",
                        e.Headline + $"\n외국인 {SignalEngine.Signed(e.ForeignTodayShares ?? 0)}주 · 기관 {SignalEngine.Signed(e.InstitutionTodayShares ?? 0)}주 (잠정)" +
                        "\n종가까지 기준가 아래로 밀리면 다시 알려드립니다.",
                        Theme.Amber);
                    break;

                case Verdict.DipCandidate when _fired.Add($"candidate:{e.Symbol}"):
                    Toast($"{e.Name} 명분일 눌림 후보",
                        e.Headline + $"\n아직 사지 마세요. {_firstAt:HH:mm}·{_finalAt:HH:mm}에 기준가 아래인지 다시 봅니다.",
                        Theme.Blue);
                    break;

                case Verdict.ShockWarn when _fired.Add($"shock:{e.Symbol}"):
                    Toast($"{e.Name} 쇼크 의심", e.Headline + "\n종목 창 → 공시 탭에서 실적 내용을 먼저 확인하세요.", Theme.Red, sticky: true);
                    break;
            }

            CheckCandidateDrop(e);
        }
    }

    // 후보 알림 이후 조건이 깨진 시각. 잠깐 튀었다 돌아오는 경우(1초 갱신 노이즈)를 거르기 위해 1분 이상 지속될 때만 알린다.
    private readonly Dictionary<string, DateTimeOffset> _dropSince = new();
    private static readonly TimeSpan DropConfirm = TimeSpan.FromMinutes(1);

    private void CheckCandidateDrop(Evaluation e)
    {
        if (!_fired.Contains($"candidate:{e.Symbol}") || _fired.Contains($"drop:{e.Symbol}"))
            return;

        bool stillOn = e.Verdict is Verdict.DipCandidate or Verdict.DipBuy or Verdict.ShockWarn;
        if (stillOn)
        {
            _dropSince.Remove(e.Symbol);
            return;
        }

        var now = Kst.Now;
        if (!_dropSince.TryGetValue(e.Symbol, out var since))
        {
            _dropSince[e.Symbol] = now;
            return;
        }
        if (now - since < DropConfirm) return;

        _fired.Add($"drop:{e.Symbol}");
        var reasons = e.Checks.Where(c => c.Passed == false).Select(c => $"· {c.Label}: {c.Detail}").ToList();
        var body = (reasons.Count > 0 ? string.Join("\n", reasons) : e.Headline)
                   + "\n\n기준가 위로 올라왔습니다. 명분일에 오르면 추격하지 않습니다. 종가 전에 다시 밀리면 14:50·15:15 판정에 나옵니다.";
        Toast($"{e.Name} 눌림 후보 해제", body, Theme.Amber);
    }

    /// <summary>정해진 시각 알림. 앱을 늦게 켰을 때 엉뚱한 시간에 뜨지 않도록 20분 창 안에서만 발화한다.</summary>
    private void RunScheduledToasts(DateTimeOffset now)
    {
        var phase = SignalEngine.GetPhase(now, _data.Market);
        if (phase == Phase.Holiday) return;

        var t = TimeOnly.FromDateTime(now.DateTime);
        bool InWindow(TimeOnly at) => t >= at && t < at.AddMinutes(20);

        if (InWindow(_brakeAt) && _fired.Add("brake"))
        {
            var today = DateOnly.FromDateTime(now.DateTime);
            var events = _settings.AllEvents.Where(e => e.ParsedDate == today)
                .Select(e => $"· {NameOf(e.Symbol)} {e.Title}{(e.Source == "DART" ? " (DART)" : "")}").Distinct().ToList();
            var body = "9~10시 움직임은 흔들리기 쉬워 판단하지 않습니다. 구경만 하세요." +
                       (_settings.Notify.CheckEveryMinutes > 0 ? $" {_settings.Notify.CheckFrom}부터 {_settings.Notify.CheckEveryMinutes}분마다 중간 점검," : "") +
                       $" 판정은 {_firstAt:HH:mm}.\n\n" +
                       (events.Count > 0
                           ? "오늘 등록된 명분\n" + string.Join("\n", events)
                           : "오늘 등록된 명분 없음 → 기회 몫은 쉬고, 기본 몫은 평소 판단(하락·보합 마감이면 괜찮음)을 따릅니다.");
            Toast("오늘의 브레이크", body, Theme.Amber);
        }

        if (_evals.Count == 0) return;

        // 장중 중간 점검 (기본 10:00~14:30, 30분마다). 판정 시각 전에도 흐름을 놓치지 않게.
        var every = _settings.Notify.CheckEveryMinutes;
        var checkFrom = NotifySettings.Parse(_settings.Notify.CheckFrom, new TimeOnly(10, 0));
        var checkTo = NotifySettings.Parse(_settings.Notify.CheckTo, new TimeOnly(14, 30));
        if (every > 0 && t >= checkFrom && t <= checkTo.AddMinutes(9) && t < _firstAt)
        {
            int slot = (int)((t - checkFrom).TotalMinutes / every);
            var slotAt = checkFrom.AddMinutes(slot * every);
            if (slotAt <= checkTo && t < slotAt.AddMinutes(10) && _fired.Add($"check-{slotAt:HHmm}"))
            {
                var lines = _evals.Select(e =>
                {
                    var line = $"{e.Name} {e.Last:N0} ({SignalEngine.Signed(e.ChangePct ?? 0)}%) · {Theme.Badge(e).text}";
                    if (e.General is { Verdict: not GeneralVerdict.Wait } gg && Theme.Badge(e).text == GeneralView.Describe(gg.Verdict))
                        line += "\n  " + gg.Opinion;
                    var flow = new List<string>();
                    if (e.ForeignTodayShares is { } f) flow.Add($"외인 {f:+#,0;-#,0;0}");
                    if (e.InstitutionTodayShares is { } ins) flow.Add($"기관 {ins:+#,0;-#,0;0}");
                    if (flow.Count > 0) line += "\n  " + string.Join(" · ", flow) + "주 (잠정)";
                    if (e.FromHighPct is { } fh) line += $" · 고가 대비 {SignalEngine.Signed(fh)}%";
                    var top = e.Tags.Take(2).Select(tg => "#" + tg.Text).ToList();
                    if (top.Count > 0) line += "\n  " + string.Join(" ", top);
                    return line;
                });
                Toast($"{slotAt:HH:mm} 중간 점검", string.Join("\n", lines) + $"\n\n최종 판정은 {_firstAt:HH:mm} · {_finalAt:HH:mm}",
                      Theme.LoudestColor(_evals));
            }
        }

        if (InWindow(_firstAt) && _fired.Add("first"))
        {
            var lines = _evals.Select(e =>
            {
                var (bt, _) = Theme.Badge(e);
                var line = $"{e.Name}: {bt}\n  {(e.General is { Verdict: not GeneralVerdict.Wait } g && bt == GeneralView.Describe(g.Verdict) ? g.Opinion : e.Headline)}";
                if (e.Plan is { Complete: false } p) line += $"\n  {p.Summary}" + (p.ReadyPart is { } rp ? $" · {rp.Reason}" : "");
                var top = e.Tags.Take(3).Select(t => "#" + t.Text).ToList();
                return top.Count > 0 ? line + "\n  " + string.Join(" ", top) : line;
            });
            Toast("14:50 판정", string.Join("\n", lines), Theme.LoudestColor(_evals));
        }

        if (InWindow(_finalAt) && _fired.Add("final"))
        {
            // 살 차례인 몫이 있는 종목: 기회 몫(명분일 눌림)이 먼저, 아니면 기본 몫(정기 분할)
            var ready = _evals.Where(e => e.Plan?.ReadyPart is not null).ToList();
            foreach (var e in ready)
            {
                var part = e.Plan!.ReadyPart!;
                var body = (part == e.Plan.Opp ? e.Headline + "\n\n" : "") + part.Reason + "\n" +
                           (part.Shares > 0
                               ? $"{part.Name} {part.NextIndex}/{part.Total}차 · {part.Shares}주 ≈ {SignalEngine.Won(part.Amount)}"
                               : part.CashNote ?? "1회분으로는 1주가 안 됩니다") +
                           (part.Shares > 0 && part.CashNote is { } cn2 ? "\n" + cn2 : "") +
                           (part.NoSplitWarning is { } nsw ? "\n⚠ " + nsw : "") +
                           $"\n{e.Plan.Summary}" +
                           "\n\n종가 동시호가 15:20~15:30. 주문은 토스 앱에서 직접 하세요.";
                Toast($"{e.Name} · {part.Name} {part.NextIndex}차 매수 차례", body, Theme.Green, sticky: true);
            }
            if (ready.Count == 0)
            {
                Toast("오늘은 매수 차례 없음",
                    "명분일 눌림도, 기본 몫 매수일도 아닙니다. 현금을 지킨 것도 수익입니다.\n" +
                    string.Join("\n", _evals.Select(e => $"{e.Name}: {SignalEngine.Describe(e.Verdict)}" + (e.Plan is { } p2 ? $" · {p2.Summary}" : ""))),
                    Theme.Gray);
            }
        }
    }

    // ───────────────────────── UI ─────────────────────────

    private readonly AlertLog _alertLog = new();
    private AlertsForm? _alertsForm;

    /// <summary>알림을 띄우고 기록에도 남긴다</summary>
    private void Toast(string title, string body, Color accent, bool sticky = false, bool record = true)
    {
        if (record) _alertLog.Add(title, body, accent);
        ToastForm.Show(title, body, accent, _settings.Notify.ToastSeconds, sticky, ShowDashboard);
    }

    private void ShowAlerts()
    {
        if (_alertsForm is { IsDisposed: false })
        {
            _alertsForm.Activate();
            return;
        }
        if (!_dashboard.Visible) ShowDashboard();
        _alertsForm = new AlertsForm(_alertLog, _dashboard);
        _alertsForm.FormClosed += (_, _) => _alertsForm = null;
        _alertsForm.Show();
    }

    /// <summary>
    /// '지금 판정': 데이터를 새로 받아 지금 기준 종목별 판정·빠진 조건·태그를 알림으로.
    /// 14:50 / 15:15 자동 판정과는 따로 돈다.
    /// </summary>
    private async Task JudgeNowAsync()
    {
        _data.ForceBars = true;
        await RefreshAsync(force: true);
        if (_evals.Count == 0)
        {
            Toast("지금 판정", _error ?? "판정할 데이터가 아직 없습니다.", Theme.Gray);
            return;
        }

        var now = Kst.Now;
        var phase = _evals[0].Phase;
        var lines = new List<string>();
        foreach (var e in _evals)
        {
            bool catDay = e.Checks.FirstOrDefault(x => x.Label == "명분")?.Passed == true;
            var head = $"{e.Name} {e.Last:N0}원 {(e.ChangePct is { } c ? SignalEngine.Signed(c) + "%" : "")} · {Theme.Badge(e).text}";
            lines.Add(head);
            if (catDay || e.General is null)
            {
                lines.Add("  명분 판단: " + e.Headline);
                var failed = e.Checks.Where(x => x.Passed == false).Select(x => x.Label).ToList();
                if (failed.Count > 0 && e.Verdict is not Verdict.Wait) lines.Add("  미충족: " + string.Join(", ", failed));
            }
            else
            {
                lines.Add("  평소 판단: " + e.General.Opinion);
                var flagged = e.General.Checks.Where(x => x.Passed == false).Select(x => $"{x.Label}({x.Detail})").ToList();
                if (flagged.Count > 0) lines.Add("  걸리는 점: " + string.Join(", ", flagged));
            }
            if (e.Plan is { } plan) lines.Add($"  {plan.Summary}" + (plan.ReadyPart is { } rp ? $" · {rp.Reason}" : ""));
            var tags = e.Tags.Take(3).Select(t => "#" + t.Text).ToList();
            if (tags.Count > 0) lines.Add("  " + string.Join(" ", tags));
        }

        var note = phase switch
        {
            Phase.PreMarket or Phase.MorningTrap or Phase.Midday or Phase.Afternoon =>
                "※ 정식 판정은 14:50. 지금 숫자 기준 참고용이고, 오후 수급에 따라 바뀔 수 있습니다.",
            Phase.Decision or Phase.ClosingAuction => "※ 판정 구간입니다.",
            Phase.Closed => "※ 장 마감 후. 오늘 종가 기준이며 수급은 저녁에 확정됩니다.",
            _ => "※ 휴장일입니다.",
        };
        lines.Add("");
        lines.Add(note);

        Toast($"지금 판정 · {now:HH:mm}", string.Join("\n", lines), Theme.LoudestColor(_evals), sticky: true);
    }

    private void UpdateUnread()
    {
        _dashboard.SetUnread(_alertLog.Unread);
        UpdateTray();
    }

    private void UpdateTray()
    {
        _tray.Icon = Theme.TrayIcon(_error is null ? Theme.LoudestColor(_evals) : Theme.Red);

        string text;
        if (_error is not null)
            text = "용머리 · 오류: " + _error;
        else if (_evals.Count == 0)
            text = "용머리 알리미";
        else
            text = "용머리 · " + string.Join(" / ", _evals.Select(e =>
                $"{e.Name} {(e.ChangePct is { } c ? SignalEngine.Signed(c) + "%" : "")} {Theme.Badge(e).text}"));

        if (_alertLog.Unread > 0) text = $"[알림 {_alertLog.Unread}] " + text;
        _tray.Text = text.Length > 127 ? text[..124] + "..." : text;
    }

    private void RenderDashboard() =>
        _dashboard.Render(_evals, _data.Account, _updatedAt, _error, _data.LastAccountError, _data.LastBaseNote, _data.MarketContext);

    private void ShowDashboard()
    {
        RenderDashboard();
        _dashboard.Show();
        _dashboard.Activate();
    }

    private void ToggleDashboard()
    {
        if (_dashboard.Visible) _dashboard.Hide();
        else if (DateTime.UtcNow - _dashboard.LastAutoHidden > TimeSpan.FromMilliseconds(400)) ShowDashboard();
    }

    private static void OpenSettings() => SettingsStore.OpenInEditor();

    /// <summary>트레이 메뉴 '예산 · 분할 설정': 저장하면 설정 파일에 쓰고 바로 다시 판정한다.</summary>
    private async void OpenBudgetSetup()
    {
        using var f = new BudgetForm(_settings);
        if (f.ShowDialog() != DialogResult.OK) return;
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log.Write("예산 설정 저장 실패: " + ex.Message); }
        await RefreshAsync(force: true);
    }

    /// <summary>트레이 메뉴 'API 키 설정': 저장하면 새 키로 앱을 다시 시작한다.</summary>
    private void OpenKeySetup()
    {
        AppSettingsHolder current;
        try { current = SettingsStore.Load(out _); }
        catch (Exception ex) { Toast("설정 파일 오류", ex.Message, Theme.Red); return; }

        using var form = new KeySetupForm(current.Settings, current.Secret, firstRun: false);
        if (form.ShowDialog() != DialogResult.OK) return;

        try
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            Process.Start(new ProcessStartInfo(exe, "--restart") { UseShellExecute = false });
            ExitThread();
        }
        catch (Exception ex)
        {
            Log.Write("재시작 실패: " + ex.Message);
            Toast("저장했습니다", "새 키는 앱을 다시 켜야 적용됩니다.", Theme.Amber);
        }
    }

    private string NameOf(string symbol) =>
        string.IsNullOrWhiteSpace(symbol) || symbol == "*" ? "전체" : _settings.Watch.FirstOrDefault(w => w.Symbol == symbol)?.Name ?? symbol;

    private static string DirectoryCache => Path.Combine(SettingsStore.Folder, "stocks.json");

    private async Task EnsureDirectoryAsync()
    {
        if (_directory.LoadedOn == Kst.Today && _directory.Count > 0) return;
        try
        {
            await _directory.LoadAsync(_client, DirectoryCache);
            _marketForm?.WatchChanged();
        }
        catch (Exception ex) when (ex is TossApiException or HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Write("종목 목록 받기 실패: " + ex.Message);
        }
    }

    private async void ShowMarket()
    {
        if (_marketForm is { IsDisposed: false })
        {
            _marketForm.Activate();
            return;
        }
        if (!_dashboard.Visible) ShowDashboard();
        _marketForm = new MarketForm(_client, _directory, _usDirectory,
            () => _usDirectory.LoadAsync(_client, Path.Combine(SettingsStore.Folder, "stocks-us.json")),
            symbol => _settings.Watch.Any(w => w.Symbol == symbol),
            SetWatched, OpenDisclosures, _dashboard);
        _marketForm.FormClosed += (_, _) => _marketForm = null;
        _marketForm.Show();
        await EnsureDirectoryAsync();
    }

    /// <summary>감시 종목 추가·해제. 설정 파일에 저장하고 바로 상황판에 반영한다.</summary>
    private async void SetWatched(StockInfo stock, bool on)
    {
        var exists = _settings.Watch.Any(w => w.Symbol == stock.Symbol);
        if (on == exists) return;

        if (on)
        {
            _settings.Watch.Add(new WatchItem { Symbol = stock.Symbol, Name = stock.Name });
            if (_settings.Watch.Count == 7)
                Toast("감시 종목이 7개가 됐어요",
                    "종목마다 분봉·수급을 따로 받아서 많아지면 갱신이 느려지거나 API 한도에 걸릴 수 있습니다. 6개 이하를 권장합니다.", Theme.Amber);
        }
        else
        {
            _settings.Watch.RemoveAll(w => w.Symbol == stock.Symbol);
        }

        try { SettingsStore.Save(_settings); }
        catch (Exception ex) { Log.Write("감시 종목 저장 실패: " + ex.Message); }

        _marketForm?.WatchChanged();
        _data.ForceBars = true;
        await RefreshAsync(force: true);
        if (on && _dart is not null) _ = _dart.RunAsync();   // 새 종목의 DART 고유번호·명분 공시
    }

    private readonly Dictionary<string, DisclosureForm> _disclosureForms = new();

    /// <summary>종목 카드를 누르면 공시 목록 창. 이미 열려 있으면 앞으로 가져온다.</summary>
    private void OpenDisclosures(string symbol, string name)
    {
        if (_disclosureForms.TryGetValue(symbol, out var open) && !open.IsDisposed)
        {
            open.Activate();
            return;
        }
        var form = new DisclosureForm(symbol, name, _settings, _dart, _dashboard, _client,
            sym => _evals.FirstOrDefault(e => e.Symbol == sym), () => _data.MarketContext);
        form.FormClosed += (_, _) => _disclosureForms.Remove(symbol);
        _disclosureForms[symbol] = form;
        form.Show();
    }

    private void OnNewCatalyst(CatalystEvent e)
    {
        var day = e.ParsedDate is { } d ? (d == Kst.Today ? "오늘" : d.ToString("M/d (ddd)", System.Globalization.CultureInfo.GetCultureInfo("ko-KR"))) : e.Date;
        Toast($"명분 등록 · {NameOf(e.Symbol)} {day}",
            e.Title + "\n\nDART 공시에서 자동으로 찾았습니다. 그날 14:50 판정에 명분으로 들어갑니다.",
            Theme.Blue);
    }

    private async Task CheckDartNowAsync()
    {
        if (_dart is null)
        {
            Toast("DART 인증키 없음",
                "설정 파일의 DartApiKey 에 opendart.fss.or.kr 에서 받은 인증키를 넣고 앱을 다시 실행하세요.", Theme.Gray);
            return;
        }
        await _dart.RunAsync();
        var upcoming = _settings.AutoEvents.Where(e => e.ParsedDate >= Kst.Today).ToList();
        Toast(_dart.LastError is null ? "DART 확인 완료" : "DART 확인 실패",
            _dart.LastError ?? (upcoming.Count > 0
                ? "앞으로의 명분\n" + string.Join("\n", upcoming.Select(e => $"· {e.Date} {NameOf(e.Symbol)} {e.Title}"))
                : "최근 45일 안에 앞으로 남은 명분 공시가 없습니다."),
            _dart.LastError is null ? Theme.Blue : Theme.Red);
        if (_dashboard.Visible) RenderDashboard();
    }

    private async Task OpenJournalAsync()
    {
        if (_journal.Count == 0)
        {
            Toast("판단 기록", "아직 기록이 없습니다. 거래일 10:00~15:40 에 앱이 켜져 있으면 종목별 판단과 밤사이 미국 반도체가 매일 쌓입니다.", Theme.Gray);
            return;
        }
        try { await _journal.OpenReportAsync(_client); }
        catch (Exception ex)
        {
            Log.Write("성적표 실패: " + ex);
            Toast("성적표 실패", ex.Message, Theme.Red);
        }
    }

    private async Task RunBacktestAsync(int years)
    {
        if (_backtesting) { Toast("백테스트 진행 중", "이미 돌고 있습니다. 끝나면 결과 파일이 열립니다.", Theme.Gray); return; }
        _backtesting = true;
        Toast("백테스트 시작", $"최근 {years}년 일봉·외국인 수급·공시를 받아 계산합니다. 몇 분 걸릴 수 있어요.\n끝나면 결과를 메모장으로 엽니다.", Theme.Gray);
        try
        {
            var progress = new Progress<string>(m => Log.Write("백테스트: " + m));
            var path = await BacktestRunner.RunAsync(_settings, _client, _dart, years, progress);
            Toast("백테스트 완료", path + "\n같은 이름의 .csv 는 엑셀로 열 수 있습니다.", Theme.Green);
            try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("메모장 열기 실패: " + ex.Message); }
        }
        catch (Exception ex)
        {
            Log.Write("백테스트 실패: " + ex);
            Toast("백테스트 실패", ex.Message, Theme.Red, sticky: true);
        }
        finally
        {
            _backtesting = false;
        }
    }


    /// <summary>
    /// 가격·기준가가 이상할 때 원인을 보려고 토스 응답 원문을 파일로 남긴다.
    /// 시세·랭킹·상하한가·분봉·일봉만 담고, 계좌·키 정보는 넣지 않는다.
    /// </summary>
    private async Task SaveDiagnosticsAsync()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"// 용머리 알리미 진단 {Kst.Now:yyyy-MM-dd HH:mm:ss} KST (계좌·키 정보 없음)");
        async Task Dump(string title, string path)
        {
            sb.AppendLine($"\n// ── {title}  GET {path}");
            try { sb.AppendLine(await _client.GetRawAsync(path)); }
            catch (Exception ex) { sb.AppendLine("// 실패: " + ex.Message); }
        }

        var symbols = string.Join(',', _settings.Watch.Select(w => w.Symbol));
        await Dump("현재가", $"/api/v1/prices?symbols={symbols}");
        await Dump("장 운영", "/api/v1/market-calendar/KR");
        await Dump("미국 반도체 현재가", "/api/v1/prices?symbols=" + string.Join(',', UsSemis.Default.Select(x => x.Symbol)));
        await Dump("SOXX 일봉 3개", "/api/v1/candles?symbol=SOXX&interval=1d&count=3");
        await Dump("거래대금 랭킹 상위 5", "/api/v1/rankings?type=MARKET_TRADING_AMOUNT&marketCountry=KR&duration=1d&count=5");
        foreach (var w in _settings.Watch)
        {
            await Dump($"{w.Name} 상하한가", $"/api/v1/price-limits?symbol={w.Symbol}");
            await Dump($"{w.Name} 일봉 3개", $"/api/v1/candles?symbol={w.Symbol}&interval=1d&count=3");
            await Dump($"{w.Name} 최근 1분봉 90개", $"/api/v1/candles?symbol={w.Symbol}&interval=1m&count=90");
        }

        var path = Path.Combine(SettingsStore.Folder, $"diag-{Kst.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, sb.ToString());
        Toast("진단 정보 저장됨", path + "\n이 파일을 Claude에게 보내주세요. 계좌·키 정보는 들어있지 않습니다.", Theme.Gray);
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("탐색기 열기 실패: " + ex.Message); }
    }

    private static void OpenFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SettingsStore.Folder}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("폴더 열기 실패: " + ex.Message); }
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _journal.Save();
        _tray.Visible = false;
        _tray.Dispose();
        _dashboard.AutoHide = false;
        _dashboard.Dispose();
        _client.Dispose();
        _dart?.Dispose();
        base.ExitThreadCore();
    }
}
