using System.Globalization;
using DragonHead.Core;

namespace DragonHead.App;

/// <summary>
/// 미니 시장 창: 종목 검색(이름·코드·초성) + 순위(거래대금·거래량·급상승·급하락·토스 인기).
/// ☆ 로 감시 종목 추가, ★ 로 해제. 줄을 누르면 그 종목 공시 창.
/// </summary>
internal sealed class MarketForm : PopupForm
{
    private enum MarketFilter { All, Kospi, Kosdaq, Nasdaq, Nyse, Amex }
    private enum TypeFilter { Stock, Etf, All }

    private static readonly (string type, string label)[] Tabs =
    {
        ("MARKET_TRADING_AMOUNT", "거래대금"),
        ("MARKET_TRADING_VOLUME", "거래량"),
        ("TOP_GAINERS", "급상승"),
        ("TOP_LOSERS", "급하락"),
        ("TOSS_SECURITIES_TRADING_AMOUNT", "토스 인기"),
    };

    private static readonly (string duration, string label)[] Durations =
    {
        ("realtime", "실시간"), ("1d", "오늘"), ("1w", "1주"), ("1mo", "1개월"),
    };

    private readonly TossClient _toss;
    private readonly StockDirectory _krDir;
    private readonly StockDirectory _usDir;
    private readonly Func<Task> _ensureUsDir;
    private bool _us;
    private StockDirectory _dir => _us ? _usDir : _krDir;
    private readonly Func<string, bool> _isWatched;
    private readonly Action<StockInfo, bool> _setWatched;
    private readonly Action<string, string> _openStock;

    private readonly TextBox _search;
    private readonly Label _placeholder;
    private readonly Panel _filters;
    private readonly FlowLayoutPanel _list;
    private readonly Label _status;
    private readonly System.Windows.Forms.Timer _refresh = new();
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 150 };

    private string _tab = Tabs[0].type;
    private string _duration = "realtime";
    private MarketFilter _market = MarketFilter.All;
    private TypeFilter _type = TypeFilter.Stock;

    private List<RankItem> _ranks = new();
    private DateTimeOffset? _rankedAt;
    private string? _note;
    private bool _loading;
    private readonly Dictionary<string, decimal> _searchPrices = new();

    private static readonly CultureInfo Ko = CultureInfo.GetCultureInfo("ko-KR");

    public MarketForm(TossClient toss, StockDirectory dir, StockDirectory usDir, Func<Task> ensureUsDir, Func<string, bool> isWatched, Action<StockInfo, bool> setWatched,
                      Action<string, string> openStock, Form? near)
    {
        _toss = toss;
        _krDir = dir;
        _usDir = usDir;
        _ensureUsDir = ensureUsDir;
        _isWatched = isWatched;
        _setWatched = setWatched;
        _openStock = openStock;

        Text = "시장";
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(1);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        ClientSize = new Size(460, 640);
        MinimumSize = new Size(380, 360);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = near?.TopMost ?? true;
        Opacity = near?.Opacity ?? 1.0;
        DoubleBuffered = true;
        KeyPreview = true;
        PlaceBeside(near);

        // ── 상단바
        var header = new Panel { Dock = DockStyle.Top, Height = 46 };
        var title = new Label { Text = "시장", AutoSize = true, Font = Theme.Font(13f, FontStyle.Bold), Location = new Point(14, 10) };
        var close = new Label
        {
            Text = "×", AutoSize = true, Font = Theme.Font(12f, FontStyle.Bold), ForeColor = Theme.Muted,
            Cursor = Cursors.Hand, Padding = new Padding(6, 0, 6, 2),
        };
        close.Click += (_, _) => Close();
        close.MouseEnter += (_, _) => close.ForeColor = Theme.Red;
        close.MouseLeave += (_, _) => close.ForeColor = Theme.Muted;
        header.Controls.AddRange(new Control[] { title, close });
        header.Layout += (_, _) => close.Location = new Point(header.Width - close.PreferredWidth - 6, 6);
        foreach (Control c in new Control[] { header, title })
            c.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) Drag(); };

        // ── 검색창
        var searchBox = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(12, 4, 12, 6) };
        var searchFrame = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(10, 7, 10, 5) };
        _search = new TextBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text,
            Font = Theme.Font(10.5f),
        };
        _placeholder = new Label
        {
            Text = "종목 검색 · 이름, 코드, 초성(ㅅㅅㅈㅈ)", AutoSize = true, Font = Theme.Font(10f), ForeColor = Theme.Muted,
            BackColor = Theme.Surface, Location = new Point(12, 8), Cursor = Cursors.IBeam,
        };
        _placeholder.Click += (_, _) => _search.Focus();
        searchFrame.Controls.Add(_placeholder);
        searchFrame.Controls.Add(_search);
        _placeholder.BringToFront();
        searchBox.Controls.Add(searchFrame);
        _search.TextChanged += (_, _) =>
        {
            _placeholder.Visible = _search.TextLength == 0;
            _debounce.Stop();
            _debounce.Start();
        };
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && _search.TextLength > 0) { _search.Clear(); e.SuppressKeyPress = true; e.Handled = true; }
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OpenFirstResult(); }
        };
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await OnSearchAsync(); };

        // ── 탭·필터
        _filters = new Panel { Dock = DockStyle.Top, Height = 96 };

        _list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            Padding = new Padding(10, 2, 10, 8),
        };
        _status = new Label
        {
            Dock = DockStyle.Bottom, Height = 24, Font = Theme.Font(8.5f), ForeColor = Theme.Muted,
            Padding = new Padding(14, 3, 14, 0), AutoEllipsis = true,
        };

        Controls.Add(_list);
        Controls.Add(_status);
        Controls.Add(_filters);
        Controls.Add(searchBox);
        Controls.Add(header);

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && _search.TextLength == 0) Close(); };
        ResizeEnd += (_, _) => { BuildFilters(); Render(); };

        _refresh.Tick += async (_, _) => { if (Visible && _search.TextLength == 0) await LoadRanksAsync(); };
        Shown += async (_, _) =>
        {
            BuildFilters();
            _search.Focus();
            await LoadRanksAsync();
        };
        FormClosed += (_, _) => { _refresh.Dispose(); _debounce.Dispose(); };
    }

    /// <summary>감시 종목이 바깥(상황판 우클릭 등)에서 바뀌면 별 표시만 다시 그린다.</summary>
    public void WatchChanged() => Render();

    // ───────────────────────── 탭·필터 ─────────────────────────

    private void BuildFilters()
    {
        _filters.SuspendLayout();
        foreach (Control c in _filters.Controls.Cast<Control>().ToList()) c.Dispose();
        _filters.Controls.Clear();

        // 줄마다 창 폭 안에서 자동 줄바꿈. 줄을 위에서부터 쌓고 패널 높이를 맞춘다.
        int width = Math.Max(240, _filters.ClientSize.Width - 24);
        int y = 2;
        void AddRow(FlowLayoutPanel row, int gapAfter)
        {
            row.Location = new Point(12, y);
            row.MaximumSize = new Size(width, 0);
            _filters.Controls.Add(row);
            row.PerformLayout();
            y += row.GetPreferredSize(new Size(width, 0)).Height + gapAfter;
        }

        var country = Row();
        foreach (var (isUs, label) in new[] { (false, "국장"), (true, "미장") })
            country.Controls.Add(Chip(label, _us == isUs, big: true, () => _ = SwitchCountryAsync(isUs)));
        AddRow(country, 6);

        var tabs = Row();
        foreach (var (type, label) in Tabs)
            tabs.Controls.Add(Chip(label, _tab == type, big: true, () => { _tab = type; FixDuration(); BuildFilters(); _ = LoadRanksAsync(); }));
        AddRow(tabs, 6);

        var row2 = Row();
        row2.Controls.Add(Caption("시장"));
        var marketChips = _us
            ? new[] { (MarketFilter.All, "전체"), (MarketFilter.Nasdaq, "나스닥"), (MarketFilter.Nyse, "뉴욕"), (MarketFilter.Amex, "아멕스") }
            : new[] { (MarketFilter.All, "전체"), (MarketFilter.Kospi, "코스피"), (MarketFilter.Kosdaq, "코스닥") };
        foreach (var (m, label) in marketChips)
            row2.Controls.Add(Chip(label, _market == m, big: false, () => { _market = m; BuildFilters(); Render(); }));
        row2.Controls.Add(Caption("종류", left: 10));
        foreach (var (t, label) in new[] { (TypeFilter.Stock, "주식"), (TypeFilter.Etf, "ETF·ETN"), (TypeFilter.All, "모든 종류") })
            row2.Controls.Add(Chip(label, _type == t, big: false, () => { _type = t; BuildFilters(); Render(); }));
        AddRow(row2, 4);

        var row3 = Row();
        row3.Controls.Add(Caption("기간"));
        bool noRealtime = _tab is "TOP_GAINERS" or "TOP_LOSERS";
        foreach (var (d, label) in Durations)
        {
            if (noRealtime && d == "realtime") continue;
            row3.Controls.Add(Chip(label, _duration == d, big: false, () => { _duration = d; BuildFilters(); _ = LoadRanksAsync(); }));
        }
        if (noRealtime)
            row3.Controls.Add(Caption("급상승·급하락은 실시간 없음", left: 6));
        AddRow(row3, 6);

        _filters.Height = y;
        _filters.ResumeLayout();

        _refresh.Interval = _duration == "realtime" ? 10_000 : 60_000;
        _refresh.Start();
    }

    private async Task SwitchCountryAsync(bool us)
    {
        if (_us == us) return;
        _us = us;
        _market = MarketFilter.All;
        _ranks = new();
        _rankedAt = null;
        _note = null;
        _searchPrices.Clear();
        BuildFilters();
        Render();
        if (us && _usDir.Count == 0)
        {
            try { await _ensureUsDir(); }
            catch (Exception ex) { Log.Write("미국 종목 목록 실패: " + ex.Message); }
        }
        await LoadRanksAsync();
        if (_search.TextLength > 0) await OnSearchAsync();
    }

    /// <summary>급상승·급하락은 실시간을 지원하지 않는다 (API 400)</summary>
    private void FixDuration()
    {
        if (_tab is "TOP_GAINERS" or "TOP_LOSERS" && _duration == "realtime") _duration = "1d";
    }

    private static FlowLayoutPanel Row() => new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
        BackColor = Color.Transparent, Margin = Padding.Empty, Padding = Padding.Empty,
    };

    private static Label Caption(string text, int left = 0) => new()
    {
        Text = text, AutoSize = true, Font = Theme.Font(8.5f), ForeColor = Theme.Muted,
        Padding = new Padding(0, 4, 2, 0), Margin = new Padding(left, 0, 4, 2),
    };

    private static Label Chip(string text, bool on, bool big, Action click)
    {
        var l = new Label
        {
            Text = text,
            AutoSize = true,
            Font = Theme.Font(big ? 9.5f : 8.5f, FontStyle.Bold),
            Padding = big ? new Padding(9, 4, 9, 4) : new Padding(7, 3, 7, 3),
            Margin = new Padding(0, 0, 4, 4),
            BackColor = on ? Theme.Text : Theme.Surface,
            ForeColor = on ? Theme.Background : Theme.Muted,
            Cursor = Cursors.Hand,
        };
        l.Click += (_, _) => click();
        return l;
    }

    // ───────────────────────── 데이터 ─────────────────────────

    private async Task LoadRanksAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            FixDuration();
            var (items, at) = await _toss.GetRankingsAsync(_tab, _duration, 100, country: _us ? "US" : "KR");
            _ranks = items;
            _rankedAt = at;
            _note = items.Count == 0 ? "이 조합은 아직 순위가 집계되지 않았습니다." : null;
        }
        catch (Exception ex) when (ex is TossApiException or HttpRequestException or TaskCanceledException)
        {
            _note = "순위 조회 실패: " + ex.Message;
            Log.Write(_note);
        }
        finally
        {
            _loading = false;
        }
        if (_search.TextLength == 0) Render();
    }

    private async Task OnSearchAsync()
    {
        Render();
        var results = CurrentSearch();
        var need = results.Select(r => r.Symbol).Where(s => !_searchPrices.ContainsKey(s)).ToList();
        if (need.Count == 0) return;
        try
        {
            foreach (var (k, v) in await _toss.GetPricesAsync(need)) _searchPrices[k] = v;
            if (_search.TextLength > 0) Render();
        }
        catch (Exception ex) when (ex is TossApiException or HttpRequestException or TaskCanceledException)
        {
            Log.Write("검색 시세 조회 실패: " + ex.Message);
        }
    }

    private List<StockInfo> CurrentSearch() => _dir.Search(_search.Text, 20);

    private void OpenFirstResult()
    {
        var first = CurrentSearch().FirstOrDefault();
        if (first is not null && !_us) _openStock(first.Symbol, first.Name);
    }

    private bool PassFilters(string symbol)
    {
        var info = _dir.Find(symbol);
        if (info is null) return _market == MarketFilter.All && _type == TypeFilter.All;
        if (_market == MarketFilter.Kospi && info.Market != "KOSPI") return false;
        if (_market == MarketFilter.Kosdaq && info.Market != "KOSDAQ") return false;
        if (_market == MarketFilter.Nasdaq && info.Market != "NASDAQ") return false;
        if (_market == MarketFilter.Nyse && info.Market != "NYSE") return false;
        if (_market == MarketFilter.Amex && info.Market != "AMEX") return false;
        if (_type == TypeFilter.Stock && info.SecurityType != "STOCK") return false;
        if (_type == TypeFilter.Etf && !info.IsEtfLike) return false;
        return true;
    }

    // ───────────────────────── 화면 ─────────────────────────

    private void Render()
    {
        if (IsDisposed) return;
        var scroll = _list.AutoScrollPosition;
        _list.SuspendLayout();
        foreach (Control c in _list.Controls.Cast<Control>().ToList()) c.Dispose();
        _list.Controls.Clear();

        int width = Math.Max(300, _list.ClientSize.Width - _list.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
        bool searching = _search.TextLength > 0;
        _filters.Visible = !searching;

        if (_dir.Count == 0)
        {
            _list.Controls.Add(Note(_us ? "미국 종목 목록을 받는 중입니다… (하루 한 번, 몇 초)" : "종목 목록을 받는 중입니다… (하루 한 번, 몇 초)", width));
        }
        else if (searching)
        {
            var results = CurrentSearch();
            if (results.Count == 0) _list.Controls.Add(Note("찾는 종목이 없습니다.", width));
            foreach (var s in results)
            {
                var price = _searchPrices.TryGetValue(s.Symbol, out var p) ? p : (decimal?)null;
                var rank = _ranks.FirstOrDefault(r => r.Symbol == s.Symbol);
                _list.Controls.Add(StockRow(null, s.Symbol, s.Name, s, price ?? rank?.LastPrice, rank?.ChangePct, null, width));
            }
            _status.Text = results.Count == 0 ? "" : _us
                ? $"{results.Count}건 · 미장은 시세만 (판정·차트는 국장 종목)"
                : $"{results.Count}건 · ☆ 감시 추가 · 줄을 누르면 공시 · Enter 첫 종목";
        }
        else
        {
            if (_note is not null) _list.Controls.Add(Note(_note, width, _note.Contains("실패") ? Theme.Red : Theme.Muted));
            var shown = _ranks.Where(r => PassFilters(r.Symbol)).Take(50).ToList();
            if (_ranks.Count > 0 && shown.Count == 0)
                _list.Controls.Add(Note("상위 100위 안에 이 필터에 맞는 종목이 없습니다.", width));
            int i = 0;
            foreach (var r in shown)
            {
                var info = _dir.Find(r.Symbol);
                _list.Controls.Add(StockRow(++i, r.Symbol, info?.Name ?? r.Symbol, info, r.LastPrice, r.ChangePct,
                    _tab.EndsWith("VOLUME") ? $"{Volume(r.Volume)}주" : _us ? UsdMoney(r.Amount) : Money(r.Amount), width));
            }
            var label = Tabs.First(t => t.type == _tab).label;
            var dur = Durations.First(d => d.duration == _duration).label;
            _status.Text = _rankedAt is { } at
                ? $"{(_us ? "미장 · " : "")}{label} · {dur} · {at:HH:mm:ss} 기준 · 상위 100위 중 {shown.Count}개" + (_us ? " · 시세만" : " · 줄을 누르면 공시")
                : _loading ? "불러오는 중…" : "";
        }

        _list.ResumeLayout();
        _list.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
    }

    private static Control Note(string text, int width, Color? color = null) => new Label
    {
        Text = text, AutoSize = false, Width = width, Height = 30, Font = Theme.Font(9f),
        ForeColor = color ?? Theme.Muted, Margin = new Padding(0, 8, 0, 0),
    };

    /// <summary>순위 · 종목명(시장·종류) · 현재가 · 등락률 · 거래대금 · ☆</summary>
    private Control StockRow(int? rank, string symbol, string name, StockInfo? info, decimal? price, decimal? pct, string? extra, int width)
    {
        var row = new Panel { Width = width, Height = 40, BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, 3), Cursor = Cursors.Hand };

        int x = 8;
        if (rank is { } rk)
        {
            row.Controls.Add(new Label
            {
                Text = rk.ToString(Ko), AutoSize = false, Width = 24, Height = 20, Location = new Point(x, 11),
                Font = Theme.Font(9f, FontStyle.Bold), ForeColor = rk <= 3 ? Theme.Text : Theme.Muted, TextAlign = ContentAlignment.MiddleRight,
            });
            x += 30;
        }

        // 오른쪽 열: ☆ · (거래대금) · 등락률 · 현재가
        bool us = _us || info?.IsUs == true;
        bool watched = !us && _isWatched(symbol);
        var star = new Label
        {
            Text = watched ? "★" : "☆", AutoSize = false, Width = 26, Height = 26, Location = new Point(width - 30, 7),
            Font = Theme.Font(12f), ForeColor = watched ? Theme.Amber : Theme.Muted, TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand,
        };
        new ToolTip().SetToolTip(star, watched ? "감시 종목에서 빼기" : "감시 종목에 추가 (상황판에 카드가 생깁니다)");
        star.Click += (_, _) =>
        {
            var target = info ?? new StockInfo(symbol, name, "", "", true);
            _setWatched(target, !_isWatched(symbol));
            Render();
        };
        if (!us) row.Controls.Add(star);
        else row.Cursor = Cursors.Default;

        int right = us ? width - 8 : star.Left - 4;
        Label Col(string text, int w, Color color, FontStyle style = FontStyle.Regular)
        {
            var l = new Label
            {
                Text = text, AutoSize = false, Width = w, Height = 20, Location = new Point(right - w, 11),
                Font = Theme.Font(9f, style), ForeColor = color, TextAlign = ContentAlignment.MiddleRight,
            };
            row.Controls.Add(l);
            right -= w + 4;
            return l;
        }

        if (extra is not null) Col(extra, 60, Theme.Muted);
        var pctColor = pct is > 0 ? Theme.Red : pct is < 0 ? Theme.Blue : Theme.Muted;
        Col(pct is { } pc ? SignalEngine.Signed(pc) + "%" : "", 74, pctColor, FontStyle.Bold);
        Col(price is { } pr ? (us ? "$" + pr.ToString("#,0.00", CultureInfo.InvariantCulture) : pr.ToString("N0", Ko)) : "-", 84, pct is null ? Theme.Text : pctColor, FontStyle.Bold);

        var nameLabel = new Label
        {
            Text = name, AutoSize = false, AutoEllipsis = true, Location = new Point(x, 4), Width = Math.Max(60, right - x), Height = 19,
            Font = Theme.Font(9.5f, FontStyle.Bold), ForeColor = Theme.Text,
        };
        var sub = new Label
        {
            Text = info is null ? symbol : $"{info.MarketLabel} · {info.TypeLabel} · {symbol}",
            AutoSize = false, Location = new Point(x, 22), Width = Math.Max(60, right - x), Height = 15,
            Font = Theme.Font(7.5f), ForeColor = Theme.Muted,
        };
        row.Controls.Add(nameLabel);
        row.Controls.Add(sub);

        if (watched) row.BackColor = Color.FromArgb(44, 50, 60);
        var bg = row.BackColor;
        var hover = Color.FromArgb(Math.Min(255, bg.R + 10), Math.Min(255, bg.G + 10), Math.Min(255, bg.B + 12));
        foreach (Control c in row.Controls.Cast<Control>().Append(row))
        {
            if (ReferenceEquals(c, star)) continue;
            if (!us) c.Click += (_, _) => _openStock(symbol, name);
            c.MouseEnter += (_, _) => row.BackColor = hover;
            c.MouseLeave += (_, _) => { if (!row.ClientRectangle.Contains(row.PointToClient(MousePosition))) row.BackColor = bg; };
        }
        return row;
    }

    private static string Money(decimal won)
    {
        if (won >= 1_000_000_000_000m) return (won / 1_000_000_000_000m).ToString("0.0", Ko) + "조";
        if (won >= 100_000_000m) return (won / 100_000_000m).ToString("N0", Ko) + "억";
        return (won / 100_000_000m).ToString("0.0", Ko) + "억";
    }

    private static string UsdMoney(decimal usd)
    {
        if (usd >= 1_000_000_000m) return "$" + (usd / 1_000_000_000m).ToString("0.0", CultureInfo.InvariantCulture) + "B";
        if (usd >= 1_000_000m) return "$" + (usd / 1_000_000m).ToString("0", CultureInfo.InvariantCulture) + "M";
        return "$" + (usd / 1_000m).ToString("0", CultureInfo.InvariantCulture) + "K";
    }

    private static string Volume(decimal v)
    {
        if (v >= 100_000_000m) return (v / 100_000_000m).ToString("0.0", Ko) + "억";
        if (v >= 10_000m) return (v / 10_000m).ToString("N0", Ko) + "만";
        return v.ToString("N0", Ko);
    }
}
