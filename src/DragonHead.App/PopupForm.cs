using System.Runtime.InteropServices;

namespace DragonHead.App;

/// <summary>
/// 제목줄 없는 어두운 보조 창 (공시 목록, 알림 기록) 공통 틀:
/// 그림자, 둥근 모서리(윈도우 11), 얇은 테두리, 가장자리로 크기 조절, Drag() 로 이동.
/// </summary>
internal abstract class PopupForm : Form
{
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>상단바를 누른 채 끌면 창 이동</summary>
    protected void Drag()
    {
        ReleaseCapture();
        SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
    }

    /// <summary>기준 창 왼쪽에 붙이고, 자리가 없으면 오른쪽에</summary>
    protected void PlaceBeside(Form? near)
    {
        var area = Screen.FromControl(near ?? this).WorkingArea;
        if (near is null)
        {
            Location = new Point(area.Right - Width - 24, area.Bottom - Height - 24);
            return;
        }
        var x = near.Left - Width - 8;
        if (x < area.Left) x = Math.Min(near.Right + 8, area.Right - Width);
        Location = new Point(x, Math.Clamp(near.Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); } catch { /* 윈도우 10 이하 */ }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(70, 255, 255, 255));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    /// <summary>가장자리 6px 로 크기 조절</summary>
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != 0x84 || (int)m.Result != 1) return;
        var p = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
        const int g = 6;
        bool l = p.X < g, r = p.X >= ClientSize.Width - g, t = p.Y < g, b = p.Y >= ClientSize.Height - g;
        m.Result = (IntPtr)((t, b, l, r) switch
        {
            (true, _, true, _) => 13, (true, _, _, true) => 14, (_, true, true, _) => 16, (_, true, _, true) => 17,
            (true, _, _, _) => 12, (_, true, _, _) => 15, (_, _, true, _) => 10, (_, _, _, true) => 11, _ => 1,
        });
    }
}
