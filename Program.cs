using IDVBuff.Lifecycle;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;
using IDVBuff.Diagnostics;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using IDVBuff.Features.Maps;
using System.Runtime.InteropServices;

namespace IDVBuff;

public static class Program
{
    private static GuiInstanceCoordinator? _guiInstance;
    internal static bool IsDevelopmentInstance { get; private set; }
    private static MemoryDiagnosticsRuntime? _memoryDiagnostics;
    private static DispatcherQueue? _guiDispatcher;

    [STAThread]
    public static int Main(string[] args)
    {
#if IDVB_DEVELOPMENT_BUILD
        // Direct EXE launches and dotnet run must follow the same policy as the launcher.
        args = [.. args, "--isolated-dev-instance"];
        if (!HasCompletedDevelopmentBuild())
        {
            MessageBoxW(0, "开发构建尚未完成或构建记录与当前 DLL 不匹配。请重新运行 dotnet build。",
                "Identity Vision Bridge", 0x30);
            return 2;
        }
#endif
        IsDevelopmentInstance = args.Any(argument =>
            string.Equals(argument, "--isolated-dev-instance", StringComparison.OrdinalIgnoreCase));
#if IDVB_DEVELOPMENT_BUILD
        try
        {
            AppDataPaths.ConfigureMemoryTestRoot(args);
            BackgroundTestRuntime.Initialize(args.Any(argument => argument.Equals("--idvb-background-test", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException
            or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
#endif
        var mainEntered = Stopwatch.GetTimestamp();
        var mainUtc = DateTimeOffset.UtcNow;
        // Velopack lifecycle processing must precede WinUI, logging, DI, and
        // single-instance work. Fast-exit hooks can terminate this process.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnFirstRun(_ => UpdateLifecycleState.RecordCurrentVelopackInstall())
            .OnRestarted(_ =>
            {
                UpdateLifecycleState.WasRestartedAfterUpdate = true;
                UpdateLifecycleState.RecordCurrentVelopackInstall();
            })
            .Run();

        var lifecycleCompleted = Stopwatch.GetTimestamp();
        if (MapRuntimeSettingsRepository.IsLogCollectionEnabled())
            StartupTimeline.Initialize(mainEntered, mainUtc, lifecycleCompleted);
        return RunApplication(args);
    }

    private static bool HasCompletedDevelopmentBuild()
    {
        try
        {
            using var receipt = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, ".idvb-build.json")));
            return receipt.RootElement.GetProperty("Schema").GetInt32() == 2
                && receipt.RootElement.GetProperty("BuildVersion").GetString() == BuildVersionInfo.BuildVersion;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException)
        {
            return false;
        }
    }

    // Keep WinUI and application type resolution out of the entry-point JIT.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApplication(string[] args)
    {
        bool returnToLogin;
        try { returnToLogin = LoginReturnProcess.WaitForPreviousProcess(args); }
        catch (Exception exception)
        {
            MessageBoxW(0, exception.Message, "Identity Vision Bridge", 0x10);
            return 2;
        }
        StartupTimeline.Write("Legacy launch redirect check begin (includes app-data path resolution).");
        if (UpdateLifecycleState.TryRedirectLegacyLaunch(args))
        {
            StartupTimeline.Write("Legacy launch redirected; exiting this process.");
            return 0;
        }
        StartupTimeline.Write("Legacy launch redirect check complete.");

        var isCli = args.Any(argument =>
            string.Equals(argument, "--cli", StringComparison.OrdinalIgnoreCase));
        var isIsolatedDevelopmentInstance = args.Any(argument =>
            string.Equals(argument, "--isolated-dev-instance", StringComparison.OrdinalIgnoreCase));
        var startupPreferences = !isCli ? MainProgramPreferences.Load() : null;
        if (startupPreferences is { SafeMode: false })
        {
            // Elevate before login, single-instance ownership, or runtime initialization.
            // Safe mode and Velopack's fast-exit hooks must remain usable without UAC.
            try
            {
                // CLI can also be selected by --command, --script, or attach options.
                if (!Cli.CliLaunchOptions.Parse(args).IsCli && AdministratorStartup.TryRelaunch(args))
                    return 0;
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                MessageBoxW(0, $"无法以管理员权限启动 Identity Vision Bridge：{exception.Message}",
                    "Identity Vision Bridge", 0x10);
                return 2;
            }
        }
        if (!isCli && !isIsolatedDevelopmentInstance)
        {
            StartupTimeline.Write("GUI instance coordination begin.");
            _guiInstance = new GuiInstanceCoordinator();
            if (!_guiInstance.TryAcquirePrimary())
            {
                StartupTimeline.Write("Secondary instance: notifying primary instance.");
                _guiInstance.NotifyPrimaryInstance();
                StartupTimeline.Write("Primary notification complete; secondary instance exits.");
                _guiInstance.Dispose();
                _guiInstance = null;
                return 0;
            }
            _guiInstance.StartListening();
        }
        else if (!isCli && isIsolatedDevelopmentInstance)
        {
            StartupTimeline.Write("Dev GUI instance coordination begin.");
            _guiInstance = new GuiInstanceCoordinator(isDevelopmentInstance: true);
            if (!_guiInstance.TryAcquirePrimary())
            {
                StartupTimeline.Write("Secondary dev instance: notifying primary instance.");
                if (!_guiInstance.NotifyPrimaryInstance())
                {
                    ShowDevelopmentInstanceConflict();
                    _guiInstance.Dispose();
                    _guiInstance = null;
                    return 2;
                }
                StartupTimeline.Write("Primary notification complete; secondary dev instance exits.");
                _guiInstance.Dispose();
                _guiInstance = null;
                return 0;
            }
            _guiInstance.StartListening();
        }

        // Each primary GUI is real application usage, including the development launcher.
        // The tracker merges concurrent lifetimes; lifecycle hooks, secondaries and CLI exit earlier.
        using var usage = !isCli
            ? ApplicationUsageTracker.Current
            : null;
        usage?.Start();

        if (!isCli)
        {
            try
            {
                _memoryDiagnostics = new MemoryDiagnosticsRuntime(AppDataPaths.RootDirectory,
                    BuildVersionInfo.BuildVersion, HandleMemoryFailure);
                _memoryDiagnostics.ConfigureNativeBackingReturns(App.CanReturnUnusedNativeBacking);
            }
            catch (Exception exception)
            {
                MessageBoxW(0, $"无法启动内存诊断，IDVB 已停止：{exception.Message}",
                    "Identity Vision Bridge · 内存诊断", 0x10);
                return 3;
            }
        }

        StartupTimeline.Write($"Launch mode: cli={isCli}; isolatedDevelopment={isIsolatedDevelopmentInstance}.");
        try
        {
            if (startupPreferences is not null)
            {
                var connectToServer = Features.Accounts.DesktopLoginGateHost.Gate.IsConfigured;
                StartupSplash.Configure(startupPreferences.SafeMode, connectToServer);
                StartupSplash.Report(connectToServer ? "正在连接服务器…" : "正在准备界面…");
                if (!returnToLogin && !startupPreferences.StartMinimized) StartupSplash.Show();
                if (!PassDesktopLoginGate(returnToLogin, !startupPreferences.StartMinimized)) return 0;
            }
            return RunWinUi();
        }
        finally
        {
            StopMemoryDiagnostics();
            StartupTimeline.StopSampling();
            StartupSplash.Close();
            // Commit usage before releasing the primary mutex to the next process.
            usage?.Dispose();
            _guiInstance?.Dispose();
        }
    }

    /// <summary>
    /// 1.7.1-preview.3: before any WinUI work, the server must confirm this account may start IDVB on this
    /// installation (or a signed offline allowance must still be valid). The public build has no gate.
    /// </summary>
    private static bool PassDesktopLoginGate(bool returnToLogin, bool showStartupSplash)
    {
        var gate = Features.Accounts.DesktopLoginGateHost.Gate;
        if (!gate.IsConfigured) return true;
        Features.Accounts.AccountSession.InteractiveLogin = async _ =>
        {
            var (outcome, identity) = await Lifecycle.LoginWindow.RunAsync(gate, Lifecycle.LoginWindow.Mode.Interactive).ConfigureAwait(false);
            return outcome == Lifecycle.LoginWindow.Result.Granted && identity is not null
                ? identity : throw new OperationCanceledException("登录已取消。");
        };
        try
        {
            if (returnToLogin)
            {
                var (outcome, _) = Lifecycle.LoginWindow.RunAsync(gate, Lifecycle.LoginWindow.Mode.Startup,
                    new(Core.Contracts.Online.DesktopAccessOutcome.LoginRequired)).GetAwaiter().GetResult();
                if (outcome != Lifecycle.LoginWindow.Result.Recheck) return false;
            }
            while (true)
            {
                StartupSplash.Report("正在连接服务器…");
                RestoreStartupSplash(showStartupSplash);
                Core.Contracts.Online.DesktopAccessResult access;
                try { access = gate.CheckAccessAsync().GetAwaiter().GetResult(); }
                catch (Exception exception)
                {
                    StartupTimeline.Write("Desktop access check failed.", exception);
                    access = new(Core.Contracts.Online.DesktopAccessOutcome.Unavailable, "连接服务器时出现错误。");
                }
                StartupTimeline.Write($"Desktop access outcome: {access.Outcome}.");
                if (access.Outcome is Core.Contracts.Online.DesktopAccessOutcome.Granted or Core.Contracts.Online.DesktopAccessOutcome.NotConfigured)
                {
                    StartupSplash.Complete(StartupSplash.Stage.Connection);
                    return true;
                }
                StartupSplash.SetVisibleAsync(false).GetAwaiter().GetResult();
                var (outcome, _) = Lifecycle.LoginWindow.RunAsync(gate, Lifecycle.LoginWindow.Mode.Startup, access).GetAwaiter().GetResult();
                StartupTimeline.Write($"Desktop login gate: {outcome}.");
                if (outcome == Lifecycle.LoginWindow.Result.Recheck) continue;
                if (outcome != Lifecycle.LoginWindow.Result.Granted) return false;
                StartupSplash.Complete(StartupSplash.Stage.Connection);
                RestoreStartupSplash(showStartupSplash);
                return true;
            }
        }
        catch (Exception exception)
        {
            StartupTimeline.Write("Desktop login gate failed.", exception);
            MessageBoxW(0, $"无法打开登录窗口：{exception.Message}", "Identity Vision Bridge", 0x10);
            return false;
        }
    }

    private static void RestoreStartupSplash(bool enabled)
    {
        if (!enabled) return;
        // Returning from logout starts with login only: there may be no splash to unhide yet.
        // Show is idempotent, so normal login continues to reuse its existing message loop.
        StartupSplash.Show();
        StartupSplash.SetVisibleAsync(true).GetAwaiter().GetResult();
    }

    internal static void StopMemoryDiagnostics() => Interlocked.Exchange(ref _memoryDiagnostics, null)?.Dispose();
    internal static void SuspendNativeBackingReturns() => Volatile.Read(ref _memoryDiagnostics)?.SuspendNativeBackingReturns();

    private static void HandleMemoryFailure(string message)
    {
        Environment.ExitCode = 3;
        // Install the deadline before any UI calls that may themselves fail.
        new Thread(() =>
        {
            Thread.Sleep(5000);
            Environment.Exit(3);
        }) { IsBackground = true, Name = "IDVB memory shutdown deadline" }.Start();
        // An error dialog must not keep recognition running until the user clicks OK.
        new Thread(() => MessageBoxW(0, message, "Identity Vision Bridge · 内存错误", 0x10))
            { IsBackground = true, Name = "IDVB memory error" }.Start();
        try { _guiDispatcher?.TryEnqueue(DispatcherQueuePriority.High, App.ExitForMemoryFailure); }
        catch (Exception exception) { Debug.WriteLine(exception); }
    }

    private static void ShowDevelopmentInstanceConflict() =>
        MessageBoxW(0, "另一个开发构建仍在运行。请关闭旧开发进程后重新启动。",
            "Identity Vision Bridge", 0x30);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint window, string text, string caption, uint type);

    // Resolve/JIT WinUI only after the independent splash thread has been started.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunWinUi()
    {
        StartupSplash.Report("正在准备界面…");
        StartupTimeline.Write("COM wrappers initialization begin.");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        StartupTimeline.Write("COM wrappers initialized; Application.Start begin.");
        Application.Start(initialization =>
        {
            StartupTimeline.Write("Application.Start callback entered.");
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            _guiDispatcher = dispatcher;
            StartupTimeline.StartSampling(action => dispatcher.TryEnqueue(() => action()));
            StartupTimeline.Write("App construction begin (includes Application base constructor).");
            _ = new App();
            StartupTimeline.Write("App construction complete.");
        });
        return Environment.ExitCode;
    }
}
