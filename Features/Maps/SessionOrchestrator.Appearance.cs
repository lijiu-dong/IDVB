namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    public async Task RefreshMapAppearanceAsync(string className)
    {
        await MapOperationProgress.RunAsync(0, .95, () => RefreshMapCacheAsync());
        MapOperationProgress.Report(.95, "正在刷新地图配色……");
        var maps = (await _mapRepository.GetTagCatalogSnapshotAsync()).Maps
            .Where(map => string.Equals(map.Class, className, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(map => map.Id);
        if (_disposed) return;
        _lastRecognition = Refresh(_lastRecognition);
        _pendingAlignmentIdentity = Refresh(_pendingAlignmentIdentity);
        _provisionalRecognition = Refresh(_provisionalRecognition);
        _optimisticPresentationRecognition = Refresh(_optimisticPresentationRecognition);
        _pendingBackgroundIdentity = Refresh(_pendingBackgroundIdentity);
        var pendingTracking = Volatile.Read(ref _pendingOrbTrackingCommit);
        while (pendingTracking is not null)
        {
            var refreshed = pendingTracking with
            {
                Recognition = Refresh(pendingTracking.Recognition) ?? pendingTracking.Recognition
            };
            var observed = Interlocked.CompareExchange(ref _pendingOrbTrackingCommit, refreshed, pendingTracking);
            if (ReferenceEquals(observed, pendingTracking)) break;
            pendingTracking = observed;
        }
        RefreshMiniMapForCurrentFloor();
        var active = _optimisticPresentationRecognition ?? _lastRecognition;
        if (_gameMapToggleState.IsOpen && _overlay.HasMap && active is not null
            && maps.ContainsKey(active.Map.Id))
        {
            var viewport = ResolveMapViewportForCurrentWindow();
            var bounds = _lastGameBounds;
            var viewportBounds = new MapScreenRect(bounds.X + viewport.X * bounds.Width,
                bounds.Y + viewport.Y * bounds.Height, viewport.Width * bounds.Width, viewport.Height * bounds.Height);
            _overlay.UpdateMap(active, bounds, _lastGameWindowHandle, Settings.ShowOverlayStatus,
                viewportBounds, preservePlayer: true);
        }

        MapOperationProgress.Report(1, "地图配色刷新完成");

        RuntimeMapRecognition? Refresh(RuntimeMapRecognition? previous)
        {
            if (previous is null || !maps.TryGetValue(previous.Map.Id, out var current)) return previous;
            // Retain identity, trusted transforms and geometry; only refresh appearance.
            previous.Map.AppearancePalette = current.AppearancePalette;
            return new RuntimeMapRecognition
            {
                Map = previous.Map, Result = previous.Result,
                FloorImagePath = _mapRepository.GetFloorOverlayPath(previous.Map, previous.Result.Floor)
            };
        }
    }
}
