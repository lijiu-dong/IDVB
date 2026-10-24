using IDVBuff.Features.Maps;
using IDVBuff.Core.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private bool _classRoutePlanningBusy;

    private async Task ShowClassRoutePlanningAsync()
    {
        if (_isPackageOperation || _classRoutePlanningBusy || _modernEditorActive || string.IsNullOrWhiteSpace(_selectedClass)) return;
        _classRoutePlanningBusy = true;
        SetClassEditBusy(true);
        using var cancellation = new CancellationTokenSource();
        var detached = false;
        ContentDialog? batchDialog = null;
        RoutedEventHandler unloaded = (_, _) => { detached = true; cancellation.Cancel(); batchDialog?.Hide(); };
        Unloaded += unloaded;
        var saved = 0; var attempted = 0; var skipped = 0;
        var failures = new List<string>();
        try
        {
            var className = _selectedClass;
            using var batchMemory = MemoryJournal.BeginOperation("RoutePlanning.Class", className);
            IReadOnlyList<MapRouteGateEndpoint>? choices = null;
            var specialTeleportFloors = new HashSet<string>();
            var maps = await RunMapOperationAsync("正在准备快速路线规划……", () => Task.Run(async () =>
            {
                var snapshot = await _repository.GetClassCatalogSnapshotAsync(className);
                var classMaps = snapshot.Maps.ToArray();
                if (classMaps.Length == 0) throw new InvalidOperationException("当前地图类没有地图。");
                for (var index = 0; index < classMaps.Length; index++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var map = classMaps[index];
                    MapOperationProgress.Report((double)index / classMaps.Length,
                        $"正在读取通路…… {index + 1} / {classMaps.Length} · {map.DisplayName}");
                    try
                    {
                        var metadata = await ReadRoutePlanningMetadataAsync(map, cancellation.Token);
                        specialTeleportFloors.UnionWith(metadata.TeleportFloors);
                        if (metadata.Gates.Count > (choices?.Count ?? 0)) choices = metadata.Gates;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception)
                    {
                        MemoryJournal.Current?.Record("route-preflight-failed", new { map.Id, map.DisplayName, exception.Message });
                    }
                    MapOperationProgress.Report((double)(index + 1) / classMaps.Length, "正在准备规划选项……");
                }
                return classMaps;
            }, cancellation.Token), cancellation);
            cancellation.Token.ThrowIfCancellationRequested();
            if (detached) return;
            if (choices is null) throw new InvalidOperationException("当前地图类没有具备通路数据和出入口门标志的地图。");
            var controls = CreateRoutePlanningControls(choices,
                maps.SelectMany(m => MapFloorRules.GetOrderedFloors(m)).Select(f => f.Key).Distinct(), specialTeleportFloors);
            var notice = (TextBlock)controls.Content.Children[^1];
            notice.Text = $"将对“{className}”的全部 {maps.Length} 张地图生成并保存路线，替换上次规划，保留手工标注。取消会保留已完成的地图。";
            using var operationProgress = new MapOperationProgressView("正在规划路线……") { Visibility = Visibility.Collapsed };
            var host = CreateRoutePlanningHost(controls.Content, operationProgress);
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "快速路线规划", Content = host,
                PrimaryButtonText = "生成全部路线", CloseButtonText = "取消" };
            batchDialog = dialog;
            var running = false; var completed = false;
            dialog.CloseButtonClick += (_, args) =>
            {
                cancellation.Cancel();
                // Wait for the active map's atomic commit before closing.
                if (running) { args.Cancel = true; dialog.CloseButtonText = "正在取消…"; }
            };
            dialog.Closing += (_, args) =>
            {
                if (!running) return;
                cancellation.Cancel(); args.Cancel = true; dialog.CloseButtonText = "正在取消…";
            };
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                // Keep the dialog open without holding a button deferral: WinUI
                // suppresses close-button events while that deferral is pending.
                // The independent task closes the dialog only after it stops.
                args.Cancel = true;
                if (running) return;
                if (controls.Entrance.SelectedItem is not MapRouteGateEndpoint from
                    || controls.Exit.SelectedItem is not MapRouteGateEndpoint to
                    || controls.DenseRoutes.IsChecked is not true && from.Id == to.Id)
                {
                    controls.Status.Text = "请选择不同的入口和出口。";
                    return;
                }
                running = true; dialog.IsPrimaryButtonEnabled = false; operationProgress.Visibility = Visibility.Visible;
                using var progressScope = MapOperationProgress.Begin(operationProgress);
                host.Children.OfType<ScrollViewer>().Single().IsEnabled = false;
                try
                {
                    for (var index = 0; index < maps.Length; index++)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        var map = maps[index]; var current = index;
                        MapOperationProgress.Report(.95 * index / maps.Length, $"正在读取通路…… {map.DisplayName}");
                        attempted++;
                        controls.Status.Text = $"{index + 1} / {maps.Length} · {map.DisplayName} · 正在读取通路…";
                        try
                        {
                            var progress = new Progress<double>(value =>
                            {
                                if (!running || cancellation.IsCancellationRequested || attempted != current + 1) return;
                                operationProgress.Report(new(.95 * (current + Math.Clamp(value, 0, .9)) / maps.Length,
                                    $"正在搜索路线…… {map.DisplayName}"));
                                controls.Status.Text = $"{current + 1} / {maps.Length} · {map.DisplayName} · 正在搜索路线…";
                            });
                            skipped += await PlanAndSaveClassMapAsync(map, controls, progress, index, maps.Length, cancellation.Token);
                            saved++;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception exception)
                        {
                            MemoryJournal.Current?.Record("route-map-failed", new { map.Id, map.DisplayName, exception.Message });
                            failures.Add($"{map.DisplayName}：{exception.Message}");
                        }
                        MapOperationProgress.Report(.95 * (index + 1) / maps.Length, "正在完成路线规划……");
                    }
                    completed = true;
                    MapOperationProgress.Report(.96, "正在刷新地图……");
                    if (saved > 0 && !App.IsSafeMode) await MapOperationProgress.RunAsync(.96, .99, () => App.Session.RefreshMapCacheAsync());
                    if (failures.Count == 0) operationProgress.Complete();
                }
                catch (OperationCanceledException) { }
                catch (Exception exception) { failures.Add(exception.Message); }
                finally
                {
                    running = false; dialog.Hide();
                }
            };
            await dialog.ShowThemedAsync(this);
            if (attempted == 0 || detached) return;
            if (!completed && saved > 0 && !App.IsSafeMode)
                await RunMapOperationAsync("正在刷新已保存路线……", () => App.Session.RefreshMapCacheAsync());
            await ShowListAsync();
            await ShowMessageAsync(completed ? "批量路线规划完成" : "批量路线规划已取消",
                $"已保存 {saved} / {maps.Length} 张地图，失败 {failures.Count} 张。未覆盖目标共 {skipped} 个（受连通性和短支路限制）。"
                + (failures.Count == 0 ? "" : "\n" + string.Join("\n", failures)));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!detached) await ShowMessageAsync("无法快速规划路线", exception.Message); }
        finally { Unloaded -= unloaded; _classRoutePlanningBusy = false; SetClassEditBusy(false); }
    }

    // Keep large per-map fields out of the batch/dialog's async state machine.
    // Failure, cancellation and success all finish this owner before the next map.
    private async Task<int> PlanAndSaveClassMapAsync(MapRecord map, RoutePlanningControls controls,
        IProgress<double> progress, int index, int total, CancellationToken token)
    {
        using var memory = MemoryJournal.BeginOperation("RoutePlanning.Map", map.Id.ToString());
        var draft = await Task.Run(() => _repository.CreateRoutePlanningDraft(map), token);
        using var draftOwner = MemoryJournal.Own("RoutePlanning.CurrentDraft");
        var navigation = await _coCreationTemplates.LoadNavigationForDraftAsync(draft, token)
            ?? throw new InvalidOperationException("地图没有可用的通路数据。");
        using var navigationOwner = MemoryJournal.Own("RoutePlanning.CurrentNavigation");
        using (MemoryJournal.BeginOperation("RoutePlanning.ValidateImages"))
            await Task.Run(() => MapNavigationRules.ValidateImagesAsync(navigation, draft.FloorPaths, token), token);
        var options = controls.ReadOptions(navigation, MapRouteGateEndpoint.FromDraft(draft, navigation));
        var planned = await Task.Run(() =>
        {
            using var search = MemoryJournal.BeginOperation("RoutePlanning.Search", map.Id.ToString());
            var route = MapExplorationRoutePlanner.Find(navigation, options, token, progress);
            return (Route: route, Annotations: MapExplorationRouteDrawing.Create(navigation, route, token));
        }, token);
        controls.Status.Text = $"{index + 1} / {total} · {map.DisplayName} · 正在保存…";
        using (MemoryJournal.BeginOperation("RoutePlanning.Save", map.Id.ToString()))
        using (MapOperationProgress.Range(.95 * (index + .9) / total, .95 * (index + 1) / total))
            await Task.Run(() => _repository.SavePlannedRouteAsync(draft, navigation, planned.Annotations, token), token);
        return planned.Route.SkippedTargetRoomIds.Count;
    }

    private async Task<(IReadOnlyList<MapRouteGateEndpoint> Gates, string[] TeleportFloors)>
        ReadRoutePlanningMetadataAsync(MapRecord map, CancellationToken token)
    {
        using var memory = MemoryJournal.BeginOperation("RoutePlanning.Preflight", map.Id.ToString());
        var draft = _repository.CreateRoutePlanningDraft(map);
        using var draftOwner = MemoryJournal.Own("RoutePlanning.PreflightDraft");
        var navigation = await _coCreationTemplates.LoadNavigationForDraftAsync(draft, token);
        using var navigationOwner = navigation is null ? null : MemoryJournal.Own("RoutePlanning.PreflightNavigation");
        return navigation is null ? (Array.Empty<MapRouteGateEndpoint>(), Array.Empty<string>())
            : (MapRouteGateEndpoint.FromDraft(draft, navigation), MapSpecialRoomTeleports.RequiredFloors(navigation).ToArray());
    }
}
