using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DragonHead.Core;

namespace DragonHead.App;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(24, 26, 31);
    public static readonly Color Surface = Color.FromArgb(34, 37, 44);
    public static readonly Color Text = Color.FromArgb(232, 234, 238);
    public static readonly Color Muted = Color.FromArgb(150, 156, 168);

    public static readonly Color Gray = Color.FromArgb(120, 126, 138);
    public static readonly Color Red = Color.FromArgb(240, 84, 84);
    public static readonly Color Amber = Color.FromArgb(240, 176, 64);
    public static readonly Color Blue = Color.FromArgb(76, 154, 255);
    public static readonly Color Green = Color.FromArgb(46, 204, 113);
    /// <summary>회피 (관망보다 아래)</summary>
    public static readonly Color Purple = Color.FromArgb(168, 120, 255);

    private static readonly Dictionary<(float, FontStyle), Font> Fonts = new();

    /// <summary>같은 글꼴은 재사용한다. 대시보드가 15초마다 다시 그려지므로 매번 만들면 GDI 핸들이 샌다.</summary>
    public static Font Font(float size, FontStyle style = FontStyle.Regular)
    {
        if (!Fonts.TryGetValue((size, style), out var f))
            Fonts[(size, style)] = f = new Font(FontManager.Family, size, style);
        return f;
    }

    public static Color Of(Verdict v) => v switch
    {
        Verdict.GapTrap or Verdict.ShockWarn => Red,
        Verdict.NoChase => Amber,
        Verdict.DipCandidate => Blue,
        Verdict.DipBuy => Green,
        Verdict.Avoid => Purple,
        _ => Gray,
    };

    public static Color Of(GeneralVerdict v) => v switch
    {
        GeneralVerdict.Good => Green,
        GeneralVerdict.CheckFirst => Amber,
        GeneralVerdict.NotToday => Purple,
        _ => Gray,
    };

    /// <summary>카드 배지·막대: 명분일엔 명분 판단, 아닌 날엔 평소 판단</summary>
    public static (string text, Color color) Badge(Evaluation e) =>
        e.General is { } g && g.Verdict != GeneralVerdict.Wait && e.Checks.FirstOrDefault(c => c.Label == "명분")?.Passed != true
            ? (GeneralView.Describe(g.Verdict), Of(g.Verdict))
            : (SignalEngine.Describe(e.Verdict), Of(e.Verdict));

    /// <summary>
    /// 트레이 아이콘·요약 알림 색: 카드 배지와 같은 기준(명분일엔 명분 판단, 아닌 날엔 평소 판단)으로 가장 중요한 것.
    /// 순서: 명분 매수 > 쇼크 > 눌림 후보 > 확인 후 > 사도 괜찮음 > 추격 금지 > 갭 > 기다려 봄 > 약세 경계 > 대기
    /// </summary>
    public static Color LoudestColor(IEnumerable<Evaluation> evals)
    {
        int Rank(Evaluation e)
        {
            var (text, _) = Badge(e);
            if (text == GeneralView.Describe(GeneralVerdict.CheckFirst)) return 6;
            if (text == GeneralView.Describe(GeneralVerdict.Good)) return 5;
            if (text == GeneralView.Describe(GeneralVerdict.Later)) return 2;
            if (text == GeneralView.Describe(GeneralVerdict.NotToday)) return 2;
            return e.Verdict switch
            {
                Verdict.DipBuy => 9, Verdict.ShockWarn => 8, Verdict.DipCandidate => 7,
                Verdict.NoChase => 4, Verdict.GapTrap => 3, Verdict.Avoid => 1, _ => 0,
            };
        }
        var top = evals.OrderByDescending(Rank).FirstOrDefault();
        return top is null || Rank(top) == 0 ? Gray : Badge(top).color;
    }

    /// <summary>여러 종목 중 가장 '중요한' 판정 하나 (트레이 아이콘 색에 쓴다).</summary>
    public static Verdict Loudest(IEnumerable<Verdict> verdicts) =>
        verdicts.DefaultIfEmpty(Verdict.Wait).MaxBy(v => v switch
        {
            Verdict.DipBuy => 7,
            Verdict.ShockWarn => 6,
            Verdict.DipCandidate => 5,
            Verdict.NoChase => 4,
            Verdict.GapTrap => 3,
            Verdict.Avoid => 2,
            _ => 0,
        });

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static readonly Dictionary<Color, Icon> IconCache = new();

    /// <summary>판정 색으로 칠한 원형 트레이 아이콘.</summary>
    public static Icon TrayIcon(Color color)
    {
        if (IconCache.TryGetValue(color, out var cached)) return cached;

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 2, 2, 28, 28);
            using var pen = new Pen(Color.FromArgb(40, 0, 0, 0), 2);
            g.DrawEllipse(pen, 2, 2, 28, 28);
            using var f = new Font("맑은 고딕", 13, FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString("용", f);
            g.DrawString("용", f, Brushes.White, (32 - size.Width) / 2 + 1, (32 - size.Height) / 2 + 1);
        }

        var hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        IconCache[color] = icon;
        return icon;
    }
}
