using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using DragonHead.Core;

namespace DragonHead.App;

internal sealed record AppSettingsHolder(AppSettings Settings, string Secret);

/// <summary>
/// %APPDATA%\DragonHead\settings.json 을 읽고 쓴다.
/// ClientSecret 은 Windows DPAPI(현재 사용자 범위)로 암호화해 저장하므로, 다른 사용자·다른 PC에서는 복호화되지 않는다.
/// </summary>
internal static class SettingsStore
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DragonHead");

    public static string SettingsPath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DragonHead.v1");

    public static AppSettingsHolder Load(out bool created)
    {
        Directory.CreateDirectory(Folder);
        created = false;

        if (!File.Exists(SettingsPath))
        {
            Save(Template());
            created = true;
        }

        var raw = File.ReadAllText(SettingsPath);
        var settings = JsonSerializer.Deserialize<AppSettings>(raw, Options)
                       ?? throw new InvalidDataException("settings.json 이 비어 있습니다.");

        // 업데이트로 새 항목(예: DartApiKey)이 생겼는데 예전 설정 파일에 없으면 기본값으로 채워 다시 저장한다.
        // 기존 값은 그대로 두고 빠진 줄만 늘어난다.
        // 안쪽 묶음(Notify 등)에 새로 생긴 항목도 파일에 보이게 (예: CheckEveryMinutes)
        var nestedNew = new[] { "CheckEveryMinutes", "ShockDropPct", "StrongVolumeRatio", "CoreRatio", "CoreIntervalDays" }.Where(k => !raw.Contains($"\"{k}\"", StringComparison.OrdinalIgnoreCase)).ToList();
        if (MissingTopLevelKeys(raw).Count > 0 || nestedNew.Count > 0)
        {
            Log.Write("설정 파일에 새 항목 추가: " + string.Join(", ", MissingTopLevelKeys(raw).Concat(nestedNew)));
            Save(settings);
        }

        string secret = "";
        if (!string.IsNullOrWhiteSpace(settings.ClientSecret))
        {
            // 평문을 발견하면 암호화해서 옮기고 평문은 지운다.
            secret = settings.ClientSecret.Trim();
            settings.ProtectedSecret = Protect(secret);
            settings.ClientSecret = "";
            Save(settings);
        }
        else if (!string.IsNullOrWhiteSpace(settings.ProtectedSecret))
        {
            try
            {
                secret = Unprotect(settings.ProtectedSecret);
            }
            catch (CryptographicException)
            {
                throw new InvalidDataException(
                    "저장된 ClientSecret 을 복호화하지 못했습니다. 다른 PC나 다른 Windows 계정에서 복사한 설정일 수 있습니다. " +
                    "ProtectedSecret 을 비우고 ClientSecret 에 다시 입력하세요.");
            }
        }

        return new AppSettingsHolder(settings, secret);
    }

    /// <summary>AppSettings 에 있는데 파일에는 없는 최상위 항목 이름</summary>
    private static List<string> MissingTopLevelKeys(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var present = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return typeof(AppSettings).GetProperties()
                .Where(p => p.CanWrite && p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true).Length == 0)
                .Select(p => p.Name)
                .Where(n => !present.Contains(n))
                .ToList();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }

    /// <summary>키 입력 창에서 저장: 시크릿은 암호화해서 ProtectedSecret 에, 평문 칸은 비운다.</summary>
    public static void SaveWithSecret(AppSettings settings, string secret)
    {
        Directory.CreateDirectory(Folder);
        settings.ProtectedSecret = Protect(secret);
        settings.ClientSecret = "";
        Save(settings);
    }

    public static void Save(AppSettings settings) =>
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options), new UTF8Encoding(false));

    public static void OpenInEditor()
    {
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{SettingsPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("설정 파일 열기 실패: " + ex.Message);
        }
    }

    private static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    private static string Unprotect(string b64) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b64), Entropy, DataProtectionScope.CurrentUser));

    private static AppSettings Template() => new()
    {
        // DartApiKey 를 넣으면 실적·IR·자사주 공시가 자동으로 명분이 된다. 아래 Events 는 직접 넣는 명분 (HBM 오피셜 뉴스 등).
        Events = new List<CatalystEvent>
        {
            // 2026-10-21 기준 보도로 넣은 날짜. 공식 공시가 나오면 맞춰 고칠 것.
            new() { Date = "2026-10-28", Symbol = "000660", Title = "SK하이닉스 3분기 실적 발표 (28~29일 예정, 공시로 확인)" },
            new() { Date = "2026-10-29", Symbol = "005930", Title = "삼성전자 3분기 확정 실적 발표" },
        },
    };
}

/// <summary>
/// 상황판 창 위치·핀·투명도. settings.json 과 분리한 이유: 사용자가 메모장으로 설정을 고치는 중에
/// 앱이 창 위치를 저장하면서 그 수정 내용을 덮어쓰지 않게.
/// </summary>
internal static class WindowStore
{
    private static string PathName => Path.Combine(SettingsStore.Folder, "window.json");

    public static WindowSettings Load()
    {
        try
        {
            if (File.Exists(PathName))
                return JsonSerializer.Deserialize<WindowSettings>(File.ReadAllText(PathName)) ?? new WindowSettings();
        }
        catch (Exception ex)
        {
            Log.Write("window.json 읽기 실패, 기본값 사용: " + ex.Message);
        }
        return new WindowSettings();
    }

    public static void Save(WindowSettings w)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            File.WriteAllText(PathName, JsonSerializer.Serialize(w, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Write("window.json 저장 실패: " + ex.Message);
        }
    }
}

internal static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = Path.Combine(SettingsStore.Folder, "log.txt");
                if (File.Exists(path) && new FileInfo(path).Length > 1_000_000)
                    File.Delete(path);
                File.AppendAllText(path, $"{Kst.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 로그 실패로 앱이 죽으면 안 된다.
        }
    }
}
