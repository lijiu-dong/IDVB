#if IDVB_DEVELOPMENT_BUILD
namespace IDVBuff.Features.Maps;

public sealed partial class MapOverlayWindow
{
    internal object CaptureDevelopmentTrackingSnapshot() => new
    {
        mapId = _mapId, floor = _mapFloorKey, hasMap = _map is not null,
        gameBounds = _gameBounds, map = _map is null ? null : new
        {
            _map.Left, _map.Top, _map.Width, _map.Height
        },
        mainVisible = _showMainContent, mapVisible = _showMapContent,
        nativeMapVisible = _mapNativeWindow.IsVisible,
        mapLayerBitmapBuilds = MapLayerBitmapBuildCount,
        mapLayerMoves = MapLayerTransformMoveCount,
        PresentCount
    };
}
#endif
