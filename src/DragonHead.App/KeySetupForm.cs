using System.Diagnostics;
using DragonHead.Core;

namespace DragonHead.App;

/// <summary>
/// API 키 입력 창. 처음 실행했거나 키가 비어 있으면 뜨고, 트레이 메뉴 'API 키 설정'에서도 연다.
/// 저장하면 settings.json 에 들어가고 ClientSecret 은 Windows DPAPI 로 암호화된다.
/// </summary>
internal sealed class KeySetupForm : Form
{
    private readonly AppSettings _settings;
    private readonly string _currentSecret;
    private readonly TextBox _id, _secret, _dart;
    private readonly Label _result;
    private readonly Button _test, _save;

    /// <summary>저장 후 앱이 쓸 시크릿 (입력을 비워 두면 기존 값 유지)</summary>
    public string Secret { get; private set; } = "";

    public KeySetupForm(AppSettings settings, string currentSecret, bool firstRun)
    {
        _settings = settings;
        _currentSecret = currentSecret;

        Text = firstRun ? "용머리 알리미 · 처음 설정" : "용머리 알리미 · API 키 설정";
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        ClientSize = new Size(520, 560);
        Font = Theme.Font(9.5f);
        TopMost = true;

        int y = 16;
        Label Text_(string text, float size = 9.5f, FontStyle style = FontStyle.Regular, Color? color = null, int height = 0)
        {
            var l = new Label
            {
                Text = text, AutoSize = height == 0, Font = Theme.Font(size, style), ForeColor = color ?? Theme.Text,
                Location = new Point(20, y), MaximumSize = new Size(480, 0),
            };
            if (height > 0) { l.Size = new Size(480, height); }
            Controls.Add(l);
            y = l.Top + (height > 0 ? height : l.PreferredHeight) + 4;
            return l;
        }
        TextBox Box(string value, bool password = false)
        {
            var t = new TextBox
            {
                Text = value, Location = new Point(20, y), Width = 480, BorderStyle = BorderStyle.FixedSingle,
                BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.Font(10f), UseSystemPasswordChar = password,
            };
            Controls.Add(t);
            y = t.Bottom + 12;
            return t;
        }
        LinkLabel Link(string text, string url)
        {
            var l = new LinkLabel
            {
                Text = text, AutoSize = true, Location = new Point(20, y), LinkColor = Theme.Blue, ActiveLinkColor = Theme.Amber,
                Font = Theme.Font(8.5f),
            };
            l.LinkClicked += (_, _) => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } };
            Controls.Add(l);
            y = l.Bottom + 6;
            return l;
        }

        Text_(firstRun ? "토스증권 API 키를 넣어 주세요" : "API 키", 13f, FontStyle.Bold);
        Text_("키는 이 PC의 설정 파일에만 저장되고, Secret 은 이 Windows 계정에서만 풀리도록 암호화됩니다.", 8.5f, color: Theme.Muted);
        y += 6;

        Text_("토스증권 Client ID", 9.5f, FontStyle.Bold);
        _id = Box(settings.ClientId);
        Text_("토스증권 Client Secret", 9.5f, FontStyle.Bold);
        _secret = Box("", password: true);
        if (!string.IsNullOrEmpty(currentSecret))
            Text_("저장된 값이 있습니다. 바꿀 때만 새로 입력하세요.", 8.5f, color: Theme.Muted);
        Link("발급: 토스증권 PC 웹(WTS) → 설정 → Open API · 같은 화면의 '허용 IP 관리'에 이 PC의 공인 IP도 등록", "https://tossinvest.com");
        y += 6;

        Text_("DART 인증키 (선택)", 9.5f, FontStyle.Bold);
        _dart = Box(settings.DartApiKey);
        Link("발급: opendart.fss.or.kr (무료) — 넣으면 실적·IR·자사주 공시가 명분으로 자동 등록됩니다", "https://opendart.fss.or.kr");
        y += 8;

        _result = new Label
        {
            Location = new Point(20, y), Size = new Size(480, 64), Font = Theme.Font(9f), ForeColor = Theme.Muted,
            Text = "연결 확인을 누르면 토스·DART 에 실제로 접속해 봅니다.",
        };
        Controls.Add(_result);
        y = _result.Bottom + 8;

        _test = new Button
        {
            Text = "연결 확인", Location = new Point(20, y), Size = new Size(120, 34), FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Surface, ForeColor = Theme.Text,
        };
        _test.Click += async (_, _) => await TestAsync();
        _save = new Button
        {
            Text = "저장", Location = new Point(380, y), Size = new Size(120, 34), FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Amber, ForeColor = Theme.Background, Font = Theme.Font(10f, FontStyle.Bold),
        };
        _save.Click += (_, _) => SaveAndClose();
        var cancel = new Button
        {
            Text = firstRun ? "나중에" : "취소", Location = new Point(250, y), Size = new Size(120, 34), FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Surface, ForeColor = Theme.Muted, DialogResult = DialogResult.Cancel,
        };
        Controls.AddRange(new Control[] { _test, cancel, _save });
        AcceptButton = _save;
        CancelButton = cancel;
        ClientSize = new Size(520, _save.Bottom + 20);
    }

    private string EffectiveSecret => _secret.Text.Trim().Length > 0 ? _secret.Text.Trim() : _currentSecret;

    private async Task TestAsync()
    {
        _test.Enabled = false;
        _result.ForeColor = Theme.Muted;
        _result.Text = "확인 중…";
        var lines = new List<string>();
        bool ok = true;

        if (_id.Text.Trim().Length == 0 || EffectiveSecret.Length == 0)
        {
            lines.Add("✗ 토스: Client ID 와 Secret 을 모두 넣어 주세요.");
            ok = false;
        }
        else
        {
            try
            {
                using var toss = new TossClient(_id.Text.Trim(), EffectiveSecret);
                var day = await toss.GetMarketDayAsync();
                lines.Add($"✓ 토스 연결 성공 ({day.Date:M/d} {(day.IsOpen ? "장 운영일" : "휴장일")})");
            }
            catch (Exception ex) when (ex is TossApiException or HttpRequestException or TaskCanceledException)
            {
                ok = false;
                lines.Add("✗ 토스: " + ex.Message);
            }
        }

        if (_dart.Text.Trim().Length > 0)
        {
            try
            {
                using var dart = new DartClient(_dart.Text.Trim());
                await dart.ListAsync("00126380", Kst.Today.AddDays(-7), Kst.Today);
                lines.Add("✓ DART 연결 성공");
            }
            catch (Exception ex) when (ex is DartException or HttpRequestException or TaskCanceledException)
            {
                ok = false;
                lines.Add("✗ DART: " + ex.Message);
            }
        }
        else
        {
            lines.Add("– DART: 키 없음 (선택 사항)");
        }

        _result.ForeColor = ok ? Theme.Green : Theme.Red;
        _result.Text = string.Join("\n", lines);
        _test.Enabled = true;
    }

    private void SaveAndClose()
    {
        if (_id.Text.Trim().Length == 0 || EffectiveSecret.Length == 0)
        {
            _result.ForeColor = Theme.Red;
            _result.Text = "토스 Client ID 와 Secret 은 꼭 필요합니다.";
            return;
        }

        _settings.ClientId = _id.Text.Trim();
        _settings.DartApiKey = _dart.Text.Trim();
        Secret = EffectiveSecret;
        try
        {
            SettingsStore.SaveWithSecret(_settings, Secret);
        }
        catch (Exception ex)
        {
            _result.ForeColor = Theme.Red;
            _result.Text = "저장 실패: " + ex.Message;
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
