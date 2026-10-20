using IDVBuff.Features.Accounts;
using IDVBuff.Lifecycle;
using Microsoft.UI.Dispatching;

namespace IDVBuff;

public partial class App
{
    private readonly CancellationTokenSource _accountLifetime = new();
    private DispatcherQueue? _accountDispatcher;
    private bool _returnToLogin;
    private int _exitStarted;

    private void StartAccountSessionMonitor()
    {
        if (!DesktopLoginGateHost.Gate.IsConfigured) return;
        _accountDispatcher = DispatcherQueue.GetForCurrentThread();
        AccountSession.Changed += DesktopAccountChanged;
        // Subscribe before reading: a loss between the startup gate and this subscription must not be missed.
        DesktopAccountChanged(null, EventArgs.Empty);
        _ = MonitorDesktopAccountAsync(_accountLifetime.Token);
    }

    private void DesktopAccountChanged(object? sender, EventArgs args)
    {
        if (AccountSession.Identity is not null && AccountSession.PublishToken is not null) return;
        _accountDispatcher?.TryEnqueue(DispatcherQueuePriority.High, ReturnToLogin);
    }

    private void ReturnToLogin()
    {
        if (IsApplicationStopping) return;
        _returnToLogin = true;
        RequestApplicationExit();
    }

    private async Task MonitorDesktopAccountAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                // The gate owns session rejection and its signed offline allowance. Network failures
                // must not manufacture a logout; a rejected current session raises AccountSession.Changed.
                try { await DesktopLoginGateHost.Gate.CheckAccessAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception exception) { WriteStartupTrace("Desktop account monitor check failed.", exception); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void StopAccountSessionMonitor()
    {
        _accountLifetime.Cancel();
        AccountSession.Changed -= DesktopAccountChanged;
    }

    private void StartLoginReturnProcess()
    {
        if (!_returnToLogin) return;
        try
        {
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            var executable = Path.Combine(AppContext.BaseDirectory, $"{typeof(App).Assembly.GetName().Name}.exe");
            using var next = System.Diagnostics.Process.Start(LoginReturnProcess.CreateStartInfo(executable,
                current.Id, current.StartTime.ToUniversalTime().Ticks, Program.IsDevelopmentInstance,
                Environment.GetCommandLineArgs()))
                ?? throw new InvalidOperationException("无法创建登录进程。");
        }
        catch (Exception exception)
        {
            WriteStartupTrace("Returning to login failed.", exception);
            System.Windows.Forms.MessageBox.Show($"无法打开登录页，请重新启动 Identity Vision Bridge。\n{exception.Message}",
                "Identity Vision Bridge", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
        }
    }
}
