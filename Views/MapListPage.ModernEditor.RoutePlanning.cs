using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private ContentDialog? _modernRoutePlanningDialog;
    private void AddModernRouteAnnotation(MapAnnotation annotation, Color color)
    {
        var planned = annotation.RoutePlanId.HasValue;
        var stroke = planned ? Math.Min(3, 3 * (_modernCanvas?.Width ?? 1280) * ModernZoomFactor / 1280) : 3;
        if (planned) AddModernRoutePolyline(annotation.Points, Color.FromArgb(240, 10, 15, 22), stroke * 5 / 3);
        AddModernRoutePolyline(annotation.Points, color, stroke);
    }

    private Button CreateModernRoutePlanningButton()
    {
        var button = new Button { Content = new TextBlock { Text = "路线规划", FontSize = 10 },
            Width = 56, Height = 42, Padding = new Thickness(2), Tag = "route-planning" };
        button.Click += async (_, _) => await ShowModernRoutePlanningAsync();
        return button;
    }

    private async Task ShowModernRoutePlanningAsync()
    {
        if (_draft is not { } draft || !_modernEditorActive || _modernRouteBusy) return;
        using var cancellation = new CancellationTokenSource();
        _modernAutomaticRouteCancellation = cancellation; _modernRouteBusy = true;
        try
        {
            var loaded = await Task.Run(() => _coCreationTemplates.LoadNavigationForDraftAsync(draft, cancellation.Token), cancellation.Token);
            if (loaded is null) throw new InvalidOperationException("这个地图没有可用的房间、门框和通道数据。");
            await Task.Run(() => MapNavigationRules.ValidateImagesAsync(loaded, draft.FloorPaths, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(draft, _draft) || !_modernEditorActive) return;
            var navigation = loaded.Clone();
            var gates = MapRouteGateEndpoint.FromDraft(draft, navigation);
            if (gates.Count < 2) throw new InvalidOperationException("至少需要两个已标记的 1F 大门、侧门或 2F 单门才能规划路线。");
            var controls = CreateRoutePlanningControls(gates, navigation.Floors.Select(f => f.FloorKey),
                MapSpecialRoomTeleports.RequiredFloors(navigation));
            var content = controls.Content; var entrance = controls.Entrance; var exit = controls.Exit;
            var coverage = controls.Coverage; var preference = controls.Preference;
            var floors = controls.Floors; var status = controls.Status;
            var progressHost = new ContentControl { Visibility = Visibility.Collapsed };
            var inputHost = CreateRoutePlanningHost(content, progressHost);
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "路线规划", PrimaryButtonText = "生成路线", CloseButtonText = "取消", Content = inputHost };
            MapExplorationRoute? planned = null;
            IReadOnlyDictionary<string, List<MapAnnotation>>? annotations = null;
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral(); args.Cancel = true;
                dialog.IsPrimaryButtonEnabled = false; inputHost.Children.OfType<ScrollViewer>().Single().IsEnabled = false;
                using var operationProgress = new MapOperationProgressView("正在规划路线……");
                progressHost.Content = operationProgress;
                progressHost.Visibility = Visibility.Visible;
                try
                {
                    if (entrance.SelectedItem is not MapRouteGateEndpoint from || exit.SelectedItem is not MapRouteGateEndpoint to) return;
                    var options = controls.ReadOptions(navigation, gates);
                    status.Text = "正在检查连通性并搜索主路线、短支路和房间标记…";
                    var progress = new Progress<double>(fraction => operationProgress.Report(
                        new(.95 * Math.Clamp(fraction, 0, 1), "正在搜索路线……")));
                    (planned, annotations) = await Task.Run(() =>
                    {
                        var route = MapExplorationRoutePlanner.Find(navigation, options, cancellation.Token, progress);
                        return (route, MapExplorationRouteDrawing.Create(navigation, route, cancellation.Token));
                    }, cancellation.Token);
                    if (cancellation.IsCancellationRequested || !ReferenceEquals(draft, _draft) || !_modernEditorActive) return;
                    operationProgress.Complete();
                    args.Cancel = false;
                }
                catch (OperationCanceledException) { if (ReferenceEquals(_modernRoutePlanningDialog, dialog)) status.Text = "规划已取消。"; }
                catch (Exception exception) { if (ReferenceEquals(_modernRoutePlanningDialog, dialog)) status.Text = exception.Message; }
                finally { inputHost.Children.OfType<ScrollViewer>().Single().IsEnabled = true; dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
            };
            dialog.CloseButtonClick += (_, _) => cancellation.Cancel();
            // Close/reset runs on the UI thread. Do not marshal dialog.Hide as a
            // WinRT delegate from a cancellation callback (it can throw at Cancel).
            _modernRoutePlanningDialog = dialog;
            var result = await dialog.ShowThemedAsync(this);
            if (ReferenceEquals(_modernRoutePlanningDialog, dialog)) _modernRoutePlanningDialog = null;
            if (result != ContentDialogResult.Primary || annotations is null || planned is null
                || cancellation.IsCancellationRequested || !ReferenceEquals(draft, _draft) || !_modernEditorActive) return;
            // Resolve every destination before writing anything: no half-applied
            // route when a floor disappeared while the dialog was open.
            var profiles = draft.Floors.Select(f => f.Key).Union(annotations.Keys).ToDictionary(key => key, key => draft.Recognition.GetFloor(key)
                ?? throw new InvalidOperationException($"楼层 {key} 已不存在。"));
            var previousNavigation = draft.Navigation;
            var replacedIds = MapPlannedRouteReplacement.FindAnnotationIds(navigation,
                profiles.ToDictionary(pair => pair.Key, pair => pair.Value.Annotations));
            var replaced = profiles.ToDictionary(pair => pair.Key, pair => pair.Value.Annotations
                .Select((annotation, index) => (Annotation: annotation, Index: index)).Where(item => replacedIds.Contains(item.Annotation.Id)).ToArray());
            draft.Navigation = navigation;
            foreach (var profile in profiles.Values) profile.Annotations.RemoveAll(a => replacedIds.Contains(a.Id));
            foreach (var (floor, items) in annotations)
            {
                profiles[floor].Annotations.AddRange(items);
            }
            RecordModernCreation("已撤销整套规划路线。", () =>
            {
                var addedIds = annotations.Values.SelectMany(a => a).Select(a => a.Id).ToHashSet();
                foreach (var (floor, profile) in profiles)
                {
                    profile.Annotations.RemoveAll(a => addedIds.Contains(a.Id));
                    foreach (var old in replaced[floor]) profile.Annotations.Insert(Math.Min(old.Index, profile.Annotations.Count), old.Annotation);
                }
                if (ReferenceEquals(draft.Navigation, navigation)) draft.Navigation = previousNavigation;
            });
            UpdateMarkerConfirmState(); RefreshModernLayerList(); RenderModernEditor();
            var skipped = planned.SkippedTargetRoomIds.Select(id => navigation.Modules.Single(m => m.Id == id).DisplayName).ToArray();
            SetModernStatus($"已生成 {planned.Stages.Count} 个阶段、{planned.Branches.Count} 条短支路，经过 {planned.VisitedRoomIds.Count} 个房间。"
                + (skipped.Length == 0 ? "可在图层中编辑，或整体撤销。" : $"受连通性和短支路限制，未覆盖 {skipped.Length} 个目标：{string.Join("、", skipped)}。"), false);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (_modernEditorActive && ReferenceEquals(draft, _draft)) SetModernStatus(exception.Message, true); }
        finally
        {
            if (ReferenceEquals(_modernAutomaticRouteCancellation, cancellation))
            {
                _modernRoutePlanningDialog = null;
                _modernAutomaticRouteCancellation = null;
                _modernRouteBusy = false;
                if (_modernEditorActive) RefreshModernToolVisuals();
            }
        }
    }
}
