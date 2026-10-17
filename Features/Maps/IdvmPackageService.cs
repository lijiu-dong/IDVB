using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using IDVBuff.UpdateCore;

namespace IDVBuff.Features.Maps;

public sealed class IdvmImportPlan : IAsyncDisposable
{
    private bool _disposed;
    private IReadOnlyList<MapImportClassDraft> _classes;

    internal IdvmImportPlan(
        Guid packageId,
        string stagingDirectory,
        IReadOnlyList<MapImportClassDraft> classes)
    {
        PackageId = packageId;
        StagingDirectory = stagingDirectory;
        _classes = classes;
        ClassCount = classes.Count;
        MapCount = classes.Sum(item => item.Maps.Count);
        IDVBuff.Core.Diagnostics.MemoryJournal.TrackResource(this, "Idvm.ImportPlan", null);
        IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedObject(classes, "Idvm.ImportPlan.Classes");
    }

    public Guid PackageId { get; }
    public int ClassCount { get; }
    public int MapCount { get; }
    internal string StagingDirectory { get; }
    internal IReadOnlyList<MapImportClassDraft> Classes
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _classes;
        }
    }
    internal bool IsDisposed => _disposed;

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        // The caller may retain the closed plan for its identity/counts. It no
        // longer owns editable drafts or their recognition/navigation payloads.
        _classes = [];
        IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(this);
        try
        {
            if (Directory.Exists(StagingDirectory))
                Directory.Delete(StagingDirectory, recursive: true);
        }
        catch
        {
            // A later temp cleanup may remove files held by an image decoder.
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Reads and writes the portable, untrusted IDVM interchange format.</summary>
public sealed partial class IdvmPackageService
{
#if IDVB_DEVELOPMENT_BUILD
    internal static Func<CancellationToken, Task>? DevelopmentImportInspectionPause;
#endif
    private const int HeaderSize = 80;
    private const int MaxEntries = 4096;
    private const long MaxSingleFileBytes = 256L * 1024 * 1024;
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private const long MaxJsonBytes = 8L * 1024 * 1024;
    private const double CoordinateTolerance = 0.000001d;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        MaxDepth = 64,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly MapRepository _repository;
    private readonly MapTagStore _tagStore;

    public IdvmPackageService(MapRepository repository, MapTagStore? tagStore = null)
    {
        _repository = repository;
        _tagStore = tagStore ?? new MapTagStore();
    }

    public Task ExportAsync(
        IdvmExportScope scope,
        string? className,
        string destination,
        CancellationToken cancellationToken = default,
        IdvmCompressionMode compressionMode = IdvmCompressionMode.Maximum) =>
        ExportCoreAsync(scope, className, destination, cancellationToken, compressionMode, null);

    internal Task ExportSnapshotAsync(MapCatalogSnapshot snapshot, IdvmExportScope scope,
        string? className, string destination, CancellationToken cancellationToken) =>
        ExportCoreAsync(scope, className, destination, cancellationToken, IdvmCompressionMode.Maximum, snapshot);

    private async Task ExportCoreAsync(IdvmExportScope scope, string? className, string destination,
        CancellationToken cancellationToken, IdvmCompressionMode compressionMode, MapCatalogSnapshot? preparedSnapshot)
    {
        using var memory = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Idvm.Export", className ?? "all-classes");
        if (string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("导出目标不能为空。", nameof(destination));
        if (!Enum.IsDefined(compressionMode))
            throw new ArgumentOutOfRangeException(nameof(compressionMode));

        cancellationToken.ThrowIfCancellationRequested();
        // A current-class export must not retain unrelated maps throughout
        // image validation and compression. The repository still performs its
        // normal catalog normalization/migration before detaching the subset.
        var snapshot = preparedSnapshot ?? (scope == IdvmExportScope.CurrentClass && !string.IsNullOrWhiteSpace(className)
            ? await _repository.GetClassCatalogSnapshotAsync(className)
            : await _repository.GetCatalogSnapshotAsync());
        var selectedMaps = SelectMaps(snapshot.Maps, scope, className);
        IDVBuff.Core.Diagnostics.MemoryJournal.Current?.Record("export-selection",
            new { scope = scope.ToString(), className, snapshotMapCount = snapshot.Maps.Count, selectedMapCount = selectedMaps.Length });
        if (selectedMaps.Length == 0)
            throw new InvalidOperationException("所选范围没有可导出的地图。");
        // Reject incomplete local drawing data before decoding images/loading
        // navigation/compressing. Never invent lost route geometry or export a
        // package that the same reader cannot open.
        foreach (var map in selectedMaps)
            foreach (var floor in MapFloorRules.GetOrderedFloors(map))
                foreach (var annotation in map.Recognition.GetFloor(floor.Key)!.Annotations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!annotation.IsValid)
                        throw new InvalidDataException($"地图“{map.Class} / {map.DisplayName}”的楼层“{floor.DisplayName}”含有无效标注（{annotation.Id}），请先在地图编辑器中检查；原始标注未被修改。");
                }

        var selectedClasses = snapshot.Classes
            .Where(name => selectedMaps.Any(map => string.Equals(
                map.Class,
                name,
                StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var staging = CreateTemporaryDirectory("export");
        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(destinationDirectory);
        var temporaryDestination = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var packageId = Guid.NewGuid();
            var createdAt = DateTimeOffset.UtcNow;
            var classIds = selectedClasses.ToDictionary(
                name => name,
                _ => Guid.NewGuid(),
                StringComparer.OrdinalIgnoreCase);
            var hasNavigation = selectedMaps.Any(map => map.HasNavigation);
            var compressed = compressionMode == IdvmCompressionMode.Maximum;
            var formatVersion = compressed ? "1.6" : hasNavigation ? "1.5" : "1.4";
            var manifest = new ManifestDto
            {
                Format = "idvm",
                FormatVersion = formatVersion,
                PackageType = "class-set",
                PackageId = packageId,
                CreatedAt = createdAt,
                MinimumReader = formatVersion,
                Compression = compressed ? SolidCompression : null,
                SupportedPlatforms = compressed || hasNavigation ? ["windows"] : IdvmPlatformCompatibility.All.ToList(),
                Capabilities = new CapabilitiesDto
                {
                    NavigationTopology = hasNavigation,
                    FloorMarkerKeys = true,
                    MapTags = true,
                    PrebuiltStructureLines = selectedMaps.Any(map =>
                        MapFloorRules.GetOrderedFloors(map).Any(floor =>
                            floor.PrebuiltStructureLine?.IsComplete is true)),
                    ContainsVectorRoutes = selectedClasses.Any(classLabel =>
                        MapRouteRules.ClassContainsVectorRoutes(selectedMaps.Where(map =>
                            string.Equals(map.Class, classLabel, StringComparison.OrdinalIgnoreCase))))
                        ? true
                        : null
                }
            };

            foreach (var classLabel in selectedClasses)
            {
                var maps = selectedMaps
                    .Where(map => string.Equals(map.Class, classLabel, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(map => map.SequenceNumber)
                    .ToArray();
                var classId = classIds[classLabel];
                manifest.Classes.Add(new ManifestClassDto
                {
                    ClassId = classId,
                    Name = classLabel,
                    MapIds = maps.Select(map => map.Id).ToList(),
                    Properties = new ManifestClassPropertiesDto
                    {
                        RemoveBackground = snapshot.ClassProperties.TryGetValue(classLabel, out var properties)
                            && properties.RemoveBackground,
                        ScanFloorKey = snapshot.ClassProperties.TryGetValue(classLabel, out properties)
                            ? MapScanFloorRules.NormalizeFloorIdentity(properties.ScanFloorKey)
                            : null,
                        ContainsVectorRoutes = MapRouteRules.ClassContainsVectorRoutes(maps)
                            ? true
                            : null
                    }
                });
                foreach (var map in maps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MapOperationProgress.Report(.55 * manifest.Maps.Count / selectedMaps.Length, "正在整理地图资源……");
                    manifest.Maps.Add(await WriteMapPayloadAsync(
                        staging,
                        classId,
                        map,
                        snapshot.ClassProperties.TryGetValue(classLabel, out var classProperties)
                            ? classProperties
                            : new MapClassProperties(),
                        cancellationToken));
                }
            }

            var selectedMapIds = selectedMaps.Select(map => map.Id).ToHashSet();
            foreach (var group in snapshot.VariantGroups
                .Where(group => group.MapIds.All(selectedMapIds.Contains))
                .OrderBy(group => group.Class, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group.PaletteSlot))
            {
                manifest.VariantGroups.Add(new ManifestVariantGroupDto
                {
                    GroupId = group.Id,
                    ClassId = classIds[group.Class],
                    PaletteSlot = group.PaletteSlot,
                    MapIds = group.MapIds.ToList()
                });
            }

            var payloadFiles = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            foreach (var file in payloadFiles)
            {
                MapOperationProgress.Report(.55 + .1 * manifest.Files.Count / payloadFiles.Length, "正在校验地图资源……");
                var relative = ToLogicalPath(staging, file);
                manifest.Files.Add(new ManifestFileDto
                {
                    Path = relative,
                    Size = new FileInfo(file).Length,
                    Sha256 = await ComputeSha256Async(file, cancellationToken)
                });
            }

            ValidateExportLimits(manifest);
            if (compressed)
                await MapOperationProgress.RunAsync(.65, .9, () => WriteSolidPayloadAsync(staging, manifest, cancellationToken));
            var manifestBytes = SerializeUtf8(manifest);
            if (manifestBytes.Length > MaxJsonBytes)
                throw new InvalidDataException("IDVM manifest 超过 8 MiB。");
            ValidateExportLimits(manifest, manifestBytes.Length);
            var manifestPath = Path.Combine(staging, "manifest.json");
            await File.WriteAllBytesAsync(manifestPath, manifestBytes, cancellationToken);
            var header = CreateHeader(packageId, createdAt, SHA256.HashData(manifestBytes),
                compressed ? (ushort)6 : hasNavigation ? (ushort)5 : (ushort)4);
            await File.WriteAllBytesAsync(Path.Combine(staging, "header"), header, cancellationToken);

            await MapOperationProgress.RunAsync(compressed ? .9 : .65, .98, () => CreateArchiveAsync(staging, temporaryDestination, cancellationToken, compressed));
            MapOperationProgress.Report(.98, "正在保存数据包……");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryDestination, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryDestination))
                File.Delete(temporaryDestination);
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    public async Task<IdvmImportPlan> InspectAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePath)
            || (!File.Exists(packagePath) && !Directory.Exists(packagePath)))
        {
            throw new FileNotFoundException("找不到要导入的 IDVM 数据包。", packagePath);
        }

        var staging = CreateTemporaryDirectory("inspect");
        try
        {
            await MapOperationProgress.RunAsync(0, .25, () => ExtractPackageAsync(packagePath, staging, cancellationToken));
            MapOperationProgress.Report(.25, "正在验证数据包……");
            var manifest = await MapOperationProgress.RunAsync(.25, .65, () => ValidatePackageAsync(staging, cancellationToken));
            var classes = await MapOperationProgress.RunAsync(.65, 1, () => BuildImportDraftsAsync(staging, manifest, cancellationToken));
            return new IdvmImportPlan(manifest.PackageId, staging, classes);
        }
        catch
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    public async Task<IdvmImportResult> ImportAsync(
        IdvmImportPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.IsDisposed || !Directory.Exists(plan.StagingDirectory))
            throw new InvalidOperationException("IDVM 导入计划已失效，请重新选择数据包。");
        try
        {
#if IDVB_DEVELOPMENT_BUILD
            if (DevelopmentImportInspectionPause is { } pause) await pause(cancellationToken);
#endif
            var result = await MapOperationProgress.RunAsync(0, .97, () => _repository.ImportBatchAsync(plan.Classes, cancellationToken));
            MapOperationProgress.Report(.97, "正在合并地图标签……");
            await MergeImportedTagsAsync(plan.Classes);
            return new IdvmImportResult(
                plan.PackageId,
                result.CreatedClasses,
                result.ImportedMaps,
                result.ImportedVariantGroups);
        }
        finally
        {
            await plan.DisposeAsync();
        }
    }

}
/*
 * 文件职责：IdvmPackageService。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
