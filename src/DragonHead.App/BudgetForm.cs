using DragonHead.Core;

namespace DragonHead.App;

/// <summary>
/// 트레이 메뉴 '예산 · 분할 설정'. 종목별 예산과, 예산을 기본 몫(정기 분할)·기회 몫(명분일 눌림)으로 나누는 비율을 고른다.
/// 프리셋(수비·균형·공격) 표를 같이 보여 주고, 아래 '직접 설정'에서 비율·간격·회차를 세부 조절한다.
/// 저장은 호출한 쪽(TrayContext)이 settings.json 에 쓴다.
/// </summary>
internal sealed class BudgetForm : Form
{
    private readonly AppSettings _settings;
    private readonly List<(WatchItem item, NumericUpDown box)> _budgets = new();
    private readonly List<Panel> _presetCards = new();
    private readonly TrackBar _ratio;
    private readonly Label _ratioText, _preview;
    private readonly RadioButton[] _interval;
    private readonly NumericUpDown _coreN, _oppN;
    private bool _syncing;

    private static readonly (int days, string label)[] Intervals = { (5, "1주"), (10, "2주"), (20, "한 달") };

    public BudgetForm(AppSettings settings)
    {
        _settings = settings;
        var cfg = settings.Strategy;

        Text = "용머리 알리미 · 예산 · 분할 설정";
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Font(9.5f);
        TopMost = true;
        const int W = 640;

        int y = 16;
        Label Text_(string text, float size = 9.5f, FontStyle style = FontStyle.Regular, Color? color = null)
        {
            var l = new Label
            {
                Text = text, AutoSize = true, MaximumSize = new Size(W - 40, 0), Font = Theme.Font(size, style),
                ForeColor = color ?? Theme.Text, Location = new Point(20, y),
            };
            Controls.Add(l);
            y = l.Top + l.PreferredHeight + 6;
            return l;
        }
        NumericUpDown Num(decimal value, decimal max, decimal inc, int x, int width)
        {
            var n = new NumericUpDown
            {
                Minimum = 0, Maximum = max, Increment = inc, ThousandsSeparator = true, Value = Math.Clamp(value, 0, max),
                Location = new Point(x, y), Width = width, BackColor = Theme.Surface, ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle, Font = Theme.Font(10f), TextAlign = HorizontalAlignment.Right,
            };
            Controls.Add(n);
            return n;
        }

        // ── 1. 종목별 예산
        Text_("1. 종목별 예산", 12f, FontStyle.Bold);
        Text_("이 종목에 넣을 총 금액입니다. 0이면 그 종목은 매수 안내를 하지 않습니다.", 8.5f, color: Theme.Muted);
        foreach (var w in settings.Watch)
        {
            var name = new Label { Text = w.Name, AutoSize = false, Width = 140, Location = new Point(20, y + 4), Font = Theme.Font(10f, FontStyle.Bold) };
            Controls.Add(name);
            var box = Num(w.Budget ?? cfg.TotalBudget, 10_000_000_000m, 100_000m, 170, 180);
            var won = new Label { Text = "원", AutoSize = true, Location = new Point(356, y + 5), ForeColor = Theme.Muted };
            Controls.Add(won);
            box.ValueChanged += (_, _) => UpdatePreview();
            _budgets.Add((w, box));
            y = box.Bottom + 8;
        }
        y += 8;

        // ── 2. 성향 (프리셋 표)
        Text_("2. 기본 몫 / 기회 몫 비율", 12f, FontStyle.Bold);
        Text_("기본 몫은 명분 없는 날 '평소 판단'(차트·수급·시장)이 괜찮다고 할 때 나눠 사고, 기회 몫은 명분일 눌림(백테스트에서 유리했던 자리)에서만 삽니다. " +
              "기본 몫이 클수록 시장에 빨리 들어가니 공격적, 기회 몫이 클수록 현금을 오래 들고 기다리니 수비적입니다.", 8.5f, color: Theme.Muted);

        // 표 머리
        var cols = new[] { ("프리셋", 70), ("기본 / 기회", 80), ("오를 때", 105), ("빠질 때", 125), ("맞는 사람", 210) };
        int x0 = 20;
        foreach (var (h, w) in cols)
        {
            Controls.Add(new Label { Text = h, AutoSize = false, Width = w, Location = new Point(x0 + 10, y), ForeColor = Theme.Muted, Font = Theme.Font(8.5f, FontStyle.Bold) });
            x0 += w;
        }
        y += 22;
        foreach (var p in BudgetPresets.All)
        {
            var card = new Panel { Location = new Point(20, y), Size = new Size(W - 40, 34), BackColor = Theme.Surface, Cursor = Cursors.Hand, Tag = p };
            int x = 10;
            var vals = new[] { p.Name, $"{p.CoreRatio * 100:0} / {(1 - p.CoreRatio) * 100:0}", p.Upside, p.Downside, p.Fits };
            for (int i = 0; i < vals.Length; i++)
            {
                var l = new Label
                {
                    Text = vals[i], AutoSize = false, Width = cols[i].Item2, Height = 34, Location = new Point(x, 0),
                    TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Font(i == 0 ? 10f : 9f, i == 0 ? FontStyle.Bold : FontStyle.Regular),
                    ForeColor = Theme.Text, Cursor = Cursors.Hand,
                };
                l.Click += (_, _) => PickPreset(p);
                card.Controls.Add(l);
                x += cols[i].Item2;
            }
            card.Click += (_, _) => PickPreset(p);
            Controls.Add(card);
            _presetCards.Add(card);
            y = card.Bottom + 4;
        }
        y += 4;
        Text_("고르기 어렵다면: " + BudgetPresets.Question, 9f, color: Theme.Amber);
        Text_("처음엔 균형형으로 시작해서 몇 달 써 보고 옮기는 걸 추천합니다.", 8.5f, color: Theme.Muted);
        y += 6;

        // ── 3. 직접 설정
        Text_("3. 직접 설정", 12f, FontStyle.Bold);
        Controls.Add(new Label { Text = "기본 몫 비율", AutoSize = true, Location = new Point(20, y + 6) });
        _ratio = new TrackBar
        {
            Minimum = 0, Maximum = 100, TickFrequency = 25, SmallChange = 5, LargeChange = 25,
            Value = (int)Math.Round(Math.Clamp(cfg.CoreRatio, 0, 1) * 100), Location = new Point(120, y), Width = 300, BackColor = Theme.Background,
        };
        Controls.Add(_ratio);
        _ratioText = new Label { AutoSize = true, Location = new Point(430, y + 6), Font = Theme.Font(10f, FontStyle.Bold) };
        Controls.Add(_ratioText);
        _ratio.ValueChanged += (_, _) =>
        {
            if (_ratio.Value % 5 != 0) { _ratio.Value = (int)Math.Round(_ratio.Value / 5.0) * 5; return; }
            UpdatePreview();
        };
        y = _ratio.Bottom + 6;

        Controls.Add(new Label { Text = "기본 몫 최소 간격", AutoSize = true, Location = new Point(20, y + 2) });
        _interval = new RadioButton[Intervals.Length];
        for (int i = 0; i < Intervals.Length; i++)
        {
            var rb = new RadioButton
            {
                Text = Intervals[i].label, AutoSize = true, Location = new Point(120 + i * 80, y), ForeColor = Theme.Text,
                Checked = cfg.CoreIntervalDays == Intervals[i].days,
            };
            rb.CheckedChanged += (_, _) => UpdatePreview();
            _interval[i] = rb;
            Controls.Add(rb);
        }
        if (_interval.All(r => !r.Checked)) _interval[1].Checked = true;
        y += 32;

        Controls.Add(new Label { Text = "회차 수", AutoSize = true, Location = new Point(20, y + 4) });
        Controls.Add(new Label { Text = "기본 몫", AutoSize = true, Location = new Point(120, y + 4), ForeColor = Theme.Muted });
        _coreN = Num(cfg.CoreTranches, 10, 1, 180, 60);
        _coreN.Minimum = 1;
        Controls.Add(new Label { Text = "기회 몫", AutoSize = true, Location = new Point(270, y + 4), ForeColor = Theme.Muted });
        _oppN = Num(cfg.OppTranches, 10, 1, 330, 60);
        _oppN.Minimum = 1;
        _coreN.ValueChanged += (_, _) => UpdatePreview();
        _oppN.ValueChanged += (_, _) => UpdatePreview();
        y = _coreN.Bottom + 12;

        // ── 미리보기
        _preview = new Label
        {
            AutoSize = false, Location = new Point(20, y), Size = new Size(W - 40, 40 + 20 * Math.Max(1, settings.Watch.Count)),
            BackColor = Theme.Surface, Padding = new Padding(10, 8, 10, 8), Font = Theme.Font(9f),
        };
        Controls.Add(_preview);
        y = _preview.Bottom + 14;

        var save = new Button
        {
            Text = "저장", Location = new Point(W - 140, y), Size = new Size(120, 34), FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Amber, ForeColor = Theme.Background, Font = Theme.Font(10f, FontStyle.Bold),
        };
        save.Click += (_, _) => Save();
        var cancel = new Button
        {
            Text = "취소", Location = new Point(W - 270, y), Size = new Size(120, 34), FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Surface, ForeColor = Theme.Muted, DialogResult = DialogResult.Cancel,
        };
        Controls.AddRange(new Control[] { cancel, save });
        AcceptButton = save;
        CancelButton = cancel;
        ClientSize = new Size(W, save.Bottom + 18);

        UpdatePreview();
    }

    private int IntervalDays => Intervals[Array.FindIndex(_interval, r => r.Checked) is var i and >= 0 ? i : 1].days;

    private void PickPreset(BudgetPresets.Preset p)
    {
        _ratio.Value = (int)Math.Round(p.CoreRatio * 100);
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_syncing) return;
        _syncing = true;
        var ratio = _ratio.Value / 100m;
        _ratioText.Text = $"기본 {_ratio.Value}% · 기회 {100 - _ratio.Value}%  ({BudgetPresets.NameOf(ratio)})";
        foreach (var card in _presetCards)
        {
            bool on = card.Tag is BudgetPresets.Preset p && p.CoreRatio == ratio;
            card.BackColor = on ? Color.FromArgb(58, 52, 30) : Theme.Surface;
            foreach (Control c in card.Controls) c.ForeColor = on ? Theme.Amber : Theme.Text;
        }

        var interval = Intervals.First(i => i.days == IntervalDays).label;
        var lines = _budgets.Select(b =>
        {
            var total = b.box.Value;
            if (total <= 0) return $"{b.item.Name}: 예산 0 → 매수 안내 안 함";
            var core = Math.Floor(total * ratio);
            var opp = total - core;
            string Part(decimal amt, decimal n) => amt <= 0 ? "없음" : $"{Man(amt)} ({n}회 × {Man(Math.Floor(amt / n))})";
            return $"{b.item.Name} {Man(total)} → 기본 {Part(core, _coreN.Value)} 최소 {interval} 간격 · 평소 판단 괜찮을 때 · 기회 {Part(opp, _oppN.Value)} 명분일 눌림";
        });
        _preview.Text = string.Join("\n", lines) + "\n실제 회당 주식 수·회차는 그날 주가로 카드에서 계산합니다 (비싼 종목은 회차가 줄어듦).";
        _syncing = false;
    }

    /// <summary>1,000,000 → 100만, 2,500,000 → 250만</summary>
    private static string Man(decimal won) =>
        won >= 10_000 ? (won / 10_000m).ToString("#,0.#") + "만" : won.ToString("N0") + "원";

    private void Save()
    {
        var cfg = _settings.Strategy;
        cfg.CoreRatio = _ratio.Value / 100m;
        cfg.CoreIntervalDays = IntervalDays;
        cfg.CoreTranches = (int)_coreN.Value;
        cfg.OppTranches = (int)_oppN.Value;
        foreach (var (item, box) in _budgets)
            item.Budget = box.Value;
        DialogResult = DialogResult.OK;
        Close();
    }
}
