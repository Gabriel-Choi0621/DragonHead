namespace DragonHead.App;

/// <summary>
/// 화면 오른쪽 아래에 뜨는 작은 알림 창. 포커스를 뺏지 않는다 (회사 화면에서 작업 중이어도 타이핑이 끊기지 않게).
/// </summary>
internal sealed class ToastForm : Form
{
    private static readonly List<ToastForm> Open = new();
    private readonly System.Windows.Forms.Timer _closeTimer = new();

    public event EventHandler? Clicked;

    private ToastForm(string title, string body, Color accent, int seconds, bool sticky)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Surface;
        Width = 380;
        Padding = new Padding(0);
        Cursor = Cursors.Hand;

        var bar = new Panel { Dock = DockStyle.Left, Width = 6, BackColor = accent };

        var titleLabel = new Label
        {
            Text = title,
            Font = Theme.Font(11f, FontStyle.Bold),
            ForeColor = accent,
            AutoSize = false,
            Location = new Point(18, 12),
            Width = Width - 52,
            Height = 24,
        };

        var close = new Label
        {
            Text = "×",
            Font = Theme.Font(10f),
            ForeColor = Theme.Muted,
            AutoSize = true,
            Location = new Point(Width - 26, 10),
            Cursor = Cursors.Hand,
        };
        close.Click += (_, _) => Close();

        var bodyLabel = new Label
        {
            Text = body,
            Font = Theme.Font(9.5f),
            ForeColor = Theme.Text,
            AutoSize = false,
            Location = new Point(18, 40),
            Width = Width - 34,
        };
        using (var g = CreateGraphics())
        {
            var measured = g.MeasureString(body, bodyLabel.Font, bodyLabel.Width);
            bodyLabel.Height = (int)Math.Ceiling(measured.Height) + 4;
        }

        var footer = new Label
        {
            Text = sticky ? "클릭하면 대시보드가 열립니다 · 직접 닫을 때까지 유지" : "클릭하면 대시보드가 열립니다",
            Font = Theme.Font(8f),
            ForeColor = Theme.Muted,
            AutoSize = true,
            Location = new Point(18, bodyLabel.Bottom + 6),
        };

        Height = footer.Bottom + 12;

        Controls.Add(close);
        Controls.Add(titleLabel);
        Controls.Add(bodyLabel);
        Controls.Add(footer);
        Controls.Add(bar);

        foreach (Control c in new Control[] { this, titleLabel, bodyLabel, footer, bar })
            c.Click += (_, _) => { Clicked?.Invoke(this, EventArgs.Empty); Close(); };

        if (!sticky)
        {
            _closeTimer.Interval = Math.Max(3, seconds) * 1000;
            _closeTimer.Tick += (_, _) => Close();
            _closeTimer.Start();
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_TOPMOST = 0x00000008;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(60, 255, 255, 255));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closeTimer.Stop();
        _closeTimer.Dispose();
        Open.Remove(this);
        Restack();
        base.OnFormClosed(e);
    }

    private static void Restack()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        int y = area.Bottom - 12;
        // 최신 알림이 맨 아래
        for (int i = Open.Count - 1; i >= 0; i--)
        {
            var t = Open[i];
            y -= t.Height;
            t.Location = new Point(area.Right - t.Width - 12, y);
            y -= 8;
        }
    }

    public static void Show(string title, string body, Color accent, int seconds, bool sticky, Action onClick)
    {
        // 너무 많이 쌓이면 오래된 것부터 닫는다.
        while (Open.Count >= 4)
            Open[0].Close();

        var t = new ToastForm(title, body, accent, seconds, sticky);
        t.Clicked += (_, _) => onClick();
        Open.Add(t);
        Restack();
        t.Show();
    }
}
