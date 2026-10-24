using System.Drawing;

namespace IDVBuff.Features.Maps;

public sealed partial class MapOverlayWindow
{
    private readonly MapOverlayNativeWindow _mapNativeWindow;
    private bool _mapLayerDirty = true;
    private int _mapLayerPixelWidth;
    private int _mapLayerPixelHeight;
    private Rectangle _mapLayerSourceRegion;
    private long _mapLayerBitmapBuildCount;
    private long _mapLayerTransformMoveCount;

    public bool IsMapContentVisible(Guid mapId, string floorKey) =>
        !_disposed && _map is not null && _mapNativeWindow.IsVisible
        && _showMainContent && _showMapContent && _mapOpacity > 0
        && _mapId == mapId && string.Equals(_mapFloorKey, floorKey, StringComparison.OrdinalIgnoreCase);

    public bool IsCaptureExclusionEnabled => _nativeWindow.IsCaptureExclusionEnabled
        && (_map is null || _mapNativeWindow.IsCaptureExclusionEnabled);
    internal long MapLayerBitmapBuildCount => Interlocked.Read(ref _mapLayerBitmapBuildCount);
    internal long MapLayerTransformMoveCount => Interlocked.Read(ref _mapLayerTransformMoveCount);

    private bool HasRetainedMapLayer(int width, int height) =>
        _mapLayerPixelWidth == width && _mapLayerPixelHeight == height
        && !_mapLayerSourceRegion.IsEmpty
        && _mapNativeWindow.HasRetainedLayer(_mapLayerSourceRegion.Width, _mapLayerSourceRegion.Height);

    public void SetMapContentVisible(bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_showMapContent == visible)
            return;
        _showMapContent = visible;
        if (!visible)
        {
            if (_mapNativeWindow.IsVisible)
                _mapNativeWindow.Hide();
        }
        if (IsVisible)
            Present();
    }

    public bool TrySetCaptureExclusion(bool enabled, out string failureReason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_nativeWindow.TrySetCaptureExclusion(enabled, out failureReason))
            return false;
        return _mapNativeWindow.TrySetCaptureExclusion(enabled, out failureReason);
    }

    public void Hide()
    {
        _mapNativeWindow.Hide();
        _nativeWindow.Hide();
    }

    private void PresentMapLayerOnly()
    {
        if (!_showMapContent)
            return;
        if (_map is not null && _showMainContent)
            PresentMapLayer(_map, ResolveOverlayDpi());
        else if (_mapNativeWindow.IsVisible)
            _mapNativeWindow.Hide();
    }

    private void PresentMapLayer(MapOverlayRenderMap map, uint dpi)
    {
        var placement = ResolveMapLayerPlacement(map);
        if (!placement.Bounds.IsValid || !placement.ClipBounds.IsValid)
        {
            _mapNativeWindow.Hide();
            return;
        }

        var width = MapOverlayRealtimeTransformPlanner.RoundToPixel(map.Width);
        var height = MapOverlayRealtimeTransformPlanner.RoundToPixel(map.Height);
        var visible = MapOverlayRealtimeTransformPlanner.VisibleRasterRegion(placement.Bounds, placement.ClipBounds);
        if (visible.IsEmpty) { _mapNativeWindow.Hide(); return; }
        var retained = HasRetainedMapLayer(width, height);
        if (_mapLayerDirty
            || !retained || !_mapLayerSourceRegion.Contains(visible))
        {
            var sourceRegion = retained && _mapLayerSourceRegion.Contains(visible) ? _mapLayerSourceRegion
                : MapOverlayRealtimeTransformPlanner.RetainedRasterRegion(visible, width, height);
            // A map close drops only this window's binding; the renderer cache
            // keeps the baked window. Reuse it whenever it still covers `visible`.
            MapOverlayBitmapRenderer.WithMapLayerCovering(
                map with { RouteLineThickness = _routeLineThickness, RouteOpacity = _routeOpacity },
                dpi,
                _showGateMarkers && map.SupportsVectorRoutes,
                false,
                _showTextAnnotations && map.SupportsVectorRoutes,
                _showBoxAnnotations && map.SupportsVectorRoutes,
                _showLineAnnotations && map.SupportsVectorRoutes,
                _mapOpacity,
                visible,
                sourceRegion,
                (bitmap, usedRegion) =>
                {
                    sourceRegion = usedRegion;
                    _mapNativeWindow.Present(
                        bitmap,
                        MapOverlayRealtimeTransformPlanner.RasterPlacement(placement.Bounds, usedRegion),
                        placement.ClipBounds,
                        _nativeWindow.Handle);
                });
            _mapLayerDirty = false;
            _mapLayerPixelWidth = width;
            _mapLayerPixelHeight = height;
            _mapLayerSourceRegion = sourceRegion;
            Interlocked.Increment(ref _mapLayerBitmapBuildCount);
            return;
        }

        _mapNativeWindow.MoveRetainedLayer(
            MapOverlayRealtimeTransformPlanner.RasterPlacement(placement.Bounds, _mapLayerSourceRegion),
            placement.ClipBounds,
            _nativeWindow.Handle);
        Interlocked.Increment(ref _mapLayerTransformMoveCount);
    }

    private void MoveMapLayerOnly()
    {
        if (!_showMapContent)
            return;
        if (_map is null || !_showMainContent)
        {
            if (_mapNativeWindow.IsVisible)
                _mapNativeWindow.Hide();
            return;
        }

        var width = MapOverlayRealtimeTransformPlanner.RoundToPixel(_map.Width);
        var height = MapOverlayRealtimeTransformPlanner.RoundToPixel(_map.Height);
        if (_mapLayerDirty || !HasRetainedMapLayer(width, height))
        {
            PresentMapLayerOnly();
            return;
        }

        var placement = ResolveMapLayerPlacement(_map);
        if (!placement.Bounds.IsValid || !placement.ClipBounds.IsValid)
        {
            _mapNativeWindow.Hide();
            return;
        }

        var visible = MapOverlayRealtimeTransformPlanner.VisibleRasterRegion(placement.Bounds, placement.ClipBounds);
        if (visible.IsEmpty) { _mapNativeWindow.Hide(); return; }
        if (!_mapLayerSourceRegion.Contains(visible)) { PresentMapLayerOnly(); return; }

        _mapNativeWindow.MoveRetainedLayer(
            MapOverlayRealtimeTransformPlanner.RasterPlacement(placement.Bounds, _mapLayerSourceRegion),
            placement.ClipBounds,
            _nativeWindow.Handle);
        Interlocked.Increment(ref _mapLayerTransformMoveCount);
    }

    private MapLayerPlacement ResolveMapLayerPlacement(MapOverlayRenderMap map)
    {
        var bounds = new MapScreenRect(
            _gameBounds.X + map.Left,
            _gameBounds.Y + map.Top,
            MapOverlayRealtimeTransformPlanner.RoundToPixel(map.Width),
            MapOverlayRealtimeTransformPlanner.RoundToPixel(map.Height));
        var clip = _gameBounds;
        if (!_allowExtend && map.ClipBounds is { IsValid: true } viewport)
        {
            clip = Intersect(
                clip,
                new MapScreenRect(
                    _gameBounds.X + viewport.X,
                    _gameBounds.Y + viewport.Y,
                    viewport.Width,
                    viewport.Height));
        }
        return new MapLayerPlacement(bounds, clip);
    }

    private uint ResolveOverlayDpi()
    {
        var dpi = _gameWindowHandle == IntPtr.Zero ? 0u : GetDpiForWindow(_gameWindowHandle);
        return dpi == 0 ? 96u : dpi;
    }

    private static MapScreenRect Intersect(MapScreenRect left, MapScreenRect right)
    {
        var x = Math.Max(left.X, right.X);
        var y = Math.Max(left.Y, right.Y);
        var rightEdge = Math.Min(left.X + left.Width, right.X + right.Width);
        var bottomEdge = Math.Min(left.Y + left.Height, right.Y + right.Height);
        return new MapScreenRect(x, y, Math.Max(0, rightEdge - x), Math.Max(0, bottomEdge - y));
    }

    private readonly record struct MapLayerPlacement(
        MapScreenRect Bounds,
        MapScreenRect ClipBounds);
}
