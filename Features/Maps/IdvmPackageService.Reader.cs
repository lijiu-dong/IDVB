using OpenCvSharp;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed partial class IdvmPackageService
{
    private static async Task ExtractPackageAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(source))
        {
            await CopyPackageDirectoryAsync(source, destination, cancellationToken);
            return;
        }

        await using var stream = File.OpenRead(source);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaxEntries)
            throw new InvalidDataException("IDVM 包的 ZIP 条目过多。");
        long total = 0;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var extracted = 0;
        foreach (var entry in archive.Entries)
        {
            MapOperationProgress.Report((double)extracted++ / Math.Max(1, archive.Entries.Count), "正在解包地图……");
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                continue;
            ValidateLogicalPath(entry.FullName);
            if (!paths.Add(entry.FullName))
                throw new InvalidDataException($"ZIP 包含重复路径：{entry.FullName}");
            if (entry.Length > (entry.FullName == SolidPayloadPath ? MaxExpandedBytes : MaxSingleFileBytes) || entry.Length < 0)
                throw new InvalidDataException($"ZIP 条目过大：{entry.FullName}");
            total = checked(total + entry.Length);
            if (total > MaxExpandedBytes)
                throw new InvalidDataException("IDVM 包展开后超过 512 MiB。");
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType is not (0 or 0x8000))
                throw new InvalidDataException($"ZIP 条目不是普通文件：{entry.FullName}");
            var target = ToPhysicalPath(destination, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyStreamLimitedAsync(input, output, entry.Length, cancellationToken,
                entry.FullName == SolidPayloadPath ? MaxExpandedBytes : MaxSingleFileBytes);
        }
    }

    private static async Task CopyPackageDirectoryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToArray();
        if (files.Length > MaxEntries)
            throw new InvalidDataException("IDVM 目录包含过多文件。");
        long total = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("IDVM 目录不得包含符号链接或重解析点。");
            var logical = ToLogicalPath(source, file);
            ValidateLogicalPath(logical);
            var length = new FileInfo(file).Length;
            if (length > (logical == SolidPayloadPath ? MaxExpandedBytes : MaxSingleFileBytes))
                throw new InvalidDataException($"文件过大：{logical}");
            total = checked(total + length);
            if (total > MaxExpandedBytes)
                throw new InvalidDataException("IDVM 目录总大小超过 512 MiB。");
            await CopyFileAsync(file, ToPhysicalPath(destination, logical), cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<MapImportClassDraft>> BuildImportDraftsAsync(
        string root,
        ManifestDto manifest,
        CancellationToken cancellationToken)
    {
        var draftsByClass = new Dictionary<Guid, List<MapDraft>>();
        var classPropertiesById = manifest.Classes.ToDictionary(
            item => item.ClassId,
            item => new MapClassProperties
            {
                RemoveBackground = item.Properties?.RemoveBackground is true,
                ContainsVectorRoutes = manifest.FormatVersion is "1.4" or "1.5" or "1.6"
                    ? item.Properties?.ContainsVectorRoutes is true
                    : null,
                ScanFloorKey = MapScanFloorRules.NormalizeFloorIdentity(
                    item.Properties?.ScanFloorKey)
            });
        foreach (var item in manifest.Classes)
            draftsByClass[item.ClassId] = [];

        var parsedMaps = 0;
        foreach (var map in manifest.Maps)
        {
            MapOperationProgress.Report((double)parsedMaps++ / manifest.Maps.Count, "正在解析地图……");
            cancellationToken.ThrowIfCancellationRequested();
            var dataRoot = Path.Combine(root, map.Root.Replace('/', Path.DirectorySeparatorChar), "data");
            var metadata = await ReadJsonAsync<MetadataDto>(Path.Combine(dataRoot, "metadata.json"), cancellationToken);
            var gatesDocument = await ReadJsonAsync<GatesDto>(Path.Combine(dataRoot, "gates.json"), cancellationToken);
            var anchorsDocument = await ReadJsonAsync<AnchorsDto>(Path.Combine(dataRoot, "anchors.json"), cancellationToken);
            ValidateMapDocuments(
                map,
                metadata,
                gatesDocument,
                anchorsDocument,
                manifest.FormatVersion is "1.2" or "1.3" or "1.4" or "1.5" or "1.6",
                manifest.FormatVersion is "1.3" or "1.4" or "1.5" or "1.6");

            var floorDefinitions = new List<FloorDefinition>();
            var floorPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            var profiles = new Dictionary<string, FloorRecognitionProfile>(StringComparer.Ordinal);
            var gates = gatesDocument.Gates.Select(ToModel).ToList();
            var sideEntranceFeaturePaths = new Dictionary<string, string>(StringComparer.Ordinal);
            var prebuiltStructureLinePaths = new Dictionary<string, string>(StringComparer.Ordinal);
            string? prebuiltStructureAlgorithmPath = null;
            for (var index = 0; index < metadata.Floors.Count; index++)
            {
                var floor = metadata.Floors[index];
                var imagePath = ToPhysicalPath(root, floor.Image);
                var image = MapImageFileValidation.ValidateImageFile(imagePath, cancellationToken);
                if (image.Width != floor.ImageWidth || image.Height != floor.ImageHeight)
                    throw new InvalidDataException($"地图“{map.Name}”楼层 {floor.Key} 的图片尺寸不一致。");
                var recognitionSize = GetRecognitionSize(floor, image.Width, image.Height);
                var anchorFloor = anchorsDocument.Floors[floor.Key];
                var profile = new FloorRecognitionProfile
                {
                    Floor = index == 0 ? MapFloor.First : MapFloor.Second,
                    FloorKey = floor.Key,
                    OrientationDegrees = floor.OrientationDegrees,
                    RecognitionRegion = ToModel(floor.RecognitionRegion),
                    FreeCropPoints = floor.FreeCropPoints.Select(point => ToModel(point)!).ToList(),
                    RecognitionPixelWidth = recognitionSize.Width,
                    RecognitionPixelHeight = recognitionSize.Height,
                    ValidMapBounds = ToPixelBounds(
                        floor.ValidMapBounds,
                        recognitionSize.Width,
                        recognitionSize.Height),
                    WholeImageIgnoreRegions = anchorFloor.WholeImageIgnoreRegions.Select(ToRequiredModel).ToList(),
                    Annotations = anchorFloor.Annotations.Select(ToModel).ToList(),
                    BackgroundLayers = (anchorFloor.BackgroundLayers ?? []).Select(ToModel).ToList()
                };
                foreach (var anchor in anchorFloor.Anchors)
                {
                    NormalizedRectangle? bounds;
                    if (!string.IsNullOrWhiteSpace(anchor.GateId))
                    {
                        var gate = gates.SingleOrDefault(item => string.Equals(
                            item.Id,
                            anchor.GateId,
                            StringComparison.Ordinal));
                        if (gate is null || anchor.Bounds is not null)
                            throw new InvalidDataException($"锚点 {anchor.Key} 的 gateId 无效或重复声明 bounds。");
                        bounds = gate.Bounds.Clone();
                    }
                    else
                    {
                        bounds = ToModel(anchor.Bounds);
                    }
                    profile.Anchors.Add(new RecognitionAnchor
                    {
                        Id = anchor.Id == Guid.Empty ? Guid.NewGuid() : anchor.Id,
                        Key = anchor.Key,
                        DisplayName = anchor.DisplayName,
                        Role = anchor.Role == "required" ? RecognitionAnchorRole.Required : RecognitionAnchorRole.Optional,
                        Weight = anchor.Weight,
                        Bounds = bounds,
                        IsBuiltIn = anchor.BuiltIn
                    });
                }
                var floorDefinition = new FloorDefinition
                {
                    Key = floor.Key,
                    DisplayName = floor.DisplayName,
                    SortOrder = floor.SortOrder,
                    MarkerKeys = MapFloorMarkerRules.Normalize(floor.MarkerKeys).ToList()
                };
                if (await ReadImportedPrebuiltStructureAsync(
                        root,
                        map,
                        floor,
                        cancellationToken) is { } importedPrebuilt)
                {
                    floorDefinition.PrebuiltStructureLine = importedPrebuilt.Asset;
                    prebuiltStructureLinePaths[floor.Key] = importedPrebuilt.LinePath;
                    if (prebuiltStructureAlgorithmPath is not null
                        && !string.Equals(
                            prebuiltStructureAlgorithmPath,
                            importedPrebuilt.AlgorithmPath,
                            StringComparison.Ordinal))
                        throw new InvalidDataException("同一地图的楼层必须引用同一个 IDVA 算法文件。");
                    prebuiltStructureAlgorithmPath = importedPrebuilt.AlgorithmPath;
                }
                floorDefinitions.Add(floorDefinition);
                floorPaths.Add(floor.Key, imagePath);
                profiles.Add(floor.Key, profile);

                // 导入侧门特征图（可选，旧包无此字段时静默跳过）
                if (floor.SideEntranceFeature is { } featureDto
                    && !string.IsNullOrWhiteSpace(featureDto.File)
                    && featureDto.Radius > 0)
                {
                    var featurePhysical = ToPhysicalPath(root, featureDto.File);
                    if (File.Exists(featurePhysical))
                    {
                        profile.SideEntranceFeatureCenterX = featureDto.CenterX;
                        profile.SideEntranceFeatureCenterY = featureDto.CenterY;
                        profile.SideEntranceFeatureRadius   = featureDto.Radius;
                        sideEntranceFeaturePaths[floor.Key] = featurePhysical;
                    }
                }
            }

            var orderedProfiles = metadata.Floors.Select(floor => profiles[floor.Key]).ToArray();
            var recognition = new MapRecognitionProfile
            {
                SchemaVersion = 8,
                FirstFloor = orderedProfiles[0],
                SecondFloor = orderedProfiles.Length > 1
                    ? orderedProfiles[1]
                    : new FloorRecognitionProfile { Floor = MapFloor.Second, FloorKey = "2f" },
                Floors = profiles,
                WholeImage = new WholeImageRecognitionSettings
                {
                    IsEnabled = metadata.Recognition.WholeImage.Enabled,
                    Weight = metadata.Recognition.WholeImage.Weight,
                    AnnotatedReferencePenalty = metadata.Recognition.WholeImage.AnnotatedReferencePenalty,
                    ReferenceMayContainAnnotations = metadata.Recognition.WholeImage.ReferenceMayContainAnnotations
                }
            };
            recognition.EnsureStandardAnchors();
            var draft = new MapDraft
            {
                SourcePackageMapId = map.MapId,
                Title = metadata.Map.Title,
                ClassProperties = classPropertiesById[map.ClassId].Clone(),
                ContentVersion = map.MapVersion,
                Source = string.Equals(metadata.Map.Source, "survey", StringComparison.Ordinal)
                    ? "survey"
                    : "manual",
                SourceProjectId = metadata.Map.SourceProjectId,
                SourceProjectRevision = metadata.Map.SourceProjectRevision,
                SourceVisualSha256 = metadata.Map.SourceVisualSha256,
                IsCoCreationTemplate = metadata.Map.IsCoCreationTemplate,
                AppearancePalette = metadata.Map.AppearancePalette?.Normalize(),
                SourceStructureSha256 = metadata.Map.SourceStructureSha256,
                Floors = floorDefinitions,
                FloorPaths = floorPaths,
                FloorPreviewPaths = new Dictionary<string, string>(floorPaths, StringComparer.Ordinal),
                FloorRecognitionSourcePaths = metadata.Floors
                    .Where(floor => !string.IsNullOrWhiteSpace(floor.RecognitionImage))
                    .ToDictionary(
                        floor => floor.Key,
                        floor => ToPhysicalPath(root, floor.RecognitionImage!),
                        StringComparer.Ordinal),
                FloorOnePath = floorPaths[metadata.Floors[0].Key],
                FloorTwoPath = metadata.Floors.Count > 1 ? floorPaths[metadata.Floors[1].Key] : null,
                Recognition = recognition,
                PortableGates = gates,
                Tags = metadata.Tags
                    .Where(tag => tag.GroupId != Guid.Empty && !string.IsNullOrWhiteSpace(tag.Value))
                    .GroupBy(tag => tag.GroupId)
                    .ToDictionary(group => group.Key, group => group.First().Value.Trim()),
                ImportedTagGroupNames = metadata.Tags
                    .Where(tag => tag.GroupId != Guid.Empty && !string.IsNullOrWhiteSpace(tag.GroupName))
                    .GroupBy(tag => tag.GroupId)
                    .ToDictionary(group => group.Key, group => group.First().GroupName.Trim()),
                SideEntranceFeaturePaths = sideEntranceFeaturePaths,
                PrebuiltStructureLinePaths = prebuiltStructureLinePaths,
                PrebuiltStructureAlgorithmPath = prebuiltStructureAlgorithmPath
            };
            if (await ReadNavigationAsync(root, manifest, map, metadata, cancellationToken) is { } navigationLoader)
                draft.DeferNavigation(navigationLoader);
            draftsByClass[map.ClassId].Add(draft);
        }

        var hasNavigation = draftsByClass.Values.SelectMany(drafts => drafts).Any(draft => draft.HasNavigation);
        if (hasNavigation != manifest.Capabilities.NavigationTopology)
            throw new InvalidDataException("IDVM 导航能力与地图数据不一致。");
        return manifest.Classes.Select(item => new MapImportClassDraft(
            item.Name.Trim(),
            draftsByClass[item.ClassId],
            classPropertiesById[item.ClassId].Clone(),
            manifest.VariantGroups
                .Where(group => group.ClassId == item.ClassId)
                .Select(group => new MapImportVariantGroupDraft(
                    group.GroupId,
                    group.PaletteSlot,
                    group.MapIds.ToArray()))
                .ToArray())).ToArray();
    }

    private async Task MergeImportedTagsAsync(
        IReadOnlyList<MapImportClassDraft> classes)
    {
        var localGroups = (await _tagStore.LoadAsync()).ToList();
        var changed = false;
        foreach (var draft in classes.SelectMany(item => item.Maps))
        {
            foreach (var pair in draft.ImportedTagGroupNames)
            {
                var group = localGroups.FirstOrDefault(item => item.Id == pair.Key);
                if (group is null)
                {
                    group = new MapTagGroup { Id = pair.Key, Name = pair.Value };
                    localGroups.Add(group);
                    changed = true;
                }
                if (draft.Tags.TryGetValue(pair.Key, out var value)
                    && !group.Tags.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    group.Tags.Add(value);
                    changed = true;
                }
            }
        }
        if (changed)
            await _tagStore.SaveAsync(localGroups);
    }

    // ── 导入 DTO 映射辅助方法 ──────────────────────────────────────────

    private static NormalizedRectangle? ToModel(RectangleDto? rectangle) => rectangle is null ? null : new NormalizedRectangle
    {
        X = Math.Clamp(rectangle.X, 0d, 1d),
        Y = Math.Clamp(rectangle.Y, 0d, 1d),
        Width = Math.Clamp(rectangle.Width, 0d, 1d),
        Height = Math.Clamp(rectangle.Height, 0d, 1d)
    };

    private static NormalizedRectangle ToRequiredModel(RectangleDto rectangle) => ToModel(rectangle)!;

    private static NormalizedPoint? ToModel(PointDto? point) => point is null ? null : new NormalizedPoint
    {
        X = Math.Clamp(point.X, 0d, 1d),
        Y = Math.Clamp(point.Y, 0d, 1d)
    };

    private static MapReferenceBounds ToPixelBounds(RectangleDto bounds, int width, int height) => new()
    {
        X = bounds.X * width,
        Y = bounds.Y * height,
        Width = bounds.Width * width,
        Height = bounds.Height * height
    };

    private static Size GetRecognitionSize(MetadataFloorDto floor, int imageWidth, int imageHeight)
    {
        if (floor.RecognitionRegion is null)
            return new Size(imageWidth, imageHeight);
        var region = floor.RecognitionRegion;
        var left = Math.Clamp((int)Math.Floor(region.X * imageWidth), 0, Math.Max(0, imageWidth - 1));
        var top = Math.Clamp((int)Math.Floor(region.Y * imageHeight), 0, Math.Max(0, imageHeight - 1));
        var right = Math.Clamp((int)Math.Ceiling((region.X + region.Width) * imageWidth), left + 1, imageWidth);
        var bottom = Math.Clamp((int)Math.Ceiling((region.Y + region.Height) * imageHeight), top + 1, imageHeight);
        return new Size(right - left, bottom - top);
    }

    private static MapGateDefinition ToModel(GateDto gate) => new()
    {
        Id = gate.Id,
        FloorKey = gate.FloorKey,
        Role = gate.Role,
        Bounds = ToRequiredModel(gate.Bounds),
        DirectionDegrees = gate.DirectionDegrees,
        Enabled = gate.Enabled,
        Confidence = gate.Confidence
    };

    private static MapAnnotation ToModel(AnnotationDto annotation) => new()
    {
        Id = annotation.Id,
        Type = annotation.Type switch
        {
            "text" => MapAnnotationType.Text,
            "outline" => MapAnnotationType.Outline,
            "line" => MapAnnotationType.Line,
            "route" => MapAnnotationType.Route,
            _ => default
        },
        ColorIndex = annotation.ColorIndex,
        ColorHex = annotation.Color,
        Bounds = ToModel(annotation.Bounds),
        Start = ToModel(annotation.Start),
        End = ToModel(annotation.End),
        Points = annotation.Points?.Select(point => ToModel(point)!).ToList() ?? [],
        Text = annotation.Text,
        RoutePlanId = annotation.RoutePlanId,
        IsMarker = annotation.IsMarker,
        FontFamily = annotation.FontFamily,
        FontSize = annotation.FontSize,
        IsBold = annotation.IsBold,
        IsItalic = annotation.IsItalic,
        IsStrikethrough = annotation.IsStrikethrough
    };

    private static MapBackgroundLayer ToModel(BackgroundLayerDto layer) => new()
    {
        Id = layer.Id,
        Semantic = layer.Semantic,
        Shape = layer.Shape == "square"
            ? MapBackgroundLayerShape.Square
            : MapBackgroundLayerShape.Circle,
        BrushSizePixels = layer.BrushSizePixels,
        Points = layer.Points.Select(point => new NormalizedPoint
        {
            X = point.X,
            Y = point.Y
        }).ToList()
    };
}
/*
 * 文件职责：IdvmPackageService.Reader。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
