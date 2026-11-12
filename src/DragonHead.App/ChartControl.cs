using System.Drawing.Drawing2D;
using System.Globalization;
using DragonHead.Core;

namespace DragonHead.App;

/// <summary>
/// 종목 창의 대화형 캔들 차트. 1분·5분·10분·일·주·월·년.
/// - 드래그로 좌우 이동, 휠로 확대·축소 (커서 위치 기준), 더블클릭으로 최신 화면
/// - 왼쪽 끝에 가까워지면 NeedOlder 이벤트 → 창이 더 옛날 봉을 받아 붙인다
/// - 마우스를 올리면 그 봉의 시가·고가·저가·종가, 점선·이동평균선·범례 위에선 초보자 설명
/// - 오른쪽 위 ? 는 '이 차트에서 중점으로 볼 것'
/// </summary>
internal sealed class ChartControl : Control
{
    private sealed record Guide(decimal Price, Color Color, string Label, string Help);

    private static readonly Color UpC = Theme.Red, DownC = Theme.Blue;
    private static readonly Color Ma20C = Theme.Amber, Ma60C = Color.FromArgb(79, 209, 197);
    private static readonly Color GridC = Color.FromArgb(42, 46, 54);
    private static readonly Color DayLineC = Color.FromArgb(70, 76, 88);
    private static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");

    // 데이터
    private ChartInterval _interval = ChartInterval.Day;
    private List<Bar> _bars = new();
    private decimal?[] _ma20 = Array.Empty<decimal?>();
    private decimal?[] _ma60 = Array.Empty<decimal?>();
    private string?[] _buyMark = Array.Empty<string?>();
    private string?[] _catMark = Array.Empty<string?>();
    private bool _moreAvailable;
    private bool _olderRequested;
    private decimal _prevClose;
    private decimal? _avgPrice;
    private HashSet<DateOnly> _buyDays = new();
    private List<(DateOnly day, string title)> _catalysts = new();
    private string? _empty = "불러오는 중…";

    // 화면 (봉 인덱스 기준): 오른쪽 끝에 오는 위치와 화면에 들어가는 봉 수
    private double _right;
    private double _count = 80;

    // 마지막으로 그린 배치 (마우스 계산용)
    private RectangleF _plot, _vol;
    private decimal _hi, _lo;
    private float _step;
    private readonly List<Guide> _guides = new();
    private readonly List<Rectangle> _labelBoxes = new();
    private readonly List<(RectangleF rect, string title, string help)> _legendHits = new();
    private RectangleF _helpButton;
    private Point? _mouse;
    private bool _showFocus;

    // 드래그
    private Point? _dragFrom;
    private double _dragRight;
    private bool _dragged;

    /// <summary>왼쪽 끝 근처까지 왔을 때 한 번 발생. 창이 더 받아서 SetSeries(keepView: true) 를 다시 부른다.</summary>
    public event Action? NeedOlder;

    /// <summary>위쪽 표시줄용: 지금 화면에 보이는 기간 설명</summary>
    public event Action<string>? ViewChanged;

    public bool LoadingOlder { get; set; }

    public ChartControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(29, 32, 38);
        ForeColor = Theme.Text;
        TabStop = true;
    }

    public ChartInterval Interval => _interval;

    public void SetMessage(string? message) { _empty = message; Invalidate(); }

    /// <summary>기준가·평단·매수일·명분일 (봉 단위가 바뀌어도 그대로)</summary>
    public void SetContext(decimal prevClose, decimal? avgPrice, IEnumerable<DateOnly> buyDays, IEnumerable<CatalystEvent> catalysts)
    {
        _prevClose = prevClose;
        _avgPrice = avgPrice is > 0 ? avgPrice : null;
        _buyDays = buyDays.ToHashSet();
        _catalysts = catalysts.Where(c => c.ParsedDate is not null)
            .GroupBy(c => c.ParsedDate!.Value).Select(g => (g.Key, g.First().Title)).OrderBy(x => x.Key).ToList();
        BuildMarks();
        Invalidate();
    }

    /// <summary>
    /// 봉 교체. keepView 면 같은 단위에서 앞쪽(과거)에 봉이 붙은 것으로 보고 보던 화면을 유지한다.
    /// 최신 봉을 보고 있었다면(오른쪽 끝) 새 봉이 생겨도 계속 최신을 따라간다.
    /// </summary>
    public void SetSeries(ChartInterval interval, IReadOnlyList<Bar> bars, bool keepView, bool moreAvailable)
    {
        var old = _bars;
        bool sameInterval = interval == _interval;
        bool followLatest = old.Count == 0 || _right >= old.Count - 1;
        _interval = interval;
        _bars = bars.ToList();
        var closes = _bars.Select(b => b.Close).ToList();
        _ma20 = ChartMath.Ma(closes, 20);
        _ma60 = ChartMath.Ma(closes, 60);
        _moreAvailable = moreAvailable;
        _olderRequested = false;
        _empty = null;
        BuildMarks();

        if (keepView && sameInterval && old.Count > 0)
        {
            int shift = _bars.FindIndex(b => b.Time == old[0].Time);
            if (shift < 0) shift = 0;
            var lastRightGap = _right - (old.Count - 1);
            _right = followLatest ? _bars.Count - 1 + Math.Max(0, lastRightGap) : _right + shift;
        }
        else
        {
            ResetView();
        }
        Clamp();
        Invalidate();
        RaiseView();
    }

    public void ResetView()
    {
        _count = DefaultCount();
        _right = _bars.Count - 1 + 2;   // 오른쪽에 빈칸 두 개
        Clamp();
        Invalidate();
        RaiseView();
    }

    private double DefaultCount()
    {
        var w = Math.Max(300, Width - 100);
        double c = _interval switch
        {
            ChartInterval.Tick => w / 8.0,
            ChartInterval.Min1 => w / 3.0,             // 1분봉: 대략 하루 (390봉)
            ChartInterval.Min5 => w / 6.0,
            ChartInterval.Min10 => w / 7.0,
            ChartInterval.Year => Math.Max(10, _bars.Count + 2),
            ChartInterval.Month => w / 9.0,
            _ => w / 7.0,
        };
        return Math.Clamp(c, 10, Math.Max(20, _bars.Count + 4));
    }

    private void Clamp()
    {
        double maxCount = Math.Max(20, _bars.Count + 6);
        _count = Math.Clamp(_count, 8, Math.Min(2000, maxCount));
        double lo = Math.Min(_bars.Count - 1, _count * 0.4);
        double hi = _bars.Count - 1 + Math.Max(2, _count * 0.3);
        _right = Math.Clamp(_right, Math.Max(0, lo), Math.Max(0, hi));
    }

    private int FirstVisible => Math.Max(0, (int)Math.Floor(_right - _count + 1));
    private int LastVisible => Math.Min(_bars.Count - 1, (int)Math.Ceiling(_right));

    private void RaiseView()
    {
        if (_bars.Count == 0) return;
        int a = FirstVisible, b = LastVisible;
        if (a > b) return;
        ViewChanged?.Invoke($"{TimeLabel(_bars[a].Time, shortForm: true)} ~ {TimeLabel(_bars[b].Time, shortForm: true)} · {b - a + 1}봉");

        if (_moreAvailable && !_olderRequested && a < Math.Max(10, _count * 0.5))
        {
            _olderRequested = true;
            NeedOlder?.Invoke();
        }
    }

    /// <summary>주·월·년봉은 그 기간 안에 매수일·명분일이 있으면 표시</summary>
    private void BuildMarks()
    {
        _buyMark = new string?[_bars.Count];
        _catMark = new string?[_bars.Count];
        if (_interval.IsIntraday() || _bars.Count == 0) return;
        for (int i = 0; i < _bars.Count; i++)
        {
            var from = DateOnly.FromDateTime(_bars[i].Time.DateTime);
            // 마지막 봉의 끝은 '그 봉 기간의 끝'과 '오늘' 중 이른 쪽 — 앞으로 올 명분(예: 다음 주 실적 발표)이 오늘 봉에 붙지 않게
            var bucketEnd = _interval switch
            {
                ChartInterval.Week => from.AddDays(7),
                ChartInterval.Month => from.AddMonths(1),
                ChartInterval.Year => from.AddYears(1),
                _ => from.AddDays(1),
            };
            var to = i + 1 < _bars.Count ? DateOnly.FromDateTime(_bars[i + 1].Time.DateTime) : bucketEnd;
            var today = Kst.Today.AddDays(1);
            if (to > today) to = today;
            int buys = _buyDays.Count(d => d >= from && d < to);
            if (buys > 0) _buyMark[i] = buys == 1 ? "▲ 이 기간 매수" : $"▲ 이 기간 매수 {buys}번";
            var cats = _catalysts.Where(c => c.day >= from && c.day < to).ToList();
            if (cats.Count > 0)
                _catMark[i] = _interval == ChartInterval.Day ? "◆ " + cats[0].title
                    : cats.Count == 1 ? $"◆ {cats[0].day:M/d} {cats[0].title}" : $"◆ 명분 {cats.Count}건 (첫: {cats[0].day:M/d} {cats[0].title})";
        }
    }

    // ───────────────────────── 그리기 ─────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        _guides.Clear();
        _labelBoxes.Clear();
        _legendHits.Clear();

        DrawLegend(g);

        if (_empty is not null || _bars.Count < 1)
        {
            var msg = _empty ?? (_interval == ChartInterval.Tick ? "체결을 모으는 중… (창을 열어 둔 동안 쌓입니다)"
                : _interval.IsIntraday() ? "분봉이 없습니다 (장 시작 전이거나 휴장일)." : "봉 데이터가 없습니다.");
            TextRenderer.DrawText(g, msg, Theme.Font(9.5f), ClientRectangle, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        DrawBars(g);
        DrawHover(g);
        if (_showFocus) DrawFocus(g);
    }

    private void DrawLegend(Graphics g)
    {
        var font = Theme.Font(8.5f);
        float x = 8, y = 6;
        void Item(string text, Color color, string? title, string help)
        {
            var size = TextRenderer.MeasureText(text + " ⓘ", font);
            TextRenderer.DrawText(g, text + " ⓘ", font, new Point((int)x, (int)y), color);
            _legendHits.Add((new RectangleF(x, y, size.Width, size.Height), title ?? "", help));
            x += size.Width + 6;
        }

        Item("캔들", Theme.Text, null, CandleHelp());
        Item("━ " + _interval.MaName(20), Ma20C, null, MaHelp(20));
        Item("━ " + _interval.MaName(60), Ma60C, null, MaHelp(60));
        Item("┅ 기준가", Theme.Muted, null, ChartHelp.BaseLine);
        if (_avgPrice is not null) Item("┅ 평단", Theme.Green, null, ChartHelp.AvgPrice);
        if (_interval.IsIntraday())
        {
            Item("┅ 당일 고가", Ma20C, null, ChartHelp.DayHigh);
            Item("▒ 13시 이후", Theme.Muted, null, ChartHelp.Afternoon);
        }
        else
        {
            if (_buyDays.Count > 0) Item("▲ 매수", Theme.Green, null, ChartHelp.BuyMark);
            if (_catalysts.Count > 0) Item("◆ 명분", Theme.Blue, null, ChartHelp.CatalystMark);
        }

        var r = new RectangleF(Width - 30, 4, 22, 22);
        _helpButton = r;
        using (var b = new SolidBrush(_showFocus ? Theme.Amber : Color.FromArgb(55, 60, 70)))
            g.FillEllipse(b, r);
        TextRenderer.DrawText(g, "?", Theme.Font(10f, FontStyle.Bold), Rectangle.Round(r), _showFocus ? Theme.Background : Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private string CandleHelp()
    {
        var unit = _interval switch
        {
            ChartInterval.Tick => $"체결 {ChartIntervals.TicksPerBar}건",
            ChartInterval.Min1 => "1분", ChartInterval.Min5 => "5분", ChartInterval.Min10 => "10분",
            ChartInterval.Day => "하루", ChartInterval.Week => "한 주", ChartInterval.Month => "한 달", _ => "1년",
        };
        return ChartHelp.Candle.Replace("캔들(봉) 하나가 하루입니다.", $"캔들(봉) 하나가 {unit}입니다.")
               + " 드래그하면 옆으로 이동, 휠로 확대·축소, 더블클릭하면 최신 화면으로 돌아옵니다.";
    }

    private string MaHelp(int n) => _interval switch
    {
        ChartInterval.Day => n == 20 ? ChartHelp.Ma20 : ChartHelp.Ma60,
        ChartInterval.Week or ChartInterval.Month or ChartInterval.Year =>
            $"{_interval.MaName(n)}: 최근 {n}개 봉 종가의 평균. 일봉보다 긴 흐름을 봅니다. 주가가 이 선 위에서 선이 위로 향하면 긴 상승 흐름이에요.",
        _ => $"{_interval.MaName(n)}: 최근 {n}개 분봉 종가의 평균. 장중 흐름을 매끄럽게 본 선이에요. 오후에 가격이 이 선 위를 지키면 매수세가 버티는 겁니다.",
    };

    private void Arrange(decimal hi, decimal lo)
    {
        const float L = 8, R = 96, T = 32, B = 24;
        float h = Height - T - B;
        float priceH = h * 0.78f;
        _plot = new RectangleF(L, T, Width - L - R, priceH);
        _vol = new RectangleF(L, T + priceH + 10, Width - L - R, h - priceH - 10);
        var pad = Math.Max(1m, (hi - lo) * 0.06m);
        _hi = hi + pad;
        _lo = lo - pad;
    }

    private float Y(decimal p) => _plot.Top + (float)((_hi - p) / (_hi - _lo)) * _plot.Height;
    private decimal PriceAt(float y) => _hi - (decimal)((y - _plot.Top) / _plot.Height) * (_hi - _lo);
    private float X(int i) => _plot.Right - (float)(_right - i + 0.5) * _step;
    private int IndexAt(float x) => (int)Math.Round(_right - (_plot.Right - x) / _step + 0.5);

    private void DrawGrid(Graphics g)
    {
        using var grid = new Pen(GridC);
        var step = ChartMath.NiceStep(_hi - _lo);
        for (var p = Math.Ceiling(_lo / step) * step; p <= _hi; p += step)
        {
            var y = Y(p);
            g.DrawLine(grid, _plot.Left, y, _plot.Right, y);
            TextRenderer.DrawText(g, p.ToString("N0", Ko), Theme.Font(9f), new Point((int)_plot.Right + 6, (int)y - 8), Theme.Muted);
        }
    }

    private void AddGuide(Graphics g, decimal price, Color color, string label, string help)
    {
        if (price <= 0 || price > _hi || price < _lo) return;
        var y = Y(price);
        using (var pen = new Pen(color, 1.2f) { DashStyle = DashStyle.Dash })
            g.DrawLine(pen, _plot.Left, y, _plot.Right, y);
        var font = Theme.Font(8.5f, FontStyle.Bold);
        var size = TextRenderer.MeasureText(label, font);
        var box = new Rectangle((int)_plot.Right + 2, (int)y - size.Height / 2 - 1, Math.Max(size.Width + 6, 90), size.Height + 2);
        // 가격표끼리 겹치면 (기준가와 당일 고가가 붙어 있을 때) 아래로, 바닥이면 위로 비켜 놓는다
        for (int guard = 0; guard < 6 && _labelBoxes.Any(r => r.IntersectsWith(box)); guard++)
        {
            var hit = _labelBoxes.First(r => r.IntersectsWith(box));
            box.Y = hit.Bottom + 2 + box.Height <= _plot.Bottom + 4 ? hit.Bottom + 2 : hit.Top - box.Height - 2;
        }
        _labelBoxes.Add(box);
        if (Math.Abs(box.Y + box.Height / 2 - y) > 2)
            using (var lp = new Pen(color)) g.DrawLine(lp, _plot.Right, y, box.Left, box.Y + box.Height / 2);
        using (var b = new SolidBrush(color)) g.FillRectangle(b, box);
        var ink = color == UpC || color == DownC ? Color.White : Color.FromArgb(17, 17, 17);
        TextRenderer.DrawText(g, label, font, box, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        _guides.Add(new Guide(price, color, label, help));
    }

    private void DrawBars(Graphics g)
    {
        Clamp();
        int a = FirstVisible, z = LastVisible;
        if (a > z) { a = Math.Max(0, _bars.Count - 2); z = _bars.Count - 1; }

        decimal hi = decimal.MinValue, lo = decimal.MaxValue;
        for (int i = a; i <= z; i++)
        {
            hi = Math.Max(hi, _bars[i].High); lo = Math.Min(lo, _bars[i].Low);
            if (_ma20[i] is { } m2) { hi = Math.Max(hi, m2); lo = Math.Min(lo, m2); }
            if (_ma60[i] is { } m6) { hi = Math.Max(hi, m6); lo = Math.Min(lo, m6); }
        }
        // 기준가·평단은 가까이 있을 때만 범위에 넣는다 (멀리 있는 선 때문에 캔들이 납작해지지 않게)
        var span = hi - lo;
        foreach (var v in new[] { _prevClose, _avgPrice ?? 0 })
            if (v > 0 && v <= hi + span * 0.25m && v >= lo - span * 0.25m) { hi = Math.Max(hi, v); lo = Math.Min(lo, v); }
        Arrange(hi, lo);
        _step = (float)(_plot.Width / _count);

        var state = g.Save();
        g.SetClip(new RectangleF(_plot.Left, _plot.Top, _plot.Width, _vol.Bottom - _plot.Top + 20));

        // 13시 이후 음영 (분봉)
        if (_interval.IsIntraday())
        {
            using var shade = new SolidBrush(Color.FromArgb(14, 255, 255, 255));
            for (int i = a; i <= z; i++)
                if (_bars[i].Time.Hour >= 13)
                    g.FillRectangle(shade, X(i) - _step / 2, _plot.Top, _step + 0.6f, _plot.Height);
        }

        g.Restore(state);
        DrawGrid(g);
        state = g.Save();
        g.SetClip(new RectangleF(_plot.Left, 0, _plot.Width, Height));

        DrawTimeAxis(g, a, z);

        // 거래량
        decimal maxVol = 1;
        for (int i = a; i <= z; i++) maxVol = Math.Max(maxVol, _bars[i].Volume);
        float body = Math.Max(1f, _step * 0.62f);
        using (var upV = new SolidBrush(Color.FromArgb(110, UpC)))
        using (var dnV = new SolidBrush(Color.FromArgb(110, DownC)))
            for (int i = a; i <= z; i++)
            {
                var h = (float)(_bars[i].Volume / maxVol) * _vol.Height;
                g.FillRectangle(_bars[i].Close >= _bars[i].Open ? upV : dnV, X(i) - body / 2, _vol.Bottom - h, body, Math.Max(0.5f, h));
            }
        TextRenderer.DrawText(g, "거래량", Theme.Font(8f), new Point((int)_vol.Left + 2, (int)_vol.Top), Theme.Muted);

        // 캔들 (너무 촘촘하면 꼬리만)
        g.SmoothingMode = SmoothingMode.None;
        using (var upP = new Pen(UpC)) using (var dnP = new Pen(DownC))
        using (var upB = new SolidBrush(UpC)) using (var dnB = new SolidBrush(DownC))
            for (int i = a; i <= z; i++)
            {
                var b = _bars[i];
                bool up = b.Close >= b.Open;
                g.DrawLine(up ? upP : dnP, X(i), Y(b.High), X(i), Y(b.Low));
                if (_step >= 2.5f)
                {
                    float yo = Y(b.Open), yc = Y(b.Close);
                    g.FillRectangle(up ? upB : dnB, X(i) - body / 2, Math.Min(yo, yc), body, Math.Max(1, Math.Abs(yo - yc)));
                }
            }
        g.SmoothingMode = SmoothingMode.AntiAlias;

        void Ma(decimal?[] ma, Color color)
        {
            using var pen = new Pen(color, 1.6f);
            PointF? prev = null;
            for (int i = Math.Max(0, a - 1); i <= Math.Min(_bars.Count - 1, z + 1); i++)
            {
                if (ma[i] is not { } v) { prev = null; continue; }
                var pt = new PointF(X(i), Y(v));
                if (prev is { } p) g.DrawLine(pen, p, pt);
                prev = pt;
            }
        }
        Ma(_ma60, Ma60C);
        Ma(_ma20, Ma20C);

        // 매수 ▲ · 명분 ◆
        if (!_interval.IsIntraday() && _step >= 4)
        {
            var markFont = Theme.Font(_step >= 7 ? 9f : 7.5f, FontStyle.Bold);
            for (int i = a; i <= z; i++)
            {
                if (_buyMark[i] is not null)
                    TextRenderer.DrawText(g, "▲", markFont, new Point((int)X(i) - 6, (int)Y(_bars[i].Low) + 3), Theme.Green);
                if (_catMark[i] is not null)
                    TextRenderer.DrawText(g, "◆", markFont, new Point((int)X(i) - 6, (int)Y(_bars[i].High) - 17), Theme.Blue);
            }
        }

        if (LoadingOlder || (_moreAvailable && a == 0))
            TextRenderer.DrawText(g, LoadingOlder ? "‹ 이전 봉 불러오는 중…" : "‹ 더 끌면 이전 봉을 불러옵니다", Theme.Font(8f),
                new Point((int)_plot.Left + 4, (int)_plot.Top + 2), Theme.Muted);
        else if (!_moreAvailable && a == 0)
            TextRenderer.DrawText(g, "‹ 받을 수 있는 가장 오래된 봉", Theme.Font(8f), new Point((int)_plot.Left + 4, (int)_plot.Top + 2), Theme.Muted);

        g.Restore(state);

        // 현재가 (토스처럼 오른쪽에 색 가격표) — 먼저 넣어서 다른 가격표가 비켜 가게
        var lastClose = _bars[^1].Close;
        var nowColor = _prevClose > 0 && lastClose < _prevClose ? DownC : _prevClose > 0 && lastClose > _prevClose ? UpC : Color.FromArgb(200, 204, 212);
        AddGuide(g, lastClose, nowColor, "현재 " + lastClose.ToString("N0", Ko), "현재가: 지금 보고 있는 봉 중 가장 최근 봉의 종가예요. 빨강은 기준가보다 높음, 파랑은 낮음.");
        AddGuide(g, _prevClose, Color.FromArgb(154, 160, 172), "기준 " + _prevClose.ToString("N0", Ko), ChartHelp.BaseLine);
        if (_avgPrice is { } avg) AddGuide(g, avg, Theme.Green, "평단 " + avg.ToString("N0", Ko), ChartHelp.AvgPrice);
        if (_interval.IsIntraday())
        {
            var lastDay = DateOnly.FromDateTime(_bars[^1].Time.DateTime);
            var dayHigh = _bars.Where(b => DateOnly.FromDateTime(b.Time.DateTime) == lastDay).Max(b => b.High);
            AddGuide(g, dayHigh, Ma20C, "고가 " + dayHigh.ToString("N0", Ko), ChartHelp.DayHigh);
        }
    }

    /// <summary>아래쪽 시간 눈금. 단위마다 끊는 기준이 다르고, 글자가 겹치면 건너뛴다.</summary>
    private void DrawTimeAxis(Graphics g, int a, int z)
    {
        using var grid = new Pen(GridC);
        using var dayLine = new Pen(DayLineC);
        float lastLabel = float.MinValue;
        var font = Theme.Font(8f);
        for (int i = Math.Max(1, a); i <= z; i++)
        {
            var t = _bars[i].Time; var p = _bars[i - 1].Time;
            string? label = null;
            bool strong = false;
            switch (_interval)
            {
                case ChartInterval.Tick:
                    if (t.Minute != p.Minute || t.Hour != p.Hour) label = t.ToString("HH:mm");
                    break;
                case ChartInterval.Min1: case ChartInterval.Min5: case ChartInterval.Min10:
                    if (t.Date != p.Date) { label = t.ToString("M/d", Ko); strong = true; }
                    else if (t.Hour != p.Hour) label = $"{t.Hour}시";
                    break;
                case ChartInterval.Day:
                    if (t.Year != p.Year) { label = t.ToString("yyyy"); strong = true; }
                    else if (t.Month != p.Month) label = t.ToString("yy.M");
                    break;
                case ChartInterval.Week:
                    if (t.Year != p.Year) { label = t.ToString("yyyy"); strong = true; }
                    else if (t.Month != p.Month && (t.Month - 1) % 3 == 0) label = t.ToString("yy.M");
                    break;
                case ChartInterval.Month:
                    if (t.Year != p.Year) { label = t.ToString("yyyy"); strong = t.Year % 5 == 0; }
                    break;
                default:
                    label = t.ToString("yyyy");
                    break;
            }
            if (label is null) continue;
            var x = X(i) - _step / 2;
            var w = TextRenderer.MeasureText(label, font).Width;
            if (strong) g.DrawLine(dayLine, x, _plot.Top, x, _vol.Bottom);
            if (x - lastLabel < w + 8) continue;   // 글자 겹침
            if (!strong) g.DrawLine(grid, x, _plot.Top, x, _vol.Bottom);
            TextRenderer.DrawText(g, label, strong ? Theme.Font(8f, FontStyle.Bold) : font, new Point((int)x + 3, (int)_vol.Bottom + 4),
                strong ? Theme.Text : Theme.Muted);
            lastLabel = x;
        }
    }

    private string TimeLabel(DateTimeOffset t, bool shortForm = false) => _interval switch
    {
        ChartInterval.Tick => t.ToString("HH:mm:ss", Ko),
        ChartInterval.Min1 or ChartInterval.Min5 or ChartInterval.Min10 => t.ToString(shortForm ? "M/d HH:mm" : "M/d (ddd) HH:mm", Ko),
        ChartInterval.Day => t.ToString(shortForm ? "yy.M.d" : "yyyy-MM-dd (ddd)", Ko),
        ChartInterval.Week => shortForm ? t.ToString("yy.M.d", Ko) + " 주" : t.ToString("yyyy-MM-dd", Ko) + " 시작 주",
        ChartInterval.Month => t.ToString(shortForm ? "yy.M" : "yyyy년 M월", Ko),
        _ => t.ToString("yyyy") + "년",
    };

    // ───────────────────────── 마우스 · 키보드 ─────────────────────────

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (!Focused) Focus();   // 휠을 받으려면 포커스가 있어야 한다
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && !_helpButton.Contains(e.Location))
        {
            _dragFrom = e.Location;
            _dragRight = _right;
            _dragged = false;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragFrom is not null && _dragged) RaiseView();
        _dragFrom = null;
        Cursor = Cursors.Cross;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.Location;
        if (_dragFrom is { } from && _step > 0)
        {
            var dx = e.X - from.X;
            if (Math.Abs(dx) > 3) _dragged = true;
            if (_dragged)
            {
                _right = _dragRight - dx / _step;
                Clamp();
                Cursor = Cursors.SizeWE;
                if (FirstVisible < Math.Max(10, _count * 0.5)) RaiseView();
            }
        }
        else
        {
            Cursor = _helpButton.Contains(e.Location) ? Cursors.Hand : Cursors.Cross;
        }
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_dragged) return;
        _showFocus = _helpButton.Contains(e.Location) ? !_showFocus : false;
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (_helpButton.Contains(e.Location)) return;
        ResetView();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Zoom(e.Delta > 0 ? 1 / 1.25 : 1.25, e.X);
    }

    /// <summary>factor &lt; 1 이면 확대. 커서 아래 봉이 제자리에 남도록 오른쪽 끝을 옮긴다.</summary>
    private void Zoom(double factor, float atX)
    {
        if (_bars.Count == 0 || _plot.Width <= 0) return;
        atX = Math.Clamp(atX, _plot.Left, _plot.Right);
        double fromRight = (_plot.Right - atX) / _plot.Width;      // 0 = 오른쪽 끝
        double anchor = _right - fromRight * _count;
        _count *= factor;
        Clamp();
        _right = anchor + fromRight * _count;
        Clamp();
        Invalidate();
        RaiseView();
    }

    private void Pan(double bars)
    {
        _right += bars;
        Clamp();
        Invalidate();
        RaiseView();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left: Pan(-Math.Max(1, _count * 0.15)); break;
            case Keys.Right: Pan(Math.Max(1, _count * 0.15)); break;
            case Keys.Up: case Keys.Add: case Keys.Oemplus: Zoom(1 / 1.25, _plot.Right); break;
            case Keys.Down: case Keys.Subtract: case Keys.OemMinus: Zoom(1.25, _plot.Right); break;
            case Keys.Home: case Keys.End: ResetView(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void DrawHover(Graphics g)
    {
        if (_mouse is not { } m || _showFocus || _dragged && _dragFrom is not null) return;

        foreach (var (rect, title, help) in _legendHits)
            if (rect.Contains(m)) { DrawTip(g, m, title.Length > 0 ? title : null, help); return; }

        if (!_plot.Contains(m) && !_vol.Contains(m)) return;

        foreach (var gd in _guides)
            if (Math.Abs(Y(gd.Price) - m.Y) <= 5) { DrawTip(g, m, gd.Label, gd.Help); return; }

        using (var cross = new Pen(Color.FromArgb(90, 255, 255, 255)) { DashStyle = DashStyle.Dot })
        {
            g.DrawLine(cross, m.X, _plot.Top, m.X, _vol.Bottom);
            if (_plot.Contains(m)) g.DrawLine(cross, _plot.Left, m.Y, _plot.Right, m.Y);
        }
        if (_plot.Contains(m))
        {
            var label = PriceAt(m.Y).ToString("N0", Ko);
            var size = TextRenderer.MeasureText(label, Theme.Font(9f, FontStyle.Bold));
            var box = new Rectangle((int)_plot.Right + 2, m.Y - size.Height / 2, size.Width + 6, size.Height);
            using (var b = new SolidBrush(Color.FromArgb(70, 75, 85))) g.FillRectangle(b, box);
            TextRenderer.DrawText(g, label, Theme.Font(9f, FontStyle.Bold), box, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        int idx = IndexAt(m.X);
        if (idx < 0 || idx >= _bars.Count) return;
        var bar = _bars[idx];
        var prev = idx > 0 ? _bars[idx - 1].Close : bar.Open;

        foreach (var (ma, n) in new[] { (_ma20, 20), (_ma60, 60) })
            if (ma[idx] is { } v && Math.Abs(Y(v) - m.Y) <= 5) { DrawTip(g, m, $"{_interval.MaName(n)} {v:N0}", MaHelp(n)); return; }

        var lines = new List<string>
        {
            TimeLabel(bar.Time),
            $"시가 {bar.Open:N0}   고가 {bar.High:N0}",
            $"저가 {bar.Low:N0}   종가 {bar.Close:N0}  ({SignalEngine.Signed(ChartMath.Pct(bar.Close, prev) ?? 0)}%)",
            $"거래량 {bar.Volume:N0}",
        };
        if (_ma20[idx] is { } a20) lines.Add($"{_interval.MaName(20)} {a20:N0} · {_interval.MaName(60)} {(_ma60[idx] is { } c ? c.ToString("N0") : "-")}");
        if (_interval.IsIntraday())
        {
            if (_prevClose > 0 && DateOnly.FromDateTime(bar.Time.DateTime) == DateOnly.FromDateTime(_bars[^1].Time.DateTime))
                lines.Add($"기준가 대비 {SignalEngine.Signed(ChartMath.Pct(bar.Close, _prevClose) ?? 0)}%");
            if (bar.Time.Hour >= 13) lines.Add("13시 이후 (최종 판단은 14:50~)");
            else if (bar.Time.Hour < 10) lines.Add("9~10시 · 판단 안 함 (아침 흔들림)");
        }
        else
        {
            if (_buyMark[idx] is { } bm) lines.Add(_interval == ChartInterval.Day ? "▲ 이날 매수" : bm);
            if (_catMark[idx] is { } cm) lines.Add(cm);
        }
        DrawInfo(g, new Point((int)X(idx), m.Y), lines);
    }

    private void DrawInfo(Graphics g, Point at, List<string> lines)
    {
        var font = Theme.Font(9.5f);
        int w = lines.Max(l => TextRenderer.MeasureText(l, font).Width) + 16;
        int h = lines.Count * (font.Height + 2) + 10;
        var x = at.X + 14 + w > _plot.Right ? at.X - w - 14 : at.X + 14;
        var y = Math.Clamp(at.Y - h / 2, (int)_plot.Top, Math.Max((int)_plot.Top, Height - h - 4));
        var box = new Rectangle(Math.Max(2, x), y, w, h);
        using (var b = new SolidBrush(Color.FromArgb(235, 40, 44, 52))) g.FillRectangle(b, box);
        using (var p = new Pen(Color.FromArgb(70, 255, 255, 255))) g.DrawRectangle(p, box);
        for (int i = 0; i < lines.Count; i++)
            TextRenderer.DrawText(g, lines[i], i == 0 ? Theme.Font(10f, FontStyle.Bold) : font,
                new Point(box.X + 8, y + 5 + i * (font.Height + 2)), Theme.Text);
    }

    private void DrawTip(Graphics g, Point at, string? title, string help)
    {
        const int W = 290;
        var font = Theme.Font(8.5f);
        var textSize = TextRenderer.MeasureText(help, font, new Size(W - 20, int.MaxValue), TextFormatFlags.WordBreak);
        int titleH = title is null ? 0 : Theme.Font(9f, FontStyle.Bold).Height + 4;
        int h = textSize.Height + titleH + 16;
        var x = Math.Min(at.X + 14, Width - W - 6);
        var y = at.Y + 18 + h > Height ? at.Y - h - 10 : at.Y + 18;
        var box = new Rectangle(Math.Max(4, x), Math.Max(4, y), W, h);
        using (var b = new SolidBrush(Color.FromArgb(245, 36, 39, 46))) g.FillRectangle(b, box);
        using (var p = new Pen(Theme.Amber)) g.DrawRectangle(p, box);
        if (title is not null)
            TextRenderer.DrawText(g, title, Theme.Font(9f, FontStyle.Bold), new Point(box.X + 10, box.Y + 8), Theme.Amber);
        TextRenderer.DrawText(g, help, font, new Rectangle(box.X + 10, box.Y + 8 + titleH, W - 20, textSize.Height + 4), Theme.Text, TextFormatFlags.WordBreak);
    }

    private void DrawFocus(Graphics g)
    {
        var (title, items) = _interval switch
        {
            ChartInterval.Day => ("일봉에서 중점으로 볼 것", ChartHelp.DailyFocus),
            ChartInterval.Tick => ("틱 차트에서 볼 것", ChartHelp.TickFocus),
            ChartInterval.Min1 or ChartInterval.Min5 or ChartInterval.Min10 => ($"{_interval.Label()}봉에서 볼 것", ChartHelp.MinuteFocus),
            _ => ($"{_interval.Label()}봉에서 볼 것", ChartHelp.LongFocus),
        };
        var font = Theme.Font(9f);
        int w = Math.Min(Width - 40, 420);
        var heights = items.Select(i => TextRenderer.MeasureText(i, font, new Size(w - 28, int.MaxValue), TextFormatFlags.WordBreak).Height).ToList();
        int h = heights.Sum() + items.Length * 6 + 70;
        var box = new Rectangle(Width - w - 12, 32, w, h);
        using (var b = new SolidBrush(Color.FromArgb(248, 34, 37, 44))) g.FillRectangle(b, box);
        using (var p = new Pen(Theme.Amber, 1.5f)) g.DrawRectangle(p, box);
        TextRenderer.DrawText(g, title, Theme.Font(10f, FontStyle.Bold), new Point(box.X + 14, box.Y + 10), Theme.Amber);
        int y = box.Y + 36;
        for (int i = 0; i < items.Length; i++)
        {
            TextRenderer.DrawText(g, items[i], font, new Rectangle(box.X + 14, y, w - 28, heights[i] + 2), Theme.Text, TextFormatFlags.WordBreak);
            y += heights[i] + 6;
        }
        TextRenderer.DrawText(g, "드래그 이동 · 휠 확대/축소 · 더블클릭 최신으로 · ←→ ↑↓ 키도 됩니다", Theme.Font(8f),
            new Point(box.X + 14, box.Bottom - 38), Theme.Muted);
        TextRenderer.DrawText(g, "선·점선·범례에 마우스를 올리면 설명 · 아무 데나 누르면 닫힘", Theme.Font(8f),
            new Point(box.X + 14, box.Bottom - 20), Theme.Muted);
    }
}
