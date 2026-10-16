#if IDVB_DEVELOPMENT_BUILD
namespace IDVBuff.Views;

public sealed partial class MainPage
{
    internal async Task<object> ReplayDevelopmentBloomAsync(CancellationToken cancellation)
    {
        if (!Program.IsDevelopmentInstance || App.IsSafeMode)
            throw new InvalidOperationException("光效回放要求正常完整开发客户端。");
        await App.ServicesReadyTask.WaitAsync(cancellation);
        await InitialReady.WaitAsync(cancellation);
        await App.MainWindowPresentationCompleted.WaitAsync(cancellation);
        await ActivateDevelopmentReplayWindowAsync(cancellation);
        await NavigateToAsync("home");
        var bloom = PageReplayDescendants(ModuleContentHost.Content as Microsoft.UI.Xaml.DependencyObject)
            .OfType<ScanModeBloom>().Single();
        var pixels = await bloom.ReplayDevelopmentPixelsAsync(cancellation);
        var texture = bloom.ReadDevelopmentTexture();
        var cache = _bloomTextureCache;
        await NavigateToAsync("main-settings");
        await Task.Delay(700, cancellation);
        var retired = bloom.ReadDevelopmentLifetime();
        if (retired.textureAlive || retired.tintAlive || bloom.IsLoaded)
            throw new InvalidOperationException("离开首页后视图仍持有光效资源。");
        var idle = cache.ReadDevelopmentState();
        if (!idle.textureAlive || idle.borrowers != 0 || idle.disposed)
            throw new InvalidOperationException("窗口光效缓存或租约寿命不一致。");
        await NavigateToAsync("home");
        var next = PageReplayDescendants(ModuleContentHost.Content as Microsoft.UI.Xaml.DependencyObject)
            .OfType<ScanModeBloom>().Single();
        var repeatedPixels = await next.ReplayDevelopmentPixelsAsync(cancellation);
        if (!ReferenceEquals(texture, next.ReadDevelopmentTexture()) || !ReferenceEquals(cache, _bloomTextureCache))
            throw new InvalidOperationException("重复进入同尺寸首页重新创建了中性纹理。");
        var window = ((App)Microsoft.UI.Xaml.Application.Current).MainWindow.AppWindow;
        var originalSize = window.Size;
        var originalPosition = window.Position;
        var presenter = window.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
        var maximized = presenter?.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
        var originalLayout = new { next.ActualWidth, next.ActualHeight,
            windowWidth = window.Size.Width, windowHeight = window.Size.Height,
            shellWidth = ActualWidth, contentWidth = ModuleContentHost.ActualWidth, scale = XamlRoot.RasterizationScale };
        object resizedWindowPixels;
        var windowGeometryChanged = false;
        try
        {
            if (maximized) presenter!.Restore();
            window.Resize(new Windows.Graphics.SizeInt32((int)Math.Ceiling((next.ActualWidth < 700 ? 2200 : 1100) * XamlRoot.RasterizationScale),
                Math.Max(800, originalSize.Height)));
            await Task.Delay(350, cancellation);
            IDVBuff.Core.Diagnostics.MemoryJournal.Current?.Record("bloom-window-resize", new {
                originalLayout, currentLayout = new { next.ActualWidth, next.ActualHeight,
                    windowWidth = window.Size.Width, windowHeight = window.Size.Height,
                    shellWidth = ActualWidth, contentWidth = ModuleContentHost.ActualWidth, scale = XamlRoot.RasterizationScale },
                presenterState = presenter?.State.ToString(), presentationComplete = App.MainWindowPresentationCompleted.IsCompletedSuccessfully }, durable: true);
            resizedWindowPixels = await next.ReplayDevelopmentPixelsAsync(cancellation);
            windowGeometryChanged = originalLayout.ActualWidth != next.ActualWidth || originalLayout.ActualHeight != next.ActualHeight;
            if (windowGeometryChanged && ReferenceEquals(texture, next.ReadDevelopmentTexture()))
                throw new InvalidOperationException("真实窗口改尺寸后仍复用旧尺寸纹理。");
        }
        finally
        {
            window.Resize(originalSize);
            window.Move(originalPosition);
            if (maximized) presenter!.Maximize();
        }
        await Task.Delay(350, cancellation);
        // The real desktop may clamp AppWindow to a viewport whose bloom stays
        // at its 560 DIP minimum. Cover the actual loaded control's SizeChanged
        // separately; do not report this fixture as window/DPI coverage.
        var originalWidth = next.Width;
        var originalHeight = next.Height;
        var beforeControlResize = next.ReadDevelopmentTexture();
        object resizedPixels;
        try
        {
            next.Width = originalWidth + 17.5;
            next.Height = originalHeight + 11.25;
            await Task.Delay(350, cancellation);
            resizedPixels = await next.ReplayDevelopmentPixelsAsync(cancellation);
            if (next.ActualWidth == originalWidth || next.ActualHeight == originalHeight
                || ReferenceEquals(beforeControlResize, next.ReadDevelopmentTexture()))
                throw new InvalidOperationException("真实控件尺寸事件没有更换中性纹理。");
        }
        finally { next.Width = originalWidth; next.Height = originalHeight; }
        await Task.Delay(350, cancellation);
        var restoredPixels = await next.ReplayDevelopmentPixelsAsync(cancellation);
        // Shell Unloaded can retire the owner before child Unloaded returns its
        // lease. The active child must still render until that lease is returned.
        cache.Dispose();
        var afterOwnerRetirementPixels = await next.ReplayDevelopmentPixelsAsync(cancellation);
        await NavigateToAsync("main-settings");
        await Task.Delay(700, cancellation);
        if (cache.ReadDevelopmentState().borrowers != 0)
            throw new InvalidOperationException("重复首页的纹理租约没有归还。");
        var disposed = cache.ReadDevelopmentState();
        if (disposed.textureAlive || disposed.borrowers != 0 || !disposed.disposed)
            throw new InvalidOperationException("缓存退役没有释放当前纹理。");
        return new { pixels, repeatedPixels, resizedWindowPixels, windowGeometryChanged,
            resizedPixels, restoredPixels, afterOwnerRetirementPixels,
            retired, idle, disposed, sameRaster = true, pid = Environment.ProcessId,
            meaning = "Native fresh-geometry pixel comparisons, actual repeat Home reuse and returned leases, then explicit cache retirement. Window intentionally owns one raster between pages; not proof of OS/driver page return or whole-client acceptance." };
    }
}
#endif
