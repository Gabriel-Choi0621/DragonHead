using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DragonHead.App;

/// <summary>
/// 앱에 내장한 Pretendard(SIL OFL 1.1)를 설치 없이 쓰게 한다.
/// WinForms 라벨은 GDI로 글자를 그리므로 GDI용(AddFontMemResourceEx)과 GDI+용(PrivateFontCollection)에 둘 다 등록해야 한다.
/// 실패하면 PC에 설치된 Pretendard → 맑은 고딕 순으로 대신 쓴다.
/// </summary>
internal static class FontManager
{
    private static readonly PrivateFontCollection Collection = new();
    private static FontFamily? _family;
    private static bool _loaded;

    [DllImport("gdi32.dll")]
    private static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);

    public static FontFamily Family
    {
        get
        {
            if (!_loaded) Load();
            return _family!;
        }
    }

    public static string FamilyName => Family.Name;

    private static void Load()
    {
        _loaded = true;
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = asm.GetManifestResourceStream(name);
                if (stream is null) continue;
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);

                // PrivateFontCollection 은 메모리가 살아 있어야 하므로 해제하지 않는다 (앱 수명 동안 2개, 약 5MB)
                var ptr = Marshal.AllocCoTaskMem(bytes.Length);
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                uint count = 0;
                AddFontMemResourceEx(ptr, (uint)bytes.Length, IntPtr.Zero, ref count);
                Collection.AddMemoryFont(ptr, bytes.Length);
            }
            _family = Collection.Families.FirstOrDefault(f => f.Name == "Pretendard");
        }
        catch (Exception ex)
        {
            Log.Write("내장 글꼴 로드 실패: " + ex.Message);
        }

        _family ??= Installed("Pretendard") ?? Installed("맑은 고딕") ?? FontFamily.GenericSansSerif;
    }

    private static FontFamily? Installed(string name)
    {
        try
        {
            using var installed = new InstalledFontCollection();
            return installed.Families.FirstOrDefault(f => f.Name == name);
        }
        catch
        {
            return null;
        }
    }
}
