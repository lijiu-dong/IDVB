using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public readonly record struct MapCatalogRevision(long LastWriteUtcTicks, long Length)
{
    public static MapCatalogRevision Empty { get; } = new(0, 0);
    internal string? ContentVersion { get; init; }
}

/// <summary>
/// Persists map metadata and its two source images in the application's local data directory.
/// </summary>
public sealed partial class MapRepository
{
    private const int CurrentStorageSchemaVersion = 20;
    private const string FloorOneRecognitionFileName = "floor-1-recognition.png";
    private const string FloorTwoRecognitionFileName = "floor-2-recognition.png";
    private const string FloorOneOverlayFileName = "floor-1-overlay.png";
    private const string FloorTwoOverlayFileName = "floor-2-overlay.png";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _rootDirectory;
    private readonly MapCoCreationTemplateCatalog _fixedTemplates;
    private readonly Lazy<SideEntranceFeaturePreprocessor> _sideEntrancePreprocessor =
        new(() => new SideEntranceFeaturePreprocessor());
    // Share only small class metadata across the management/runtime repositories.
    // Every consumer receives a copy; catalog revisions invalidate external edits.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,
        (MapCatalogRevision Revision, string[] Classes)> ClassNamesCache = new(StringComparer.OrdinalIgnoreCase);

    public MapRepository(string? rootDirectory = null, string? coCreationTemplateRoot = null)
    {
        _fixedTemplates = new MapCoCreationTemplateCatalog(coCreationTemplateRoot);
        _rootDirectory = rootDirectory ?? Path.Combine(
            global::IDVBuff.AppDataPaths.RootDirectory,
            "Maps");
        RecoverInterruptedIdvmImports();
        RecoverRetiredSubscriptionMaps();
    }

    private string CatalogPath => Path.Combine(_rootDirectory, "maps.json");
    internal string PreparedFloorCacheDirectory => Path.Combine(_rootDirectory, ".prepared-floor-cache");


    public Task<MapRecord> SaveAsync(MapDraft draft) =>
        Task.Run(() => SaveCoreAsync(draft));

    private async Task<MapRecord> SaveCoreAsync(
        MapDraft draft,
        bool gateAlreadyHeld = false,
        ImportCatalogReuse? importCatalog = null)
    {
        using var navigationOperation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Repository.SaveNavigation", draft.Id?.ToString());
        var navigation = draft.LoadNavigationForSave();
        using var navigationOwnership = navigation is null ? null : IDVBuff.Core.Diagnostics.MemoryJournal.Own("Repository.SaveNavigation");
        if (navigation is not null) IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedObject(navigation, "Repository.SaveNavigation");
        ValidateDraft(draft, navigation);
        if (navigation is not null)
            await MapNavigationRules.ValidateImagesAsync(navigation, draft.FloorPaths);

        if (!gateAlreadyHeld)
            await Gate.WaitAsync();
        string? stagingDirectory = null;
        string? backupDirectory = null;
        string? targetDirectory = null;
        var isNewRecord = false;
        var createdDownsampleOriginals = new List<string>();
        try
        {
            var catalog = await ReadCatalogAsync(importCatalog);
            var existing = draft.Id is { } id ? catalog.Maps.SingleOrDefault(map => map.Id == id) : null;
            if (draft.Id is not null && existing is null && !draft.CreateAsImportedCopy)
                throw new InvalidOperationException("找不到要编辑的地图。");
            if (existing?.TemplateOrigin is { } origin && draft.TemplateOrigin != origin)
                throw new InvalidOperationException("模板基础资源已更新，请重新打开地图后保存；当前草稿未覆盖新版。");

            if (existing?.IsCoCreationTemplate is true)
            {
                draft.IsCoCreationTemplate = true;
                draft.SourceVisualSha256 = existing.SourceVisualSha256;
                if (await _fixedTemplates.GetFixedStructureAsync(existing.SourceVisualSha256) is null)
                    BindStoredPortableFixedStructure(draft, existing);
            }
            await _fixedTemplates.BindFixedStructureAsync(draft);

            var record = existing ?? new MapRecord
            {
                Id = draft.Id ?? Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow
            };
            isNewRecord = existing is null;
            record.Title = draft.Title?.Trim() ?? string.Empty;
            record.IsCoCreationTemplate = draft.IsCoCreationTemplate || existing?.IsCoCreationTemplate is true;
            record.TemplateOrigin = existing?.TemplateOrigin ?? draft.TemplateOrigin;
            record.AppearancePalette = record.IsCoCreationTemplate ? draft.AppearancePalette?.Normalize() : null;
            record.Source = string.Equals(draft.Source, "survey", StringComparison.Ordinal)
                ? "survey"
                : "manual";
            // An open editor may predate publication or legacy-copy repair.
            // Existing catalog provenance is authoritative when saving edits.
            if (existing is null)
            {
                record.AcquisitionKind = draft.AcquisitionKind;
                record.SubscriptionId = draft.SubscriptionId;
                record.SubscriptionPublisherHandle = draft.SubscriptionPublisherHandle;
                record.SubscriptionPublisherIsOfficial = draft.SubscriptionPublisherIsOfficial;
                record.SubscriptionPublisherIsBuilder = draft.SubscriptionPublisherIsBuilder;
                record.SubscriptionPublisherKeyId = draft.SubscriptionPublisherKeyId;
                record.SubscriptionVersion = draft.SubscriptionVersion;
            }
            record.SourceProjectId = draft.SourceProjectId;
            record.SourceProjectRevision = draft.SourceProjectRevision;
            record.SourceVisualSha256 = draft.SourceVisualSha256;
            record.SourceStructureSha256 = draft.SourceStructureSha256;
            record.ContentVersion = existing is null
                ? Math.Max(1, draft.ContentVersion)
                : Math.Max(1, existing.ContentVersion + 1);
            record.PortableGates = draft.PortableGates
                .Select(gate => gate.Clone())
                .ToList();
            record.Navigation = navigation;
            record.Tags = (draft.Tags ?? [])
                .Where(pair => pair.Key != Guid.Empty && !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value.Trim());
            record.UpdatedAt = DateTimeOffset.UtcNow;

            // A draft can only be saved into an existing canonical class.
            var requestedClass = NormalizeClassName(draft.Class) ?? "S1";
            var targetClass = catalog.Classes.SingleOrDefault(candidate => string.Equals(
                candidate, requestedClass, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("所选 Class 已不存在，请返回列表后重试。");
            if (isNewRecord)
            {
                var classMaps = catalog.Maps.Where(map => string.Equals(
                    map.Class, targetClass, StringComparison.OrdinalIgnoreCase)).ToArray();
                var nextByCount = classMaps.Length + 1;
                record.SequenceNumber = classMaps.Any(map => map.SequenceNumber == nextByCount)
                    ? Math.Max(nextByCount, classMaps.Max(map => map.SequenceNumber) + 1)
                    : nextByCount;
            }
            if (existing is not null
                && !string.Equals(existing.Class, targetClass, StringComparison.OrdinalIgnoreCase)
                && catalog.VariantGroups.Any(group => group.MapIds.Contains(existing.Id)))
            {
                throw new InvalidOperationException("已绑定变体的地图不能移动到其他 Class，请先解绑变体组合。");
            }
            record.Class = targetClass;
            var classDownsampleFactor = MapRepository.ClampImageDownsampleFactor(
                GetClassProperties(catalog, targetClass).ImageDownsampleFactor);
            if (draft.IsCoCreationTemplate && classDownsampleFactor != 0)
                throw new InvalidOperationException("共创模板的固定轮廓不支持降采样，请选择未降采样的地图类。");
            if (draft.Floors.Count > 0)
                record.Floors = draft.Floors
                    .OrderBy(floor => floor.SortOrder)
                    .ThenBy(floor => floor.Key, StringComparer.Ordinal)
                    .Select((floor, index) => new FloorDefinition
                    {
                        Key = floor.Key,
                        DisplayName = floor.DisplayName,
                        SortOrder = index + 1,
                        MarkerKeys = MapFloorMarkerRules.Normalize(floor.MarkerKeys).ToList(),
                        PrebuiltStructureLine = floor.PrebuiltStructureLine?.Clone()
                    })
                    .ToList();
            record.Recognition = draft.Recognition.Clone();
            if (record.Navigation is not null && MapRouteRules.MapContainsVectorRoutes(record))
            {
                var routeProperties = GetClassProperties(catalog, record.Class);
                routeProperties.ContainsVectorRoutes = true;
                catalog.ClassProperties[record.Class] = routeProperties;
            }
            record.NormalizeRecognition();
            ValidateFloorDefinitions(record);

            Directory.CreateDirectory(_rootDirectory);
            stagingDirectory = Path.Combine(_rootDirectory, $".pending-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);
            var orderedFloorKeys = record.Floors
                .OrderBy(floor => floor.SortOrder)
                .ThenBy(floor => floor.Key, StringComparer.Ordinal)
                .Select(floor => floor.Key)
                .ToArray();
            if (isNewRecord && classDownsampleFactor != 0)
                ScaleBackgroundBrushes(record.Recognition, 0, classDownsampleFactor);

            async Task<string> CopyFloorInputAsync(string key, string path)
            {
                if (isNewRecord && classDownsampleFactor != 0)
                {
                    var index = Array.IndexOf(orderedFloorKeys, key);
                    var originals = GetDownsampleOriginalDirectory(
                        Path.Combine(_rootDirectory, ".downsample-originals"), record.Id);
                    Directory.CreateDirectory(originals);
                    path = EnsureOriginalImage(
                        path,
                        originals,
                        $"floor-{index + 1:D3}-visual",
                        createdDownsampleOriginals);
                    path = await CreateDownsampleInputAsync(
                        path,
                        stagingDirectory,
                        $"floor-{index + 1:D3}-visual-input",
                        classDownsampleFactor,
                        CancellationToken.None);
                }
                return await CopyImageToDirectoryAsync(path, stagingDirectory, GetFloorImageFilePrefix(key));
            }

            // Copy each floor's original image by its explicit floor key.
            var floorImageFileNames = new Dictionary<string, string>();
            foreach (var (key, path) in draft.FloorPaths
                .Where(kvp => IsSupportedImage(kvp.Value) && File.Exists(kvp.Value))
                .OrderBy(kvp => draft.Floors.FirstOrDefault(f => f.Key == kvp.Key)?.SortOrder ?? 99)
                .ThenBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                floorImageFileNames[key] = await CopyFloorInputAsync(key, path);
            }
            // Legacy draft fields are positional fallbacks only. Resolve them
            // to the actual ordered floor keys before persisting any file.
            for (var index = 0; index < orderedFloorKeys.Length && index < 2; index++)
            {
                var key = orderedFloorKeys[index];
                if (floorImageFileNames.ContainsKey(key))
                    continue;
                var legacyPath = index == 0 ? draft.FloorOnePath : draft.FloorTwoPath;
                if (IsSupportedImage(legacyPath) && File.Exists(legacyPath!))
                    floorImageFileNames[key] = await CopyFloorInputAsync(key, legacyPath!);
            }
            // Legacy readers still resolve the first two floors through these
            // fields, so keep them aligned with the reordered floor list even
            // when the current IDs are custom names.
            if (orderedFloorKeys.Length > 0
                && floorImageFileNames.TryGetValue(orderedFloorKeys[0], out var firstFloorFileName))
            {
                record.FloorOneFileName = firstFloorFileName;
            }
            if (orderedFloorKeys.Length > 1
                && floorImageFileNames.TryGetValue(orderedFloorKeys[1], out var secondFloorFileName))
            {
                record.FloorTwoFileName = secondFloorFileName;
            }

            await PopulateFloorImageMetadataAsync(
                record,
                stagingDirectory,
                floorImageFileNames);
            if (record.Navigation is not null)
                await MapNavigationRules.RebindGeneratedImagesAsync(record.Navigation,
                    floorImageFileNames.ToDictionary(pair => pair.Key, pair => Path.Combine(stagingDirectory, pair.Value)));

            MapOperationProgress.Report(.25, "正在生成地图资源……");
            var processedFloors = 0;
            // Generate and explicitly bind both derived assets for each floor.
            foreach (var (key, fileName) in floorImageFileNames)
            {
                MapOperationProgress.Report(.25 + .65 * processedFloors++ / Math.Max(1, floorImageFileNames.Count), "正在处理地图楼层……");
                var sourcePath = Path.Combine(stagingDirectory, fileName);
                if (!File.Exists(sourcePath)) continue;
                var floor = record.Floors.Single(candidate => candidate.Key == key);
                var profile = record.Recognition.GetFloor(key)!;
                var recognitionFileName = GetFloorRecognitionFileName(key);
                var overlayFileName = GetFloorOverlayFileName(key);
                var recognitionPath = Path.Combine(stagingDirectory, recognitionFileName);
                var overlayPath = Path.Combine(stagingDirectory, overlayFileName);
                var thumbnailPath = Path.Combine(stagingDirectory, GetFloorThumbnailFileName(key));
                var thumbnailGenerated = false;
                var hasSurveyStructure = draft.FloorRecognitionSourcePaths.TryGetValue(
                    key,
                    out var surveyStructurePath)
                    && IsSupportedImage(surveyStructurePath)
                    && File.Exists(surveyStructurePath);
                if (hasSurveyStructure && isNewRecord && classDownsampleFactor != 0)
                {
                    var index = Array.IndexOf(orderedFloorKeys, key);
                    var originals = GetDownsampleOriginalDirectory(
                        Path.Combine(_rootDirectory, ".downsample-originals"), record.Id);
                    Directory.CreateDirectory(originals);
                    surveyStructurePath = EnsureOriginalImage(
                        surveyStructurePath!,
                        originals,
                        $"floor-{index + 1:D3}-recognition",
                        createdDownsampleOriginals);
                    surveyStructurePath = await CreateDownsampleInputAsync(
                        surveyStructurePath,
                        stagingDirectory,
                        $"floor-{index + 1:D3}-recognition-input",
                        classDownsampleFactor,
                        CancellationToken.None);
                }
                var classProperties = GetClassProperties(catalog, record.Class);
                var removeBackground = draft.RemoveBackgroundOverride
                    ?? classProperties.RemoveBackground;
                var backgroundRemovalIntensity = draft.BackgroundRemovalIntensityOverride
                    ?? classProperties.BackgroundRemovalIntensity;
                var needsIndependentRecognition = removeBackground
                    || profile.BackgroundLayers.Count > 0
                    || !UsesWholeSourceImage(profile);
                if (hasSurveyStructure)
                {
                    if (TryWriteOwnedUnmaskedRegionAssets(surveyStructurePath!, recognitionPath,
                            profile, overlayPath, removeBackground, needsIndependentRecognition || !UsesWholeSourceImage(profile),
                            needsIndependentRecognition ? thumbnailPath : null))
                    {
                        thumbnailGenerated = needsIndependentRecognition;
                        if (!needsIndependentRecognition && UsesWholeSourceImage(profile))
                            await CopyRecognitionSourceAsync(surveyStructurePath!, recognitionPath);
                    }
                    else
                    {
                        using var processed = ProcessRecognitionSource(
                            surveyStructurePath!,
                            profile,
                            removeBackground,
                            backgroundRemovalIntensity,
                            "测绘识别结构无法解码。");
                        if (!needsIndependentRecognition && UsesWholeSourceImage(profile))
                            await CopyRecognitionSourceAsync(surveyStructurePath!, recognitionPath);
                        else if (!Cv2.ImWrite(recognitionPath, processed.Recognition))
                            throw new InvalidOperationException("无法保存测绘识别图。");
                        if (!Cv2.ImWrite(overlayPath, processed.Overlay))
                            throw new InvalidOperationException("无法保存测绘透明叠加图。");
                        profile.RecognitionPixelWidth = processed.Recognition.Width;
                        profile.RecognitionPixelHeight = processed.Recognition.Height;
                        profile.ValidMapBounds ??= MapReferenceBounds.FullImage(
                            processed.Recognition.Width,
                            processed.Recognition.Height);
                    }
                }
                else
                {
                    thumbnailGenerated = CreateRecognitionAssets(
                        sourcePath,
                        recognitionPath,
                        profile,
                        overlayPath,
                        removeBackground,
                        backgroundRemovalIntensity,
                        needsIndependentRecognition ? thumbnailPath : null);
                }
                await PopulateDerivedImageMetadataAsync(
                    floor,
                    sourcePath,
                    recognitionPath,
                    overlayPath,
                    profile,
                    forceRecognitionPath: hasSurveyStructure || needsIndependentRecognition);
                if (draft.IsCoCreationTemplate)
                    await ImportFixedTemplateStructureAsync(stagingDirectory, floor, key, draft);
                else
                    await ImportPrebuiltStructureLineAsync(stagingDirectory, floor, key, draft);

                // 侧门特征预处理：若侧门锚点已标注，生成特征图
                // Imported feature metadata does not include the current source
                // hash or algorithm version. Generate against the staged prebuilt.
                await TryGenerateSideEntranceFeatureAsync(stagingDirectory, profile, floor.PrebuiltStructureLine?.FileName);

                var recognitionSourcePath = needsIndependentRecognition || !UsesWholeSourceImage(profile)
                    ? Path.Combine(stagingDirectory, recognitionFileName)
                    : sourcePath;
                if (!thumbnailGenerated)
                    await CreateThumbnailAsync(recognitionSourcePath, thumbnailPath);
                await PopulateThumbnailMetadataAsync(floor, thumbnailPath);

                // Keep the legacy first/second asset names aligned with the
                // ordered floor list, even when those floors use custom IDs
                // or the literal 1f/2f IDs have been reordered.
                var orderedPosition = Array.IndexOf(orderedFloorKeys, key);
                var compatibilityRecognitionFileName = orderedPosition switch
                {
                    0 => FloorOneRecognitionFileName,
                    1 => FloorTwoRecognitionFileName,
                    _ => null
                };
                var compatibilityOverlayFileName = orderedPosition switch
                {
                    0 => FloorOneOverlayFileName,
                    1 => FloorTwoOverlayFileName,
                    _ => null
                };
                if (compatibilityRecognitionFileName is not null
                    && compatibilityOverlayFileName is not null
                    && (!string.Equals(recognitionFileName, compatibilityRecognitionFileName, StringComparison.Ordinal)
                        || !string.Equals(overlayFileName, compatibilityOverlayFileName, StringComparison.Ordinal)))
                {
                    var compatibilityRecognitionPath = Path.Combine(
                        stagingDirectory,
                        compatibilityRecognitionFileName);
                    var compatibilityOverlayPath = Path.Combine(
                        stagingDirectory,
                        compatibilityOverlayFileName);
                    if (hasSurveyStructure)
                    {
                        await CopyRecognitionSourceAsync(
                            recognitionPath,
                            compatibilityRecognitionPath);
                        using var compatibilityImage = DecodeImage(compatibilityRecognitionPath);
                        CreateWhiteKeyOverlay(compatibilityImage, compatibilityOverlayPath);
                    }
                    else
                    {
                        CreateRecognitionAssets(
                            sourcePath,
                            compatibilityRecognitionPath,
                            profile,
                            compatibilityOverlayPath,
                            removeBackground,
                            backgroundRemovalIntensity);
                    }
                }
            }
            PreserveNavigationSnapshots(record.Id, stagingDirectory);
            targetDirectory = GetMapDirectory(record.Id);
            if (Directory.Exists(targetDirectory))
            {
                backupDirectory = Path.Combine(_rootDirectory, $".backup-{record.Id:N}");
                if (Directory.Exists(backupDirectory))
                    Directory.Delete(backupDirectory, recursive: true);
                Directory.Move(targetDirectory, backupDirectory);
            }

            Directory.Move(stagingDirectory, targetDirectory);
            stagingDirectory = null;

            if (existing is null)
                catalog.Maps.Add(record);

            MapOperationProgress.Report(.95, "正在保存地图索引……");
            await WriteCatalogAsync(catalog, importCatalog: importCatalog);

            if (backupDirectory is not null && Directory.Exists(backupDirectory))
                Directory.Delete(backupDirectory, recursive: true);

            return record.Clone();
        }
        catch
        {
            if (targetDirectory is not null && backupDirectory is not null && Directory.Exists(backupDirectory))
            {
                if (Directory.Exists(targetDirectory))
                    Directory.Delete(targetDirectory, recursive: true);
                Directory.Move(backupDirectory, targetDirectory);
            }
            else if (isNewRecord && targetDirectory is not null && Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory, recursive: true);
            }
            foreach (var path in createdDownsampleOriginals)
                if (File.Exists(path)) File.Delete(path);
            throw;
        }
        finally
        {
            if (stagingDirectory is not null && Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
            if (!gateAlreadyHeld)
                Gate.Release();
        }
    }

}
