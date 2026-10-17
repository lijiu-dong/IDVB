using OpenCvSharp;

namespace IDVBuff.Features.Maps;
/// <summary>
/// Persists map metadata and its two source images in the application's local data directory.
/// </summary>
public sealed partial class MapRepository
{
    public string GetFloorTwoPath(MapRecord record)
    {
        var secondFloor = MapFloorRules.GetOrderedFloors(record).Skip(1).FirstOrDefault();
        return secondFloor is not null && !string.IsNullOrWhiteSpace(secondFloor.ImageFileName)
            ? GetSafeMapFilePath(GetMapDirectory(record.Id), secondFloor.ImageFileName)
            : GetStoredFloorImagePath(record.Id, record.FloorTwoFileName, "floor-2");
    }
    public string GetFloorOneRecognitionPath(MapRecord record) =>
        GetFloorRecognitionPath(
            record,
            MapFloorRules.GetOrderedFloors(record).FirstOrDefault()?.Key
                 ?? record.Recognition.FirstFloor.FloorKey);
    public string GetFloorTwoRecognitionPath(MapRecord record) =>
        GetFloorRecognitionPath(
            record,
            MapFloorRules.GetOrderedFloors(record).Skip(1).FirstOrDefault()?.Key
                 ?? record.Recognition.SecondFloor.FloorKey);
    public string GetFloorOneOverlayPath(MapRecord record) =>
        GetFloorOverlayPath(
            record,
            MapFloorRules.GetOrderedFloors(record).FirstOrDefault()?.Key
                 ?? record.Recognition.FirstFloor.FloorKey);
    public string GetFloorTwoOverlayPath(MapRecord record) =>
        GetFloorOverlayPath(
            record,
            MapFloorRules.GetOrderedFloors(record).Skip(1).FirstOrDefault()?.Key
                ?? record.Recognition.SecondFloor.FloorKey);

    // ── V6: floor-key-based path helpers ──────────────────────────
    public string GetFloorImagePath(MapRecord record, string floorKey)
    {
        var floor = record.Floors.FirstOrDefault(f => f.Key == floorKey);
        if (floor is null)
            throw new InvalidOperationException($"地图不包含楼层 '{floorKey}'。");
        EnsureSafeFloorKey(floorKey);
        // Explicit floor metadata is authoritative. The positional fallback is
        // only for pre-V9 records during migration/repair.
        return GetOrderedFloorPosition(record, floorKey) switch
        {
            0 => GetFloorOnePath(record),
            1 => GetFloorTwoPath(record),
            _ => GetEditableFloorImagePath(record, floorKey)
        };
    }

    /// <summary>Loads the complete management snapshot in one catalog read.</summary>
    public Task<MapCatalogSnapshot> GetCatalogSnapshotAsync() =>
        Task.Run(() => GetCatalogSnapshotCoreAsync(null));

    public Task<MapCatalogSnapshot> GetClassCatalogSnapshotAsync(string className) =>
        Task.Run(() => GetCatalogSnapshotCoreAsync(className));

    internal Task<MapRecord?> GetMapSnapshotAsync(Guid id) => Task.Run(async () =>
        (await GetCatalogSnapshotCoreAsync(null, id)).Maps.SingleOrDefault());

    private async Task<MapCatalogSnapshot> GetCatalogSnapshotCoreAsync(string? className, Guid? mapId = null)
    {
        using var memory = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Catalog.Snapshot", className);
        await Gate.WaitAsync();
        try
        {
            var revision = GetCatalogRevision();
            var catalog = mapId is not null ? await ReadMapCatalogAsync(mapId.Value) : className is null
                ? await ReadCatalogAsync()
                : await ReadClassCatalogAsync(className);
            var snapshot = new MapCatalogSnapshot(
                catalog.Classes.ToArray(),
                catalog.Maps.Where(record => (mapId is null || record.Id == mapId)
                        && (className is null || string.Equals(record.Class, className, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(record => record.SequenceNumber)
                    .Select(record => DetachFreshCatalogRecord(catalog, record))
                    .ToArray())
            {
                ClassProperties = catalog.ClassProperties.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Clone(),
                    StringComparer.OrdinalIgnoreCase),
                VariantGroups = catalog.VariantGroups
                    .Select(group => group.Clone())
                    .ToArray()
            };
            RememberEditableSnapshot(snapshot, revision);
            IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedObject(snapshot.Maps, "Catalog.DetachedMaps");
            return snapshot;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>轻量获取 Class 名称列表，命中修订版本缓存时不做反序列化与深克隆。</summary>
    public Task<IReadOnlyList<string>> GetMapClassesAsync() => Task.Run(GetMapClassesCoreAsync);

    private async Task<IReadOnlyList<string>> GetMapClassesCoreAsync()
    {
        var revision = GetCatalogRevision();
        if (ClassNamesCache.TryGetValue(CatalogPath, out var cached) && revision == cached.Revision)
            return cached.Classes.ToArray();

        await Gate.WaitAsync();
        try
        {
            revision = GetCatalogRevision();
            if (ClassNamesCache.TryGetValue(CatalogPath, out cached) && revision == cached.Revision)
                return cached.Classes.ToArray();

            var catalog = await ReadCatalogAsync();
            return catalog.Classes.ToArray();
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task VerifyMapContentAsync(Guid id)
    {
        await Gate.WaitAsync();
        try
        {
            var catalog = await ReadCatalogAsync();
            var record = catalog.Maps.SingleOrDefault(map => map.Id == id)
                ?? throw new InvalidOperationException($"Map {id} was not found.");
            await VerifyFloorImageBindingsAsync(record);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<string> CreateClassAsync(string name)
    {
        var requestedName = NormalizeClassName(name)
            ?? throw new InvalidOperationException("Class 名称不能为空。");
        await Gate.WaitAsync();
        try
        {
            var catalog = await ReadCatalogAsync();
            if (catalog.Classes.Any(candidate => string.Equals(
                    candidate, requestedName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("已存在同名 Class。");
            catalog.Classes.Add(requestedName);
            catalog.ClassProperties[requestedName] = new MapClassProperties();
            await WriteCatalogAsync(catalog);
            return requestedName;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Creates a new class without ever merging with an existing name.</summary>
    public Task<string> CreateUniqueClassAsync(string sourceName) =>
        CreateUniqueClassAsync(sourceName, beforeCommit: null);

    private async Task<string> CreateUniqueClassAsync(
        string sourceName,
        Action<string>? beforeCommit)
    {
        var requestedName = NormalizeClassName(sourceName)
            ?? throw new InvalidOperationException("Class 名称不能为空。");
        await Gate.WaitAsync();
        try
        {
            var catalog = await ReadCatalogAsync();
            var uniqueName = BuildUniqueImportedClassName(requestedName, catalog.Classes);
            beforeCommit?.Invoke(uniqueName);
            catalog.Classes.Add(uniqueName);
            catalog.ClassProperties[uniqueName] = new MapClassProperties();
            await WriteCatalogAsync(catalog);
            return uniqueName;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Removes a class and all maps assigned to it as one catalog operation.</summary>
    public async Task<MapClassDeletionResult> DeleteClassAsync(string className, IReadOnlySet<Guid>? expectedMapIds = null)
        => await DeleteClassCoreAsync(className, preserveOneClass: true, expectedMapIds);

    internal Task<MapClassDeletionResult> RollBackImportedClassAsync(string className)
        => DeleteClassCoreAsync(className, preserveOneClass: false);

    private async Task<MapClassDeletionResult> DeleteClassCoreAsync(string className, bool preserveOneClass, IReadOnlySet<Guid>? expectedMapIds = null)
    {
        await Gate.WaitAsync();
        var stagedDirectories = new List<(string Original, string Staged)>();
        try
        {
            var catalog = await ReadCatalogAsync();
            var canonicalName = catalog.Classes.SingleOrDefault(candidate => string.Equals(
                candidate, className, StringComparison.OrdinalIgnoreCase));
            if (canonicalName is null)
                throw new InvalidOperationException("找不到要删除的 Class。");
            if (preserveOneClass && catalog.Classes.Count <= 1)
                throw new InvalidOperationException("至少需要保留一个 Class。");

            var mapsToDelete = catalog.Maps.Where(map => string.Equals(
                map.Class, canonicalName, StringComparison.OrdinalIgnoreCase)).ToArray();
            // Check inside the catalog gate, before moving any resources. The
            // confirmed class must still contain precisely the confirmed maps.
            if (expectedMapIds is not null && !mapsToDelete.Select(map => map.Id).ToHashSet().SetEquals(expectedMapIds))
                throw new InvalidOperationException("地图类内容已变化，请重新确认删除。");
            var stagedMaps = 0;
            foreach (var map in mapsToDelete)
            {
                MapOperationProgress.Report(.8 * stagedMaps++ / Math.Max(1, mapsToDelete.Length), "正在删除地图类……");
                var original = GetMapDirectory(map.Id);
                if (!Directory.Exists(original))
                    continue;
                var staged = Path.Combine(_rootDirectory, $".delete-class-{map.Id:N}-{Guid.NewGuid():N}");
                Directory.Move(original, staged);
                stagedDirectories.Add((original, staged));
            }

            catalog.Maps.RemoveAll(map => mapsToDelete.Any(candidate => candidate.Id == map.Id));
            catalog.VariantGroups.RemoveAll(group => string.Equals(
                group.Class,
                canonicalName,
                StringComparison.OrdinalIgnoreCase));
            catalog.Classes.Remove(canonicalName);
            catalog.ClassProperties.Remove(canonicalName);
            catalog.TemplateInstances.RemoveAll(i => string.Equals(i.LocalClass, canonicalName, StringComparison.OrdinalIgnoreCase));
            await WriteCatalogAsync(catalog);
            foreach (var (_, staged) in stagedDirectories)
                if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
            return new MapClassDeletionResult(canonicalName, mapsToDelete.Length);
        }
        catch
        {
            foreach (var (original, staged) in stagedDirectories.AsEnumerable().Reverse())
                if (Directory.Exists(staged) && !Directory.Exists(original)) Directory.Move(staged, original);
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    private string GetEditableFloorImagePath(MapRecord record, string floorKey)
    {
        EnsureSafeFloorKey(floorKey);
        var floor = record.Floors.FirstOrDefault(candidate => candidate.Key == floorKey);
        if (floor is not null && !string.IsNullOrWhiteSpace(floor.ImageFileName))
            return GetSafeMapFilePath(GetMapDirectory(record.Id), floor.ImageFileName);

        if (GetOrderedFloorPosition(record, floorKey) == 0)
            return GetFloorOnePath(record);
        if (GetOrderedFloorPosition(record, floorKey) == 1)
            return GetFloorTwoPath(record);

        var directory = GetMapDirectory(record.Id);
        var prefix = $"floor-{floorKey}";
        var existingPath = Directory.EnumerateFiles(directory, $"{prefix}.*")
            .FirstOrDefault(path => IsSupportedImage(path));
        return existingPath ?? Path.Combine(directory, $"{prefix}.png");
    }

    public string GetFloorRecognitionPath(MapRecord record, string floorKey)
    {
        var floor = record.Floors.FirstOrDefault(candidate => candidate.Key == floorKey)
            ?? throw new InvalidOperationException($"地图不包含楼层 '{floorKey}'。");
        if (!string.IsNullOrWhiteSpace(floor.RecognitionFileName))
            return GetSafeMapFilePath(GetMapDirectory(record.Id), floor.RecognitionFileName);

        var profile = record.Recognition.GetFloor(floorKey)
            ?? throw new InvalidOperationException($"地图楼层 '{floorKey}' 缺少识别配置。");
        return UsesWholeSourceImage(profile) && profile.BackgroundLayers.Count == 0
            ? GetFloorImagePath(record, floorKey)
            : Path.Combine(GetMapDirectory(record.Id), GetFloorRecognitionFileName(floorKey));
    }

    public string GetFloorOverlayPath(MapRecord record, string floorKey)
        => record.IsCoCreationTemplate && record.AppearancePalette is not null
            ? GetAppearancePath(record, floorKey, thumbnail: false)
            : GetOriginalFloorOverlayPath(record, floorKey);

    private string GetOriginalFloorOverlayPath(MapRecord record, string floorKey)
    {
        var floor = record.Floors.FirstOrDefault(candidate => candidate.Key == floorKey)
            ?? throw new InvalidOperationException($"地图不包含楼层 '{floorKey}'。");
        if (!string.IsNullOrWhiteSpace(floor.OverlayFileName))
            return GetSafeMapFilePath(GetMapDirectory(record.Id), floor.OverlayFileName);
        return Path.Combine(GetMapDirectory(record.Id), GetFloorOverlayFileName(floorKey));
    }

    public string GetFloorThumbnailPath(MapRecord record, string floorKey)
        => record.IsCoCreationTemplate && record.AppearancePalette is not null
            ? GetAppearancePath(record, floorKey, thumbnail: true)
            : GetOriginalFloorThumbnailPath(record, floorKey);

    private string GetOriginalFloorThumbnailPath(MapRecord record, string floorKey)
    {
        var floor = record.Floors.FirstOrDefault(candidate => candidate.Key == floorKey)
            ?? throw new InvalidOperationException($"地图不包含楼层 '{floorKey}'。");
        if (!string.IsNullOrWhiteSpace(floor.ThumbnailFileName))
            return GetSafeMapFilePath(GetMapDirectory(record.Id), floor.ThumbnailFileName);
        return Path.Combine(GetMapDirectory(record.Id), GetFloorThumbnailFileName(floorKey));
    }
    public MapCatalogRevision GetCatalogRevision()
    {
        if (!File.Exists(CatalogPath))
            return MapCatalogRevision.Empty;
        var info = new FileInfo(CatalogPath);
        return new MapCatalogRevision(info.LastWriteTimeUtc.Ticks, info.Length);
    }
    public static bool IsSupportedImage(string? path)
    {
        var extension = Path.GetExtension(path ?? string.Empty);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }
}
