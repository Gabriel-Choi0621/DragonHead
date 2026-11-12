using System.Drawing.Drawing2D;

namespace DragonHead.App;

/// <summary>
/// 어두운 테마에 맞춘 가로 슬라이더. 기본 TrackBar 는 다크 배경에서 색을 바꿀 수 없어 직접 그린다.
/// 끄는 동안 ValueChanged, 손을 떼면 Committed.
/// </summary>
internal sealed class OpacitySlider : Control
{
    private int _value = 100;
    private bool _dragging;

    public int Minimum { get; init; } = 40;
    public int Maximum { get; init; } = 100;

    public event EventHandler? ValueChanged;
    public event EventHandler? Committed;

    public OpacitySlider()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        Size = new Size(200, 28);
        BackColor = Theme.Surface;
    }

    public int Value
    {
        get => _value;
        set
        {
            var v = Math.Clamp(value, Minimum, Maximum);
            if (v == _value) return;
            _value = v;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private Rectangle Track => new(10, Height / 2 - 2, Width - 20, 4);

    private int ValueFromX(int x)
    {
        var t = Track;
        var ratio = Math.Clamp((x - t.Left) / (double)t.Width, 0, 1);
        // 5% 단위로 끊는다
        var raw = Minimum + ratio * (Maximum - Minimum);
        return (int)(Math.Round(raw / 5) * 5);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        Capture = true;
        Value = ValueFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) Value = ValueFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
        Committed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Value += e.Delta > 0 ? 5 : -5;
        Committed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Left or Keys.Down) { Value -= 5; Committed?.Invoke(this, EventArgs.Empty); }
        if (e.KeyCode is Keys.Right or Keys.Up) { Value += 5; Committed?.Invoke(this, EventArgs.Empty); }
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        var t = Track;
        var ratio = (_value - Minimum) / (double)(Maximum - Minimum);
        int knobX = t.Left + (int)(t.Width * ratio);

        using (var back = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
            g.FillRectangle(back, t);
        using (var fill = new SolidBrush(Theme.Text))
            g.FillRectangle(fill, t.Left, t.Top, knobX - t.Left, t.Height);

        var knob = new Rectangle(knobX - 7, Height / 2 - 7, 14, 14);
        using (var kb = new SolidBrush(Theme.Text))
            g.FillEllipse(kb, knob);
        using (var kp = new Pen(Theme.Background, 2))
            g.DrawEllipse(kp, knob);
    }
}
