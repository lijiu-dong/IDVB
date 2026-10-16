using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private CancellationTokenSource? _modernAutomaticRouteCancellation;
    private NormalizedPoint? _modernRouteStart;
    private NormalizedPoint? _modernRouteHover;
    private bool _modernRouteBusy;
    private MapNavigationTopology? _modernRoutePreviewNavigation;
    private PathGeometry? _modernRoutePreviewGeometry;
    private Microsoft.UI.Xaml.Shapes.Path? _modernRoutePreviewPath;
    private string? _modernRoutePreviewSignature;

    private bool CanUseModernAutomaticRoute() => _draft is { } draft
        && (MapAutomaticRouteCanvas.Supports(draft, _activeFloorKey)
            || draft.Navigation is null && _coCreationTemplates.IsTemplateDraft(draft));

    private void ResetModernAutomaticRoute()
    {
        var cancellation = _modernAutomaticRouteCancellation;
        _modernAutomaticRouteCancellation = null;
        _modernRouteBusy = false;
        cancellation?.Cancel();
        var dialog = _modernRoutePlanningDialog;
        _modernRoutePlanningDialog = null;
        dialog?.Hide();
        _modernRouteStart = null; _modernRouteHover = null;
        _modernRoutePreviewNavigation = null; _modernRoutePreviewGeometry = null;
        _modernRoutePreviewPath = null;
        _modernRoutePreviewSignature = null;
    }

    private async Task SelectModernAutomaticRouteAsync()
    {
        if (_draft is not { } draft || !CanUseModernAutomaticRoute() || _modernRouteBusy) return;
        var floorKey = _activeFloorKey;
        using var cancellation = new CancellationTokenSource();
        _modernAutomaticRouteCancellation = cancellation;
        _modernRouteBusy = true;
        try
        {
            SetModernStatus("正在准备可通行路线…", false);
            var navigation = await _coCreationTemplates.LoadNavigationForDraftAsync(draft, cancellation.Token);
            if (navigation is null) throw new InvalidOperationException("这个地图没有可用的自动路线数据。");
            await MapNavigationRules.ValidateImagesAsync(navigation, draft.FloorPaths, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(draft, _draft) || floorKey != _activeFloorKey || !_modernEditorActive) return;
            draft.Navigation = navigation;
            _modernToolState.AutomaticRouteAvailable = MapAutomaticRouteCanvas.Supports(draft, floorKey);
            SelectModernTool(MapEditorTool.AutomaticRoute);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (_modernEditorActive && ReferenceEquals(draft, _draft)) SetModernStatus(exception.Message, true); }
        finally
        {
            if (ReferenceEquals(_modernAutomaticRouteCancellation, cancellation))
            {
                _modernAutomaticRouteCancellation = null;
                _modernRouteBusy = false;
                if (_modernEditorActive) RefreshModernToolVisuals();
            }
        }
    }

    private NormalizedPoint? ResolveModernRoutePoint(NormalizedPoint point)
    {
        if (_draft?.Navigation is not { } navigation || _modernCanvas is null) return null;
        var profile = GetActiveFloorProfile();
        foreach (var anchor in profile.Anchors.Where(a => MapRouteGateEndpoint.IsAllowed(_activeFloorKey, a.Key) && a.Bounds?.IsValid is true))
        {
            var bounds = MapRecognitionCoordinates.ToSourceRectangle(anchor.Bounds!, profile.GetEffectiveRecognitionRegion());
            var gate = new NormalizedPoint { X = bounds.X + bounds.Width / 2, Y = bounds.Y + bounds.Height / 2 };
            if (MapAutomaticRouteCanvas.CanPlaceEndpoint(navigation, _activeFloorKey, gate)
                && Math.Pow((gate.X - point.X) * _modernCanvas.Width * ModernZoomFactor, 2)
                + Math.Pow((gate.Y - point.Y) * _modernCanvas.Height * ModernZoomFactor, 2) <= 14 * 14)
                return gate;
        }
        var snapped = MapAutomaticRouteCanvas.SnapPoint(point, GetActiveFloorProfile().Annotations, navigation, _activeFloorKey,
            _modernCanvas.Width * ModernZoomFactor, _modernCanvas.Height * ModernZoomFactor);
        return MapAutomaticRouteCanvas.CanPlaceEndpoint(navigation, _activeFloorKey, snapped) ? snapped : null;
    }

    private async Task CommitModernRoutePointAsync(NormalizedPoint point)
    {
        if (_draft is not { Navigation: { } navigation } draft || _modernRouteBusy
            || _modernToolState.ActiveTool != MapEditorTool.AutomaticRoute
            || !MapAutomaticRouteCanvas.Supports(draft, _activeFloorKey)) return;
        var endpoint = ResolveModernRoutePoint(point);
        if (endpoint is null) { SetModernStatus("请在当前楼层的可通行区域内选择端点。", true); return; }
        if (_modernRouteStart is null)
        {
            _modernRouteStart = endpoint; _modernRouteHover = endpoint;
            SetModernStatus("已确认阶段起点，请点击终点；靠近阶段端点或预览线时自动吸附。", false);
            RenderModernEditor(); return;
        }
        var start = _modernRouteStart.Clone(); var floorKey = _activeFloorKey; var color = _currentAnnotationColor;
        using var cancellation = new CancellationTokenSource();
        _modernAutomaticRouteCancellation = cancellation; _modernRouteBusy = true;
        try
        {
            SetModernStatus("正在自动连线…", false);
            var stage = await Task.Run(() => MapAutomaticRouteCanvas.Find(navigation, floorKey, start, endpoint, color, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(draft, _draft) || floorKey != _activeFloorKey
                || !_modernEditorActive || _modernToolState.ActiveTool != MapEditorTool.AutomaticRoute) return;
            var profile = draft.Recognition.GetFloor(floorKey)!;
            profile.Annotations.Add(stage);
            RecordModernCreation("已撤销自动路线阶段。", () => profile.Annotations.RemoveAll(item => item.Id == stage.Id));
            _modernRouteStart = null;
            _modernSelectedAnnotationIds.Clear(); _modernSelectedAnnotationIds.Add(stage.Id);
            _modernSelection = new EditorSelection(MapEditorElementKind.Annotation, stage.Id);
            UpdateMarkerConfirmState(); RefreshModernLayerList(); RenderModernEditor();
            SetModernStatus("已创建一个路线阶段；继续点击下一阶段的起点和终点。", false);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (_modernEditorActive && ReferenceEquals(draft, _draft)) SetModernStatus(exception.Message, true); }
        finally
        {
            if (ReferenceEquals(_modernAutomaticRouteCancellation, cancellation))
            {
                _modernAutomaticRouteCancellation = null;
                _modernRouteBusy = false;
            }
        }
    }

    private void AddModernAutomaticRoutePreview()
    {
        if (_modernCanvas is null || _modernToolState.ActiveTool != MapEditorTool.AutomaticRoute) return;
        void Dot(NormalizedPoint point, Color color, double radius)
        {
            var size = radius * 2 / ModernZoomFactor;
            var dot = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(color),
                Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)), StrokeThickness = 1.5 / ModernZoomFactor, IsHitTestVisible = false };
            Canvas.SetLeft(dot, point.X * _modernCanvas.Width - size / 2);
            Canvas.SetTop(dot, point.Y * _modernCanvas.Height - size / 2); _modernCanvas.Children.Add(dot);
        }
        foreach (var stage in GetActiveFloorProfile().Annotations.Where(stage => stage.Type == MapAnnotationType.Route && stage.IsValid
            && IsModernItemVisible("graphics", string.Empty) && IsModernItemVisible("graphics", ModernAnnotationKey(stage.Id))))
        { Dot(stage.Start!, ParseEditorColor(stage.EffectiveColorHex), 4); Dot(stage.End!, ParseEditorColor(stage.EffectiveColorHex), 4); }
        if (_modernRouteStart is not null) Dot(_modernRouteStart, Color.FromArgb(255, 75, 220, 128), 6);
        if (_modernRouteHover is not null) Dot(_modernRouteHover, ParseEditorColor(_currentAnnotationColor), 6);
    }

    private void AddModernRoutePolyline(IReadOnlyList<NormalizedPoint> points, Color color, double thickness = 3)
    {
        if (_modernCanvas is null) return;
        var line = new Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = thickness / ModernZoomFactor,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
        foreach (var point in points) line.Points.Add(new(point.X * _modernCanvas.Width, point.Y * _modernCanvas.Height));
        _modernCanvas.Children.Add(line);
    }

    private void AddModernAutomaticRouteNetworkPreview()
    {
        if (_modernExportRendering || _modernToolState.ActiveTool != MapEditorTool.AutomaticRoute
            || _modernCanvas is null || _draft?.Navigation is not { } navigation) return;
        var signature = FormattableString.Invariant($"{_activeFloorKey}:{_modernCanvas.Width:R}:{_modernCanvas.Height:R}:")
            + string.Join("\n", navigation.Doors.Where(door => !door.Enabled).Select(door => door.Id));
        if (!ReferenceEquals(navigation, _modernRoutePreviewNavigation) || signature != _modernRoutePreviewSignature
            || _modernRoutePreviewGeometry is null)
        {
            var geometry = new PathGeometry();
            foreach (var link in MapAutomaticRouteCanvas.PreviewLinks(navigation, _activeFloorKey))
            {
                var figure = new PathFigure { StartPoint = new(link.Points[0].X * _modernCanvas.Width, link.Points[0].Y * _modernCanvas.Height),
                    IsClosed = false, IsFilled = false };
                foreach (var point in link.Points.Skip(1))
                    figure.Segments.Add(new LineSegment { Point = new(point.X * _modernCanvas.Width, point.Y * _modernCanvas.Height) });
                geometry.Figures.Add(figure);
            }
            _modernRoutePreviewNavigation = navigation; _modernRoutePreviewSignature = signature; _modernRoutePreviewGeometry = geometry;
        }
        var preview = _modernRoutePreviewPath ??= new Microsoft.UI.Xaml.Shapes.Path
        {
            Tag = "automatic-route-network-preview",
            Stroke = new SolidColorBrush(Color.FromArgb(110, 145, 195, 210)), StrokeThickness = 1.3 / ModernZoomFactor,
            StrokeDashArray = new DoubleCollection { 3, 4 }, StrokeDashCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None, IsHitTestVisible = false
        };
        // A WinUI geometry has one dependency-object owner. Reuse its Path as
        // well as its data when the canvas removes and re-adds preview visuals.
        if (!ReferenceEquals(preview.Data, _modernRoutePreviewGeometry)) preview.Data = _modernRoutePreviewGeometry;
        preview.StrokeThickness = 1.3 / ModernZoomFactor;
        _modernCanvas.Children.Add(preview);
    }

    private bool AddModernRouteSelectionAdorner()
    {
        if (FindModernSelectedAnnotation() is not { Type: MapAnnotationType.Route } route) return false;
        if (!IsModernItemVisible("graphics", string.Empty) || !IsModernItemVisible("graphics", ModernAnnotationKey(route.Id))) return true;
        AddModernRoutePolyline(route.Points, Color.FromArgb(180, 255, 255, 255), 6);
        AddModernRoutePolyline(route.Points, ParseEditorColor(route.EffectiveColorHex));
        return true;
    }
}
