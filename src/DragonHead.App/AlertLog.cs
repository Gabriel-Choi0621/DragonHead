using System.Globalization;
using System.Text.Json;
using DragonHead.Core;

namespace DragonHead.App;

/// <summary>뜬 알림 한 건</summary>
internal sealed class AlertRecord
{
    public DateTimeOffset Time { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>알림 색 (ARGB)</summary>
    public int Color { get; set; }
    public bool Read { get; set; }
}

/// <summary>
/// 알림 기록. 토스트를 못 보고 지나쳐도 상황판의 '알림'에서 다시 볼 수 있게.
/// %APPDATA%\DragonHead\alerts.json 에 최근 7일·최대 300건을 남긴다.
/// </summary>
internal sealed class AlertLog
{
    private static string PathName => System.IO.Path.Combine(SettingsStore.Folder, "alerts.json");
    private readonly List<AlertRecord> _items;

    public event Action? Changed;

    public AlertLog()
    {
        try
        {
            _items = File.Exists(PathName)
                ? JsonSerializer.Deserialize<List<AlertRecord>>(File.ReadAllText(PathName)) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            Log.Write("alerts.json 읽기 실패: " + ex.Message);
            _items = new();
        }
        Trim();
    }

    /// <summary>최신이 앞</summary>
    public IReadOnlyList<AlertRecord> Items => _items;

    public int Unread => _items.Count(a => !a.Read);

    public void Add(string title, string body, Color color)
    {
        _items.Insert(0, new AlertRecord { Time = Kst.Now, Title = title, Body = body, Color = color.ToArgb() });
        Trim();
        Save();
        Changed?.Invoke();
    }

    public void MarkAllRead()
    {
        if (Unread == 0) return;
        foreach (var a in _items) a.Read = true;
        Save();
        Changed?.Invoke();
    }

    public void Clear()
    {
        _items.Clear();
        Save();
        Changed?.Invoke();
    }

    private void Trim()
    {
        var cutoff = Kst.Now.AddDays(-7);
        _items.RemoveAll(a => a.Time < cutoff);
        if (_items.Count > 300) _items.RemoveRange(300, _items.Count - 300);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            File.WriteAllText(PathName, JsonSerializer.Serialize(_items, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
            }));
        }
        catch (Exception ex)
        {
            Log.Write("alerts.json 저장 실패: " + ex.Message);
        }
    }
}

/// <summary>알림 기록 창. 날짜별로 묶어 최신순. 열면 전부 읽음 처리.</summary>
internal sealed class AlertsForm : PopupForm
{
    private readonly AlertLog _log;
    private readonly FlowLayoutPanel _list;
    private readonly Label _status;
    private static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");

    public AlertsForm(AlertLog log, Form? near)
    {
        _log = log;

        Text = "알림 기록";
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(1);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        ClientSize = new Size(400, 520);
        MinimumSize = new Size(320, 260);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = near?.TopMost ?? true;
        Opacity = near?.Opacity ?? 1.0;
        DoubleBuffered = true;
        PlaceBeside(near);

        var header = new Panel { Dock = DockStyle.Top, Height = 46 };
        var title = new Label { Text = "알림 기록", AutoSize = true, Font = Theme.Font(13f, FontStyle.Bold), Location = new Point(14, 10) };
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
        var clear = new Label
        {
            Text = "비우기",
            AutoSize = true,
            Font = Theme.Font(8.5f, FontStyle.Underline),
            ForeColor = Theme.Muted,
            Cursor = Cursors.Hand,
        };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "알림 기록을 모두 지울까요?", "알림 기록", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                _log.Clear();
        };
        header.Controls.AddRange(new Control[] { title, close, clear });
        header.Layout += (_, _) =>
        {
            close.Location = new Point(header.Width - close.PreferredWidth - 6, 6);
            clear.Location = new Point(close.Left - clear.PreferredWidth - 8, 14);
        };
        foreach (Control c in new Control[] { header, title })
            c.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) Drag(); };

        _list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(10, 0, 10, 8),
        };
        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            Font = Theme.Font(8.5f),
            ForeColor = Theme.Muted,
            Padding = new Padding(14, 3, 14, 0),
        };
        Controls.Add(_list);
        Controls.Add(_status);
        Controls.Add(header);

        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        ResizeEnd += (_, _) => Render();
        _log.Changed += OnChanged;
        FormClosed += (_, _) => _log.Changed -= OnChanged;
        Shown += (_, _) => Render();
    }

    private void OnChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(Render); else Render();
    }

    private void Render()
    {
        // 지금 보고 있으니 안 읽은 표시는 이번 화면에만 남기고 기록은 읽음으로
        var unread = _log.Items.Where(a => !a.Read).ToHashSet();

        _list.SuspendLayout();
        var old = _list.Controls.Cast<Control>().ToList();
        _list.Controls.Clear();
        foreach (var c in old) c.Dispose();

        int width = Math.Max(260, _list.ClientSize.Width - _list.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
        var today = Kst.Today;

        if (_log.Items.Count == 0)
            _list.Controls.Add(new Label
            {
                Text = "아직 뜬 알림이 없습니다.",
                AutoSize = false, Width = width, Height = 30,
                Font = Theme.Font(9f), ForeColor = Theme.Muted, Margin = new Padding(0, 8, 0, 0),
            });

        foreach (var day in _log.Items.GroupBy(a => DateOnly.FromDateTime(a.Time.ToKst().DateTime)))
        {
            var label = day.Key == today ? "오늘" : day.Key == today.AddDays(-1) ? "어제" : day.Key.ToString("M월 d일 (ddd)", Ko);
            _list.Controls.Add(new Label
            {
                Text = label,
                AutoSize = false, Width = width, Height = 24,
                Font = Theme.Font(9f, FontStyle.Bold), ForeColor = Theme.Muted, Margin = new Padding(0, 8, 0, 2),
            });
            foreach (var a in day)
                _list.Controls.Add(Row(a, width, unread.Contains(a)));
        }
        _list.ResumeLayout();

        _status.Text = _log.Items.Count == 0 ? "" : $"최근 7일 {_log.Items.Count}건";
        _log.MarkAllRead();
    }

    private static Control Row(AlertRecord a, int width, bool isNew)
    {
        var accent = Color.FromArgb(a.Color);
        var bg = isNew ? Color.FromArgb(44, 52, 66) : Theme.Surface;
        var row = new Panel { Width = width, BackColor = bg, Margin = new Padding(0, 0, 0, 4) };
        var bar = new Panel { Dock = DockStyle.Left, Width = 4, BackColor = accent };

        var time = new Label
        {
            Text = a.Time.ToKst().ToString("HH:mm"),
            AutoSize = true, Font = Theme.Font(8.5f), ForeColor = Theme.Muted, Location = new Point(12, 8),
        };
        var title = new Label
        {
            Text = a.Title + (isNew ? "  •" : ""),
            AutoSize = false, Font = Theme.Font(9.5f, FontStyle.Bold), ForeColor = accent,
            Location = new Point(56, 7), Width = width - 66,
        };
        title.Height = TextRenderer.MeasureText(title.Text, title.Font, new Size(title.Width, int.MaxValue), TextFormatFlags.WordBreak).Height + 2;
        var body = new Label
        {
            Text = a.Body,
            AutoSize = false, Font = Theme.Font(9f), ForeColor = Theme.Text,
            Location = new Point(56, title.Bottom + 2), Width = width - 66,
        };
        body.Height = string.IsNullOrWhiteSpace(a.Body)
            ? 0
            : TextRenderer.MeasureText(a.Body, body.Font, new Size(body.Width, int.MaxValue), TextFormatFlags.WordBreak).Height + 2;

        row.Controls.AddRange(new Control[] { time, title, body });
        row.Controls.Add(bar);
        row.Height = Math.Max(time.Bottom, body.Bottom) + 8;
        return row;
    }
}
