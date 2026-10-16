#if IDVB_DEVELOPMENT_BUILD
using System.Runtime.InteropServices;
using IDVBuff.Core.Diagnostics;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class MainPage
{
    private Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap? _backgroundReplayRaster;

    private async Task ActivateDevelopmentReplayWindowAsync(CancellationToken cancellation)
    {
        if (!Program.IsDevelopmentInstance || App.IsSafeMode)
            throw new InvalidOperationException("窗口回放要求正常开发客户端。");
        var window = ((App)Microsoft.UI.Xaml.Application.Current).MainWindow;
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (IDVBuff.Lifecycle.BackgroundTestRuntime.Enabled)
        {
            window.AppWindow.Show(false);
            InvalidateMeasure(); UpdateLayout();
            if (_backgroundReplayRaster is null)
            {
                _backgroundReplayRaster = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                MemoryJournal.ObserveManagedObject(_backgroundReplayRaster, "Development.Background.XamlRaster");
            }
            var raster = _backgroundReplayRaster;
            await raster.RenderAsync(this);
            if (raster.PixelWidth <= 0 || raster.PixelHeight <= 0)
                throw new InvalidOperationException("后台 XAML 树未完成真实渲染。");
            MemoryJournal.Current?.Record("full-client-ui-background-render", new { handle = handle.ToInt64(),
                raster.PixelWidth, raster.PixelHeight, desktop = IDVBuff.Lifecycle.BackgroundTestRuntime.DesktopName,
                meaning = "Private desktop XAML raster; not interactive-desktop presentation/fluency acceptance" });
            return;
        }
        ReplayWindowNative.ShowWindow(handle, ReplayWindowNative.IsIconic(handle) ? 9 : 5);
        window.Activate();
        ReplayWindowNative.SetForegroundWindow(handle);
        if (ReplayWindowNative.GetForegroundWindow() != handle)
        {
            var currentThread = ReplayWindowNative.GetCurrentThreadId();
            var foregroundThread = ReplayWindowNative.GetWindowThreadProcessId(ReplayWindowNative.GetForegroundWindow(), out _);
            if (foregroundThread != 0 && foregroundThread != currentThread
                && ReplayWindowNative.AttachThreadInput(currentThread, foregroundThread, true))
            {
                try
                {
                    ReplayWindowNative.BringWindowToTop(handle);
                    ReplayWindowNative.SetActiveWindow(handle);
                    ReplayWindowNative.SetForegroundWindow(handle);
                }
                finally { ReplayWindowNative.AttachThreadInput(currentThread, foregroundThread, false); }
            }
        }
        var foreground = ReplayWindowNative.GetForegroundWindow();
        MemoryJournal.Current?.Record("full-client-ui-foreground", new { handle = handle.ToInt64(), foreground = foreground.ToInt64(),
            verified = foreground == handle, meaning = "Actual IDVB HWND activation before UI events; OG is separately activated before every capture." });
        if (foreground != handle) throw new InvalidOperationException("Windows 未允许 IDVB 回放窗口切到前台。");
        // Observe real presentation work after activation, rather than assuming
        // that an uploaded bitmap was also consumed by an occluded visual tree.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = 0;
        void Rendered(object? sender, object args) { if (++frames == 2) completion.TrySetResult(); }
        CompositionTarget.Rendering += Rendered;
        try
        {
            InvalidateMeasure();
            UpdateLayout();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation);
        }
        finally { CompositionTarget.Rendering -= Rendered; }
    }

    private static class ReplayWindowNative
    {
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window, int command);
        [DllImport("user32.dll")] internal static extern bool IsIconic(nint window);
        [DllImport("user32.dll")] internal static extern bool BringWindowToTop(nint window);
        [DllImport("user32.dll")] internal static extern nint SetActiveWindow(nint window);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
        [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint thread, uint attach, bool enabled);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    }
}
#endif
