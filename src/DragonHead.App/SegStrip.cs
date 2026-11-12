namespace DragonHead.App;

/// <summary>
/// 조각마다 색·굵기가 다른 글자 줄. 폭을 넘으면 조각 단위로 다음 줄로 넘기고, 필요한 높이를 PreferredHeight 로 알려 준다.
/// (Label 여러 개를 FlowLayoutPanel 에 넣으면 폭 계산이 어긋나 글자가 겹치거나 잘려서 직접 그린다)
/// </summary>
internal sealed class SegStrip : Control
{
    private List<(string text, Color color, bool bold)> _parts = new();
    private float _size = 9f;
    private string _key = "";

    public SegStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    public float FontSize { get => _size; set { _size = value; Invalidate(); } }

    /// <summary>내용을 바꾼다. 같은 내용이면 다시 그리지 않는다. 높이가 바뀌면 true.</summary>
    public bool SetParts(List<(string text, Color color, bool bold)> parts)
    {
        var key = string.Join("|", parts.Select(p => p.text + p.color.ToArgb() + p.bold));
        if (key == _key) return false;
        _key = key;
        _parts = parts;
        var h = Measure(Width);
        bool changed = h != Height;
        Height = h;
        Invalidate();
        return changed;
    }

    /// <summary>한 줄에 다 들어갈 때의 폭 (제목 옆처럼 줄바꿈 없이 쓸 때)</summary>
    public int SingleLineWidth()
    {
        using var g = CreateGraphics();
        return _parts.Sum(p => TextRenderer.MeasureText(g, p.text, Font(p.bold), Size.Empty, Flags).Width) + 2;
    }

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
    private Font Font(bool bold) => Theme.Font(_size, bold ? FontStyle.Bold : FontStyle.Regular);

    private int Measure(int width)
    {
        if (_parts.Count == 0 || width <= 0) return Theme.Font(_size).Height + 2;
        using var g = CreateGraphics();
        int x = 0, lines = 1;
        foreach (var p in _parts)
        {
            var w = TextRenderer.MeasureText(g, p.text, Font(p.bold), Size.Empty, Flags).Width;
            if (x > 0 && x + w > width && p.text.Trim().Length > 0) { x = 0; lines++; }
            x += w;
        }
        return lines * (Theme.Font(_size).Height + 2);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        var h = Measure(Width);
        if (h != Height) Height = h;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        int x = 0, y = 0, lineH = Theme.Font(_size).Height + 2;
        foreach (var p in _parts)
        {
            var font = Font(p.bold);
            var w = TextRenderer.MeasureText(e.Graphics, p.text, font, Size.Empty, Flags).Width;
            if (x > 0 && x + w > Width && p.text.Trim().Length > 0) { x = 0; y += lineH; }
            TextRenderer.DrawText(e.Graphics, p.text, font, new Point(x, y + (lineH - font.Height) / 2), p.color, Flags);
            x += w;
        }
    }
}
