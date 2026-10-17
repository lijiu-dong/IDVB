namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private MapViewportColorSignature? GetCurrentMapViewportPresenceReference(string? detectedFloor = null)
    {
        var identity = _lastRecognition ?? _pendingAlignmentIdentity;
        if (identity is null)
            return null;
        var floorKey = detectedFloor ?? _currentFloorKey
            ?? identity.Result.Floor
            ?? MapFloorRules.GetPrimaryFloorKey(identity.Map);
        var key = new MapViewportReferenceKey(
            identity.Map.Id,
            NormalizeMapViewportFloorKey(floorKey));
        lock (_mapViewportReferenceGate)
        {
            return _mapViewportReferences.GetValueOrDefault(key);
        }
    }

    private void RememberMapViewportPresenceReference(
        RuntimeMapRecognition recognition,
        CapturedGameFrame frame)
    {
        var key = new MapViewportReferenceKey(
            recognition.Map.Id,
            NormalizeMapViewportFloorKey(recognition.Result.Floor));
        var signature = MapViewportPresenceDetector.CreateSignature(frame.Image);
        lock (_mapViewportReferenceGate)
        {
            _mapViewportReferences[key] = signature;
        }
    }

    private void ClearMapViewportPresenceReferences()
    {
        lock (_mapViewportReferenceGate)
        {
            _mapViewportReferences.Clear();
        }
    }

    private static string NormalizeMapViewportFloorKey(string? floorKey) =>
        string.IsNullOrWhiteSpace(floorKey)
            ? "1f"
            : floorKey.Trim().ToLowerInvariant();

    private readonly record struct MapViewportReferenceKey(
        Guid MapId,
        string FloorKey);
}
