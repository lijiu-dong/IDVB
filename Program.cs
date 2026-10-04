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

        // Lifecycle hooks and secondary processes have already exited. Only the primary
        // normal GUI owns usage accounting; CLI and isolated diagnostics do not contribute.
        using var usage = !isCli && !isIsolatedDevelopmentInstance
            ? ApplicationUsageTracker.Current
            : null;
        usage?.Start();

        StartupTimeline.Write($"Launch mode: cli={isCli}; isolatedDevelopment={isIsolatedDevelopmentInstance}.");
        if (!isCli)
        {
            var startupPreferences = MainProgramPreferences.Load();
            StartupSplash.Configure(startupPreferences.SafeMode);
            if (!startupPreferences.StartMinimized)
                StartupSplash.Show();
        }
        try
        {
            return RunWinUi();
        }
        finally
        {
            StartupTimeline.StopSampling();
            StartupSplash.Close();
            // Commit usage before releasing the primary mutex to the next process.
            usage?.Dispose();
            _guiInstance?.Dispose();
        }
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
            StartupTimeline.StartSampling(action => dispatcher.TryEnqueue(() => action()));
            StartupTimeline.Write("App construction begin (includes Application base constructor).");
            _ = new App();
            StartupTimeline.Write("App construction complete.");
        });
        return Environment.ExitCode;
    }
}
