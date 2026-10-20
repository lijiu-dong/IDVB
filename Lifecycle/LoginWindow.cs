using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using IDVBuff.Core.Contracts.Online;
using IDVBuff.Diagnostics;
using Forms = System.Windows.Forms;

namespace IDVBuff.Lifecycle;

/// <summary>
/// Native login / registration window in the startup splash style (1.7.1-preview.3). It talks only to
/// <see cref="IDesktopLoginGate"/>; the server decides access. Startup mode first asks the server whether
/// this installation may start; interactive mode (in-app "登录") opens directly on the login page.
/// </summary>
internal sealed partial class LoginWindow : Forms.Form
{
    internal enum Mode { Startup, Interactive }
    internal enum Result { Granted, Cancelled }

    private const int BaseWidth = 560, BaseHeight = 640;
    private static readonly Color Background = Color.FromArgb(250, 251, 253);
    private static readonly Color Border = Color.FromArgb(224, 229, 237);
    private static readonly Color Title = Color.FromArgb(28, 38, 56);
    private static readonly Color Muted = Color.FromArgb(101, 112, 130);
    private static readonly Color Accent = Color.FromArgb(48, 112, 224);
    private static readonly Color Danger = Color.FromArgb(196, 43, 28);

    private readonly IDesktopLoginGate _gate;
    private readonly Mode _mode;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Forms.Panel _content = new() { BackColor = Background };
    private readonly Font _titleFont = new("Microsoft YaHei UI", 15f, FontStyle.Bold);
    private readonly Font _bodyFont = new("Microsoft YaHei UI", 10f);
    private readonly Font _smallFont = new("Microsoft YaHei UI", 9f);
    private Bitmap? _logo;
    private bool _busy;
    // Values carried between the two steps of a flow; cleared when the window closes.
    private string _email = "", _password = "", _displayName = "";

    internal Result Outcome { get; private set; } = Result.Cancelled;
    internal AccountIdentity? Identity { get; private set; }

    internal LoginWindow(IDesktopLoginGate gate, Mode mode)
    {
        _gate = gate;
        _mode = mode;
        Text = "Identity Vision Bridge · 登录";
        FormBorderStyle = Forms.FormBorderStyle.None;
        StartPosition = Forms.FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        MaximizeBox = MinimizeBox = false;
        AutoScaleMode = Forms.AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(BaseWidth, BaseHeight);
        BackColor = Background;
        Font = _bodyFont;
        DoubleBuffered = true;
        KeyPreview = true;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "IDVB_icon_multisize.ico");
            Icon = new Icon(path);
            using var large = new Icon(path, new Size(256, 256));
            _logo = large.ToBitmap();
        }
        catch (Exception exception) { StartupTimeline.Write("Login window icon unavailable.", exception); }
        _content.SetBounds(40, 150, BaseWidth - 80, BaseHeight - 190);
        Controls.Add(_content);
        var close = new Forms.Label { Text = "✕", AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Muted,
            Font = _bodyFont, Cursor = Forms.Cursors.Hand, Bounds = new Rectangle(BaseWidth - 44, 8, 36, 32) };
        close.Click += (_, _) => Close();
        Controls.Add(close);
        MouseDown += (_, e) => DragWindow(e);
        KeyDown += (_, e) => { if (e.KeyCode == Forms.Keys.Escape && !_busy) Close(); };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        if (_mode == Mode.Startup) _ = CheckAccessAsync(null);
        else ShowLogin(null);
    }

    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var scale = DeviceDpi / 96f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = new Pen(Border);
        g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        if (_logo is not null) g.DrawImage(_logo, 40 * scale, 36 * scale, 52 * scale, 52 * scale);
        using var title = new SolidBrush(Title);
        using var muted = new SolidBrush(Muted);
        g.DrawString("Identity Vision Bridge", _titleFont, title, 40 * scale, 98 * scale);
        g.DrawString("IDVB 账户", _smallFont, muted, 106 * scale, 54 * scale);
    }

    protected override void OnFormClosing(Forms.FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        _lifetime.Cancel();
        _email = _password = _displayName = "";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lifetime.Dispose();
            _logo?.Dispose();
            _titleFont.Dispose();
            _bodyFont.Dispose();
            _smallFont.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- Pages ----

    private async Task CheckAccessAsync(string? notice)
    {
        Page("正在连接 IDVB 服务器…", notice ?? "正在确认账户与使用资格，请稍候。");
        DesktopAccessResult result;
        try { result = await _gate.CheckAccessAsync(_lifetime.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception exception)
        {
            StartupTimeline.Write("Desktop access check failed.", exception);
            result = new(DesktopAccessOutcome.Unavailable, "连接服务器时出现错误。");
        }
        StartupTimeline.Write($"Desktop access outcome: {result.Outcome}.");
        switch (result.Outcome)
        {
            case DesktopAccessOutcome.Granted or DesktopAccessOutcome.NotConfigured:
                Finish(result.Identity);
                break;
            case DesktopAccessOutcome.GrantedOffline:
                ShowOfflineNotice(result);
                break;
            case DesktopAccessOutcome.Disabled:
                ShowMessage("账户已停用", result.Message ?? "该账户已被停用。", ("退出", Close));
                break;
            case DesktopAccessOutcome.Unavailable:
                ShowMessage("暂时无法连接", result.Message ?? "无法连接 IDVB 服务器。",
                    ("重试", () => _ = CheckAccessAsync(null)), ("退出", Close));
                break;
            default:
                ShowLogin(result.Message);
                break;
        }
    }

    private void ShowOfflineNotice(DesktopAccessResult result)
    {
        var until = result.OfflineUntil?.ToLocalTime().ToString("HH:mm") ?? "-";
        ShowMessage("离线模式", $"{result.Message}\n\n将使用上次联网时取得的离线许可进入，有效至 {until}。离线期间只能使用已缓存的资源。",
            ("继续", () => Finish(result.Identity)), ("重试联网", () => _ = CheckAccessAsync(null)));
    }

    private void ShowLogin(string? notice)
    {
        var y = Page("登录", notice ?? "使用 IDVB 账户（邮箱）登录。");
        var email = Input("邮箱", ref y, _email);
        var password = Input("密码", ref y, "", secret: true);
        var error = ErrorLabel(ref y);
        var submit = Button("登录", ref y);
        Link("注册新账户", 0, ref y, () => ShowRegister(null));
        y -= Scale(30);
        Link("忘记密码", Scale(160), ref y, () => ShowResetRequest(email.Text));
        submit.Click += async (_, _) =>
        {
            if (!ValidEmail(email.Text, error) || !Require(password.Text.Length > 0, error, "请输入密码。")) return;
            _email = email.Text.Trim(); _password = password.Text;
            await RunAsync(error, () => _gate.LoginAsync(_email, _password, _lifetime.Token), step =>
            {
                if (step.Kind == DesktopAuthStepKind.VerificationRequired) ShowCode(CodeFlow.Login, step.Message);
                else if (step.Kind == DesktopAuthStepKind.SignedIn) AfterSignIn(step.Identity);
                else Fail(error, step);
            });
        };
        AcceptButton = submit;
        (email.Text.Length == 0 ? email : password).Select();
    }

    private void ShowRegister(string? notice)
    {
        var y = Page("注册", notice ?? "新账户只支持 QQ 邮箱和 Gmail。");
        var name = Input("邮箱用户名", ref y, "", width: Scale(300));
        var domain = new Forms.ComboBox { DropDownStyle = Forms.ComboBoxStyle.DropDownList, Font = _bodyFont,
            Bounds = new Rectangle(Scale(310), y - Scale(34), _content.Width - Scale(310), Scale(30)) };
        foreach (var item in _gate.RegistrationDomains) domain.Items.Add("@" + item);
        if (domain.Items.Count > 0) domain.SelectedIndex = 0;
        _content.Controls.Add(domain);
        var display = Input("昵称（2–32 个字符）", ref y, _displayName);
        var password = Input("密码（至少 10 个字符）", ref y, "", secret: true);
        var confirm = Input("确认密码", ref y, "", secret: true);
        var error = ErrorLabel(ref y);
        var submit = Button("发送验证码", ref y);
        Link("已有账户？返回登录", 0, ref y, () => ShowLogin(null));
        submit.Click += async (_, _) =>
        {
            var local = name.Text.Trim().ToLowerInvariant();
            if (!Require(LocalPart().IsMatch(local) && !local.Contains(".."), error, "邮箱用户名只能包含字母、数字、点、下划线和连字符。")
                || !Require(domain.SelectedItem is string, error, "请选择邮箱后缀。")
                || !Require(display.Text.Trim().Length is >= 2 and <= 32, error, "昵称需要 2–32 个字符。")
                || !Require(password.Text.Length is >= 10 and <= 128, error, "密码需要 10–128 个字符。")
                || !Require(password.Text == confirm.Text, error, "两次输入的密码不一致。")) return;
            // The suffix comes only from the list; the server enforces the same list again.
            _email = local + (string)domain.SelectedItem!; _password = password.Text; _displayName = display.Text.Trim();
            await RunAsync(error, () => _gate.StartRegistrationAsync(_email, _lifetime.Token), step =>
            {
                if (step.Kind == DesktopAuthStepKind.CodeSent) ShowCode(CodeFlow.Register, step.Message);
                else Fail(error, step);
            });
        };
        AcceptButton = submit;
        name.Select();
    }

    private void ShowResetRequest(string? email)
    {
        var y = Page("找回密码", "输入注册邮箱，我们会发送验证码。");
        var input = Input("邮箱", ref y, email?.Trim() ?? _email);
        var error = ErrorLabel(ref y);
        var submit = Button("发送验证码", ref y);
        Link("返回登录", 0, ref y, () => ShowLogin(null));
        submit.Click += async (_, _) =>
        {
            if (!ValidEmail(input.Text, error)) return;
            _email = input.Text.Trim();
            await RunAsync(error, () => _gate.RequestPasswordResetAsync(_email, _lifetime.Token), step =>
            {
                if (step.Kind == DesktopAuthStepKind.CodeSent) ShowCode(CodeFlow.Reset, step.Message);
                else Fail(error, step);
            });
        };
        AcceptButton = submit;
        input.Select();
    }

    private enum CodeFlow { Login, Register, Reset }

    private void ShowCode(CodeFlow flow, string? notice)
    {
        var heading = flow switch { CodeFlow.Login => "邮箱验证", CodeFlow.Register => "完成注册", _ => "设置新密码" };
        var y = Page(heading, notice ?? "验证码已发送，请查收邮件。");
        var code = Input("6 位验证码", ref y, "", width: Scale(200));
        code.MaxLength = 6;
        Forms.TextBox? password = null, confirm = null;
        if (flow == CodeFlow.Reset)
        {
            password = Input("新密码（至少 10 个字符）", ref y, "", secret: true);
            confirm = Input("确认新密码", ref y, "", secret: true);
        }
        var error = ErrorLabel(ref y);
        var submit = Button(flow switch { CodeFlow.Login => "验证并登录", CodeFlow.Register => "完成注册", _ => "修改密码" }, ref y);
        Link("重新发送验证码", 0, ref y, async () => await RunAsync(error, () => flow switch
        {
            CodeFlow.Login => _gate.LoginAsync(_email, _password, _lifetime.Token),
            CodeFlow.Register => _gate.StartRegistrationAsync(_email, _lifetime.Token),
            _ => _gate.RequestPasswordResetAsync(_email, _lifetime.Token),
        }, step =>
        {
            if (step.Kind is DesktopAuthStepKind.Failed) Fail(error, step);
            else if (step.Kind == DesktopAuthStepKind.SignedIn) AfterSignIn(step.Identity);
            else { error.ForeColor = Muted; error.Text = step.Message ?? "验证码已重新发送。"; }
        }));
        y -= Scale(30);
        Link("返回", Scale(200), ref y, () => { if (flow == CodeFlow.Register) ShowRegister(null); else ShowLogin(null); });
        submit.Click += async (_, _) =>
        {
            if (!Require(Regex.IsMatch(code.Text.Trim(), "^[0-9]{6}$"), error, "请输入邮件中的 6 位数字验证码。")) return;
            if (flow == CodeFlow.Reset && (!Require(password!.Text.Length is >= 10 and <= 128, error, "密码需要 10–128 个字符。")
                || !Require(password.Text == confirm!.Text, error, "两次输入的密码不一致。"))) return;
            await RunAsync(error, () => flow switch
            {
                CodeFlow.Login => _gate.ConfirmLoginAsync(_email, _password, code.Text, _lifetime.Token),
                CodeFlow.Register => _gate.ConfirmRegistrationAsync(_email, code.Text, _password, _displayName, _lifetime.Token),
                _ => _gate.ConfirmPasswordResetAsync(_email, code.Text, password!.Text, _lifetime.Token),
            }, step =>
            {
                if (step.Kind == DesktopAuthStepKind.SignedIn) AfterSignIn(step.Identity);
                else if (step.Kind == DesktopAuthStepKind.PasswordChanged) { _password = ""; ShowLogin(step.Message); }
                else Fail(error, step);
            });
        };
        AcceptButton = submit;
        code.Select();
    }

    private void ShowMessage(string heading, string message, params (string Text, Action Action)[] actions)
    {
        var y = Page(heading, message);
        var x = 0;
        foreach (var (text, action) in actions)
        {
            var button = Button(text, ref y, x, Scale(150));
            button.Click += (_, _) => action();
            if (x == 0) AcceptButton = button;
            x += Scale(166);
            y -= Scale(52);
        }
    }

    private void AfterSignIn(AccountIdentity? identity)
    {
        _password = "";
        Identity = identity;
        if (_mode == Mode.Interactive) { Outcome = Result.Granted; Close(); return; }
        _ = CheckAccessAsync("登录成功，正在确认使用资格…");
    }

    private void Finish(AccountIdentity? identity)
    {
        Identity ??= identity;
        Outcome = Result.Granted;
        Close();
    }

    // ---- Layout helpers (logical 96-dpi units, scaled by the current DPI) ----

    private int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96f);

    private int Page(string heading, string message)
    {
        AcceptButton = null;
        _content.SuspendLayout();
        foreach (Forms.Control control in _content.Controls.Cast<Forms.Control>().ToArray()) control.Dispose();
        _content.Controls.Clear();
        _content.Controls.Add(new Forms.Label { Text = heading, Font = _titleFont, ForeColor = Title, AutoSize = false,
            Bounds = new Rectangle(0, 0, _content.Width, Scale(36)) });
        var text = new Forms.Label { Text = message, Font = _bodyFont, ForeColor = Muted, AutoSize = false,
            Bounds = new Rectangle(0, Scale(40), _content.Width, Scale(60)) };
        _content.Controls.Add(text);
        _content.ResumeLayout();
        return Scale(108);
    }

    private Forms.TextBox Input(string label, ref int y, string value, bool secret = false, int? width = null)
    {
        _content.Controls.Add(new Forms.Label { Text = label, Font = _smallFont, ForeColor = Muted, AutoSize = false,
            Bounds = new Rectangle(0, y, _content.Width, Scale(20)) });
        var box = new Forms.TextBox { Text = value, Font = _bodyFont, UseSystemPasswordChar = secret, MaxLength = secret ? 128 : 254,
            Bounds = new Rectangle(0, y + Scale(22), width ?? _content.Width, Scale(30)) };
        _content.Controls.Add(box);
        y += Scale(64);
        return box;
    }

    private Forms.Label ErrorLabel(ref int y)
    {
        var label = new Forms.Label { Font = _smallFont, ForeColor = Danger, AutoSize = false,
            Bounds = new Rectangle(0, y, _content.Width, Scale(40)) };
        _content.Controls.Add(label);
        y += Scale(42);
        return label;
    }

    private Forms.Button Button(string text, ref int y, int x = 0, int? width = null)
    {
        var button = new Forms.Button { Text = text, Font = _bodyFont, FlatStyle = Forms.FlatStyle.Flat, BackColor = Accent,
            ForeColor = Color.White, Cursor = Forms.Cursors.Hand, Bounds = new Rectangle(x, y, width ?? _content.Width, Scale(40)) };
        button.FlatAppearance.BorderSize = 0;
        _content.Controls.Add(button);
        y += Scale(52);
        return button;
    }

    private void Link(string text, int x, ref int y, Action action)
    {
        var link = new Forms.LinkLabel { Text = text, Font = _smallFont, LinkColor = Accent, ActiveLinkColor = Accent, AutoSize = true,
            Location = new Point(x, y) };
        link.LinkClicked += (_, _) => { if (!_busy) action(); };
        _content.Controls.Add(link);
        y += Scale(30);
    }

    private static bool Require(bool condition, Forms.Label error, string message)
    {
        if (!condition) { error.ForeColor = Danger; error.Text = message; }
        return condition;
    }

    private static bool ValidEmail(string value, Forms.Label error) =>
        Require(Regex.IsMatch(value.Trim(), @"^[^\s@]+@[^\s@]+\.[^\s@]+$") && value.Trim().Length <= 254, error, "请输入有效的邮箱地址。");

    private static void Fail(Forms.Label error, DesktopAuthStep step)
    {
        error.ForeColor = Danger;
        error.Text = step.FieldErrors is { Count: > 0 } fields && step.Message is null
            ? string.Join("；", fields.Values) : step.Message ?? "请求失败，请稍后重试。";
    }

    private async Task RunAsync(Forms.Label error, Func<Task<DesktopAuthStep>> work, Action<DesktopAuthStep> next)
    {
        if (_busy) return;
        _busy = true;
        UseWaitCursor = true;
        foreach (Forms.Control control in _content.Controls) if (control is Forms.Button) control.Enabled = false;
        error.ForeColor = Muted;
        error.Text = "请稍候…";
        try { next(await work()); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            StartupTimeline.Write("Login step failed.", exception);
            error.ForeColor = Danger;
            error.Text = "请求失败，请稍后重试。";
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
            if (!IsDisposed) foreach (Forms.Control control in _content.Controls) if (control is Forms.Button) control.Enabled = true;
        }
    }

    private void DragWindow(Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, 2, 0); // WM_NCLBUTTONDOWN, HTCAPTION
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9._-]{0,62}[a-z0-9])?$")] private static partial Regex LocalPart();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    // ---- Hosting ----

    /// <summary>Runs the window on its own STA message loop (WinUI is not running yet, or owns the UI thread).</summary>
    internal static Task<(Result Outcome, AccountIdentity? Identity)> RunAsync(IDesktopLoginGate gate, Mode mode)
    {
        var completion = new TaskCompletionSource<(Result, AccountIdentity?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
                using var window = new LoginWindow(gate, mode);
                Forms.Application.Run(window);
                completion.TrySetResult((window.Outcome, window.Identity));
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true, Name = "IDVB login window" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
