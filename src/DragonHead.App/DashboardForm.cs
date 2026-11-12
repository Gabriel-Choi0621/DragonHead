using System.Runtime.InteropServices;
using DragonHead.Core;

namespace DragonHead.App;

/// <summary>
/// 트레이 아이콘을 클릭하면 뜨는 작은 상황판.
/// 열려 있는 동안 1초마다 갱신되므로 매번 다시 그리지 않는다:
/// 카드를 '줄 목록(CardSpec)'으로 만든 뒤, 줄 구조가 같으면 라벨 글자·색만 바꾸고 구조가 바뀐 카드만 새로 만든다.
/// </summary>
internal sealed class DashboardForm : Form
{
    private readonly Label _clock;
    private readonly Label _phase;
    private readonly Label _brake;
    private readonly FlowLayoutPanel _list;
    private readonly Label _footer;

    public event EventHandler? RefreshRequested;
    public event EventHandler? SettingsRequested;

    /// <summary>'지금 판정' 버튼 (14:50 자동 판정과 별개로, 지금 데이터 기준)</summary>
    public event Action? JudgeNowRequested;

    /// <summary>투명도·고정이 바뀌면 (딸린 창들이 따라가도록)</summary>
    public event Action? LookChanged;

    /// <summary>종목 카드를 누르면 (종목코드, 종목명)</summary>
    public event Action<string, string>? StockClicked;

    private readonly WindowSettings _win;
    private readonly Action _saveSettings;
    private readonly Label _pin;
    private readonly Label _opacity;
    private readonly Label _alerts;
    private int _unread;

    /// <summary>상단 '알림' 버튼을 누르면</summary>
    public event Action? AlertsRequested;

    /// <summary>상단 '시장' 버튼 (검색·순위 창)</summary>
    public event Action? MarketRequested;
    /// <summary>'예산·분할' 버튼 / 카드 우클릭</summary>
    public event Action? BudgetRequested;

    /// <summary>종목 카드 우클릭 → 감시 해제</summary>
    public event Action<string, string>? UnwatchRequested;

    private const int ResizeGrip = 6;

    public DashboardForm(WindowSettings win, Action saveSettings)
    {
        _win = win;
        _saveSettings = saveSettings;

        Text = "용머리 알리미";
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        // 윈도우 기본 제목줄 대신 자체 상단바를 쓴다 (끌어서 이동, 가장자리로 크기 조절은 WndProc 에서)
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(ResizeGrip);
        MinimumSize = new Size(420, 360);
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        ClientSize = new Size(470, 720);
        ShowInTaskbar = false;
        TopMost = win.Pinned;
        Opacity = Math.Clamp(win.Opacity, 0.4, 1.0);
        DoubleBuffered = true;

        RestoreBounds_();

        var header = new Panel { Dock = DockStyle.Top, Height = 96, Padding = new Padding(16, 12, 16, 0) };
        _clock = new Label { AutoSize = true, Font = Theme.Font(16f, FontStyle.Bold), Location = new Point(12, 10) };
        _phase = new Label { AutoSize = true, Font = Theme.Font(9.5f), ForeColor = Theme.Muted, Location = new Point(14, 44) };
        _brake = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            Font = Theme.Font(9f, FontStyle.Bold),
            ForeColor = Theme.Amber,
            Location = new Point(14, 66),
            Size = new Size(430, 22),
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
        };

        // 상단 오른쪽: 핀 · 투명도 · 닫기
        var tools = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _pin = ToolButton("", "항상 위에 고정 (클릭해서 켜기/끄기)");
        _pin.Click += (_, _) => TogglePin();
        _opacity = ToolButton("", "투명도 — 클릭하면 슬라이더, 마우스 휠로 5%씩");
        _opacity.Click += (_, _) => ShowOpacitySlider();
        _opacity.MouseWheel += (_, e) => SetOpacity(Opacity + (e.Delta > 0 ? 0.05 : -0.05));
        var market = ToolButton("시장", "종목 검색 · 거래대금/거래량/급상승 순위 · 감시 종목 추가");
        market.Click += (_, _) => MarketRequested?.Invoke();
        _alerts = ToolButton("", "지금까지 뜬 알림 기록");
        _alerts.Click += (_, _) => AlertsRequested?.Invoke();
        var close = ToolButton("×", "닫기 (앱은 트레이에서 계속 실행)");
        close.Font = Theme.Font(12f, FontStyle.Bold);
        close.Padding = new Padding(6, 0, 6, 2);
        close.Click += (_, _) => Hide();
        close.MouseEnter += (_, _) => close.ForeColor = Theme.Red;
        close.MouseLeave += (_, _) => close.ForeColor = Theme.Muted;
        tools.Controls.AddRange(new Control[] { market, _alerts, _pin, _opacity, close });
        header.Controls.AddRange(new Control[] { _clock, _phase, _brake, tools });
        header.Layout += (_, _) =>
        {
            tools.Location = new Point(header.ClientSize.Width - tools.Width - 6, 6);
            // 창이 좁으면 버튼 줄이 시계와 겹치지 않게 시계를 버튼 아래로 내린다
            int clockTop = _clock.Left + _clock.PreferredWidth + 8 > tools.Left ? tools.Bottom + 2 : 10;
            if (_clock.Top != clockTop)
            {
                _clock.Top = clockTop;
                _phase.Top = clockTop + 34;
                _brake.Top = clockTop + 56;
                header.Height = clockTop + 86;
            }
        };
        UpdateToolLabels();

        // 상단바 아무 데나 끌면 창 이동
        foreach (Control c in new Control[] { header, _clock, _phase, _brake })
            c.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) BeginDrag(); };

        _list = new BufferedFlowPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(12, 4, 12, 4),
        };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 74, Padding = new Padding(16, 6, 16, 8) };
        _footer = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Font = Theme.Font(8.5f),
            ForeColor = Theme.Muted,
            AutoEllipsis = true,
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, FlowDirection = FlowDirection.RightToLeft };
        var refresh = MakeButton("지금 새로고침");
        refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        var judge = MakeButton("지금 판정");
        judge.ForeColor = Theme.Amber;
        judge.Click += (_, _) => JudgeNowRequested?.Invoke();
        new ToolTip().SetToolTip(judge, "지금 데이터로 판정해서 알림으로 보여줍니다. 14:50·15:15 자동 판정은 그대로.");
        var budget = MakeButton("예산·분할");
        budget.Click += (_, _) => BudgetRequested?.Invoke();
        new ToolTip().SetToolTip(budget, "종목별 예산과 수비형·균형형·공격형 비율을 정합니다.");
        var settings = MakeButton("설정 파일");
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        buttons.Controls.Add(refresh);
        buttons.Controls.Add(judge);
        buttons.Controls.Add(budget);
        buttons.Controls.Add(settings);
        bottom.Controls.Add(buttons);
        bottom.Controls.Add(_footer);

        Controls.Add(_list);
        Controls.Add(bottom);
        Controls.Add(header);

        // 폭이 바뀌면 줄바꿈이 달라지므로 카드를 전부 새로 만든다
        ResizeEnd += (_, _) => { ClearCards(); Rerender(); SaveBounds(); };
        // 다른 창을 눌러도 닫히지 않는다. ✕ 로만 닫는다.
    }

    // ───────────────────────── 창 꾸밈 (제목줄 없는 창) ─────────────────────────

    private static Label ToolButton(string text, string tip)
    {
        var l = new Label
        {
            Text = text,
            AutoSize = true,
            Font = Theme.Font(9.5f, FontStyle.Bold),
            ForeColor = Theme.Muted,
            Cursor = Cursors.Hand,
            Padding = new Padding(6, 3, 6, 3),
            Margin = new Padding(2, 0, 0, 0),
        };
        new ToolTip().SetToolTip(l, tip);
        l.MouseEnter += (_, _) => l.BackColor = Theme.Surface;
        l.MouseLeave += (_, _) => l.BackColor = Color.Transparent;
        return l;
    }

    private void UpdateToolLabels()
    {
        _pin.Text = TopMost ? "● 고정" : "○ 고정";
        _pin.ForeColor = TopMost ? Theme.Text : Theme.Muted;
        _opacity.Text = $"투명 {Math.Round(Opacity * 100)}%";
        _alerts.Text = _unread > 0 ? $"알림 {_unread}" : "알림";
        _alerts.ForeColor = _unread > 0 ? Theme.Amber : Theme.Muted;
    }

    /// <summary>안 읽은 알림 수 (상단 '알림 N' 표시)</summary>
    public void SetUnread(int count)
    {
        if (_unread == count) return;
        _unread = count;
        UpdateToolLabels();
    }

    private void TogglePin()
    {
        TopMost = !TopMost;
        _win.Pinned = TopMost;
        LookChanged?.Invoke();
        UpdateToolLabels();
        _saveSettings();
    }

    private ToolStripDropDown? _opacityPopup;

    /// <summary>투명도 버튼 아래에 작은 슬라이더를 띄운다. 끄는 동안 바로 반영, 바깥을 누르면 닫힌다.</summary>
    private void ShowOpacitySlider()
    {
        if (_opacityPopup is { Visible: true })
        {
            _opacityPopup.Close();
            return;
        }

        var slider = new OpacitySlider { Value = (int)Math.Round(Opacity * 100) };
        var caption = new Label
        {
            Text = "투명도",
            AutoSize = true,
            Font = Theme.Font(8.5f),
            ForeColor = Theme.Muted,
            BackColor = Theme.Surface,
            Location = new Point(10, 8),
        };
        var value = new Label
        {
            Text = $"{slider.Value}%",
            AutoSize = true,
            Font = Theme.Font(8.5f, FontStyle.Bold),
            ForeColor = Theme.Text,
            BackColor = Theme.Surface,
        };
        slider.Location = new Point(0, 26);
        var panel = new Panel { Size = new Size(200, 60), BackColor = Theme.Surface };
        panel.Controls.AddRange(new Control[] { caption, value, slider });
        value.Location = new Point(panel.Width - value.PreferredWidth - 10, 8);

        slider.ValueChanged += (_, _) =>
        {
            Opacity = slider.Value / 100.0;          // 끄는 동안 바로 보이게
            LookChanged?.Invoke();
            value.Text = $"{slider.Value}%";
            value.Left = panel.Width - value.PreferredWidth - 10;
            UpdateToolLabels();
        };
        slider.Committed += (_, _) => SetOpacity(slider.Value / 100.0);

        var host = new ToolStripControlHost(panel) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = panel.Size };
        _opacityPopup?.Dispose();
        _opacityPopup = new ToolStripDropDown
        {
            Padding = new Padding(1),
            BackColor = Color.FromArgb(70, 70, 78),   // 얇은 테두리 역할
            DropShadowEnabled = true,
        };
        _opacityPopup.Items.Add(host);
        _opacityPopup.Closed += (_, _) => SetOpacity(slider.Value / 100.0);
        _opacityPopup.Show(_opacity, new Point(_opacity.Width - panel.Width - 2, _opacity.Height + 4));
        slider.Focus();
    }

    private void SetOpacity(double value)
    {
        Opacity = Math.Clamp(Math.Round(value * 20) / 20, 0.4, 1.0);
        _win.Opacity = Opacity;
        UpdateToolLabels();
        _saveSettings();
        LookChanged?.Invoke();
    }

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void BeginDrag()
    {
        const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 0x2;
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        SaveBounds();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x00020000;
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11 이면 모서리를 둥글게 (10에서는 조용히 무시됨)
        try
        {
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        }
        catch { /* dwmapi 없음 */ }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(70, 255, 255, 255));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    /// <summary>가장자리 6px 를 잡으면 크기 조절</summary>
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84;
        base.WndProc(ref m);
        if (m.Msg != WM_NCHITTEST || (int)m.Result != 1 /* HTCLIENT */) return;

        var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
        bool l = p.X < ResizeGrip, r = p.X >= ClientSize.Width - ResizeGrip;
        bool t = p.Y < ResizeGrip, b = p.Y >= ClientSize.Height - ResizeGrip;
        int hit = (t, b, l, r) switch
        {
            (true, _, true, _) => 13,   // HTTOPLEFT
            (true, _, _, true) => 14,   // HTTOPRIGHT
            (_, true, true, _) => 16,   // HTBOTTOMLEFT
            (_, true, _, true) => 17,   // HTBOTTOMRIGHT
            (true, _, _, _) => 12,      // HTTOP
            (_, true, _, _) => 15,      // HTBOTTOM
            (_, _, true, _) => 10,      // HTLEFT
            (_, _, _, true) => 11,      // HTRIGHT
            _ => 1,
        };
        m.Result = (IntPtr)hit;
    }

    private void RestoreBounds_()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        if (_win is { Width: > 0, Height: > 0 })
            Size = new Size(Math.Max(MinimumSize.Width, _win.Width!.Value), Math.Max(MinimumSize.Height, _win.Height!.Value));
        if (Height > area.Height - 24) Height = area.Height - 24;

        var saved = _win is { X: not null, Y: not null } ? new Point(_win.X!.Value, _win.Y!.Value) : (Point?)null;
        // 저장된 위치가 지금 연결된 화면 밖이면(모니터 분리 등) 기본 위치로
        if (saved is { } pt && Screen.AllScreens.Any(s => s.WorkingArea.Contains(pt.X + 40, pt.Y + 20)))
            Location = pt;
        else
            Location = new Point(area.Right - Width - 12, Math.Max(area.Top, area.Bottom - Height - 12));
    }

    private void SaveBounds()
    {
        if (WindowState != FormWindowState.Normal) return;
        _win.X = Left; _win.Y = Top; _win.Width = Width; _win.Height = Height;
        _saveSettings();
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        // 끌어서 옮긴 직후 위치 저장 (드래그 중 매번 저장하지 않도록 짧게 늦춘다)
        _moveSave?.Stop();
        _moveSave ??= new System.Windows.Forms.Timer { Interval = 600 };
        _moveSave.Tick -= MoveSaveTick;
        _moveSave.Tick += MoveSaveTick;
        _moveSave.Start();
    }

    private System.Windows.Forms.Timer? _moveSave;

    private void MoveSaveTick(object? sender, EventArgs e)
    {
        _moveSave?.Stop();
        if (Visible) SaveBounds();
    }

    /// <summary>호환용: 예전 자동 숨김 기능은 없앴다.</summary>
    public bool AutoHide { get; set; }

    /// <summary>호환용: 자동 숨김이 없으므로 항상 '오래 전'.</summary>
    public DateTime LastAutoHidden => DateTime.MinValue;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // X 를 눌러도 앱은 계속 돈다. 트레이 메뉴의 '종료'로만 끝난다.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    private static Button MakeButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        ForeColor = Theme.Text,
        BackColor = Theme.Surface,
        Font = Theme.Font(9f),
        Margin = new Padding(6, 0, 0, 0),
    };

    private bool _hideAmounts;
    private Action? _lastRender;

    public bool HideAmounts
    {
        get => _hideAmounts;
        set => _hideAmounts = value;
    }

    private void Rerender() => _lastRender?.Invoke();

    // ───────────────────────── 렌더링 ─────────────────────────

    public void Render(IReadOnlyList<Evaluation> evals, AccountSummary? account, DateTimeOffset? updatedAt, string? error,
                       string? accountNote, string? baseNote = null, MarketContext? market = null)
    {
        _lastRender = () => Render(evals, account, updatedAt, error, accountNote, baseNote, market);
        var cash = account?.CashKrw;
        var now = Kst.Now;
        var phase = evals.Count > 0 ? evals[0].Phase : SignalEngine.GetPhase(now, null);

        SetText(_clock, now.ToString("MM.dd (ddd) HH:mm", System.Globalization.CultureInfo.GetCultureInfo("ko-KR")));
        SetText(_phase, $"{SignalEngine.Describe(phase)} · {Countdown(now, phase)}");
        SetText(_brake, phase switch
        {
            Phase.PreMarket or Phase.MorningTrap => "지금은 구경만 하는 시간입니다. 판정은 14:50.",
            Phase.Midday => "흥분 금지. 오후 1시부터 지지를 보고, 판정은 14:50.",
            Phase.Decision => "판정 구간입니다. 아래 체크가 전부 ✓ 인지 보세요.",
            Phase.ClosingAuction => "종가 동시호가 중 (15:20~15:30).",
            _ => "",
        });

        var specs = new List<CardSpec>();
        if (market is not null) specs.Add(MarketSpec(market));
        if (account is not null) specs.Add(AccountSpec(account));
        specs.AddRange(evals.Select(StockSpec));
        ApplyCards(specs);

        var parts = new List<string>();
        if (updatedAt is { } u) parts.Add($"갱신 {u:HH:mm:ss}");
        if (cash is { } c && account is null) parts.Add($"매수가능 {Amt(c)}");
        parts.Add("외국인 수치는 장중 잠정치(주식 수), 금액은 현재가 곱한 근사치");
        var footer = string.Join(" · ", parts);
        if (!string.IsNullOrEmpty(baseNote)) footer += "\n" + baseNote;
        if (!string.IsNullOrEmpty(accountNote)) footer += "\n계좌: " + accountNote;
        if (!string.IsNullOrEmpty(error)) footer = "⚠ " + error;
        var footerColor = string.IsNullOrEmpty(error) ? Theme.Muted : Theme.Red;
        if (_footer.ForeColor != footerColor) _footer.ForeColor = footerColor;
        SetText(_footer, footer);
    }

    private static void SetText(Control c, string text)
    {
        if (c.Text != text) c.Text = text;
    }

    private int CardWidth => Math.Max(300, _list.ClientSize.Width - _list.Padding.Horizontal - 4);

    private void ClearCards()
    {
        var old = _list.Controls.Cast<Control>().ToList();
        _list.Controls.Clear();
        foreach (var c in old) c.Dispose();
    }

    private void ApplyCards(IReadOnlyList<CardSpec> specs)
    {
        var views = _list.Controls.OfType<CardView>().ToList();
        var width = CardWidth;

        bool sameSet = views.Count == specs.Count
            && views.Zip(specs).All(p => p.First.Key == p.Second.Key && p.First.Width == width);

        if (!sameSet)
        {
            var scroll = _list.AutoScrollPosition;
            _list.SuspendLayout();
            ClearCards();
            foreach (var s in specs) _list.Controls.Add(new CardView(s, width));
            _list.ResumeLayout();
            _list.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
            return;
        }

        for (int i = 0; i < specs.Count; i++)
        {
            if (views[i].TryUpdate(specs[i])) continue;

            // 줄 구조가 바뀐 카드만 같은 자리에서 교체
            var scroll = _list.AutoScrollPosition;
            _list.SuspendLayout();
            var replacement = new CardView(specs[i], width);
            _list.Controls.Add(replacement);
            _list.Controls.SetChildIndex(replacement, i);
            _list.Controls.Remove(views[i]);
            views[i].Dispose();
            _list.ResumeLayout();
            _list.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
        }
    }

    private static string Countdown(DateTimeOffset now, Phase phase)
    {
        var today = DateOnly.FromDateTime(now.DateTime);
        var target = Kst.At(today, 14, 50);
        if (phase is Phase.Holiday or Phase.Closed or Phase.Decision or Phase.ClosingAuction)
            return phase == Phase.Decision ? "동시호가까지 " + Span(Kst.At(today, 15, 20) - now) : "";
        return "판정까지 " + Span(target - now);
    }

    private static string Span(TimeSpan t) =>
        t <= TimeSpan.Zero ? "0분" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}시간 {t.Minutes}분" : $"{t.Minutes}분";

    // ───────────────────────── 종목 카드 ─────────────────────────

    private CardSpec StockSpec(Evaluation e)
    {
        // 첫 줄: 종목명은 흰색, 가격·등락률은 기준가 대비 상승 빨강 / 하락 파랑 / 보합 흰색
        var lines = new List<Line> { new(e.Name, Theme.Font(11f, FontStyle.Bold), Theme.Text) };
        var priceColor = e.ChangePct is { } cp && cp != 0 ? PnlColor(cp) : Theme.Text;
        var price = new List<Seg> { new($"{e.Last:N0}원", priceColor, Bold: true) };
        if (e.ChangePct is { } c) price.Add(new(SignalEngine.Signed(c) + "%", priceColor, Bold: true));

        // 큰 가격 옆에 작게: NXT 프리·애프터마켓 실시간 가격과 KRX 가격 대비 % (다음 갭 미리보기)
        // 큰 흰 가격(KRX) 아래 작은 줄: "애프터마켓 224,000"은 회색, 뒤의 %(기준가 대비, 토스 앱과 동일)만 상승 빨강·하락 파랑
        List<Seg>? sub = null;
        if (e.ExtendedPrice is { } ext)
        {
            static string Won(decimal d) => (d > 0 ? "+" : d < 0 ? "-" : "") + Math.Abs(d).ToString("N0") + "원";
            sub = new List<Seg> { new($"{(e.Phase == Phase.PreMarket ? "프리마켓" : "애프터마켓")} {ext:N0}", Theme.Muted) };

            // 종가 대비 (장 전엔 전일 종가 = 기준가라 같은 값이므로 생략)
            if (e.Phase != Phase.PreMarket && e.ExtendedVsLastPct is { } vl)
            {
                sub.Add(new(" · 종가 대비 ", Theme.Muted));
                sub.Add(new($"{Won(ext - e.Last)} ({SignalEngine.Signed(vl)}%)", PnlColor(vl), Bold: true));
            }
            if (e.ExtendedChangePct is { } vb && e.PrevClose > 0)
            {
                sub.Add(new(" · 기준가 대비 ", Theme.Muted));
                sub.Add(new($"{Won(ext - e.PrevClose)} ({SignalEngine.Signed(vb)}%)", PnlColor(vb), Bold: true));
            }
        }

        var small = Theme.Font(9f);
        bool catalystDay = e.Checks.FirstOrDefault(c => c.Label == "명분")?.Passed == true;
        void CheckLines(IEnumerable<Check> checks)
        {
            bool first = true;
            foreach (var ck in checks)
            {
                var mark = ck.Passed switch { true => "✓", false => "✗", null => "–" };
                var fore = ck.Passed switch { true => Theme.Green, false => Theme.Red, null => Theme.Muted };
                lines.Add(new($"{mark} {ck.Label}  ·  {ck.Detail}", small, fore, GapBefore: first ? 2 : 0));
                first = false;
            }
        }

        // 판단 두 개: 명분일엔 '명분 판단'(기회 몫), 아닌 날엔 '평소 판단'(기본 몫 · 차트·수급·시장 종합)
        if (catalystDay)
        {
            lines.Add(new($"명분 판단 · {SignalEngine.Describe(e.Verdict)}", Theme.Font(9.5f, FontStyle.Bold), Theme.Of(e.Verdict)));
            lines.Add(new(e.Headline, Theme.Font(9.5f), Theme.Text));
            AddTagLines(lines, e.Tags);
            CheckLines(e.Checks.Where(c => c.Label != "외국인 연속"));
        }
        else
        {
            if (e.General is { } g)
            {
                lines.Add(new($"평소 판단 · {GeneralView.Describe(g.Verdict)}", Theme.Font(9.5f, FontStyle.Bold), Theme.Of(g.Verdict)));
                lines.Add(new(g.Opinion, Theme.Font(9.5f), Theme.Text));
            }
            AddTagLines(lines, e.Tags);
            if (e.General is { } g2) CheckLines(g2.Checks);
            lines.Add(new("명분 판단 · 오늘은 명분 없음" + (e.Verdict == Verdict.GapTrap ? " · 갭상승 경계" : ""), small, Theme.Muted, GapBefore: 2));
        }

        // 오늘 수급: 순매수 + 빨강 / 순매도 - 파랑 (토스 앱과 같은 색). 금액은 주식 수 × 현재가 근사치.
        {
            var segs = new List<Seg> { new("오늘 수급  ", Theme.Muted) };
            void Flow(string who, long? shares)
            {
                segs.Add(new(who + " ", Theme.Muted));
                if (shares is not { } sh)
                {
                    segs.Add(new(e.Phase is Phase.Closed ? "없음(마감 후 확정 전)" : "아직 없음", Theme.Muted));
                    return;
                }
                var col = sh > 0 ? Theme.Red : sh < 0 ? Theme.Blue : Theme.Text;
                var amt = e.Last > 0 ? $" ({SignalEngine.Eok(sh * e.Last)})" : "";
                segs.Add(new($"{SignalEngine.Signed(sh)}주{amt}", col, Bold: true));
            }
            Flow("외국인", e.ForeignTodayShares);
            segs.Add(new("  ·  ", Theme.Muted));
            Flow("기관", e.InstitutionTodayShares);
            segs.Add(new(e.ForeignTodayConfirmed ? "  확정" : "  잠정", Theme.Muted));
            lines.Add(new(string.Concat(segs.Select(x => x.Text)), small, Theme.Text, GapBefore: 2) { Segs = segs });
        }

        if (e.Holding is { Quantity: > 0 } h)
            lines.Add(new($"보유 {Qty(h.Quantity)}주 · 평단 {Amt(h.AveragePrice)} · {SignalEngine.Signed(h.ProfitPct)}%", small, PnlColor(h.ProfitPct)));

        if (e.Plan is { } plan)
        {
            // 예산 분할: '균형형 · 예산 400만' 다음 줄에 기본 몫 / 기회 몫 상태
            var head = $"{BudgetPresets.NameOf(plan.CoreRatio)} · 예산 {Amt(plan.Budget)}" +
                       (plan.BudgetIsDefault ? " (공통 기본값 · 우클릭 → 예산·분할 설정)" : "") +
                       (plan.Estimated ? " · 회차는 매입금액으로 추정" : "");
            lines.Add(new(head, small, Theme.Muted, GapBefore: 4));
            foreach (var part in new[] { plan.Core, plan.Opp })
            {
                if (!part.Enabled) continue;
                var ready = part.Ready == true && !part.Complete;
                var mark = part.Complete ? "✓" : ready ? "●" : "·";
                var size = part.SharesPerTranche > 0 ? $" · 회당 {part.SharesPerTranche}주" + (part.Total < part.ConfiguredTotal ? $" (주가가 높아 {part.Total}회로)" : "") : "";
                var text = $"{mark} {part.Name} {part.Done}/{part.Total}{size} · {part.Reason.Replace(part.Name + " ", "")}";
                if (ready && part.Shares > 0)
                    text += $"\n  → {part.Shares}주 ≈ {Amt(part.Amount)} · 종가 동시호가에 토스 앱에서 직접";
                if (ready && part.CashNote is { } cn)
                    text += "\n  ⚠ " + cn;
                if (part.NoSplitWarning is { } ns && !part.Complete)
                    text += "\n  ⚠ " + ns;
                lines.Add(new(text, Theme.Font(9f, ready ? FontStyle.Bold : FontStyle.Regular), part.NoSplit && !part.Complete ? Theme.Amber : ready ? Theme.Green : Theme.Muted));
            }
        }

        var (badgeText, color) = Theme.Badge(e);
        var symbol = e.Symbol; var name = e.Name;
        return new CardSpec("stock:" + e.Symbol, color, lines)
        {
            Click = () => StockClicked?.Invoke(symbol, name),
            Menu = new (string, Action)[]
            {
                ("공시 보기", () => StockClicked?.Invoke(symbol, name)),
                ("예산 · 분할 설정", () => BudgetRequested?.Invoke()),
                ("감시 종목에서 빼기", () => UnwatchRequested?.Invoke(symbol, name)),
            },
            Badge = badgeText,
            BadgeColor = color,
            TitleSub = sub,
            TitlePrice = price,
            TitlePriceValue = e.Last,
            Spark = e.Intraday.Count > 1 ? new SparkData(e.Intraday, e.PrevClose, e.DayHigh) : null,
        };
    }

    // ───────────────────────── 태그 ─────────────────────────

    /// <summary>
    /// 태그는 좋은 것(▲ 초록)·나쁜 것(▼ 주황)·중립(· 회색)으로 줄을 나눈다. 체크(✓ 초록 / ✗ 빨강)와 색 뜻을 맞춘 것.
    /// 빨강·파랑은 가격·손익(상승·하락) 전용.
    /// 줄 구조가 바뀌면 카드가 다시 그려지므로, 비어 있어도 줄 자리는 항상 유지한다.
    /// </summary>
    private static void AddTagLines(List<Line> lines, IReadOnlyList<Tag> tags)
    {
        string Join(Tone t) => string.Join("  ", tags.Where(x => x.Tone == t).Select(x => "#" + x.Text));
        var pos = Join(Tone.Positive);
        var neg = Join(Tone.Negative);
        var neu = Join(Tone.Neutral);
        if (pos.Length + neg.Length + neu.Length == 0) return;

        var font = Theme.Font(8.5f, FontStyle.Bold);
        if (pos.Length > 0) lines.Add(new("▲ " + pos, font, Theme.Green, GapBefore: 2));
        if (neg.Length > 0) lines.Add(new("▼ " + neg, font, Theme.Amber, GapBefore: pos.Length > 0 ? 0 : 2));
        if (neu.Length > 0) lines.Add(new("· " + neu, font, Theme.Muted, GapBefore: pos.Length + neg.Length > 0 ? 0 : 2));
    }

    // ───────────────────────── 시장 카드 ─────────────────────────

    private CardSpec MarketSpec(MarketContext m)
    {
        bool us = _win.MarketTab == "US";
        var spec = us ? UsMarketSpec(m) : KrMarketSpec(m);
        return new CardSpec("market-" + (us ? "us" : "kr"), Theme.Muted, spec)
        {
            Link = us ? "국장 보기" : "미장 보기",
            LinkClick = () => { _win.MarketTab = us ? "KR" : "US"; _saveSettings(); Rerender(); },
        };
    }

    private List<Line> KrMarketSpec(MarketContext m)
    {
        var small = Theme.Font(9f);
        var lines = new List<Line>();
        var kospi = m.KospiLast is { } k
            ? $"국장 · 코스피 {k:N2}  {(m.KospiChangePct is { } kp ? SignalEngine.Signed(kp) + "%" : "")}"
            : "국장 · 코스피 -";
        lines.Add(new(kospi, Theme.Font(10.5f, FontStyle.Bold), m.KospiChangePct is { } kc ? PnlColor(kc) : Theme.Text));

        if (m.TodayFlow is { } f)
        {
            lines.Add(new($"외국인 {SignalEngine.Eok(f.ForeignNet)} · 기관 {SignalEngine.Eok(f.InstitutionNet)} · 개인 {SignalEngine.Eok(f.IndividualNet)}  (코스피 전체, 잠정)",
                small, Theme.Muted));
        }
        // 밤사이 미국 반도체 한 줄 (자세한 건 미장 탭)
        var semis = UsSemis.Semis(m.UsOvernight);
        if (semis.Count > 0)
        {
            var sox = semis[0];
            lines.Add(new($"밤사이 미국({UsSemis.D(sox.SessionDate)}) {UsSemis.Line(semis)}", small, PnlColor(sox.ChangePct)));
        }
        AddTagLines(lines, TagEngine.ForMarket(m));
        return lines;
    }

    /// <summary>미장 탭: 지수(ETF)·반도체를 종목별로. 열려 있으면 지금, 아니면 마지막 마감.</summary>
    private List<Line> UsMarketSpec(MarketContext m)
    {
        var small = Theme.Font(9f);
        bool live = m.UsLive.Count > 0;
        var src = live ? m.UsLive : m.UsOvernight;
        var lines = new List<Line>();
        if (src.Count == 0)
        {
            lines.Add(new("미장 · 시세 없음", Theme.Font(10.5f, FontStyle.Bold), Theme.Text));
            lines.Add(new("미국 종목 시세를 아직 못 받았습니다. 로그에 '미국' 오류가 있는지 확인하세요.", small, Theme.Muted));
            return lines;
        }

        var idx = UsSemis.Idx(src);
        var head = idx.FirstOrDefault() ?? src[0];
        var segs = new List<Seg> { new("미장 · ", Theme.Text, Bold: true) };
        for (int i = 0; i < idx.Count; i++)
        {
            if (i > 0) segs.Add(new("  ", Theme.Muted));
            segs.Add(new($"{idx[i].Name} {SignalEngine.Signed(idx[i].ChangePct)}%", PnlColor(idx[i].ChangePct), Bold: true));
        }
        lines.Add(new(string.Concat(segs.Select(x => x.Text)), Theme.Font(10.5f, FontStyle.Bold), PnlColor(head.ChangePct)) { Segs = segs });

        foreach (var x in UsSemis.All.Select(a => src.FirstOrDefault(s => s.Symbol == a.Symbol)).OfType<UsMove>())
        {
            var row = new List<Seg>
            {
                new($"{x.Name} ", Theme.Text, Bold: true),
                new($"{x.Symbol}  ", Theme.Muted),
                new($"${x.Close:#,0.00}  ", Theme.Text),
                new($"{SignalEngine.Signed(x.ChangePct)}%", PnlColor(x.ChangePct), Bold: true),
            };
            lines.Add(new(string.Concat(row.Select(r => r.Text)), small, Theme.Text) { Segs = row });
        }

        var et = UsSemis.ToEt(Kst.Now);
        lines.Add(new(live
                ? $"지금 미국장 · 현지 {et:HH:mm} · 직전 마감 대비"
                : $"{UsSemis.D(head.SessionDate)} 마감 기준 (현지 날짜) · 미국 정규장은 한국 시간 {(et.Offset == TimeSpan.FromHours(-4) ? "22:30~05:00" : "23:30~06:00")}",
            Theme.Font(8f), Theme.Muted, GapBefore: 2));
        lines.Add(new("지수는 ETF로 대신 봅니다 (나스닥100 = QQQ, S&P500 = SPY, 반도체 = SOXX)", Theme.Font(8f), Theme.Muted));
        return lines;
    }

    // ───────────────────────── 계좌 카드 ─────────────────────────

    private const string Mask = "•••••";

    /// <summary>국내 관례대로 수익은 빨강, 손실은 파랑.</summary>
    private static Color PnlColor(decimal v) => v > 0 ? Theme.Red : v < 0 ? Theme.Blue : Theme.Muted;

    private string Amt(decimal won) => _hideAmounts ? Mask : SignalEngine.Won(won);

    private string Usd(decimal usd) => _hideAmounts ? Mask : "$" + usd.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);

    private string SignedAmt(decimal won) => _hideAmounts ? Mask : (won > 0 ? "+" : "") + SignalEngine.Won(won);

    private string SignedUsd(decimal usd) =>
        _hideAmounts ? Mask : (usd > 0 ? "+$" : usd < 0 ? "-$" : "$") + Math.Abs(usd).ToString("N2", System.Globalization.CultureInfo.InvariantCulture);

    private string SignedMoney(Money m) =>
        m.Usd is { } u && u != 0 ? $"{SignedAmt(m.Krw)} / {SignedUsd(u)}" : SignedAmt(m.Krw);

    private static string Qty(decimal q) => q.ToString("#,0.######", System.Globalization.CultureInfo.InvariantCulture);

    private static string Pct(decimal v) => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private void ToggleAccount()
    {
        _win.AccountCollapsed = !_win.AccountCollapsed;
        _saveSettings();
        Rerender();
    }

    private CardSpec AccountSpec(AccountSummary a)
    {
        var small = Theme.Font(9f);
        var titleFont = Theme.Font(11f, FontStyle.Bold);

        if (_win.AccountCollapsed)
        {
            // 접힘: '내 계좌 ▸  총자산  오늘 ±%' 한 줄
            var total = a.KrwConversionReliable ? Amt(a.TotalKrw) : Amt(a.CashKrw + a.MarketValue.Krw) + "+$";
            var segs = new List<Seg> { new(total, Theme.Text, Bold: true) };
            if (a.Items.Count > 0 || a.DailyPct != 0)
                segs.Add(new($"오늘 {SignalEngine.Signed(a.DailyPct)}%", a.DailyPct == 0 ? Theme.Muted : PnlColor(a.DailyPct), Bold: true));
            return new CardSpec("account", Theme.Text, new List<Line> { new("내 계좌 ›", titleFont, Theme.Text) })
            {
                TitlePrice = segs,
                Link = _hideAmounts ? "금액 보기" : "금액 숨기기",
                LinkClick = () => { _hideAmounts = !_hideAmounts; Rerender(); },
                Click = ToggleAccount,
                ClickHint = "클릭하면 펼치기",
            };
        }

        var lines = new List<Line> { new("내 계좌 ⌃", titleFont, Theme.Text) };

        if (a.KrwConversionReliable)
            lines.Add(new($"총자산 {Amt(a.TotalKrw)}", Theme.Font(14f, FontStyle.Bold), Theme.Text));
        else
            lines.Add(new($"원화 {Amt(a.CashKrw + a.MarketValue.Krw)} + 달러 {Usd(a.MarketValue.Usd ?? 0)}", Theme.Font(12f, FontStyle.Bold), Theme.Text));

        lines.Add(new($"주식 {Amt(a.StocksKrw)} · 예수금 {Amt(a.CashKrw)} ({Pct(a.CashWeightPct)}%)", small, Theme.Muted));
        lines.Add(new($"총 손익  {SignedMoney(a.ProfitLoss)}  ({SignalEngine.Signed(a.ProfitPct)}%)", Theme.Font(9.5f, FontStyle.Bold), PnlColor(a.ProfitPct), GapBefore: 2));
        lines.Add(new($"오늘      {SignedMoney(a.DailyProfitLoss)}  ({SignalEngine.Signed(a.DailyPct)}%)", Theme.Font(9.5f), PnlColor(a.DailyPct)));

        if (a.Items.Count == 0)
        {
            lines.Add(new("보유 종목 없음 · 전액 현금", small, Theme.Muted, GapBefore: 4));
        }
        else
        {
            lines.Add(Line.Separator);
            foreach (var h in a.Items.OrderByDescending(a.ItemKrw))
            {
                var weight = a.KrwConversionReliable ? $"비중 {Pct(a.WeightPct(h))}%" : null;
                lines.Add(new($"{h.Name}  ·  {Qty(h.Quantity)}주", Theme.Font(9.5f, FontStyle.Bold), Theme.Text, Right: weight));

                var value = h.IsUsd
                    ? $"평가 {Usd(h.MarketValue)}" + (a.UsdKrw is > 0 && !_hideAmounts ? $" (≈{SignalEngine.Won(Math.Round(a.ItemKrw(h)))})" : "")
                    : $"평가 {Amt(h.MarketValue)}";
                var pnl = h.IsUsd ? SignedUsd(h.ProfitLoss) : SignedAmt(h.ProfitLoss);
                lines.Add(new($"{value}  ·  {pnl} ({SignalEngine.Signed(h.ProfitPct)}%)", small, PnlColor(h.ProfitPct)));
            }
        }

        var foot = $"기준 {a.At:HH:mm}";
        if (a.HasUsd)
            foot += a.UsdKrw is > 0
                ? $" · 달러 종목은 매매기준율 {a.UsdKrw:N1}원으로 환산"
                : " · 환율 조회 실패로 총자산·비중에서 달러 종목을 합치지 못함";
        lines.Add(new(foot, Theme.Font(8f), Theme.Muted, GapBefore: 4));

        return new CardSpec("account", Theme.Text, lines)
        {
            Link = _hideAmounts ? "금액 보기" : "금액 숨기기",
            LinkClick = () => { _hideAmounts = !_hideAmounts; Rerender(); },
            Click = ToggleAccount,
            ClickHint = "클릭하면 한 줄로 접기",
        };
    }

    // ───────────────────────── 카드 모델 / 뷰 ─────────────────────────

    /// <summary>작은 글씨 줄의 한 조각. 조각마다 색이 다를 수 있다.</summary>
    private sealed record Seg(string Text, Color Color, bool Bold = false);

    /// <summary>카드의 한 줄. Right 는 같은 줄 오른쪽 끝의 작은 글자 (예: 비중).</summary>
    private sealed record Line(string Text, Font Font, Color Color, int GapBefore = 0, string? Right = null, bool IsSeparator = false)
    {
        /// <summary>조각마다 색이 다른 줄 (예: 외국인 +12만주 빨강 · 기관 -4만주 파랑). Text 는 높이 계산용으로 조각을 이은 글자.</summary>
        public List<Seg>? Segs { get; init; }

        public static readonly Line Separator = new("", Theme.Font(9f), Theme.Muted, GapBefore: 6, IsSeparator: true);
    }

    private sealed class CardSpec(string key, Color bar, List<Line> lines)
    {
        public string Key { get; } = key;
        public Color Bar { get; } = bar;
        public List<Line> Lines { get; } = lines;
        /// <summary>첫 줄 오른쪽의 색깔 배지 (판정)</summary>
        public string? Badge { get; init; }
        public Color BadgeColor { get; init; }
        /// <summary>첫 줄 오른쪽의 밑줄 링크</summary>
        public string? Link { get; init; }
        public Action? LinkClick { get; init; }
        /// <summary>카드 아무 데나 눌렀을 때 (링크·배지 제외)</summary>
        public Action? Click { get; init; }
        public string ClickHint { get; init; } = "클릭하면 공시 목록 · 우클릭 메뉴";
        /// <summary>우클릭 메뉴 (카드 키가 같으면 처음 만든 것을 계속 쓴다)</summary>
        public IReadOnlyList<(string text, Action action)>? Menu { get; init; }
        /// <summary>첫 줄 글자 바로 뒤에 붙는 작은 글씨 (예: 애프터마켓 가격)</summary>
        public List<Seg>? TitleSub { get; init; }
        /// <summary>첫 줄 종목명 바로 뒤에 같은 크기로 붙는 가격·등락률 조각 (색 다름)</summary>
        public List<Seg>? TitlePrice { get; init; }
        /// <summary>가격이 바뀐 방향으로 잠깐 깜빡이기 위한 값</summary>
        public decimal? TitlePriceValue { get; init; }
        /// <summary>오늘 흐름 미니 차트 (종목 카드)</summary>
        public SparkData? Spark { get; init; }

        /// <summary>이 값이 같으면 라벨 배치가 같으므로 글자만 바꿔도 된다.</summary>
        public string Shape =>
            string.Join("|", Lines.Select(l => $"{l.Font.Size}:{(int)l.Font.Style}:{l.GapBefore}:{l.IsSeparator}:{l.Right is not null}"))
            + $"#{Badge is not null}#{Link is not null}#{TitleSub?.Count ?? 0}#{TitlePrice?.Count ?? 0}#{Spark is not null}";
    }

    private sealed class CardView : Panel
    {
        private const int Left0 = 16;
        private const int RightPad = 12;
        private const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;

        private readonly Panel _bar;
        private readonly List<Label?> _lines = new();   // 구분선 자리는 null
        private readonly List<Label?> _rights = new();
        private readonly Label? _badge;
        private readonly Label? _link;
        private readonly List<Label> _subParts = new();
        private readonly List<Label> _priceParts = new();
        private SparkPanel? _spark;
        private decimal? _lastPrice;
        private System.Windows.Forms.Timer? _flash;
        private int _subTop, _subRows;
        private readonly string _shape;
        private Action? _linkClick;
        private Action? _click;

        public string Key { get; }

        public CardView(CardSpec spec, int width)
        {
            Key = spec.Key;
            _shape = spec.Shape;
            _linkClick = spec.LinkClick;
            DoubleBuffered = true;
            Width = width;
            BackColor = Theme.Surface;
            Margin = new Padding(0, 6, 0, 6);

            _bar = new Panel { Dock = DockStyle.Left, Width = 5, BackColor = spec.Bar };

            if (spec.Badge is not null)
            {
                _badge = new Label
                {
                    Font = Theme.Font(9f, FontStyle.Bold),
                    ForeColor = Color.White,
                    BackColor = spec.BadgeColor,
                    AutoSize = true,
                    Padding = new Padding(6, 2, 6, 2),
                    Text = spec.Badge,
                };
                Controls.Add(_badge);
            }
            if (spec.Link is not null)
            {
                _link = new Label
                {
                    Font = Theme.Font(8.5f, FontStyle.Underline),
                    ForeColor = Theme.Muted,
                    AutoSize = true,
                    Cursor = Cursors.Hand,
                    Text = spec.Link,
                };
                _link.Click += (_, _) => _linkClick?.Invoke();
                Controls.Add(_link);
            }

            int y = 10;
            foreach (var line in spec.Lines)
            {
                y += line.GapBefore;
                if (line.IsSeparator)
                {
                    Controls.Add(new Panel { Location = new Point(Left0, y), Size = new Size(Width - Left0 - RightPad, 1), BackColor = Color.FromArgb(60, 255, 255, 255) });
                    _lines.Add(null);
                    _rights.Add(null);
                    y += 8;
                    continue;
                }

                Label? right = null;
                if (line.Right is not null)
                {
                    right = new Label { Font = Theme.Font(9f), ForeColor = Theme.Muted, AutoSize = true, Text = line.Right };
                    Controls.Add(right);
                }

                var l = line.Segs is { } lsegs ? new SegLabel { Segs = lsegs } : new Label();
                l.Font = line.Font;
                l.ForeColor = line.Color;
                l.AutoSize = false;
                l.Location = new Point(Left0, y);
                l.Width = TextWidth(_lines.Count == 0, right);
                l.Text = line.Text;
                l.Height = Measure(line.Text, line.Font, l.Width);
                Controls.Add(l);
                _lines.Add(l);
                _rights.Add(right);
                if (right is not null) right.Location = new Point(Width - right.PreferredWidth - RightPad, y + 1);
                y = l.Bottom + 2;

                if (_lines.Count == 1 && spec.TitlePrice is { Count: > 0 } prices)
                {
                    foreach (var seg in prices)
                    {
                        var part = new Label { Font = line.Font, ForeColor = seg.Color, AutoSize = true, Text = seg.Text, Padding = new Padding(2, 0, 2, 0) };
                        Controls.Add(part);
                        _priceParts.Add(part);
                    }
                    _lastPrice = spec.TitlePriceValue;
                }

                // 첫 줄(큰 가격) 바로 아래에 작은 프리·애프터마켓 줄 (조각별 색, 넘치면 다음 줄로)
                if (_lines.Count == 1 && spec.TitleSub is { Count: > 0 } segs)
                {
                    foreach (var seg in segs)
                    {
                        var part = new Label
                        {
                            Font = Theme.Font(8.5f, seg.Bold ? FontStyle.Bold : FontStyle.Regular),
                            ForeColor = seg.Color,
                            AutoSize = true,
                            Text = seg.Text,
                        };
                        Controls.Add(part);
                        _subParts.Add(part);
                    }
                    _subTop = y - 1;
                    _subRows = FlowSub();
                    y += _subRows * (_subParts[0].PreferredHeight + 1) + 2;
                }

                // 가격 줄 아래 오늘 흐름 미니 차트
                if (_lines.Count == 1 && spec.Spark is { } spark)
                {
                    _spark = new SparkPanel { Location = new Point(Left0, y + 2), Size = new Size(Width - Left0 - RightPad, 44), Data = spark };
                    Controls.Add(_spark);
                    new ToolTip { InitialDelay = 400 }.SetToolTip(_spark,
                        "오늘 흐름 · 회색 점선 = 기준가, 노란 점선 = 당일 고가, 옅은 음영 = 13시 이후(최종 판단 14:50 전후)\n누르면 큰 차트");
                    y = _spark.Bottom + 6;
                }
            }

            PlaceTopRight();
            Height = y + 10;
            Controls.Add(_bar);
            foreach (var p in _subParts) p.BringToFront();
            foreach (var p in _priceParts) p.BringToFront();
            _badge?.BringToFront();
            _link?.BringToFront();

            _click = spec.Click;
            if (spec.Menu is { Count: > 0 } menuItems)
            {
                var menu = new ContextMenuStrip();
                foreach (var (text, action) in menuItems) menu.Items.Add(text, null, (_, _) => action());
                foreach (Control c in Controls.Cast<Control>().Append(this)) c.ContextMenuStrip = menu;
                Disposed += (_, _) => menu.Dispose();
            }
            if (spec.Click is not null)
            {
                // 카드 전체(안의 글자 포함)를 누를 수 있게. 링크는 자기 동작만.
                var hover = Blend(Theme.Surface, Color.White, 0.04);
                foreach (Control c in Controls.Cast<Control>().Append(this))
                {
                    if (ReferenceEquals(c, _link)) continue;
                    c.Cursor = Cursors.Hand;
                    c.Click += (_, _) => _click?.Invoke();
                    c.MouseEnter += (_, _) => SetHover(true, hover);
                    c.MouseLeave += (_, _) => { if (!ClientRectangle.Contains(PointToClient(MousePosition))) SetHover(false, hover); };
                }
                new ToolTip { InitialDelay = 600 }.SetToolTip(this, spec.ClickHint);
            }
        }

        /// <summary>마우스를 올리면 카드가 살짝 밝아진다. 글자 라벨은 카드 배경을 그대로 따라간다.</summary>
        private void SetHover(bool on, Color hover)
        {
            var bg = on ? hover : Theme.Surface;
            if (BackColor != bg) BackColor = bg;
        }

        /// <summary>첫 줄은 배지/링크 자리를, 오른쪽 글자가 있는 줄은 그 자리를 비운다.</summary>
        private int TextWidth(bool isTitle, Label? right)
        {
            int reserve = 0;
            if (isTitle)
            {
                var topRight = (Control?)_badge ?? _link;
                if (topRight is not null) reserve = topRight.PreferredSize.Width + 6;
            }
            if (right is not null) reserve = Math.Max(reserve, right.PreferredWidth + 4);
            return Width - Left0 - RightPad - reserve;
        }

        private static int Measure(string text, Font font, int width) =>
            TextRenderer.MeasureText(string.IsNullOrEmpty(text) ? " " : text, font, new Size(width, int.MaxValue), Wrap).Height + 2;

        private void PlaceTopRight()
        {
            if (_lines.Count == 0 || _lines[0] is not { } title) return;
            var topRight = (Control?)_badge ?? _link;
            if (topRight is not null)
            {
                topRight.Location = new Point(Width - topRight.PreferredSize.Width - RightPad, title.Top + (_badge is null ? 2 : 0));
                title.Width = TextWidth(true, null);
            }

            if (_priceParts.Count > 0)
            {
                // 종목명 실제 글자 폭 바로 뒤에 이어 붙인다
                int x = title.Left + TextRenderer.MeasureText(title.Text, title.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + 8;
                int limit = (topRight?.Left ?? Width - RightPad) - 4;
                foreach (var p in _priceParts)
                {
                    p.Location = new Point(x, title.Top);
                    p.Visible = x + p.PreferredWidth <= limit;
                    x += p.PreferredWidth + 2;
                }
            }
        }

        /// <summary>가격이 바뀌면 오른 쪽은 빨강, 내린 쪽은 파랑 배경으로 0.5초 깜빡인다.</summary>
        private void Flash(bool up)
        {
            if (_priceParts.Count == 0) return;
            _priceParts[0].BackColor = Blend(Theme.Surface, up ? Theme.Red : Theme.Blue, 0.28);
            _flash ??= new System.Windows.Forms.Timer { Interval = 500 };
            _flash.Stop();
            _flash.Tick -= EndFlash;
            _flash.Tick += EndFlash;
            _flash.Start();
        }

        private void EndFlash(object? sender, EventArgs e)
        {
            _flash?.Stop();
            if (_priceParts.Count > 0) _priceParts[0].BackColor = Color.Transparent;
        }

        private static Color Blend(Color a, Color b, double t) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        protected override void Dispose(bool disposing)
        {
            if (disposing) _flash?.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>조각들을 왼쪽부터 이어 붙이고, 카드 폭을 넘으면 다음 줄로. 사용한 줄 수를 돌려준다.</summary>
        private int FlowSub()
        {
            if (_subParts.Count == 0) return 0;
            int limit = Width - RightPad;
            int x = Left0, row = 0, lineH = _subParts[0].PreferredHeight + 1;
            foreach (var p in _subParts)
            {
                var w = p.PreferredWidth;
                if (x > Left0 && x + w > limit)
                {
                    row++;
                    x = Left0;
                }
                p.Location = new Point(x, _subTop + row * lineH);
                x += w - 2; // AutoSize 라벨의 좌우 여백을 조금 겹쳐 조각 사이가 벌어지지 않게
            }
            return row + 1;
        }

        /// <summary>줄 구조와 줄 높이가 그대로면 글자·색만 바꾸고 true. 다시 만들어야 하면 false.</summary>
        public bool TryUpdate(CardSpec spec)
        {
            if (spec.Key != Key || spec.Shape != _shape) return false;

            // 배지·링크 글자 폭이 바뀌면 첫 줄 폭도 바뀐다 → 먼저 반영
            if (_badge is not null)
            {
                if (_badge.Text != spec.Badge) _badge.Text = spec.Badge;
                if (_badge.BackColor != spec.BadgeColor) _badge.BackColor = spec.BadgeColor;
            }
            if (_link is not null && _link.Text != spec.Link) _link.Text = spec.Link;
            if (_subParts.Count > 0 && spec.TitleSub is { } segs2)
            {
                for (int k = 0; k < segs2.Count; k++)
                {
                    if (_subParts[k].Text != segs2[k].Text) _subParts[k].Text = segs2[k].Text;
                    if (_subParts[k].ForeColor != segs2[k].Color) _subParts[k].ForeColor = segs2[k].Color;
                }
                // 글자 길이가 바뀌어 줄 수가 달라지면 아래 줄들이 밀리므로 카드를 새로 만든다
                if (FlowSub() != _subRows) return false;
            }
            if (_spark is not null && spec.Spark is { } sp)
            {
                _spark.Data = sp;
                _spark.Invalidate();
            }
            if (_priceParts.Count > 0 && spec.TitlePrice is { } prices2)
            {
                for (int k = 0; k < prices2.Count; k++)
                {
                    if (_priceParts[k].Text != prices2[k].Text) _priceParts[k].Text = prices2[k].Text;
                    if (_priceParts[k].ForeColor != prices2[k].Color) _priceParts[k].ForeColor = prices2[k].Color;
                }
                if (spec.TitlePriceValue is { } now && _lastPrice is { } before && now != before)
                    Flash(now > before);
                _lastPrice = spec.TitlePriceValue;
            }
            _linkClick = spec.LinkClick;
            _click = spec.Click;
            if (_bar.BackColor != spec.Bar) _bar.BackColor = spec.Bar;
            PlaceTopRight();

            for (int i = 0; i < spec.Lines.Count; i++)
            {
                var line = spec.Lines[i];
                var label = _lines[i];
                if (label is null) continue;

                var right = _rights[i];
                if (right is not null && right.Text != line.Right)
                {
                    right.Text = line.Right;
                    right.Left = Width - right.PreferredWidth - RightPad;
                    label.Width = TextWidth(i == 0, right);
                }

                // 줄바꿈 수가 달라지면 아래 줄들이 밀리므로 카드를 새로 만든다
                if (Measure(line.Text, line.Font, label.Width) != label.Height) return false;

                if ((label is SegLabel) != (line.Segs is not null)) return false;
                if (label.Text != line.Text) label.Text = line.Text;
                if (label.ForeColor != line.Color) label.ForeColor = line.Color;
                if (label is SegLabel sl && line.Segs is { } ns) { sl.Segs = ns; sl.Invalidate(); }
            }
            PlaceTopRight();   // 제목 글자 폭이 바뀌었을 수 있으니 옆 글씨 위치를 다시 맞춘다
            return true;
        }
    }

    /// <summary>조각마다 색이 다른 한 줄 라벨. 폭을 넘으면 조각 단위로 다음 줄로 넘긴다.</summary>
    private sealed class SegLabel : Label
    {
        public List<Seg> Segs { get; set; } = new();

        public SegLabel() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            const TextFormatFlags F = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            int x = 0, y = 0, lineH = Font.Height;
            foreach (var seg in Segs)
            {
                var font = seg.Bold ? Theme.Font(Font.Size, FontStyle.Bold) : Font;
                var w = TextRenderer.MeasureText(e.Graphics, seg.Text, font, new Size(int.MaxValue, lineH), F).Width;
                if (x > 0 && x + w > Width && seg.Text.Trim().Length > 0) { x = 0; y += lineH + 1; }
                TextRenderer.DrawText(e.Graphics, seg.Text, font, new Point(x, y), seg.Color, F);
                x += w;
            }
        }
    }

    /// <summary>카드 미니 차트 재료</summary>
    private sealed record SparkData(IReadOnlyList<Bar> Bars, decimal PrevClose, decimal? DayHigh);

    /// <summary>
    /// 오늘 1분봉을 작은 선으로. 회색 점선 = 기준가, 노란 점선 = 당일 고가, 옅은 음영 = 13시 이후(최종 판단 14:50 전후).
    /// 선 색은 기준가보다 위면 빨강, 아래면 파랑.
    /// </summary>
    private sealed class SparkPanel : Panel
    {
        public SparkData? Data { get; set; }

        public SparkPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Color.FromArgb(29, 32, 38);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (Data is not { Bars.Count: > 1 } d) return;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var day = DateOnly.FromDateTime(d.Bars[0].Time.DateTime);
            var open = Kst.At(day, 9, 0);
            float W = Width - 4, H = Height - 6;
            decimal hi = d.Bars.Max(b => b.High), lo = d.Bars.Min(b => b.Low);
            if (d.PrevClose > 0) { hi = Math.Max(hi, d.PrevClose); lo = Math.Min(lo, d.PrevClose); }
            if (hi == lo) { hi += 1; lo -= 1; }
            float X(DateTimeOffset t) => 2 + (float)((t - open).TotalMinutes / 390.0) * W;
            float Y(decimal p) => 3 + (float)((hi - p) / (hi - lo)) * H;

            var x13 = X(Kst.At(day, 13, 0));
            using (var shade = new SolidBrush(Color.FromArgb(16, 255, 255, 255)))
                g.FillRectangle(shade, x13, 0, Width - x13, Height);

            using (var dash = new Pen(Color.FromArgb(120, 154, 160, 172)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                if (d.PrevClose > 0) g.DrawLine(dash, 0, Y(d.PrevClose), Width, Y(d.PrevClose));
            if (d.DayHigh is { } dh)
                using (var dot = new Pen(Color.FromArgb(140, Theme.Amber)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot })
                    g.DrawLine(dot, 0, Y(dh), Width, Y(dh));

            var last = d.Bars[^1].Close;
            var color = d.PrevClose > 0 && last < d.PrevClose ? Theme.Blue : Theme.Red;
            using var pen = new Pen(color, 1.5f);
            g.DrawLines(pen, d.Bars.Select(b => new PointF(X(b.Time), Y(b.Close))).ToArray());
            using var dotBrush = new SolidBrush(color);
            g.FillEllipse(dotBrush, X(d.Bars[^1].Time) - 2.5f, Y(last) - 2.5f, 5, 5);
        }
    }

    private sealed class BufferedFlowPanel : FlowLayoutPanel
    {
        public BufferedFlowPanel() => DoubleBuffered = true;
    }
}
