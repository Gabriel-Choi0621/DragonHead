using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Serialization;

namespace DragonHead.Core;

/// <summary>
/// 판단 기록 한 줄: 한 종목·한 거래일의 마지막 판단. 수익률은 저장하지 않고 성적표를 만들 때 일봉으로 계산한다.
/// </summary>
public sealed class JournalEntry
{
    public DateOnly Date { get; set; }
    public string Symbol { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>마지막으로 갱신한 시각 (HH:mm)</summary>
    public string At { get; set; } = "";
    public decimal Price { get; set; }
    public decimal? ChangePct { get; set; }
    public decimal? KospiPct { get; set; }
    public long? Foreign { get; set; }
    public long? Institution { get; set; }
    public bool FlowConfirmed { get; set; }
    public string? Catalyst { get; set; }
    /// <summary>명분 판단 (SignalEngine.Describe)</summary>
    public string Verdict { get; set; } = "";
    /// <summary>평소 판단 (GeneralView.Describe). 명분일엔 '대기'</summary>
    public string? General { get; set; }
    public string? Opinion { get; set; }
    public bool CoreReady { get; set; }
    public bool OppReady { get; set; }
    public bool Bought { get; set; }
    /// <summary>밤사이 미국 (티커 → %). UsDate 는 그 미국 장 날짜.</summary>
    public Dictionary<string, decimal> Us { get; set; } = new();
    public DateOnly? UsDate { get; set; }
    [JsonIgnore] public decimal? UsSox => Us.TryGetValue("SOXX", out var v) ? v : null;
}

public static class Journal
{
    /// <summary>이 시각 이후로는 그날 기록을 고치지 않는다 (종가 기준 판단이 마지막).</summary>
    public static readonly TimeOnly FreezeAt = new(15, 40);
    public static readonly TimeOnly StartAt = new(10, 0);

    /// <summary>
    /// 판단을 기록에 반영한다. 거래일 10:00~15:40 사이엔 덮어쓰고, 그 뒤엔 그날 기록이 없을 때만 한 번 넣는다.
    /// 매수 여부(Bought)는 체결 내역으로 언제든 갱신한다. 바뀐 게 있으면 true.
    /// </summary>
    public static bool Upsert(List<JournalEntry> journal, Snapshot s, Evaluation e, DateTimeOffset now)
    {
        bool changed = false;
        // 매수 표시: 최근 기록에 체결 반영
        var buyDays = s.Fills.Where(f => f.IsBuy && f.Symbol == s.Symbol).Select(f => f.Date).ToHashSet();
        foreach (var j in journal.Where(j => j.Symbol == s.Symbol && !j.Bought && buyDays.Contains(j.Date)))
        {
            j.Bought = true; changed = true;
        }

        if (s.Market is not { IsOpen: true } || e.Phase is Phase.Holiday) return changed;
        var kst = now.ToKst();
        var today = DateOnly.FromDateTime(kst.DateTime);
        var tod = TimeOnly.FromDateTime(kst.DateTime);
        if (tod < StartAt || e.ChangePct is null || s.Last <= 0) return changed;

        var existing = journal.FirstOrDefault(j => j.Date == today && j.Symbol == s.Symbol);
        if (existing is not null && tod > FreezeAt) return changed;

        var j2 = existing ?? new JournalEntry { Date = today, Symbol = s.Symbol };
        var before = existing is null ? null : Key(existing);
        j2.Name = s.Name;
        j2.At = kst.ToString("HH:mm", CultureInfo.InvariantCulture);
        j2.Price = s.Last;
        j2.ChangePct = e.ChangePct;
        j2.KospiPct = s.MarketContext?.KospiChangePct;
        j2.Foreign = e.ForeignTodayShares;
        j2.Institution = e.InstitutionTodayShares;
        j2.FlowConfirmed = e.ForeignTodayConfirmed;
        j2.Catalyst = s.CatalystToday;
        j2.Verdict = SignalEngine.Describe(e.Verdict);
        j2.General = e.General is { } g ? GeneralView.Describe(g.Verdict) : null;
        j2.Opinion = e.General?.Opinion;
        j2.CoreReady = e.Plan?.Core is { Ready: true };
        j2.OppReady = e.Plan?.Opp is { Ready: true };
        j2.Bought = j2.Bought || buyDays.Contains(today);
        var us = s.MarketContext?.UsOvernight ?? Array.Empty<UsMove>();
        if (us.Count > 0)
        {
            j2.Us = us.ToDictionary(m => m.Symbol, m => m.ChangePct);
            j2.UsDate = us[0].SessionDate;
        }
        if (existing is null) { journal.Add(j2); return true; }
        // 가격·시각만 바뀐 건 '변경 없음' (파일 저장 줄이기). 판단·수급·매수가 바뀌면 변경.
        return changed || before != Key(j2);
    }

    private static string Key(JournalEntry j) =>
        $"{j.Verdict}|{j.General}|{j.CoreReady}|{j.OppReady}|{j.Bought}|{j.FlowConfirmed}|{j.UsDate}|{j.Catalyst}";

    // ───────────────────────── 성적 ─────────────────────────

    public sealed record Scored(JournalEntry E, decimal? Close, decimal? R5, decimal? R20);

    /// <summary>그날 종가 기준 5·20거래일 뒤 수익률. 일봉에 그날이 없으면 기록 가격으로.</summary>
    public static List<Scored> Score(IEnumerable<JournalEntry> entries, IReadOnlyDictionary<string, IReadOnlyList<Bar>> daily)
    {
        var list = new List<Scored>();
        foreach (var e in entries.OrderBy(x => x.Date).ThenBy(x => x.Symbol))
        {
            decimal? close = null, r5 = null, r20 = null;
            if (daily.TryGetValue(e.Symbol, out var bars) && bars.Count > 0)
            {
                var days = bars.Select(b => DateOnly.FromDateTime(b.Time.DateTime)).ToList();
                int i = days.IndexOf(e.Date);
                if (i >= 0)
                {
                    close = bars[i].Close;
                    if (i + 5 < bars.Count) r5 = Math.Round((bars[i + 5].Close / close.Value - 1m) * 100m, 2);
                    if (i + 20 < bars.Count) r20 = Math.Round((bars[i + 20].Close / close.Value - 1m) * 100m, 2);
                }
            }
            list.Add(new Scored(e, close ?? e.Price, r5, r20));
        }
        return list;
    }

    public sealed record Group(string Label, int N, int N5, decimal? Avg5, decimal? Avg20, int N20, decimal? Win5, decimal? SameDay);

    public static Group Make(string label, IReadOnlyList<Scored> rows)
    {
        var r5 = rows.Where(r => r.R5 is not null).Select(r => r.R5!.Value).ToList();
        var r20 = rows.Where(r => r.R20 is not null).Select(r => r.R20!.Value).ToList();
        var same = rows.Where(r => r.E.ChangePct is not null).Select(r => r.E.ChangePct!.Value).ToList();
        return new Group(label, rows.Count, r5.Count,
            r5.Count > 0 ? Math.Round(r5.Average(), 2) : null,
            r20.Count > 0 ? Math.Round(r20.Average(), 2) : null, r20.Count,
            r5.Count > 0 ? Math.Round(r5.Count(x => x > 0) * 100m / r5.Count, 0) : null,
            same.Count > 0 ? Math.Round(same.Average(), 2) : null);
    }

    public static IEnumerable<(string title, string note, List<Group> groups)> Tables(IReadOnlyList<Scored> rows)
    {
        var all = Make("전체 (기록한 모든 날)", rows);
        var plain = rows.Where(r => r.E.Catalyst is null).ToList();
        var cat = rows.Where(r => r.E.Catalyst is not null).ToList();

        yield return ("평소 판단 (명분 없는 날)", "앱이 '사도 괜찮음'이라고 한 날이 '전체'보다 나으면 판단이 도움이 된 것",
            new[] { all }.Concat(plain.GroupBy(r => r.E.General ?? "-").OrderBy(g => g.Key).Select(g => Make(g.Key, g.ToList()))).ToList());
        if (cat.Count > 0)
            yield return ("명분 판단 (명분일)", "명분일은 '눌림 매수'가 '전체'보다 나은지",
                new[] { all }.Concat(cat.GroupBy(r => r.E.Verdict).OrderBy(g => g.Key).Select(g => Make(g.Key, g.ToList()))).ToList());
        yield return ("매수 차례 · 실제 매수", "앱이 매수 차례라고 한 날, 실제로 산 날",
            new List<Group>
            {
                all,
                Make("기본 몫 매수 차례", rows.Where(r => r.E.CoreReady).ToList()),
                Make("기회 몫 매수 차례", rows.Where(r => r.E.OppReady).ToList()),
                Make("매수 차례 아님", rows.Where(r => !r.E.CoreReady && !r.E.OppReady).ToList()),
                Make("실제로 산 날", rows.Where(r => r.E.Bought).ToList()),
            });
        var us = rows.Where(r => r.E.UsSox is not null).ToList();
        if (us.Count > 0)
            yield return ("밤사이 미국 반도체 (반도체 ETF SOXX 기준)", "'같은 날'은 그날 한국 종목 등락. 미국이 크게 빠진 다음 날 사는 게 나았는지 본다",
                new List<Group>
                {
                    all,
                    Make("SOXX -3% 이하", us.Where(r => r.E.UsSox <= -3m).ToList()),
                    Make("SOXX -3 ~ -1.5%", us.Where(r => r.E.UsSox is > -3m and <= -1.5m).ToList()),
                    Make("SOXX ±1.5% 안", us.Where(r => r.E.UsSox is > -1.5m and < 1.5m).ToList()),
                    Make("SOXX +1.5 ~ +3%", us.Where(r => r.E.UsSox is >= 1.5m and < 3m).ToList()),
                    Make("SOXX +3% 이상", us.Where(r => r.E.UsSox >= 3m).ToList()),
                    Make("미국 약세 + 사도 괜찮음", us.Where(r => r.E.UsSox <= -1.5m && r.E.General == "사도 괜찮음").ToList()),
                });
    }

    // ───────────────────────── 출력 ─────────────────────────

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string P(decimal? v) => v is { } x ? (x > 0 ? "+" : "") + x.ToString("0.##", CultureInfo.InvariantCulture) + "%" : "-";
    private static string C(decimal? v) => v is > 0 ? "up" : v is < 0 ? "down" : "";

    public static string Html(IReadOnlyList<Scored> rows, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append("""
<!doctype html><html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<link rel="stylesheet" href="https://cdn.jsdelivr.net/gh/orioncactus/pretendard@v1.3.9/dist/web/static/pretendard.min.css">
<style>
:root{--bg:#181a1f;--card:#22252c;--text:#e8eaee;--muted:#969ca8;--line:#30343c;--up:#f05454;--down:#4c9aff;--amber:#f0b040}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.6 Pretendard,"Malgun Gothic",sans-serif}
main{max-width:1000px;margin:0 auto;padding:28px 16px 60px}h1{font-size:24px;margin:0}
h2{font-size:18px;margin:30px 0 8px;padding-top:6px;border-top:1px solid var(--line)}
.sub{color:var(--muted);font-size:13px}.up{color:var(--up)}.down{color:var(--down)}
.card{background:var(--card);border-radius:10px;padding:14px 16px;margin:10px 0;overflow-x:auto}
table{width:100%;border-collapse:collapse;font-size:14px}th,td{padding:6px 8px;border-bottom:1px solid var(--line);text-align:right;white-space:nowrap}
th{color:var(--muted);font-weight:600}td:first-child,th:first-child{text-align:left}td.l{text-align:left;white-space:normal}
.help{background:#1d2026;border-left:3px solid var(--amber);padding:10px 14px;border-radius:6px;font-size:14px}
.base td{color:var(--muted)}
</style>
""");
        sb.Append($"<title>판단 성적표 {now.ToKst():yyyy-MM-dd}</title></head><body><main>");
        sb.Append($"<h1>판단 성적표</h1><div class='sub'>{now.ToKst():yyyy-MM-dd HH:mm} 기준 · 기록 {rows.Count}건 " +
                  (rows.Count > 0 ? $"({rows.Min(r => r.E.Date):yyyy-MM-dd} ~ {rows.Max(r => r.E.Date):yyyy-MM-dd})" : "") + "</div>");
        int done5 = rows.Count(r => r.R5 is not null);
        sb.Append("<div class='help' style='margin-top:12px'>매일 종목별로 그날의 마지막 판단(10:00~15:40)과 밤사이 미국 반도체를 기록하고, " +
                  "그날 종가에 샀다면 5·20거래일 뒤 수익률이 얼마였는지 붙입니다. 각 표의 첫 줄 '전체'가 기준선입니다. " +
                  $"<b>표본이 작을 땐(그룹당 20건 미만) 우연이 크니 방향만 보세요.</b> 지금 5일 결과가 나온 기록은 {done5}건입니다.</div>");

        foreach (var (title, note, groups) in Tables(rows))
        {
            sb.Append($"<h2>{E(title)}</h2><div class='sub'>{E(note)}</div><div class='card'><table>" +
                      "<tr><th>구분</th><th>기록</th><th>같은 날</th><th>5일 뒤 평균</th><th>전체 대비</th><th>5일 승률</th><th>20일 뒤 평균</th><th>전체 대비</th></tr>");
            var b = groups[0];
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                if (i > 0 && g.N == 0) continue;
                decimal? d5 = i > 0 && g.Avg5 is { } a5 && b.Avg5 is { } b5 ? a5 - b5 : null;
                decimal? d20 = i > 0 && g.Avg20 is { } a20 && b.Avg20 is { } b20 ? a20 - b20 : null;
                sb.Append($"<tr class='{(i == 0 ? "base" : "")}'><td>{E(g.Label)}</td><td>{g.N}{(g.N5 < g.N ? $" <span class='sub'>({g.N5})</span>" : "")}</td>" +
                          $"<td class='{C(g.SameDay)}'>{P(g.SameDay)}</td><td class='{C(g.Avg5)}'>{P(g.Avg5)}</td><td class='{C(d5)}'>{(d5 is null ? "" : P(d5).TrimEnd('%') + "%p")}</td>" +
                          $"<td>{(g.Win5 is { } w ? w + "%" : "-")}</td><td class='{C(g.Avg20)}'>{P(g.Avg20)}</td><td class='{C(d20)}'>{(d20 is null ? "" : P(d20).TrimEnd('%') + "%p")}</td></tr>");
            }
            sb.Append("</table></div>");
        }

        sb.Append("<h2>최근 기록</h2><div class='card'><table><tr><th>날짜</th><th>종목</th><th>등락</th><th>코스피</th><th>밤사이 미국</th><th>명분 판단</th><th>평소 판단</th><th>차례</th><th>5일 뒤</th><th>20일 뒤</th></tr>");
        foreach (var r in rows.OrderByDescending(r => r.E.Date).ThenBy(r => r.E.Symbol).Take(60))
        {
            var e = r.E;
            var us = e.Us.Count == 0 ? "-" : string.Join(" ", UsSemis.All.Where(x => e.Us.ContainsKey(x.Symbol)).Select(x => $"{x.Name} <span class='{C(e.Us[x.Symbol])}'>{P(e.Us[x.Symbol])}</span>"));
            var turn = (e.CoreReady ? "기본" : "") + (e.OppReady ? (e.CoreReady ? "·" : "") + "기회" : "") + (e.Bought ? " <b>✓산 날</b>" : "");
            sb.Append($"<tr><td>{e.Date:MM.dd (ddd)}</td><td>{E(e.Name)}</td><td class='{C(e.ChangePct)}'>{P(e.ChangePct)}</td><td class='{C(e.KospiPct)}'>{P(e.KospiPct)}</td>" +
                      $"<td class='l'>{us}</td><td>{E(e.Catalyst is null ? "-" : e.Verdict)}</td><td>{E(e.General ?? "-")}</td><td>{turn}</td>" +
                      $"<td class='{C(r.R5)}'>{(r.R5 is null ? "<span class='sub'>대기</span>" : P(r.R5))}</td><td class='{C(r.R20)}'>{(r.R20 is null ? "<span class='sub'>대기</span>" : P(r.R20))}</td></tr>");
        }
        sb.Append("</table></div><p class='sub'>수익률은 그날 종가에 샀다고 가정한 값입니다(실제 매수가와 다를 수 있음). 같은 CSV가 journal.csv 로 함께 저장됩니다.</p>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    public static string Csv(IReadOnlyList<Scored> rows)
    {
        var sb = new StringBuilder();
        var usCols = UsSemis.All.Select(x => x.Symbol).ToList();
        sb.AppendLine("date,symbol,name,at,price,change_pct,kospi_pct,foreign,institution,flow_confirmed,catalyst,verdict,general,core_ready,opp_ready,bought,us_date," +
                      string.Join(",", usCols.Select(c => "us_" + c)) + ",close,ret5,ret20");
        static string Q(string? s) => s is null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";
        static string N(decimal? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
        foreach (var r in rows)
        {
            var e = r.E;
            sb.AppendLine(string.Join(",", new[]
            {
                e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), e.Symbol, Q(e.Name), e.At, N(e.Price), N(e.ChangePct), N(e.KospiPct),
                e.Foreign?.ToString(CultureInfo.InvariantCulture) ?? "", e.Institution?.ToString(CultureInfo.InvariantCulture) ?? "", e.FlowConfirmed ? "1" : "0",
                Q(e.Catalyst), Q(e.Verdict), Q(e.General), e.CoreReady ? "1" : "0", e.OppReady ? "1" : "0", e.Bought ? "1" : "0",
                e.UsDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            }.Concat(usCols.Select(c => e.Us.TryGetValue(c, out var v) ? N(v) : "")).Concat(new[] { N(r.Close), N(r.R5), N(r.R20) })));
        }
        return sb.ToString();
    }
}
