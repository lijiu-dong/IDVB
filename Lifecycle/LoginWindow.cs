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
/// <see cref="IDesktopLoginGate"/>; the server decides access. Startup mode presents the result obtained
/// by the startup splash; interactive mode (in-app "登录") opens directly on the login page.
/// </summary>
internal sealed partial class LoginWindow : Forms.Form
{
    internal enum Mode { Startup, Interactive }
    internal enum Result { Granted, Cancelled, Recheck }

    private const int BaseWidth = 420;
    private static readonly Color Background = Color.FromArgb(250, 251, 253);
    private static readonly Color Border = Color.FromArgb(224, 229, 237);
    private static readonly Color Title = Color.FromArgb(28, 38, 56);
    private static readonly Color Muted = Color.FromArgb(101, 112, 130);
    private static readonly Color Accent = Color.FromArgb(48, 112, 224);
    private static readonly Color Danger = Color.FromArgb(196, 43, 28);

    private readonly IDesktopLoginGate _gate;
    private readonly Mode _mode;
    private readonly DesktopAccessResult? _startupAccess;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Forms.Panel _content = new() { BackColor = Background };
    private Font _titleFont = new("Segoe UI", 20f, FontStyle.Bold, GraphicsUnit.Pixel);
    private Font _bodyFont = new("Segoe UI", 14f, FontStyle.Regular, GraphicsUnit.Pixel);
    private Font _smallFont = new("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    private bool _busy;
    private bool _versionUpdatePending;
    // Values carried between the two steps of a flow; cleared when the window closes.
    private string _email = "", _password = "", _displayName = "";

    internal Result Outcome { get; private set; } = Result.Cancelled;
    internal AccountIdentity? Identity { get; private set; }

    internal LoginWindow(IDesktopLoginGate gate, Mode mode, DesktopAccessResult? startupAccess = null)
    {
        _gate = gate;
        _mode = mode;
        _startupAccess = startupAccess;
        Text = "登录";
        FormBorderStyle = Forms.FormBorderStyle.None;
        StartPosition = Forms.FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        MaximizeBox = MinimizeBox = false;
        AutoScaleMode = Forms.AutoScaleMode.None;
        ClientSize = new Size(BaseWidth, 420);
        BackColor = Background;
        Font = _bodyFont;
        DoubleBuffered = true;
        SetStyle(Forms.ControlStyles.ResizeRedraw, true);
        KeyPreview = true;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "IDVB_icon_multisize.ico");
            Icon = new Icon(path);
        }
        catch (Exception exception) { StartupTimeline.Write("Login window icon unavailable.", exception); }
        Controls.Add(_content);
        _closeButton = new ActionButton { Text = "×", IsQuiet = true, AccessibleName = "关闭", TabStop = false };
        _closeButton.Click += (_, _) => Close();
        Controls.Add(_closeButton);
        InitializeTransition();
        MouseDown += (_, e) => DragWindow(e);
        KeyDown += (_, e) => { if (e.KeyCode == Forms.Keys.Escape && !_busy && !_versionUpdatePending) Close(); };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        if (_mode == Mode.Startup) PresentAccessResult(_startupAccess ?? new(DesktopAccessOutcome.LoginRequired));
        else ShowLogin(null);
    }

    protected override void OnFormClosing(Forms.FormClosingEventArgs e)
    {
        if (_versionUpdatePending && e.CloseReason == Forms.CloseReason.UserClosing)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
        StopTransition();
        _lifetime.Cancel();
        _email = _password = _displayName = "";
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _lifetime.Dispose();
            _transitionTimer.Dispose();
            _titleFont.Dispose();
            _bodyFont.Dispose();
            _smallFont.Dispose();
            foreach (var font in _retiredFonts) font.Dispose();
            _retiredFonts.Clear();
        }
    }

    // ---- Pages ----

    private void PresentAccessResult(DesktopAccessResult result)
    {
        switch (result.Outcome)
        {
            case DesktopAccessOutcome.UpdateRequired:
                _versionUpdatePending = true;
                _closeButton.Visible = false;
                Text = "Identity Vision Bridge";
                ShowMessage("需要更新", DesktopAccessResult.VersionUpdateMessage, ("确认", () =>
                {
                    _versionUpdatePending = false;
                    Close();
                }));
                break;
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
                    ("重试", RequestAccessCheck), ("退出", Close));
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
            ("继续", () => Finish(result.Identity)), ("重试联网", RequestAccessCheck));
    }

    private void ShowLogin(string? notice)
    {
        var y = Page("登录", notice ?? "");
        var email = Input("邮箱", ref y, _email);
        var password = Input("密码", ref y, "", secret: true);
        var error = ErrorLabel(ref y);
        var agreement = Agreement(ref y, error);
        var submit = Button("登录", ref y);
        BindAgreement(agreement, submit);
        Links(ref y, ("注册新账户", () => ShowRegister(null)), ("忘记密码", () => ShowResetRequest(email.Text)));
        FitPage();
        submit.Click += async (_, _) =>
        {
            if (!Require(agreement.Checked, error, "请先阅读并同意软件许可和使用条款。")) return;
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
        var y = Page("创建账户", notice ?? "先验证邮箱，再设置昵称和密码。");
        var name = Input("邮箱", ref y, "", width: Scale(204));
        var domain = new DomainSelector(_gate.RegistrationDomains) { Font = _bodyFont,
            AccessibleName = "邮箱后缀",
            Bounds = new Rectangle(Scale(212), y - Scale(50), ContentWidth - Scale(212), Scale(40)) };
        _content.Controls.Add(domain);
        var error = ErrorLabel(ref y);
        var submit = Button("发送验证码", ref y);
        Links(ref y, ("已有账户？返回登录", () => ShowLogin(null)));
        FitPage();
        submit.Click += async (_, _) =>
        {
            var local = name.Text.Trim().ToLowerInvariant();
            if (!Require(LocalPart().IsMatch(local) && !local.Contains(".."), error, "邮箱用户名只能包含字母、数字、点、下划线和连字符。")
                || !Require(domain.SelectedDomain is not null, error, "请选择邮箱后缀。")) return;
            // The suffix comes only from the list; the server enforces the same list again.
            _email = local + "@" + domain.SelectedDomain;
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
        var y = Page("找回密码", "输入邮箱以接收验证码。", 420);
        var input = Input("邮箱", ref y, email?.Trim() ?? _email);
        var error = ErrorLabel(ref y);
        var submit = Button("发送验证码", ref y);
        Links(ref y, ("返回登录", () => ShowLogin(null)));
        FitPage();
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
        var y = Page("邮箱验证", notice ?? "验证码已发送，请查收邮件。");
        _content.Controls.Add(new Forms.Label { Text = "6 位验证码", Font = _smallFont, ForeColor = Muted,
            TextAlign = ContentAlignment.MiddleCenter, Bounds = new(0, y, ContentWidth, Scale(18)) });
        var code = new VerificationCodeInput { Font = _bodyFont,
            Bounds = new(0, y + Scale(22), ContentWidth, Scale(40)) };
        _content.Controls.Add(code);
        y += Scale(72);
        var error = ErrorLabel(ref y);
        var submit = Button(flow == CodeFlow.Login ? "验证并登录" : "验证并继续", ref y);
        Links(ref y, ("重新发送验证码", async () => await RunAsync(error, () => flow switch
        {
            CodeFlow.Login => _gate.LoginAsync(_email, _password, _lifetime.Token),
            CodeFlow.Register => _gate.StartRegistrationAsync(_email, _lifetime.Token),
            _ => _gate.RequestPasswordResetAsync(_email, _lifetime.Token),
        }, step =>
        {
            if (step.Kind is DesktopAuthStepKind.Failed) Fail(error, step);
            else if (step.Kind == DesktopAuthStepKind.SignedIn) AfterSignIn(step.Identity);
            else { error.ForeColor = Muted; error.Text = step.Message ?? "验证码已重新发送。"; }
        })), ("返回", () =>
        {
            if (flow == CodeFlow.Register) ShowRegister(null);
            else if (flow == CodeFlow.Reset) ShowResetRequest(_email);
            else ShowLogin(null);
        }));
        FitPage();
        submit.Click += async (_, _) =>
        {
            if (!Require(Regex.IsMatch(code.Value, "^[0-9]{6}$"), error, "请输入邮件中的 6 位数字验证码。")) { code.FocusFirstEmpty(); return; }
            var verifiedCode = code.Value;
            await RunAsync(error, () => flow switch
            {
                CodeFlow.Login => _gate.ConfirmLoginAsync(_email, _password, code.Value, _lifetime.Token),
                CodeFlow.Register => _gate.VerifyRegistrationCodeAsync(_email, verifiedCode, _lifetime.Token),
                _ => _gate.VerifyPasswordResetCodeAsync(_email, verifiedCode, _lifetime.Token),
            }, step =>
            {
                if (step.Kind == DesktopAuthStepKind.SignedIn) AfterSignIn(step.Identity);
                else if (step.Kind == DesktopAuthStepKind.CodeVerified && flow != CodeFlow.Login) ShowAccountDetails(flow, verifiedCode);
                else Fail(error, step);
            });
        };
        AcceptButton = submit;
        code.FocusFirstEmpty();
    }

    private void ShowAccountDetails(CodeFlow flow, string verifiedCode)
    {
        var register = flow == CodeFlow.Register;
        var y = Page(register ? "设置账户信息" : "设置新密码", "邮箱验证通过。", register ? 500 : 420);
        var display = register ? Input("昵称（2–32 个字符）", ref y, _displayName) : null;
        var password = Input(register ? "密码（至少 10 个字符）" : "新密码（至少 10 个字符）", ref y, "", secret: true);
        var confirm = Input(register ? "确认密码" : "确认新密码", ref y, "", secret: true);
        var error = ErrorLabel(ref y);
        var agreement = register ? Agreement(ref y, error) : null;
        var submit = Button(register ? "创建账户" : "修改密码", ref y);
        if (agreement is not null) BindAgreement(agreement, submit);
        Links(ref y, ("返回邮箱验证", () => ShowCode(flow, null)));
        FitPage();
        submit.Click += async (_, _) =>
        {
            if (agreement is not null && !Require(agreement.Checked, error, "请先阅读并同意软件许可和使用条款。")) return;
            if (register && !Require(display!.Text.Trim().Length is >= 2 and <= 32, error, "昵称需要 2–32 个字符。")) return;
            if (!Require(password.Text.Length is >= 10 and <= 128, error, "密码需要 10–128 个字符。")
                || !Require(password.Text == confirm.Text, error, "两次输入的密码不一致。")) return;
            if (register) _displayName = display!.Text.Trim();
            await RunAsync(error, () => register
                ? _gate.ConfirmRegistrationAsync(_email, verifiedCode, password.Text, _displayName, _lifetime.Token)
                : _gate.ConfirmPasswordResetAsync(_email, verifiedCode, password.Text, _lifetime.Token), step =>
            {
                if (register && step.Kind == DesktopAuthStepKind.SignedIn) AfterSignIn(step.Identity);
                else if (!register && step.Kind == DesktopAuthStepKind.PasswordChanged) { _password = ""; ShowLogin(step.Message); }
                else Fail(error, step);
            });
        };
        AcceptButton = submit;
        (display ?? password).Select();
    }

    private void ShowMessage(string heading, string message, params (string Text, Action Action)[] actions)
    {
        var y = Page(heading, message, messageHeight: 132);
        var x = 0;
        foreach (var (text, action) in actions)
        {
            var button = Button(text, ref y, x, Scale(150));
            button.Click += (_, _) => action();
            if (x == 0) AcceptButton = button;
            x += Scale(166);
            y -= Scale(54);
        }
        FitPage();
    }

    private void AfterSignIn(AccountIdentity? identity)
    {
        _password = "";
        Identity = identity;
        if (_mode == Mode.Interactive) { Outcome = Result.Granted; Close(); return; }
        RequestAccessCheck();
    }

    private void RequestAccessCheck()
    {
        Outcome = Result.Recheck;
        Close();
    }

    private void Finish(AccountIdentity? identity)
    {
        Identity ??= identity;
        Outcome = Result.Granted;
        Close();
    }

    // ---- Layout helpers (logical 96-dpi units, scaled by the current DPI) ----

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
        try
        {
            var step = await work();
            if (!IsDisposed && !_lifetime.IsCancellationRequested) Navigate(() => next(step));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (!IsDisposed && !_lifetime.IsCancellationRequested)
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
            UpdateAgreementButton();
        }
    }

    private void DragWindow(Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left) return;
        StopTransition();
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, 2, 0); // WM_NCLBUTTONDOWN, HTCAPTION
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9._-]{0,62}[a-z0-9])?$")] private static partial Regex LocalPart();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    // ---- Hosting ----

    /// <summary>Runs the window on its own STA message loop (WinUI is not running yet, or owns the UI thread).</summary>
    internal static Task<(Result Outcome, AccountIdentity? Identity)> RunAsync(IDesktopLoginGate gate, Mode mode,
        DesktopAccessResult? startupAccess = null)
    {
        var completion = new TaskCompletionSource<(Result, AccountIdentity?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
                Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.ThrowException, threadScope: true);
                using var window = new LoginWindow(gate, mode, startupAccess);
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
