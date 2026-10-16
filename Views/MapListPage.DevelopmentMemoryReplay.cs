#if IDVB_DEVELOPMENT_BUILD
using IDVBuff.Core.Diagnostics;
using IDVBuff.Diagnostics;
using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    // Runs inside the complete development GUI and its existing session. No
    // fixture repository, safe-mode bypass, separate business process, or GC.
    internal async Task<object> ReplayDevelopmentMemoryAsync(string mapClass, int floorCycles, int classSwitches, int editorCycles,
        CancellationToken cancellation, string? restoreFloor = null)
    {
        await InitialListReady.WaitAsync(cancellation);
        if (_classComboBox is null || _classEditButton is null || XamlRoot is null)
            throw new InvalidOperationException("地图列表尚未完成加载。");
        var classItem = _classComboBox.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, mapClass));
        _classComboBox.SelectedItem = classItem;
        await Task.Delay(300, cancellation);
        var original = _classProperties.TryGetValue(mapClass, out var properties) ? properties.ScanFloorKey : null;
        var maps = _loadedMaps.Where(map => string.Equals(map.Class, mapClass, StringComparison.OrdinalIgnoreCase)).ToArray();
        var floors = MapScanFloorRules.BuildOptions(maps).Where(option => option.IsEligible).Select(option => option.FloorIdentity).ToArray();
        if (restoreFloor is not null && !floors.Contains(restoreFloor, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("恢复楼层必须是此地图类可用于扫描的楼层。");
        if (floorCycles > 0 && floors.Length < 2) throw new InvalidOperationException("此地图类没有两个可用于扫描的楼层。");
        var runId = Guid.NewGuid();
        MemoryJournal.Current?.Record("full-client-replay-start", new { runId, build = BuildVersionInfo.BuildVersion,
            mapClass, originalScanFloor = original, restoreFloor, floorCycles, classSwitches, editorCycles,
            pid = Environment.ProcessId, fullApplication = true, safeMode = App.IsSafeMode,
            memoryLimitBytes = MemoryBudget.LimitBytes,
            restoration = "Completed/cancelled runs restore scan selection; budget termination may precede restoration. Original selection is recorded here." }, durable: true);
        try
        {
            for (var cycle = 0; cycle < floorCycles; cycle++)
            {
                cancellation.ThrowIfCancellationRequested();
                await ReplayScanFloorDialogAsync(floors[cycle % floors.Length], cancellation);
                ReplayCheckpoint(runId, "scan-floor-applied", cycle);
            }
            for (var cycle = 0; cycle < classSwitches; cycle++)
            {
                cancellation.ThrowIfCancellationRequested();
                _classComboBox!.SelectedIndex = (_classComboBox.SelectedIndex + 1) % _classComboBox.Items.Count;
                await Task.Delay(250, cancellation);
                if (cycle % 8 == 7) ReplayCheckpoint(runId, "class-switch", cycle);
            }
            _classComboBox!.SelectedItem = _classComboBox.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, mapClass));
            await Task.Delay(300, cancellation);
            for (var cycle = 0; cycle < editorCycles; cycle++)
            {
                cancellation.ThrowIfCancellationRequested();
                // The same edit entry used by card/toolbar events; cancel is
                // invoked through the actual visible WinUI button's provider.
                var map = _loadedMaps.First(map => string.Equals(map.Class, mapClass, StringComparison.OrdinalIgnoreCase));
                await EditMapAsync(map);
                await WaitReplayAsync(() => _modernEditorActive && _modernCanvas is { Children.Count: > 0 }, cancellation);
                await WaitReplayAsync(() => _modernBitmap is { IsReady: true }, cancellation);
                foreach (var floor in map.Floors)
                {
                    SwitchModernFloor(floor.Key, true);
                    await WaitReplayAsync(() => _modernBitmap is { IsReady: true }, cancellation);
                    await Task.Delay(300, cancellation);
                }
                ReplayCheckpoint(runId, "editor-floor", cycle);
                InvokeReplay(ReplayDescendants(this).OfType<Button>().Single(button => Equals(button.Content, "取消")));
                await WaitReplayAsync(() => !_modernEditorActive && _draft is null && _classComboBox is not null, cancellation);
                ReplayCheckpoint(runId, "editor-cancel", cycle);
            }
            return new { runId, build = BuildVersionInfo.BuildVersion, floorCycles, classSwitches, editorCycles,
                memory = ProcessMemoryReader.Read(), meaning = "Full client/UI/session replay. Not a live-game recognition acceptance." };
        }
        finally
        {
            if (!MemoryBudget.Process.Exceeded && _modernEditorActive)
            {
                InvokeReplay(ReplayDescendants(this).OfType<Button>().Single(button => Equals(button.Content, "取消")));
                await WaitReplayAsync(() => !_modernEditorActive && _draft is null && _classComboBox is not null, CancellationToken.None);
            }
            if (!MemoryBudget.Process.Exceeded && _classComboBox is not null)
            {
                _classComboBox.SelectedItem = _classComboBox.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, mapClass));
                if ((restoreFloor ?? original) is { } restore) await ReplayScanFloorDialogAsync(restore, CancellationToken.None);
                else
                {
                    await _repository.SetClassScanFloorAsync(mapClass, null);
                    await App.Session.RefreshMapCacheAsync();
                    await ShowListAsync();
                }
                MemoryJournal.Current?.Record("full-client-replay-restored", new { runId, mapClass, restoredScanFloor = restoreFloor ?? original }, durable: true);
            }
        }
    }

    private async Task ReplayScanFloorDialogAsync(string floor, CancellationToken cancellation)
    {
        var pending = ShowClassPropertiesDialogAsync();
        ContentDialog? dialog = null;
        try
        {
            await WaitReplayAsync(() => (dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot)
                .SelectMany(popup => ReplayDescendants(popup.Child)).OfType<ContentDialog>()
                .FirstOrDefault(item => Equals(item.Title, "地图类编辑"))) is not null, cancellation);
            var selector = ReplayDescendants(dialog!).OfType<ComboBox>().Single(combo => Equals(combo.Header, "用于扫描的楼层"));
            selector.SelectedItem = selector.Items.OfType<ComboBoxItem>().Single(item => item.IsEnabled && Equals(item.Tag, floor));
            await Task.Delay(150, cancellation);
            InvokeReplay(ReplayDescendants(dialog!).OfType<Button>().Single(button => button.Name == "PrimaryButton"));
            await pending.WaitAsync(TimeSpan.FromMinutes(2), cancellation);
            if (!_classProperties.TryGetValue(_selectedClass!, out var result) || !string.Equals(result.ScanFloorKey, floor, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("真实地图类属性应用未提交所选扫描楼层。");
        }
        catch
        {
            dialog?.Hide();
            throw;
        }
    }

    private static async Task WaitReplayAsync(Func<bool> ready, CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!ready())
        {
            cancellation.ThrowIfCancellationRequested();
            MemoryBudget.Process.ThrowIfExceeded();
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("客户端 UI 回放等待状态超时。");
            await Task.Delay(50, cancellation);
        }
    }

    private static void ReplayCheckpoint(Guid runId, string action, int iteration) => MemoryJournal.Current?.Record("full-client-replay-step",
        new { runId, action, iteration, reading = ProcessMemoryReader.Read(), managedLiveBytes = GC.GetTotalMemory(false),
            allocatedTotalBytes = GC.GetTotalAllocatedBytes(false) });

    private static void InvokeReplay(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static IEnumerable<DependencyObject> ReplayDescendants(DependencyObject? root)
    {
        if (root is null) yield break;
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in ReplayDescendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
#endif
