namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private readonly object _vpsg3PreparationGate = new();
    private Guid? _selectedFloorMapId;
    private string? _selectedFloorPreparationKey;
    private Task _selectedFloorPreparation = Task.CompletedTask;
    private long _selectedFloorGeneration;

    // Class-wide scanning consumes each map's configured scan floor. After
    // identity selection, prepare the other floors of that map while capture
    // is starting. Every alignment prewarm awaits this same task, so a missing
    // prepared floor cannot silently divert the consumer into a slower fallback.
    internal Task PrepareSelectedMapFloorsAsync(MapRecord map,
        bool identitySelection = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_matchScopedResources || _disposed || _residentMapClass is null
            || !string.Equals(map.Class, _residentMapClass, StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;
        var fingerprint = MapFeatureCacheRules.ComputeContentFingerprint(map);
        var key = $"{map.Id:N}|{map.UpdatedAt.UtcTicks}|{fingerprint}";
        lock (_vpsg3PreparationGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed || _residentMapClass is null
                || !string.Equals(map.Class, _residentMapClass, StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;
            // A delayed alignment prewarm follows identity selection; it must
            // never become a new selection and evict the current map's floors.
            if (!identitySelection && _selectedFloorMapId is { } selected && selected != map.Id)
                return Task.CompletedTask;
            var current = _maps.FirstOrDefault(item => item.Id == map.Id);
            // An old capture/prewarm must never replace a newer catalog's slot.
            if (current is null || current.UpdatedAt != map.UpdatedAt
                || MapFeatureCacheRules.ComputeContentFingerprint(current) != fingerprint)
                return Task.CompletedTask;
            if (_selectedFloorPreparationKey == key) return _selectedFloorPreparation;
            var scanFloors = ResidentMaps(_maps).Select(item =>
                (item.Id, MapScanFloorRules.ResolveScanFloorKey(item).Trim().ToLowerInvariant())).ToHashSet();
            _vpsg3Registry.RetireUnselectedAuxiliaryFloors(map.Id, scanFloors);
            _selectedFloorMapId = map.Id;
            _selectedFloorPreparationKey = key;
            _selectedFloorGeneration++;
            _selectedFloorPreparation = TriggerVpsg3Rebuild([map], new HashSet<Guid> { map.Id }, null, false);
            return _selectedFloorPreparation;
        }
    }

    private bool TryBeginSelectedFloorBuild(Vpsg3IndexCacheKey key, long? selectionGeneration)
    {
        lock (_vpsg3PreparationGate)
            return IsSelectedFloorBuildCurrent(key, selectionGeneration) && _vpsg3Registry.TryBeginBuild(key);
    }

    private bool TryPublishSelectedFloor(Vpsg3IndexCacheKey key, Vpsg3PreparedFloor floor, long? selectionGeneration)
    {
        lock (_vpsg3PreparationGate)
        {
            if (IsSelectedFloorBuildCurrent(key, selectionGeneration))
                return _vpsg3Registry.TryPublishFloor(key, floor);
            floor.Dispose();
            return false;
        }
    }

    private bool IsSelectedFloorBuildCurrent(Vpsg3IndexCacheKey key, long? selectionGeneration)
    {
        if (!_matchScopedResources) return true;
        if (_disposed || _residentMapClass is null) return false;
        var map = _maps.FirstOrDefault(item => item.Id == key.MapId);
        if (map is null || map.UpdatedAt != key.UpdatedAt
            || !string.Equals(map.Class, _residentMapClass, StringComparison.OrdinalIgnoreCase)) return false;
        return selectionGeneration is { } generation
            ? _selectedFloorMapId == key.MapId && _selectedFloorGeneration == generation
            : _selectedFloorMapId == key.MapId || string.Equals(key.FloorKey,
                MapScanFloorRules.ResolveScanFloorKey(map), StringComparison.OrdinalIgnoreCase);
    }

    private Task ReconcileScanFloorResidencyAsync()
    {
        if (!_matchScopedResources || _residentMapClass is null) return Task.CompletedTask;
        lock (_vpsg3PreparationGate)
        {
            var maps = ResidentMaps(_maps);
            var scanFloors = maps.Select(map =>
                (map.Id, MapScanFloorRules.ResolveScanFloorKey(map).Trim().ToLowerInvariant())).ToHashSet();
            _vpsg3Registry.RetireUnselectedAuxiliaryFloors(_selectedFloorMapId ?? Guid.Empty, scanFloors);
            var missing = maps.Where(map =>
            {
                if (!TryGetVpsg3FloorLease(map, MapScanFloorRules.ResolveScanFloorKey(map), out var lease)) return true;
                lease!.Dispose();
                return false;
            }).ToArray();
            return missing.Length == 0 ? Task.CompletedTask
                : TriggerVpsg3Rebuild(missing, missing.Select(map => map.Id).ToHashSet(), null, true);
        }
    }

    private void RecordSelectedFloorBuildFailure(Vpsg3IndexCacheKey key, string reason, long? selectionGeneration)
    {
        lock (_vpsg3PreparationGate)
            if (IsSelectedFloorBuildCurrent(key, selectionGeneration))
                _vpsg3Registry.RecordBuildFailure(key, reason);
    }
}
