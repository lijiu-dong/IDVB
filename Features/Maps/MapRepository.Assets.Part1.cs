using OpenCvSharp;
using System.Security.Cryptography;

namespace IDVBuff.Features.Maps;
public sealed partial class MapRepository
{

    public async Task EnsureDerivedAssetsAsync(IReadOnlyList<MapRecord> maps)
    {
        MapOperationProgress.Report(0, "正在读取地图资源目录……");
        var classProperties = await GetClassPropertiesSnapshotAsync();
        await EnsureDerivedAssetsFromSnapshotAsync(maps, classProperties);
    }

    internal async Task EnsureDerivedAssetsFromSnapshotAsync(IReadOnlyList<MapRecord> maps,
        IReadOnlyDictionary<string, MapClassProperties> classProperties)
    {
        using var memory = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Catalog.EnsureDerived", $"maps={maps.Count}");
        var assetsChanged = await Task.Run(() =>
            {
                var changed = false;
                var totalFloors = Math.Max(1, maps.Sum(map => MapFloorRules.GetOrderedFloors(map).Count));
                var completedFloors = 0;
                foreach (var map in maps)
                {
                    using var mapMemory = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Catalog.ValidateMapAssets", map.Id.ToString());
                    map.NormalizeRecognition();
                    var fixedTemplate = _fixedTemplates.GetFixedStructureAsync(map.SourceVisualSha256).GetAwaiter().GetResult();
                    if (fixedTemplate is not null)
                        fixedTemplate.ValidateGeometry(map.Floors, map.Recognition);
                    var scanFloorKey = MapScanFloorRules.ResolveScanFloorKey(map);
                    if (fixedTemplate is null && !map.Recognition.HasRequiredIdentificationData()
                        && !MapScanFloorRules.HasRequiredScanMarkers(
                            map,
                            scanFloorKey))
                    {
                        completedFloors += MapFloorRules.GetOrderedFloors(map).Count;
                        MapOperationProgress.Report(.95 * completedFloors / totalFloors, "正在校验地图资源……");
                        continue;
                    }

                    foreach (var floor in MapFloorRules.GetOrderedFloors(map))
                    {
                        MapOperationProgress.Report(.95 * completedFloors++ / totalFloors,
                            $"正在校验地图资源：{map.DisplayName} · {floor.DisplayName}……");
                        var profile = MapFloorRules.GetFloorProfile(map, floor.Key);
                        if (profile is null)
                            continue;

                        var previousWidth = profile.RecognitionPixelWidth;
                        var previousHeight = profile.RecognitionPixelHeight;
                        var removeBackground = classProperties.TryGetValue(
                            map.Class,
                            out var properties)
                            && properties.RemoveBackground;
                        var backgroundRemovalIntensity = properties is null
                            ? MapBackgroundProcessor.DefaultBackgroundRemovalIntensity
                            : properties.BackgroundRemovalIntensity;
                        var needsIndependentRecognition = removeBackground
                            || profile.BackgroundLayers.Count > 0
                            || !UsesWholeSourceImage(profile);
                        var sourcePath = GetFloorImagePath(map, floor.Key);
                        var recognitionPath = needsIndependentRecognition
                            ? Path.Combine(
                                GetMapDirectory(map.Id),
                                GetFloorRecognitionFileName(floor.Key))
                            : GetFloorRecognitionPath(map, floor.Key);
                        var overlayPath = GetOriginalFloorOverlayPath(map, floor.Key);
                        if (!File.Exists(sourcePath))
                            continue;

                        var recognitionMatches = MatchesStoredDerivedMetadata(
                            recognitionPath,
                            floor.RecognitionSha256,
                            floor.RecognitionWidth,
                            floor.RecognitionHeight,
                            floor.RecognitionFileLength,
                            floor.RecognitionLastWriteUtcTicks,
                            floor.ImageSha256,
                            requiresFile: needsIndependentRecognition,
                            out var recognitionMetadata);
                        recognitionMatches &= string.Equals(
                            floor.RecognitionSourceSha256,
                            floor.ImageSha256,
                            StringComparison.OrdinalIgnoreCase);
                        var overlayMatches = MatchesStoredDerivedMetadata(
                            overlayPath,
                            floor.OverlaySha256,
                            floor.OverlayWidth,
                            floor.OverlayHeight,
                            floor.OverlayFileLength,
                            floor.OverlayLastWriteUtcTicks,
                            floor.RecognitionSha256,
                            requiresFile: true,
                            out var overlayMetadata)
                            && string.Equals(
                                floor.OverlaySourceSha256,
                                floor.RecognitionSha256,
                                StringComparison.OrdinalIgnoreCase);
                        // A byte-identical file can acquire a new timestamp when
                        // copied/restored. Persist its fully verified stamp once,
                        // so later matches do not decompress the same PNG again.
                        if (recognitionMatches && recognitionMetadata is { } refreshedRecognition)
                        {
                            floor.RecognitionFileLength = refreshedRecognition.FileLength;
                            floor.RecognitionLastWriteUtcTicks = refreshedRecognition.LastWriteUtcTicks;
                            changed = true;
                        }
                        if (overlayMatches && overlayMetadata is { } refreshedOverlay)
                        {
                            floor.OverlayFileLength = refreshedOverlay.FileLength;
                            floor.OverlayLastWriteUtcTicks = refreshedOverlay.LastWriteUtcTicks;
                            changed = true;
                        }
                        if (!recognitionMatches
                            || !overlayMatches
                            || floor.RecognitionWidth != profile.RecognitionPixelWidth
                            || floor.RecognitionHeight != profile.RecognitionPixelHeight)
                        {
                            CreateRecognitionAssets(
                                sourcePath,
                                recognitionPath,
                                profile,
                                overlayPath,
                                removeBackground,
                                backgroundRemovalIntensity);
                            PopulateDerivedImageMetadataAsync(
                                floor,
                                sourcePath,
                                recognitionPath,
                                overlayPath,
                                profile,
                                forceRecognitionPath: needsIndependentRecognition).GetAwaiter().GetResult();
                            changed = true;
                        }

                        // Restore the current fixed contour before deriving scan
                        // features. Its changed hash invalidates binary-era features
                        // before either scanning or alignment can cache them.
                        if (fixedTemplate is not null)
                            changed |= RestoreFixedTemplateFloorAsync(map, floor, fixedTemplate).GetAwaiter().GetResult();
                        else if (map.IsCoCreationTemplate)
                            changed |= RebindPortableFixedFloorAsync(map, floor).GetAwaiter().GetResult();
                        changed |= EnsureCurrentSideEntranceFeature(
                            map,
                            floor.Key,
                            profile,
                            recognitionPath);

                        changed |= previousWidth != profile.RecognitionPixelWidth
                            || previousHeight != profile.RecognitionPixelHeight;
                    }
                }
                return changed;
            });

        MapOperationProgress.Report(.95, "正在保存地图资源目录……");
        if (assetsChanged)
        {
            await Gate.WaitAsync();
            try
            {
                var catalog = await ReadCatalogAsync();
                foreach (var map in maps)
                {
                    var stored = catalog.Maps.FirstOrDefault(candidate => candidate.Id == map.Id);
                    if (stored is null || stored.TemplateOrigin != map.TemplateOrigin)
                        continue;
                    stored.IsCoCreationTemplate |= map.IsCoCreationTemplate;
                    foreach (var floor in MapFloorRules.GetOrderedFloors(map))
                    {
                        var sourceProfile = MapFloorRules.GetFloorProfile(map, floor.Key);
                        var storedProfile = MapFloorRules.GetFloorProfile(stored, floor.Key);
                        if (sourceProfile is null || storedProfile is null)
                            continue;
                        storedProfile.RecognitionPixelWidth = sourceProfile.RecognitionPixelWidth;
                        storedProfile.RecognitionPixelHeight = sourceProfile.RecognitionPixelHeight;
                        storedProfile.ValidMapBounds = sourceProfile.ValidMapBounds?.Clone();
                        storedProfile.SideEntranceFeatureFileName =
                            sourceProfile.SideEntranceFeatureFileName;
                        storedProfile.SideEntranceFeatureSha256 =
                            sourceProfile.SideEntranceFeatureSha256;
                        storedProfile.SideEntranceFeatureSourceSha256 =
                            sourceProfile.SideEntranceFeatureSourceSha256;
                        storedProfile.SideEntranceFeatureAlgorithmVersion =
                            sourceProfile.SideEntranceFeatureAlgorithmVersion;
                        storedProfile.SideEntranceFeatureCenterX =
                            sourceProfile.SideEntranceFeatureCenterX;
                        storedProfile.SideEntranceFeatureCenterY =
                            sourceProfile.SideEntranceFeatureCenterY;
                        storedProfile.SideEntranceFeatureRadius =
                            sourceProfile.SideEntranceFeatureRadius;

                        var storedFloor = stored.Floors.FirstOrDefault(
                            candidate => string.Equals(
                                candidate.Key,
                                floor.Key,
                                StringComparison.Ordinal));
                        if (storedFloor is not null)
                        {
                            var sourceFloor = map.Floors.First(candidate => string.Equals(
                                candidate.Key,
                                floor.Key,
                                StringComparison.Ordinal));
                            storedFloor.ImageFileName = sourceFloor.ImageFileName;
                            storedFloor.ImageSha256 = sourceFloor.ImageSha256;
                            storedFloor.ImageWidth = sourceFloor.ImageWidth;
                            storedFloor.ImageHeight = sourceFloor.ImageHeight;
                            storedFloor.ImageFileLength = sourceFloor.ImageFileLength;
                            storedFloor.ImageLastWriteUtcTicks = sourceFloor.ImageLastWriteUtcTicks;
                            storedFloor.RecognitionFileName = sourceFloor.RecognitionFileName;
                            storedFloor.RecognitionSha256 = sourceFloor.RecognitionSha256;
                            storedFloor.RecognitionSourceSha256 = sourceFloor.RecognitionSourceSha256;
                            storedFloor.RecognitionWidth = sourceFloor.RecognitionWidth;
                            storedFloor.RecognitionHeight = sourceFloor.RecognitionHeight;
                            storedFloor.RecognitionFileLength = sourceFloor.RecognitionFileLength;
                            storedFloor.RecognitionLastWriteUtcTicks = sourceFloor.RecognitionLastWriteUtcTicks;
                            storedFloor.OverlayFileName = sourceFloor.OverlayFileName;
                            storedFloor.OverlaySha256 = sourceFloor.OverlaySha256;
                            storedFloor.OverlaySourceSha256 = sourceFloor.OverlaySourceSha256;
                            storedFloor.OverlayWidth = sourceFloor.OverlayWidth;
                            storedFloor.OverlayHeight = sourceFloor.OverlayHeight;
                            storedFloor.OverlayFileLength = sourceFloor.OverlayFileLength;
                            storedFloor.OverlayLastWriteUtcTicks = sourceFloor.OverlayLastWriteUtcTicks;
                            storedFloor.PrebuiltStructureLine = sourceFloor.PrebuiltStructureLine?.Clone();
                        }
                    }
                }
                await WriteCatalogAsync(catalog);
            }
            finally
            {
                Gate.Release();
            }
        }
        MapOperationProgress.Report(1, "地图资源校验完成");
    }

    private static void ValidateDraft(MapDraft draft, MapNavigationTopology? navigation)
    {
        if (navigation is not null)
            MapNavigationRules.Validate(navigation, draft.Floors.Select(f => f.Key));
        // V6: validate at least one floor has a valid image
        var validFloorPaths = draft.FloorPaths
            .Where(kvp => IsSupportedImage(kvp.Value) && File.Exists(kvp.Value))
            .ToList();
        // Fallback to legacy FloorOnePath/FloorTwoPath if FloorPaths is empty
        if (validFloorPaths.Count == 0)
        {
            if (IsSupportedImage(draft.FloorOnePath) && File.Exists(draft.FloorOnePath))
                validFloorPaths.Add(new KeyValuePair<string, string>("1f", draft.FloorOnePath!));
            if (IsSupportedImage(draft.FloorTwoPath) && File.Exists(draft.FloorTwoPath))
                validFloorPaths.Add(new KeyValuePair<string, string>("2f", draft.FloorTwoPath!));
        }

        if (validFloorPaths.Count == 0)
            throw new InvalidOperationException("请至少选择一个有效的地图图片（PNG、JPG 或 JPEG）。");

        if (draft.Floors.Count > 0)
        {
            var orderedFloors = draft.Floors
                .OrderBy(floor => floor.SortOrder)
                .ThenBy(floor => floor.Key, StringComparer.Ordinal)
                .ToArray();
            for (var index = 0; index < orderedFloors.Length; index++)
            {
                var floor = orderedFloors[index];
                EnsureSafeFloorKey(floor.Key);
                var path = draft.FloorPaths.TryGetValue(floor.Key, out var floorPath)
                    ? floorPath
                    : index switch
                    {
                        0 => draft.FloorOnePath,
                        1 => draft.FloorTwoPath,
                        _ => null
                    };
                if (!IsSupportedImage(path) || !File.Exists(path))
                {
                    throw new InvalidOperationException(
                        $"Floor '{floor.Key}' is missing a valid source image. Select it again before saving.");
                }

                ValidateReadableImage(path!, $"Floor '{floor.Key}'");
            }
        }
        else
        {
            foreach (var (_, path) in validFloorPaths)
                ValidateReadableImage(path, "Map source image");
        }

        if (draft.Floors.Count > 0)
        {
            draft.Recognition.NormalizeForFloors(
                draft.Floors
                    .OrderBy(floor => floor.SortOrder)
                    .ThenBy(floor => floor.Key, StringComparer.Ordinal)
                    .ToArray());
        }
        else
        {
            draft.Recognition.EnsureStandardAnchors();
        }
        var primaryFloorKey = draft.Floors
            .OrderBy(floor => floor.SortOrder)
            .ThenBy(floor => floor.Key, StringComparer.Ordinal)
            .FirstOrDefault()?.Key
            ?? draft.Recognition.FirstFloor.FloorKey;
        if (!draft.Recognition.HasGateMarkers(primaryFloorKey)
            && !(string.Equals(
                    primaryFloorKey,
                    draft.Recognition.FirstFloor.FloorKey,
                    StringComparison.Ordinal)
                && draft.Recognition.HasFirstFloorGateMarkers()))
            throw new InvalidOperationException("请先完成第一张图片的大门和侧门标记。");
    }
}
