using System.Security.Cryptography;
using IDVBuff.UpdateCore;

namespace IDVBuff.Features.Maps;

public sealed partial class IdvmPackageService
{
    private static async Task<ManifestDto> ValidatePackageAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var headerPath = Path.Combine(root, "header");
        var manifestPath = Path.Combine(root, "manifest.json");
        if (!File.Exists(headerPath) || !File.Exists(manifestPath))
            throw new InvalidDataException("IDVM 包缺少 header 或 manifest.json。");
        var header = await File.ReadAllBytesAsync(headerPath, cancellationToken);
        if (header.Length != HeaderSize)
            throw new InvalidDataException("IDVM header 长度必须为 80 字节。");
        var parsedHeader = ParseHeader(header);
        var manifestBytes = await ReadLimitedBytesAsync(manifestPath, MaxJsonBytes, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(
            parsedHeader.ManifestSha256,
            SHA256.HashData(manifestBytes)))
        {
            throw new InvalidDataException("manifest.json 的 SHA-256 与 header 不一致。");
        }

        var manifest = Deserialize<ManifestDto>(manifestBytes, "manifest.json");
        if (manifest.Classes is null || manifest.Maps is null
            || manifest.VariantGroups is null || manifest.Files is null
            || manifest.Capabilities is null)
        {
            throw new InvalidDataException(
                "manifest 的 classes、maps、variantGroups、files 和 capabilities 必须有效。");
        }
        var isVersion10 = parsedHeader.MajorVersion == 1
            && parsedHeader.MinorVersion == 0
            && manifest.FormatVersion == "1.0"
            && manifest.MinimumReader == "1.0";
        var isVersion11 = parsedHeader.MajorVersion == 1
            && parsedHeader.MinorVersion == 1
            && manifest.FormatVersion == "1.1"
            && manifest.MinimumReader == "1.1";
        var isVersion12 = parsedHeader.MajorVersion == 1
            && parsedHeader.MinorVersion == 2
            && manifest.FormatVersion == "1.2"
            && manifest.MinimumReader == "1.2";
        var isVersion13 = parsedHeader.MajorVersion == 1
            && parsedHeader.MinorVersion == 3
            && manifest.FormatVersion == "1.3"
            && manifest.MinimumReader == "1.3";
        var isVersion14 = parsedHeader.MajorVersion == 1
            && parsedHeader.MinorVersion == 4
            && manifest.FormatVersion == "1.4"
            && manifest.MinimumReader == "1.4";
        var isVersion15 = parsedHeader.MajorVersion == 1
            && parsedHeader.MinorVersion == 5
            && manifest.FormatVersion == "1.5"
            && manifest.MinimumReader == "1.5";
        var isVersion16 = parsedHeader.MinorVersion == 6
            && manifest.FormatVersion == "1.6" && manifest.MinimumReader == "1.6";
        if (!isVersion16 && manifest.Capabilities.NavigationTopology != isVersion15)
            throw new InvalidDataException("导航能力必须声明为 IDVM 1.5 或更高版本。");
        if (isVersion16 ? manifest.Compression != SolidCompression : manifest.Compression is not null)
            throw new InvalidDataException("IDVM 压缩方式与格式版本不一致。");
        if (manifest.Format != "idvm"
            || (!isVersion10 && !isVersion11 && !isVersion12 && !isVersion13 && !isVersion14 && !isVersion15 && !isVersion16)
            || manifest.PackageType != "class-set")
        {
            throw new InvalidDataException("不支持的 IDVM 格式或读取器版本。");
        }
        if (isVersion10 && manifest.VariantGroups.Count != 0)
            throw new InvalidDataException("IDVM 1.0 包不能声明变体组合。");
        if (isVersion11 && !manifest.Capabilities.VariantGroups)
            throw new InvalidDataException("IDVM 1.1 包必须声明 variantGroups 能力。");
        if (isVersion12 && !manifest.Capabilities.FloorMarkerKeys)
            throw new InvalidDataException("IDVM 1.2 包必须声明 floorMarkerKeys 能力。");
        if (isVersion13 && (!manifest.Capabilities.FloorMarkerKeys || !manifest.Capabilities.MapTags))
            throw new InvalidDataException("IDVM 1.3 包必须声明 floorMarkerKeys 和 mapTags 能力。");
        if (isVersion14 || isVersion15 || isVersion16)
        {
            if (!manifest.Capabilities.FloorMarkerKeys || !manifest.Capabilities.MapTags)
                throw new InvalidDataException("IDVM 1.4 包必须声明 floorMarkerKeys 和 mapTags 能力。");
            ValidateSupportedPlatforms(manifest.SupportedPlatforms);
            if (!IdvmPlatformCompatibility.SupportsWindows(manifest.SupportedPlatforms))
                throw new IdvmPlatformNotSupportedException(manifest.SupportedPlatforms!);
            var routeClasses = manifest.Classes.Count(item => item.Properties?.ContainsVectorRoutes is true);
            if ((manifest.Capabilities.ContainsVectorRoutes is true) != (routeClasses > 0))
                throw new InvalidDataException("IDVM 1.4 包的矢量路线能力与地图类声明不一致。");
        }
        if (manifest.PackageId == Guid.Empty || manifest.PackageId != parsedHeader.PackageId)
            throw new InvalidDataException("header 与 manifest 的 packageId 不一致。");
        if (manifest.CreatedAt.ToUnixTimeMilliseconds() != parsedHeader.CreatedAtUnixMilliseconds)
            throw new InvalidDataException("header 与 manifest 的创建时间不一致。");
        if (manifest.Classes.Count is < 1 or > 256 || manifest.Maps.Count is < 1 or > 4096)
            throw new InvalidDataException("IDVM 包的 Class 或地图数量超出限制。");

        ValidateExportLimits(manifest, manifestBytes.Length);
        ValidateManifestRelationships(manifest, isVersion12 || isVersion13 || isVersion14 || isVersion15 || isVersion16);
        if (isVersion16)
            await RestoreSolidPayloadAsync(root, manifest, cancellationToken);
        else if (manifest.Files.Any(file => file.StorageOffset is not null))
            throw new InvalidDataException("旧版 IDVM 不允许声明压缩存储偏移。");

        var actualFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => ToLogicalPath(root, path))
            .Where(path => path is not "header" and not "manifest.json")
            .ToHashSet(StringComparer.Ordinal);
        var declaredPaths = new HashSet<string>(StringComparer.Ordinal);
        var verifiedFiles = 0;
        foreach (var file in manifest.Files)
        {
            MapOperationProgress.Report((isVersion16 ? .7 : 0) + (isVersion16 ? .3 : 1) * verifiedFiles++ / manifest.Files.Count, "正在验证地图资源……");
            ValidateLogicalPath(file.Path);
            if (!file.Path.StartsWith("maps/", StringComparison.Ordinal)
                || !declaredPaths.Add(file.Path))
                throw new InvalidDataException($"manifest 包含重复或未知路径：{file.Path}");
            var path = ToPhysicalPath(root, file.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size)
                throw new InvalidDataException($"文件大小不匹配：{file.Path}");
            var hash = await ComputeSha256Async(path, cancellationToken);
            if (!IsSha256(file.Sha256)
                || !string.Equals(hash, file.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"文件摘要不匹配：{file.Path}");
        }
        if (!actualFiles.SetEquals(declaredPaths))
            throw new InvalidDataException("manifest 文件清单与包内容不一致。");

        return manifest;
    }

    private static void ValidateManifestRelationships(
        ManifestDto manifest,
        bool allowMarkers)
    {
        var classIds = new HashSet<Guid>();
        var classNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mapIds = new HashSet<Guid>();
        foreach (var item in manifest.Classes)
        {
            if (item is null || item.MapIds is null
                || item.ClassId == Guid.Empty || !classIds.Add(item.ClassId)
                || string.IsNullOrWhiteSpace(item.Name) || !classNames.Add(item.Name.Trim())
                || item.MapIds.Count == 0 || item.MapIds.Count != item.MapIds.Distinct().Count())
            {
                throw new InvalidDataException("manifest 包含无效或重复的 Class。");
            }
        }
        foreach (var map in manifest.Maps)
        {
            if (map is null || map.Floors is null
                || map.MapId == Guid.Empty || !mapIds.Add(map.MapId) || map.MapVersion <= 0
                || !classIds.Contains(map.ClassId) || string.IsNullOrWhiteSpace(map.Name))
                throw new InvalidDataException("manifest 包含无效或重复的地图。");
            var expectedRoot = $"maps/{map.MapId:N}";
            if (!string.Equals(map.Root, expectedRoot, StringComparison.Ordinal))
                throw new InvalidDataException($"地图 {map.MapId} 的资源根路径无效。");
            ValidateFloors(map.Floors, expectedRoot, allowMarkers);
        }
        foreach (var item in manifest.Classes)
        {
            var expected = manifest.Maps.Where(map => map.ClassId == item.ClassId)
                .Select(map => map.MapId).ToHashSet();
            if (!expected.SetEquals(item.MapIds))
                throw new InvalidDataException($"Class “{item.Name}”的地图索引不一致。");
        }
        if (manifest.Classes.SelectMany(item => item.MapIds).Count() != manifest.Maps.Count)
            throw new InvalidDataException("地图必须且只能属于一个 Class。");

        var mapClassById = manifest.Maps.ToDictionary(map => map.MapId, map => map.ClassId);
        var groupIds = new HashSet<Guid>();
        var groupedMapIds = new HashSet<Guid>();
        var paletteSlotsByClass = new Dictionary<Guid, HashSet<int>>();
        var groupCountsByClass = new Dictionary<Guid, int>();
        foreach (var group in manifest.VariantGroups)
        {
            if (group is null || group.MapIds is null
                || group.GroupId == Guid.Empty || !groupIds.Add(group.GroupId)
                || !classIds.Contains(group.ClassId)
                || group.PaletteSlot is < 0 or >= MapVariantGroup.PaletteSize
                || group.MapIds.Count < 2
                || group.MapIds.Count != group.MapIds.Distinct().Count())
            {
                throw new InvalidDataException("manifest 包含无效或重复的变体组合。");
            }

            if (!paletteSlotsByClass.TryGetValue(group.ClassId, out var paletteSlots))
            {
                paletteSlots = [];
                paletteSlotsByClass.Add(group.ClassId, paletteSlots);
            }
            if (!paletteSlots.Add(group.PaletteSlot))
                throw new InvalidDataException("同一 Class 的变体组合颜色槽不能重复。");

            groupCountsByClass.TryGetValue(group.ClassId, out var groupCount);
            if (groupCount >= MapVariantGroup.PaletteSize)
                throw new InvalidDataException("同一 Class 的变体组合不能超过 12 组。");
            groupCountsByClass[group.ClassId] = groupCount + 1;

            foreach (var mapId in group.MapIds)
            {
                if (!mapClassById.TryGetValue(mapId, out var mapClassId)
                    || mapClassId != group.ClassId)
                {
                    throw new InvalidDataException("变体组合包含缺失地图或跨 Class 成员。");
                }
                if (!groupedMapIds.Add(mapId))
                    throw new InvalidDataException("同一地图不能属于多个变体组合。");
            }
        }
    }

    private static void ValidateFloors(
        IReadOnlyList<ManifestFloorDto> floors,
        string root,
        bool allowMarkers)
    {
        if (floors.Count == 0)
            throw new InvalidDataException("地图必须至少包含一个楼层。");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < floors.Count; index++)
        {
            var floor = floors[index];
            if (!IsSafeIdentifier(floor.Key) || !keys.Add(floor.Key)
                || string.IsNullOrWhiteSpace(floor.DisplayName) || floor.SortOrder != index + 1)
                throw new InvalidDataException("地图包含无效的楼层 ID、名称或顺序。");
            ValidateMarkerKeys(floor.MarkerKeys, allowMarkers);
            ValidateLogicalPath(floor.Image);
            if (!floor.Image.StartsWith($"{root}/maps/", StringComparison.Ordinal)
                || !MapRepository.IsSupportedImage(floor.Image))
                throw new InvalidDataException($"楼层 {floor.Key} 的图片路径无效。");
        }
    }

    private static void ValidateMapDocuments(
        ManifestMapDto map,
        MetadataDto metadata,
        GatesDto gates,
        AnchorsDto anchors,
        bool requireFloorMarkerSchema,
        bool requireTagSchema)
    {
        if (metadata.Map is null || metadata.Floors is null || metadata.Tags is null || metadata.Recognition?.WholeImage is null
            || gates.Gates is null || anchors.Floors is null)
            throw new InvalidDataException("地图数据文件缺少必需对象或数组。");
        if (metadata.SchemaVersion is not (1 or 2 or 3) || gates.SchemaVersion != 1
            || anchors.SchemaVersion is not (1 or 2 or 3 or 4))
            throw new InvalidDataException("不支持的数据 schemaVersion。");
        if (requireTagSchema && metadata.SchemaVersion != 3)
            throw new InvalidDataException("IDVM 1.3 地图 metadata 必须使用 schemaVersion 3。");
        if (requireFloorMarkerSchema && !requireTagSchema && metadata.SchemaVersion != 2)
        {
            throw new InvalidDataException(
                "IDVM 1.2 地图 metadata 必须使用 schemaVersion 2。");
        }
        if (!requireFloorMarkerSchema && metadata.SchemaVersion != 1)
            throw new InvalidDataException("IDVM 1.0/1.1 地图 metadata 必须使用 schemaVersion 1。");
        ValidateTags(metadata.Tags, requireTagSchema);
        if (metadata.Map.Id != map.MapId || metadata.Map.ClassId != map.ClassId
            || metadata.Map.CoordinateSystem != "normalized-top-left-y-down"
            || string.IsNullOrWhiteSpace(metadata.Map.Title))
            throw new InvalidDataException($"地图 {map.MapId} 的 metadata 标识不一致。");
        if (metadata.Floors.Count != map.Floors.Count)
            throw new InvalidDataException($"地图 {map.MapId} 的楼层清单不一致。");
        var backgroundLayerIds = new HashSet<Guid>();
        for (var index = 0; index < map.Floors.Count; index++)
        {
            var declared = map.Floors[index];
            var floor = metadata.Floors[index];
            if (floor.Key != declared.Key || floor.DisplayName != declared.DisplayName
                || floor.SortOrder != declared.SortOrder || floor.Image != declared.Image
                || !MapFloorMarkerRules.Normalize(floor.MarkerKeys).SequenceEqual(
                    MapFloorMarkerRules.Normalize(declared.MarkerKeys),
                    StringComparer.Ordinal)
                || floor.ImageWidth <= 0 || floor.ImageHeight <= 0
                || floor.OrientationDegrees is not (0 or 90 or 180 or 270)
                || !anchors.Floors.ContainsKey(floor.Key))
            {
                throw new InvalidDataException($"地图 {map.MapId} 的楼层 metadata 无效。");
            }
            ValidateMarkerKeys(floor.MarkerKeys, requireFloorMarkerSchema);
            ValidateRectangle(floor.RecognitionRegion, allowNull: true, "recognitionRegion");
            if (floor.FreeCropPoints.Count is 1 or 2)
                throw new InvalidDataException("freeCropPoints 必须为空或至少包含三个点。");
            foreach (var point in floor.FreeCropPoints)
                ValidatePoint(point, "freeCropPoints");
            ValidateRectangle(floor.ValidMapBounds, allowNull: false, "validMapBounds");
            if (!string.IsNullOrWhiteSpace(floor.RecognitionImage))
            {
                ValidateLogicalPath(floor.RecognitionImage);
                if (!floor.RecognitionImage.StartsWith($"{map.Root}/data/", StringComparison.Ordinal)
                    || !MapRepository.IsSupportedImage(floor.RecognitionImage))
                {
                    throw new InvalidDataException(
                        $"Floor {floor.Key} has an invalid recognition image path.");
                }
            }
            if (floor.PrebuiltStructureLine is { } prebuilt)
            {
                ValidatePrebuiltStructureLine(map, floor, prebuilt);
                var recognitionSize = GetRecognitionSize(floor, floor.ImageWidth, floor.ImageHeight);
                if (prebuilt.Width != recognitionSize.Width
                    || prebuilt.Height != recognitionSize.Height)
                    throw new InvalidDataException($"Floor {floor.Key} 的预制线图尺寸无效。");
            }
            var anchorFloor = anchors.Floors[floor.Key];
            foreach (var region in anchorFloor.WholeImageIgnoreRegions)
                ValidateRectangle(region, allowNull: false, "wholeImageIgnoreRegions");
            var anchorIds = new HashSet<Guid>();
            var anchorKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var anchor in anchorFloor.Anchors)
            {
                if (anchor.Id == Guid.Empty || !anchorIds.Add(anchor.Id)
                    || !IsSafeIdentifier(anchor.Key) || !anchorKeys.Add(anchor.Key)
                    || string.IsNullOrWhiteSpace(anchor.DisplayName)
                    || anchor.Role is not ("required" or "optional")
                    || !double.IsFinite(anchor.Weight) || anchor.Weight < 0d
                    || (anchor.Role == "required"
                        && string.IsNullOrWhiteSpace(anchor.GateId)
                        && anchor.Bounds is null))
                    throw new InvalidDataException($"楼层 {floor.Key} 包含无效锚点。");
                ValidateRectangle(
                    anchor.Bounds,
                    allowNull: anchor.Role == "optional" || !string.IsNullOrWhiteSpace(anchor.GateId),
                    "anchor.bounds");
            }
            foreach (var annotation in anchorFloor.Annotations)
            {
                if (annotation.IsMarker && (annotation.Type != "text" || !MapMarkerTool.IsValidLabel(annotation.Text)))
                    throw new InvalidDataException("地图包含无效标记图案。");
                if (!IsValidAnnotation(annotation, anchors.SchemaVersion))
                    throw new InvalidDataException($"楼层 {floor.Key} 包含无效标注。");
                ValidateAnnotationGeometry(annotation);
                if (annotation.Type == "route" && metadata.Navigation is null)
                    throw new InvalidDataException("自动路线阶段必须具有所属地图的导航数据。");
            }
            if (anchors.SchemaVersion >= 4 && anchorFloor.BackgroundLayers is null)
                throw new InvalidDataException("schema 4 的 anchors.json 必须包含 backgroundLayers 数组。");
            foreach (var layer in anchorFloor.BackgroundLayers ?? [])
            {
                if (anchors.SchemaVersion < 4)
                    throw new InvalidDataException("旧版 anchors.json 不允许包含 backgroundLayers。");
                if (layer is null
                    || layer.Id == Guid.Empty
                    || !backgroundLayerIds.Add(layer.Id)
                    || layer.Semantic != "background"
                    || layer.Shape is not ("circle" or "square")
                    || layer.BrushSizePixels is < 1 or > 1024
                    || layer.Points is not { Count: > 0 })
                {
                    throw new InvalidDataException($"楼层 {floor.Key} 包含无效遮瑕层。");
                }
                foreach (var point in layer.Points)
                    ValidatePoint(point, "backgroundLayer.point");
            }
        }
        if (anchors.Floors.Count != metadata.Floors.Count)
            throw new InvalidDataException("anchors.json 包含未知楼层。");

        var floorKeys = metadata.Floors.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var gateIds = new HashSet<string>(StringComparer.Ordinal);
        var mainFloors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gate in gates.Gates)
        {
            if (!IsSafeIdentifier(gate.Id) || !gateIds.Add(gate.Id)
                || !floorKeys.Contains(gate.FloorKey)
                || gate.Role is not ("mainEntrance" or "sideEntrance" or "exit" or "unknown")
                || !double.IsFinite(gate.DirectionDegrees)
                || !double.IsFinite(gate.Confidence) || gate.Confidence is < 0d or > 1d)
                throw new InvalidDataException("gates.json 包含无效门数据。");
            ValidateRectangle(gate.Bounds, allowNull: false, "gate.bounds");
            if (gate.Role == "mainEntrance" && !mainFloors.Add(gate.FloorKey))
                throw new InvalidDataException("同一楼层只能包含一个 mainEntrance。");
        }
        foreach (var anchor in anchors.Floors.Values.SelectMany(item => item.Anchors))
            if (!string.IsNullOrWhiteSpace(anchor.GateId) && !gateIds.Contains(anchor.GateId))
                throw new InvalidDataException($"锚点 {anchor.Key} 引用了不存在的门。");

        var primaryFloorKey = metadata.Floors[0].Key;
        var primaryAnchors = anchors.Floors[primaryFloorKey].Anchors;
        foreach (var (anchorKey, gateRole) in new[]
        {
            ("main-entrance", "mainEntrance"),
            ("side-entrance", "sideEntrance")
        })
        {
            var gate = gates.Gates.SingleOrDefault(item =>
                item.FloorKey == primaryFloorKey && item.Role == gateRole);
            if (gate is null || !primaryAnchors.Any(anchor =>
                    anchor.Key == anchorKey && anchor.GateId == gate.Id))
            {
                throw new InvalidDataException(
                    $"主楼层必须包含通过 gateId 关联的 {anchorKey} 门锚点。");
            }
        }
    }

    // ── 路径安全验证 ──────────────────────────────────────────────────

    private static void ValidateLogicalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains('\\') || path.Any(char.IsControl)
            || path.Split('/').Any(segment => segment.Length == 0 || segment == "." || segment == ".."
                || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.IndexOfAny([':', '<', '>', '"', '|', '?', '*']) >= 0
                || IsWindowsDeviceName(segment)))
        {
            throw new InvalidDataException($"IDVM 包含不安全路径：{path}");
        }
    }

    private static bool IsWindowsDeviceName(string segment)
    {
        var name = segment.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL"
            || (name.Length == 4 && (name.StartsWith("COM") || name.StartsWith("LPT"))
                && (name[3] is >= '0' and <= '9' or '¹' or '²' or '³'));
    }

    private static string ToPhysicalPath(string root, string logicalPath)
    {
        ValidateLogicalPath(logicalPath);
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, logicalPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"路径越出 IDVM 根目录：{logicalPath}");
        return fullPath;
    }

    private static string ToLogicalPath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string CreateTemporaryDirectory(string purpose)
    {
        var root = Path.Combine(Path.GetTempPath(), "IDVBuff", "IDVM", purpose, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    // ── 验证工具方法 ──────────────────────────────────────────────────

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSafeIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && !value.Contains('/')
        && !value.Contains('\\')
        && value is not "." and not "..";

    private static bool IsValidPortableGate(MapGateDefinition gate) =>
        IsSafeIdentifier(gate.Id)
        && IsSafeIdentifier(gate.FloorKey)
        && gate.Role is "mainEntrance" or "sideEntrance" or "exit" or "unknown"
        && gate.Bounds.IsValid
        && double.IsFinite(gate.DirectionDegrees)
        && double.IsFinite(gate.Confidence)
        && gate.Confidence is >= 0d and <= 1d;

    private static bool IsGateAnchor(string key) => key is "main-entrance" or "side-entrance";
    private static string GateRoleForAnchor(string key) => key == "main-entrance" ? "mainEntrance" : "sideEntrance";

    private static RectangleDto? ToDto(NormalizedRectangle? rectangle) => rectangle is null ? null : new RectangleDto
    {
        X = rectangle.X,
        Y = rectangle.Y,
        Width = rectangle.Width,
        Height = rectangle.Height
    };

    private static PointDto? ToDto(NormalizedPoint? point) => point is null ? null : new PointDto
    {
        X = point.X,
        Y = point.Y
    };

    private static bool IsValidAnnotation(AnnotationDto annotation, int schemaVersion)
    {
        if (annotation.Id == Guid.Empty)
            return false;
        if (schemaVersion == 1)
        {
            return annotation.Type is "text" or "outline"
                && annotation.ColorIndex is >= 0 and <= 8;
        }
        return annotation.Type is "text" or "outline" or "line" or "route"
            && MapAnnotationColor.TryNormalize(annotation.Color, out _);
    }

}
