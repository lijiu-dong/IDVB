using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private Task<MapCatalogSnapshot> LoadRefreshCatalogAsync() => _matchScopedResources
        ? _repository.GetRecognitionCatalogSnapshotAsync(_residentMapClass)
        : _repository.GetCatalogSnapshotAsync();

    private Task<MapCatalogSnapshot> HealRefreshCatalogAsync(MapCatalogSnapshot catalog) =>
        MapOperationProgress.RunAsync(0, .35,
            () => _repository.HealPrebuiltStructureSnapshotAsync(catalog, CancellationToken.None, onlyOutdated: true,
                recognitionProjection: _matchScopedResources, residentClass: _residentMapClass));

    // Called only for the newly detached, fully validated asset snapshot. Never
    // mutate a previously published map still held by a recognition consumer.
    private void ProjectInactiveRecognitionMaps(IReadOnlyList<MapRecord> maps)
    {
        if (!_matchScopedResources) return;
        long points = 0, annotations = 0;
        foreach (var map in maps)
        {
            if (string.Equals(map.Class, _residentMapClass, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var profile in map.Recognition.Floors.Values.Distinct())
            {
                points += profile.FreeCropPoints.Count + profile.BackgroundLayers.Sum(layer => (long)layer.Points.Count);
                annotations += profile.Annotations.Count;
                points += profile.Annotations.Sum(annotation => (long)annotation.Points.Count);
                if (profile.Annotations.Count != 0)
                    MemoryJournal.ObserveManagedObject(profile.Annotations, "Recognition.InactiveAnnotations");
                profile.FreeCropPoints = [];
                profile.BackgroundLayers = [];
                profile.Annotations = [];
            }
            map.IsListProjection = true;
        }
        MemoryJournal.Current?.Record("recognition-catalog-scope", new
        {
            residentClass = _residentMapClass, maps = maps.Count, detachedEditablePoints = points,
            detachedAnnotations = annotations,
            meaning = "inactive recognition ownership detached; managed collection is independent of OS residency"
        });
    }

    // Preparation holds _cacheGate and precedes all scan/alignment consumers.
    // Rehydrate only the selected class, including exact crop strokes and route
    // annotations. Inactive metadata must never reach an image/render consumer.
    private async Task LoadResidentRecognitionCatalogAsync(string mapClass)
    {
        using var operation = MemoryJournal.BeginOperation("Recognition.LoadResidentCatalog", mapClass);
        MapOperationProgress.Report(.1, "正在读取当前对局地图类……");
        var catalog = await _repository.GetClassCatalogSnapshotAsync(mapClass);
        await MapOperationProgress.RunAsync(.11, .17,
            () => _repository.EnsureDerivedAssetsFromSnapshotAsync(catalog.Maps, catalog.ClassProperties));
        var resident = catalog.Maps.ToDictionary(map => map.Id);
        var snapshot = _maps.Where(map => !string.Equals(map.Class, mapClass, StringComparison.OrdinalIgnoreCase))
            .Concat(resident.Values).OrderBy(map => map.SequenceNumber).ToArray();
        var cache = await MapOperationProgress.RunAsync(.17, .2,
            () => Task.Run(() => BuildFingerprintCache(snapshot, null)));
        _maps = cache.Maps;
        _fingerprints = cache.Fingerprints;
        TotalMapCount = _maps.Count;
        MapOperationProgress.Report(.2, "当前地图类元数据已就绪");
    }
}
