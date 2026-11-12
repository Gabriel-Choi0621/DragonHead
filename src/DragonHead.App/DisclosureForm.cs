using System.Diagnostics;
using System.Globalization;
using DragonHead.Core;

namespace DragonHead.App;

/// <summary>
/// 종목 창. 카드나 시장 창의 줄을 누르면 뜬다.
///   차트 탭: 1분·5분·10분·일·주·월·년 캔들(이동평균·거래량·기준가/평단·매수/명분 표시).
///            드래그로 이동, 휠로 확대, 왼쪽 끝으로 가면 이전 봉을 더 받아 붙인다. 마우스 설명, ? 도움말
///   공시 탭: 앞으로 남은 명분일 + 최근 90일 DART 공시 (줄을 누르면 원문)
///   리포트: 판정·차트·추세·수급·분할 매수·공시를 한 장짜리 HTML로 저장하고 브라우저로 연다
/// </summary>
internal sealed class DisclosureForm : PopupForm
{
    private enum Filter { Catalyst, Major, All }

    private readonly string _symbol;
    private readonly string _name;
    private readonly AppSettings _settings;
    private readonly DartSync? _dart;
    private readonly FlowLayoutPanel _list;
    private readonly Label _status;
    private readonly Dictionary<Filter, Label> _tabs = new();
    private Filter _filter = Filter.Major;
    private List<DartDisclosure>? _items;

    private readonly TossClient _toss;
    private readonly Func<string, Evaluation?> _evalOf;
    private readonly Func<MarketContext?> _marketOf;
    private readonly ChartControl _chart = new() { Dock = DockStyle.Fill };
    private readonly Panel _chartRow = new() { Location = new Point(12, 112), Height = 26, BackColor = Theme.Background };
    private readonly FlowLayoutPanel _filterRow;
    private readonly Dictionary<bool, Label> _mainTabs = new();     // true = 차트, false = 공시
    private readonly Dictionary<ChartInterval, Label> _modeChips = new();
    private readonly CandleHistory _dayHist = new("1d");
    private readonly CandleHistory _minHist = new("1m");
    private ChartInterval _interval = ChartInterval.Day;
    private bool _loadingOlder;
    private readonly TickBuffer _ticks = new();
    // 제목 옆 가격 · 아래 작은 줄 (상황판 카드와 같은 정보: 등락, 프리·애프터마켓, 오늘 수급)
    private readonly SegStrip _priceRow = new() { FontSize = 12f };
    private readonly SegStrip _subRow = new() { FontSize = 8.5f };
    private int _rowTop = 112;   // 차트 단위 줄 / 공시 필터 줄의 y (위 글자 줄이 접히면 내려간다)
    private readonly System.Windows.Forms.Timer _headTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _tickTimer = new() { Interval = 1000 };
    private bool _tickBusy;
    private string _viewText = "";
    private bool _chartTab = true;
    private List<Bar> _daily = new();
    private List<Bar> _minute = new();
    private decimal _last, _prevClose;
    private bool _reporting;
    private string? _pending;

    private static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");

    /// <summary>'주요' 필터에서 빼는 정례·지분 공시 (삼성처럼 임원 지분 보고가 수십 건씩 쌓이는 경우)</summary>
    private static readonly string[] Noise =
    {
        "소유상황보고서", "대량보유상황보고서", "대규모기업집단", "특수관계인", "공정거래", "증권발행실적", "투자설명서",
        "일괄신고", "채무증권", "임원ㆍ주요주주", "최대주주등소유주식", "기업지배구조",
    };

    public DisclosureForm(string symbol, string name, AppSettings settings, DartSync? dart, Form? near,
                          TossClient toss, Func<string, Evaluation?> evalOf, Func<MarketContext?> marketOf)
    {
        _symbol = symbol;
        _name = name;
        _settings = settings;
        _dart = dart;
        _toss = toss;
        _evalOf = evalOf;
        _marketOf = marketOf;

        Text = name;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(1);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        var win = WindowStore.Load();
        var scr = Screen.FromControl(near ?? this).WorkingArea;
        ClientSize = new Size(Math.Min(Math.Max(win.StockWidth ?? 1000, 640), scr.Width - 40), Math.Min(Math.Max(win.StockHeight ?? 780, 520), scr.Height - 40));
        MinimumSize = new Size(640, 520);
        if (Enum.TryParse<ChartInterval>(win.ChartInterval, out var savedIv)) _interval = savedIv;
        ResizeEnd += (_, _) =>
        {
            var w = WindowStore.Load();
            w.StockWidth = ClientSize.Width; w.StockHeight = ClientSize.Height;
            WindowStore.Save(w);
        };
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = near?.TopMost ?? true;
        Opacity = near?.Opacity ?? 1.0;
        DoubleBuffered = true;

        // 상황판 왼쪽에 붙여서, 자리가 없으면 오른쪽에
        var area = Screen.FromControl(near ?? this).WorkingArea;
        if (near is not null)
        {
            var x = near.Left - Width - 8;
            if (x < area.Left) x = Math.Min(near.Right + 8, area.Right - Width);
            Location = new Point(x, Math.Clamp(near.Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        }
        else
        {
            Location = new Point(area.Right - Width - 24, area.Bottom - Height - 24);
        }

        // 상단바
        var header = new Panel { Dock = DockStyle.Top, Height = 144 };
        var title = new Label { Text = name, AutoSize = true, Font = Theme.Font(13f, FontStyle.Bold), Location = new Point(14, 10) };
        var close = new Label
        {
            Text = "×",
            AutoSize = true,
            Font = Theme.Font(12f, FontStyle.Bold),
            ForeColor = Theme.Muted,
            Cursor = Cursors.Hand,
            Padding = new Padding(6, 0, 6, 2),
        };
        close.Click += (_, _) => Close();
        close.MouseEnter += (_, _) => close.ForeColor = Theme.Red;
        close.MouseLeave += (_, _) => close.ForeColor = Theme.Muted;
        var refresh = new Label
        {
            Text = "새로고침",
            AutoSize = true,
            Font = Theme.Font(8.5f, FontStyle.Underline),
            ForeColor = Theme.Muted,
            Cursor = Cursors.Hand,
        };
        refresh.Click += async (_, _) => { await LoadChartAsync(); await LoadAsync(force: true); };

        // 1줄: [차트] [공시]                                    [리포트]
        var mainTabs = new FlowLayoutPanel { AutoSize = true, Location = new Point(12, 78), WrapContents = false, BackColor = Color.Transparent };
        foreach (var (isChart, label) in new[] { (true, "차트"), (false, "공시") })
        {
            var t = new Label
            {
                Text = label, AutoSize = true, Font = Theme.Font(10f, FontStyle.Bold),
                Padding = new Padding(12, 4, 12, 4), Margin = new Padding(0, 0, 4, 0), Cursor = Cursors.Hand,
            };
            t.Click += (_, _) => { _chartTab = isChart; ShowTab(); };
            _mainTabs[isChart] = t;
            mainTabs.Controls.Add(t);
        }
        var report = new Label
        {
            Text = "리포트", AutoSize = true, Font = Theme.Font(10f, FontStyle.Bold), ForeColor = Theme.Background, BackColor = Theme.Amber,
            Padding = new Padding(12, 4, 12, 4), Cursor = Cursors.Hand,
        };
        new ToolTip().SetToolTip(report, "판정·차트·추세·수급·분할 매수·공시를 한 장짜리 리포트로 만들어 브라우저로 엽니다");
        report.Click += async (_, _) => await MakeReportAsync();

        // 2줄(차트): [1분][5분][10분] [일][주][월][년]  · 2줄(공시): [명분] [주요] [전체]
        var modes = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Location = Point.Empty };
        foreach (var iv in Enum.GetValues<ChartInterval>())
        {
            var c = new Label
            {
                Text = iv.Label(), AutoSize = true, Font = Theme.Font(8.5f, FontStyle.Bold),
                Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 0, iv is ChartInterval.Tick or ChartInterval.Min10 ? 12 : 3, 0), Cursor = Cursors.Hand,
            };
            c.Click += async (_, _) => await SwitchIntervalAsync(iv);
            _modeChips[iv] = c;
            modes.Controls.Add(c);
        }
        modes.Controls.Add(new Label
        {
            Text = "드래그 이동 · 휠 확대 · 더블클릭 최신 · ? 보는 법", AutoSize = true, Font = Theme.Font(8f), ForeColor = Theme.Muted,
            Padding = new Padding(8, 4, 0, 0),
        });
        _chartRow.Controls.Add(modes);

        var tabs = new FlowLayoutPanel { AutoSize = true, Location = new Point(12, 112), WrapContents = false, BackColor = Color.Transparent };
        _filterRow = tabs;
        foreach (var (f, label) in new[] { (Filter.Catalyst, "명분"), (Filter.Major, "주요"), (Filter.All, "전체") })
        {
            var t = new Label
            {
                Text = label,
                AutoSize = true,
                Font = Theme.Font(9f, FontStyle.Bold),
                Padding = new Padding(10, 3, 10, 3),
                Margin = new Padding(0, 0, 4, 0),
                Cursor = Cursors.Hand,
            };
            t.Click += (_, _) => { _filter = f; Render(); };
            _tabs[f] = t;
            tabs.Controls.Add(t);
        }

        header.Controls.AddRange(new Control[] { title, close, refresh, mainTabs, report, _chartRow, tabs, _priceRow, _subRow });
        header.Layout += (_, _) =>
        {
            _priceRow.Location = new Point(title.Right + 8, title.Top + 3);
            _priceRow.Width = Math.Max(80, refresh.Left - _priceRow.Left - 8);
            _subRow.Location = new Point(14, title.Bottom + 4);
            _subRow.Width = header.Width - 28;
            // 아래 줄들은 작은 글씨 줄 높이에 맞춰 내려간다 (좁은 창에서 두 줄로 접히면 그만큼)
            mainTabs.Top = _subRow.Bottom + 8;
            report.Top = mainTabs.Top;
            _rowTop = mainTabs.Bottom + 8;
            (_chartTab ? (Control)_chartRow : tabs).Top = _rowTop;
            var h = _rowTop + 32;
            if (header.Height != h) header.Height = h;
            close.Location = new Point(header.Width - close.PreferredWidth - 6, 6);
            refresh.Location = new Point(close.Left - refresh.PreferredWidth - 8, 14);
            report.Left = header.Width - report.PreferredWidth - 14;
            _chartRow.Width = header.Width - 24;
        };
        foreach (Control c in new Control[] { header, title })
            c.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) Drag(); };

        _list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(10, 4, 10, 8),
        };
        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Font = Theme.Font(8.5f),
            ForeColor = Theme.Muted,
            Padding = new Padding(14, 4, 14, 0),
            AutoEllipsis = true,
        };

        _chart.NeedOlder += async () => await LoadOlderAsync();
        _tickTimer.Tick += async (_, _) => await PollTicksAsync();
        _headTimer.Tick += (_, _) => UpdateHead();
        FormClosed += (_, _) => { _headTimer.Stop(); _headTimer.Dispose(); };
        FormClosed += (_, _) => { _tickTimer.Stop(); _tickTimer.Dispose(); };
        _chart.ViewChanged += text => { _viewText = text; if (_chartTab && !_reporting) _status.Text = ChartStatus(); };

        Controls.Add(_list);
        Controls.Add(_chart);
        Controls.Add(_status);
        Controls.Add(header);

        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        ResizeEnd += (_, _) => Render();
        Shown += async (_, _) =>
        {
            ShowTab();
            PaintModeChips();
            UpdateHead();
            _headTimer.Start();
            await LoadChartAsync();
            UpdateHead();
            _tickTimer.Start();
            await LoadAsync(force: false);
        };
    }

    // ───────────────────────── 탭 · 차트 ─────────────────────────

    private void ShowTab()
    {
        foreach (var (isChart, t) in _mainTabs)
        {
            t.BackColor = isChart == _chartTab ? Theme.Text : Theme.Surface;
            t.ForeColor = isChart == _chartTab ? Theme.Background : Theme.Muted;
        }
        _chart.Visible = _chartTab;
        // 두 줄(봉 단위 / 공시 필터)이 같은 자리에 겹쳐 있어서, 안 쓰는 쪽은 화면 밖으로 치워 둔다
        // (투명 배경 패널이 다른 줄을 덮어 앞쪽 칩이 안 보이던 문제)
        _chartRow.Visible = _chartTab;
        _filterRow.Visible = !_chartTab;
        _chartRow.Location = new Point(_chartTab ? 12 : -3000, _rowTop);
        _filterRow.Location = new Point(_chartTab ? -3000 : 12, _rowTop);
        (_chartTab ? (Control)_chartRow : _filterRow).BringToFront();
        _list.Visible = !_chartTab;
        if (_chartTab) _status.Text = ChartStatus();
        else Render(_pending);
    }

    /// <summary>
    /// 제목 옆 '1,220,000원 -5.72%' 와 아래 '애프터마켓 1,219,000 · 종가 대비 … · 기준가 대비 … · 외국인 … · 기관 …'.
    /// 감시 종목이면 상황판 판정 값(1초 갱신)을, 아니면 차트를 불러올 때 받은 가격을 쓴다.
    /// </summary>
    private void UpdateHead()
    {
        var ev = _evalOf(_symbol);
        decimal last = ev?.Last ?? _last, prev = ev?.PrevClose is > 0 ? ev.PrevClose : _prevClose;
        if (last <= 0) return;
        decimal? chg = ev?.ChangePct ?? (prev > 0 ? Math.Round((last / prev - 1m) * 100m, 2) : null);

        static Color Pnl(decimal v) => v > 0 ? Theme.Red : v < 0 ? Theme.Blue : Theme.Text;
        static string WonS(decimal d) => (d > 0 ? "+" : d < 0 ? "-" : "") + Math.Abs(d).ToString("N0") + "원";
        var big = new List<(string, Color, bool)> { ($"{last:N0}원", chg is { } c0 && c0 != 0 ? Pnl(c0) : Theme.Text, true) };
        if (chg is { } c) big.Add(($"  {SignalEngine.Signed(c)}%", Pnl(c), true));

        var sub = new List<(string, Color, bool)>();
        if (ev?.ExtendedPrice is { } ext)
        {
            sub.Add(($"{(ev.Phase == Phase.PreMarket ? "프리마켓" : "애프터마켓")} {ext:N0}", Theme.Muted, false));
            if (ev.Phase != Phase.PreMarket && ev.ExtendedVsLastPct is { } vl)
            {
                sub.Add((" · 종가 대비 ", Theme.Muted, false));
                sub.Add(($"{WonS(ext - last)} ({SignalEngine.Signed(vl)}%)", Pnl(vl), true));
            }
            if (ev.ExtendedChangePct is { } vb && prev > 0)
            {
                sub.Add((" · 기준가 대비 ", Theme.Muted, false));
                sub.Add(($"{WonS(ext - prev)} ({SignalEngine.Signed(vb)}%)", Pnl(vb), true));
            }
            sub.Add(("   ", Theme.Muted, false));
        }
        else if (prev > 0)
        {
            sub.Add(($"기준가 {prev:N0}", Theme.Muted, false));
            sub.Add(("   ", Theme.Muted, false));
        }
        if (ev is not null)
        {
            void Flow(string who, long? sh)
            {
                sub.Add((who + " ", Theme.Muted, false));
                sub.Add(sh is { } v ? ($"{SignalEngine.Signed(v)}주 ({SignalEngine.Eok(v * last)})", Pnl(v), true) : ("-", Theme.Muted, false));
            }
            Flow("외국인", ev.ForeignTodayShares);
            sub.Add((" · ", Theme.Muted, false));
            Flow("기관", ev.InstitutionTodayShares);
            sub.Add((ev.ForeignTodayConfirmed ? " 확정" : " 잠정", Theme.Muted, false));
            // 판정은 카드 배지와 같은 기준 (명분일엔 명분 판단, 아닌 날엔 평소 판단)
            var (bt, bc) = Theme.Badge(ev);
            sub.Add(("  · " + bt, bc, true));
        }

        _priceRow.SetParts(big);
        if (_subRow.SetParts(sub)) _subRow.Parent?.PerformLayout();
    }

    private void PaintModeChips()
    {
        foreach (var (iv, c) in _modeChips)
        {
            c.BackColor = iv == _interval ? Theme.Text : Theme.Surface;
            c.ForeColor = iv == _interval ? Theme.Background : Theme.Muted;
        }
    }

    private string ChartStatus() =>
        _daily.Count == 0 ? "" : $"{_last:N0}원 · 기준가 {_prevClose:N0}원 · " +
            (_interval == ChartInterval.Tick
                ? $"{ChartIntervals.TicksPerBar}틱봉 · {(_ticks.Since is { } t ? t.ToString("HH:mm:ss") + "부터 " : "")}체결 {_ticks.Ticks.Count:N0}건" +
                  (_ticks.Gaps > 0 ? $" · 몰려서 놓친 구간 {_ticks.Gaps}번" : "")
                : $"{_interval.Label()}봉 {_viewText}") +
            (_loadingOlder ? " · 이전 봉 받는 중…" : "");

    /// <summary>틱 차트: 1초마다 최근 체결 50건을 받아 이어 붙인다 (틱 차트를 보고 있을 때만)</summary>
    private async Task PollTicksAsync()
    {
        if (_tickBusy || _interval != ChartInterval.Tick || !Visible || !_chartTab) return;
        _tickBusy = true;
        try
        {
            var added = _ticks.Add(await _toss.GetTradesAsync(_symbol));
            if (added > 0 && _interval == ChartInterval.Tick) ApplySeries(keepView: true);
            else if (_chartTab) _status.Text = ChartStatus();
        }
        catch (Exception ex) when (ex is TossApiException or HttpRequestException or TaskCanceledException)
        {
            Log.Write("체결 조회 실패: " + ex.Message);
            if (_chartTab) _status.Text = "체결 조회 실패: " + ex.Message;
        }
        finally { _tickBusy = false; }
    }

    /// <summary>
    /// 처음 받는 양: 일봉 3페이지(약 2년 반), 1분봉 4페이지(정규장 기준 하루 남짓).
    /// 월봉은 약 10년, 년봉은 받을 수 있는 만큼 필요해서 고를 때 더 받는다.
    /// </summary>
    private async Task EnsureDepthAsync(ChartInterval iv)
    {
        if (iv == ChartInterval.Tick)
        {
            if (_ticks.Ticks.Count == 0) _ticks.Add(await _toss.GetTradesAsync(_symbol));
            return;
        }
        var hist = iv.IsMinute() ? _minHist : _dayHist;
        int wantDays = iv switch { ChartInterval.Week => 1300, ChartInterval.Month => 2500, ChartInterval.Year => int.MaxValue, _ => 0 };
        if (!hist.Loaded) await hist.LoadOlderAsync(_toss, _symbol, iv.IsMinute() ? 4 : 3);
        int guard = 0;
        while (!iv.IsMinute() && !hist.Exhausted && hist.Count < wantDays && guard++ < 40)
        {
            _status.Text = $"{iv.Label()}봉용 과거 일봉 받는 중… ({hist.Count}일)";
            await hist.LoadOlderAsync(_toss, _symbol, 3);
        }
    }

    private void ApplySeries(bool keepView)
    {
        if (_interval == ChartInterval.Tick)
        {
            _chart.SetSeries(_interval, ChartIntervals.FromTicks(_ticks.Ticks, ChartIntervals.TicksPerBar), keepView, moreAvailable: false);
            if (_chartTab) _status.Text = ChartStatus();
            return;
        }
        var hist = _interval.IsMinute() ? _minHist : _dayHist;
        _chart.SetSeries(_interval, ChartIntervals.Aggregate(hist.Bars, _interval), keepView, !hist.Exhausted);
        if (_chartTab) _status.Text = ChartStatus();
    }

    private async Task SwitchIntervalAsync(ChartInterval iv)
    {
        _interval = iv;
        PaintModeChips();
        var w = WindowStore.Load();
        w.ChartInterval = iv.ToString();
        WindowStore.Save(w);
        try
        {
            await EnsureDepthAsync(iv);
            if (_interval != iv) return;   // 받는 사이 다른 단위를 눌렀으면 그쪽이 그린다
            ApplySeries(keepView: false);
        }
        catch (Exception ex) when (ex is TossApiException or HttpRequestException or TaskCanceledException)
        {
            Log.Write("차트 단위 전환 실패: " + ex.Message);
            _chart.SetMessage("차트를 불러오지 못했습니다: " + ex.Message);
        }
    }

    /// <summary>차트를 왼쪽 끝까지 끌었을 때: 이전 봉을 더 받아 앞에 붙인다 (보던 화면은 그대로)</summary>
    private async Task LoadOlderAsync()
    {
        if (_loadingOlder) return;
        var iv = _interval;
        if (iv == ChartInterval.Tick) return;
        var hist = iv.IsMinute() ? _minHist : _dayHist;
        if (hist.Exhausted) return;
        _loadingOlder = true;
        _chart.LoadingOlder = true;
        _chart.Invalidate();
        if (_chartTab) _status.Text = ChartStatus();
        try
        {
            int pages = iv switch { ChartInterval.Min1 => 2, ChartInterval.Min5 or ChartInterval.Min10 => 4, ChartInterval.Day => 3, _ => 8 };
            // 1분봉 페이지에는 프리·애프터마켓이 섞여 있어 정규장 봉이 하나도 안 붙을 수 있다 → 붙을 때까지 몇 번 더
            int added = 0, tries = 0;
            do added += await hist.LoadOlderAsync(_toss, _symbol, pages);
            while (added < 60 && !hist.Exhausted && ++tries < 5);
            if (iv == _interval) ApplySeries(keepView: true);
            _daily = _dayHist.Bars.ToList();
        }
        catch (Exception ex) when (ex is TossApiException or HttpRequestException or TaskCanceledException)
        {
            Log.Write("이전 봉 받기 실패: " + ex.Message);
            _status.Text = "이전 봉을 받지 못했습니다: " + ex.Message;
        }
        finally
        {
            _loadingOlder = false;
            _chart.LoadingOlder = false;
            _chart.Invalidate();
        }
    }

    /// <summary>
    /// 일봉·1분봉을 받고 (처음이면 과거까지, 새로고침이면 최신 쪽만), 판정 중인 종목이면 기준가·평단·매수일을 거기서 가져온다.
    /// </summary>
    private async Task LoadChartAsync()
    {
        bool first = !_dayHist.Loaded;
        if (first) _chart.SetMessage("차트 불러오는 중…");
        try
        {
            if (!_dayHist.Loaded) await _dayHist.LoadOlderAsync(_toss, _symbol, 3);
            else await _dayHist.RefreshLatestAsync(_toss, _symbol);
            if (!_minHist.Loaded) await _minHist.LoadOlderAsync(_toss, _symbol, 4);
            else await _minHist.RefreshLatestAsync(_toss, _symbol);
            await EnsureDepthAsync(_interval);

            _daily = _dayHist.Bars.ToList();
            var today = Kst.Today;
            _minute = _minHist.Bars.Where(b => DateOnly.FromDateTime(b.Time.DateTime) == today).ToList();

            var ev = _evalOf(_symbol);
            if (ev is { PrevClose: > 0 })
            {
                _prevClose = ev.PrevClose;
                _last = ev.Last;
            }
            else
            {
                var prices = await _toss.GetPricesAsync(new[] { _symbol });
                _last = prices.GetValueOrDefault(_symbol);
                var past = _daily.Where(b => DateOnly.FromDateTime(b.Time.DateTime) < today).ToList();
                _prevClose = past.Count > 0 ? past[^1].Close : 0;
            }

            var catalysts = _settings.AllEvents.Where(c => c.Symbol == _symbol || string.IsNullOrWhiteSpace(c.Symbol) || c.Symbol == "*");
            _chart.SetContext(_prevClose, ev?.Holding?.AveragePrice, ev?.Plan?.BuyDays ?? Array.Empty<DateOnly>(), catalysts);
            ApplySeries(keepView: !first);
        }
        catch (Exception ex) when (ex is TossApiException or HttpRequestException or TaskCanceledException)
        {
            Log.Write("차트 불러오기 실패: " + ex.Message);
            _chart.SetMessage("차트를 불러오지 못했습니다: " + ex.Message);
        }
    }

    // ───────────────────────── 리포트 ─────────────────────────

    private async Task MakeReportAsync()
    {
        if (_reporting) return;
        _reporting = true;
        var before = _status.Text;
        _status.Text = "리포트 만드는 중… (수급·공시를 모으는 데 몇 초)";
        try
        {
            if (_daily.Count == 0) await LoadChartAsync();
            // 리포트의 '주가 기록' 월·년봉용으로 일봉을 10년 가까이 받아 둔다 (이미 있으면 바로 넘어감)
            _status.Text = "리포트 만드는 중… (과거 일봉 확인)";
            try { await EnsureDepthAsync(ChartInterval.Month); }
            catch (Exception ex) when (ex is TossApiException or HttpRequestException or TaskCanceledException) { Log.Write("리포트용 과거 일봉 실패: " + ex.Message); }
            _status.Text = "리포트 만드는 중… (수급·공시를 모으는 데 몇 초)";
            var investors = await _toss.GetInvestorTradingAsync(_symbol, 100);   // 표의 일봉 수급 열용으로 넉넉히
            List<ProgramDay> programs;
            try { programs = await _toss.GetProgramTradesAsync(_symbol, 10); }
            catch (TossApiException) { programs = new(); }

            List<DartDisclosure> disclosures = _items ?? new();
            string? dartNote = null;
            if (_dart is null) dartNote = "DART 인증키가 없어 공시는 빠졌습니다.";
            else if (_items is null)
            {
                try { disclosures = await _dart.GetRecentAsync(_symbol, 90); }
                catch (Exception ex) when (ex is DartException or HttpRequestException or TaskCanceledException) { dartNote = "공시 조회 실패: " + ex.Message; }
            }

            List<QuarterFin> quarters = new();
            string? finNote = null;
            if (_dart is null) finNote = "DART 인증키가 없어 기업 정보(실적·PBR)는 빠졌습니다.";
            else
            {
                _status.Text = "리포트 만드는 중… (분기 실적 — 처음 한 번은 수십 초)";
                try { quarters = await _dart.GetQuartersAsync(_symbol); }
                catch (Exception ex) when (ex is DartException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                { finNote = "분기 실적 조회 실패: " + ex.Message; Log.Write(finNote); }
            }

            var ev = _evalOf(_symbol);
            var data = new ReportData
            {
                Symbol = _symbol, Name = _name, At = Kst.Now, Last = _last, PrevClose = _prevClose, Eval = ev,
                Daily = _daily.Skip(Math.Max(0, _daily.Count - 250)).ToList(), Minute = _minute, Investors = investors, Programs = programs, Disclosures = disclosures,
                Catalysts = _settings.AllEvents.Where(c => c.Symbol == _symbol || string.IsNullOrWhiteSpace(c.Symbol) || c.Symbol == "*")
                    .GroupBy(c => (c.Date, c.Title)).Select(g => g.First()).ToList(),
                HistoryDaily = _dayHist.Bars, HistoryMinute = _minHist.Bars,
                Holding = ev?.Holding, BuyDays = ev?.Plan?.BuyDays ?? Array.Empty<DateOnly>(), Market = _marketOf(), DartNote = dartNote,
                Quarters = quarters, FinNote = finNote,
            };

            var folder = Path.Combine(SettingsStore.Folder, "reports");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"{_name}-{Kst.Now:yyyyMMdd-HHmm}.html");
            await File.WriteAllTextAsync(path, HtmlReport.Build(data), new System.Text.UTF8Encoding(false));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            _status.Text = "리포트를 브라우저로 열었습니다 · " + path;
        }
        catch (Exception ex)
        {
            Log.Write("리포트 실패: " + ex);
            _status.Text = "리포트 실패: " + ex.Message;
        }
        finally
        {
            _reporting = false;
            if (_status.Text.StartsWith("리포트 만드는")) _status.Text = before;
        }
    }

    // ───────────────────────── 데이터 ─────────────────────────

    private async Task LoadAsync(bool force)
    {
        if (_dart is null)
        {
            _items = null;
            Render("DART 인증키가 없어 공시를 못 가져옵니다. 설정 파일의 DartApiKey 에 넣고 재시작하세요.");
            return;
        }

        Render("불러오는 중…");
        try
        {
            _items = await _dart.GetRecentAsync(_symbol, 90, force);
            Render();
        }
        catch (Exception ex) when (ex is DartException or HttpRequestException or TaskCanceledException)
        {
            Log.Write("공시 목록 실패: " + ex.Message);
            _items = null;
            Render(ex.Message);
        }
    }

    // ───────────────────────── 화면 ─────────────────────────

    private void Render(string? message = null)
    {
        if (_chartTab) { _pending = message; return; }   // 공시 탭이 보일 때만 그린다 (메시지는 기억)
        foreach (var (f, t) in _tabs)
        {
            t.BackColor = f == _filter ? Theme.Text : Theme.Surface;
            t.ForeColor = f == _filter ? Theme.Background : Theme.Muted;
        }

        _list.SuspendLayout();
        var old = _list.Controls.Cast<Control>().ToList();
        _list.Controls.Clear();
        foreach (var c in old) c.Dispose();

        int width = Math.Max(280, _list.ClientSize.Width - _list.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);

        // 앞으로 남은 명분 (수동 + 자동)
        var today = Kst.Today;
        var upcoming = _settings.AllEvents
            .Where(e => (e.Symbol == _symbol || string.IsNullOrWhiteSpace(e.Symbol) || e.Symbol == "*") && e.ParsedDate >= today)
            .GroupBy(e => (e.Date, e.Title)).Select(g => g.First())
            .OrderBy(e => e.Date)
            .ToList();
        _list.Controls.Add(Section("앞으로 남은 명분", width));
        if (upcoming.Count == 0)
            _list.Controls.Add(Note("없음 — 기회 몫은 쉬고, 기본 몫은 평소 판단대로", width));
        foreach (var e in upcoming)
        {
            var d = e.ParsedDate!.Value;
            var when = d == today ? "오늘" : $"{d.ToString("M/d (ddd)", Ko)} · D-{d.DayNumber - today.DayNumber}";
            _list.Controls.Add(Row(when, e.Source == "DART" ? "DART" : "직접", e.Source == "DART" ? Theme.Blue : Theme.Muted,
                e.Title, width, e.Url, highlight: d == today));
        }

        // 최근 공시
        _list.Controls.Add(Section("최근 90일 공시", width, top: 14));
        if (message is not null)
        {
            _list.Controls.Add(Note(message, width, message.StartsWith("불러오는") ? Theme.Muted : Theme.Red));
        }
        else if (_items is not null)
        {
            var shown = _items.Where(d => _filter switch
            {
                Filter.Catalyst => DartClassifier.Classify(d.ReportName) != CatalystKind.None,
                Filter.Major => !Noise.Any(n => d.ReportName.Replace(" ", "").Contains(n)),
                _ => true,
            }).ToList();

            if (shown.Count == 0)
                _list.Controls.Add(Note(_filter == Filter.Catalyst ? "최근 90일에 명분 공시가 없습니다." : "해당하는 공시가 없습니다.", width));

            foreach (var d in shown.Take(150))
            {
                var kind = DartClassifier.Classify(d.ReportName);
                var tag = kind == CatalystKind.None ? "" : DartClassifier.Describe(kind);
                var color = kind switch
                {
                    CatalystKind.Earnings or CatalystKind.InvestorRelations => Theme.Green,
                    CatalystKind.Buyback or CatalystKind.Cancellation => Theme.Blue,
                    CatalystKind.Dividend => Theme.Amber,
                    _ => Theme.Muted,
                };
                var fresh = d.ReceiptDate == today;
                _list.Controls.Add(Row(d.ReceiptDate.ToString("yy.MM.dd", Ko), tag, color, d.ReportName, width, d.ViewerUrl, highlight: fresh));
            }

            _status.Text = $"{shown.Count}건 / 전체 {_items.Count}건 · 줄을 누르면 DART 원문이 열립니다";
        }
        if (message is not null) _status.Text = "";

        _list.ResumeLayout();
    }

    private static Control Section(string text, int width, int top = 4) => new Label
    {
        Text = text,
        AutoSize = false,
        Width = width,
        Height = 22,
        Font = Theme.Font(9f, FontStyle.Bold),
        ForeColor = Theme.Muted,
        Margin = new Padding(0, top, 0, 2),
    };

    private static Control Note(string text, int width, Color? color = null)
    {
        var l = new Label
        {
            Text = text,
            AutoSize = false,
            Width = width,
            Font = Theme.Font(9f),
            ForeColor = color ?? Theme.Muted,
            Margin = new Padding(0, 2, 0, 6),
        };
        l.Height = TextRenderer.MeasureText(text, l.Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak).Height + 4;
        return l;
    }

    /// <summary>날짜 · 종류 태그 · 제목 한 줄. 누르면 링크를 연다.</summary>
    private static Control Row(string date, string tag, Color tagColor, string title, int width, string? url, bool highlight)
    {
        var bg = highlight ? Color.FromArgb(44, 52, 66) : Theme.Surface;
        var row = new Panel { Width = width, BackColor = bg, Margin = new Padding(0, 0, 0, 4), Cursor = url is null ? Cursors.Default : Cursors.Hand };

        var dateLabel = new Label { Text = date, AutoSize = true, Font = Theme.Font(8.5f), ForeColor = Theme.Muted, Location = new Point(10, 8) };
        row.Controls.Add(dateLabel);
        int x = 10 + Math.Max(dateLabel.PreferredWidth, 104) + 6;

        if (tag.Length > 0)
        {
            var tagLabel = new Label
            {
                Text = tag,
                AutoSize = true,
                Font = Theme.Font(8f, FontStyle.Bold),
                ForeColor = Theme.Background,
                BackColor = tagColor,
                Padding = new Padding(4, 1, 4, 1),
                Location = new Point(x, 7),
            };
            row.Controls.Add(tagLabel);
        }

        var titleLabel = new Label
        {
            Text = title,
            AutoSize = false,
            Font = Theme.Font(9f),
            ForeColor = Theme.Text,
            Location = new Point(10, tag.Length > 0 ? 28 : 26),
            Width = width - 20,
        };
        // 날짜 줄과 태그를 첫 줄에, 제목은 그 아래 (긴 공시 제목이 많아서)
        titleLabel.Height = TextRenderer.MeasureText(title, titleLabel.Font, new Size(titleLabel.Width, int.MaxValue), TextFormatFlags.WordBreak).Height + 2;
        row.Controls.Add(titleLabel);
        row.Height = titleLabel.Bottom + 8;

        if (url is not null)
        {
            var hover = Color.FromArgb(Math.Min(255, bg.R + 10), Math.Min(255, bg.G + 10), Math.Min(255, bg.B + 12));
            foreach (Control c in row.Controls.Cast<Control>().Append(row))
            {
                c.Cursor = Cursors.Hand;
                c.Click += (_, _) => Open(url);
                c.MouseEnter += (_, _) => row.BackColor = hover;
                c.MouseLeave += (_, _) => { if (!row.ClientRectangle.Contains(row.PointToClient(MousePosition))) row.BackColor = bg; };
            }
            new ToolTip { InitialDelay = 500 }.SetToolTip(titleLabel, "누르면 DART 원문이 브라우저로 열립니다");
        }
        return row;
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("브라우저 열기 실패: " + ex.Message); }
    }
}
