#if IDVB_DEVELOPMENT_BUILD
using IDVBuff.Core.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class MainPage
{
    internal async Task<object> CleanupDevelopmentCopyAsync(string receiptDirectory, CancellationToken cancellation)
    {
        if (!Program.IsDevelopmentInstance || App.IsSafeMode || App.Session.MatchSnapshot.IsStarted)
            throw new InvalidOperationException("副本清理要求正常开发客户端且已经结束对局。");
        await App.ServicesReadyTask.WaitAsync(cancellation);
        await ActivateDevelopmentReplayWindowAsync(cancellation);
        await NavigateToAsync("map-list");
        if (ModuleContentHost.Content is not MapListPage page) throw new InvalidOperationException("地图列表未加载。");
        return await page.CleanupDevelopmentCopyAsync(receiptDirectory, cancellation);
    }

    internal async Task<object> ReplayDevelopmentDataAsync(string mapClass, CancellationToken cancellation, int inspectionPauseSeconds = 0, bool copyOnly = false, bool exerciseEditor = false)
    {
        if (!Program.IsDevelopmentInstance || App.IsSafeMode || App.Session.MatchSnapshot.IsStarted)
            throw new InvalidOperationException("数据操作回放要求正常开发客户端且已经结束对局。");
        await App.ServicesReadyTask.WaitAsync(cancellation);
        await ActivateDevelopmentReplayWindowAsync(cancellation);
        await NavigateToAsync("map-list", initialMapClass: mapClass);
        if (ModuleContentHost.Content is not MapListPage page) throw new InvalidOperationException("地图列表未加载。");
        return await page.ReplayDevelopmentDataAsync(mapClass, cancellation, inspectionPauseSeconds, copyOnly, exerciseEditor);
    }

    internal async Task<object> ReplayDevelopmentMemoryAsync(string mapClass, int floorCycles, int classSwitches, int editorCycles,
        CancellationToken cancellation, string? restoreFloor = null)
    {
        if (!Program.IsDevelopmentInstance || App.IsSafeMode)
            throw new InvalidOperationException("内存客户端回放要求正常模式的开发版 IDVB。");
        await App.ServicesReadyTask.WaitAsync(cancellation);
        await ActivateDevelopmentReplayWindowAsync(cancellation);
        await NavigateToAsync("map-list", initialMapClass: mapClass);
        if (ModuleContentHost.Content is not MapListPage page)
            throw new InvalidOperationException("地图列表未进入真实主窗口。");
        return await page.ReplayDevelopmentMemoryAsync(mapClass, floorCycles, classSwitches, editorCycles, cancellation, restoreFloor);
    }
}
#endif
