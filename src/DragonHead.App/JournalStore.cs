using System.Diagnostics;
using System.Text.Json;
using DragonHead.Core;

namespace DragonHead.App;

/// <summary>판단 기록 파일 (%APPDATA%\DragonHead\journal.json) 과 성적표 만들기.</summary>
internal sealed class JournalStore
{
    private static string PathJson => Path.Combine(SettingsStore.Folder, "journal.json");
    private readonly List<JournalEntry> _entries;
    private DateTimeOffset _savedAt = DateTimeOffset.MinValue;
    private bool _dirty;

    public JournalStore()
    {
        try { _entries = File.Exists(PathJson) ? JsonSerializer.Deserialize<List<JournalEntry>>(File.ReadAllText(PathJson)) ?? new() : new(); }
        catch (Exception ex)
        {
            Log.Write("판단 기록 읽기 실패 (새로 시작, 기존 파일은 .bak 으로 보관): " + ex.Message);
            try { File.Copy(PathJson, PathJson + ".bak", overwrite: true); } catch { }
            _entries = new();
        }
    }

    public int Count => _entries.Count;

    /// <summary>판단이 바뀌면 바로, 가격만 바뀌면 10분마다 저장.</summary>
    public void Record(IReadOnlyList<Snapshot> snaps, IReadOnlyList<Evaluation> evals)
    {
        bool changed = false;
        int before = _entries.Count;
        for (int i = 0; i < Math.Min(snaps.Count, evals.Count); i++)
        {
            try { changed |= Journal.Upsert(_entries, snaps[i], evals[i], Kst.Now); }
            catch (Exception ex) { Log.Write("판단 기록 실패: " + ex.Message); }
        }
        // Upsert 는 가격만 바뀐 경우 false 지만 내용은 갱신됨 (기록 시간대에만)
        var tod = TimeOnly.FromDateTime(Kst.Now.DateTime);
        if (tod >= Journal.StartAt && tod <= Journal.FreezeAt) _dirty = true;
        if (changed || _entries.Count != before || (_dirty && Kst.Now - _savedAt > TimeSpan.FromMinutes(10))) Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            var tmp = PathJson + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_entries.OrderBy(e => e.Date).ThenBy(e => e.Symbol), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, PathJson, overwrite: true);
            _savedAt = Kst.Now;
            _dirty = false;
        }
        catch (Exception ex) { Log.Write("판단 기록 저장 실패: " + ex.Message); }
    }

    /// <summary>기록 종목들의 일봉을 받아 수익률을 붙이고 HTML·CSV 로 저장 후 연다.</summary>
    public async Task<string> OpenReportAsync(TossClient client)
    {
        Save();
        var daily = new Dictionary<string, IReadOnlyList<Bar>>();
        foreach (var g in _entries.GroupBy(e => e.Symbol))
        {
            var from = g.Min(e => e.Date);
            var bars = new List<Bar>();
            DateTimeOffset? before = null;
            for (int page = 0; page < 10; page++)
            {
                var (chunk, next) = await client.GetCandlesAsync(g.Key, "1d", 200, before);
                bars.AddRange(chunk);
                if (next is null || chunk.Count == 0 || chunk.Min(b => DateOnly.FromDateTime(b.Time.DateTime)) <= from) break;
                before = next;
            }
            daily[g.Key] = bars.GroupBy(b => DateOnly.FromDateTime(b.Time.DateTime)).Select(x => x.Last()).OrderBy(b => b.Time).ToList();
        }
        var scored = Journal.Score(_entries, daily);
        var folder = Path.Combine(SettingsStore.Folder, "reports");
        Directory.CreateDirectory(folder);
        var html = Path.Combine(folder, "판단성적표.html");
        await File.WriteAllTextAsync(html, Journal.Html(scored, Kst.Now), new System.Text.UTF8Encoding(false));
        // 엑셀이 한글을 깨지 않게 BOM 포함
        await File.WriteAllTextAsync(Path.Combine(SettingsStore.Folder, "journal.csv"), Journal.Csv(scored), new System.Text.UTF8Encoding(true));
        Process.Start(new ProcessStartInfo(html) { UseShellExecute = true });
        return html;
    }
}
