using OpenCvSharp;
using System.Security.Cryptography;
using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    private static async Task<string> CopyImageToDirectoryAsync(string sourcePath, string destinationDirectory, string filePrefix)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        var fileName = $"{filePrefix}{extension}";
        var targetPath = Path.Combine(destinationDirectory, fileName);
        await using var source = File.OpenRead(sourcePath);
        await using var destination = File.Create(targetPath);
        await source.CopyToAsync(destination);
        return fileName;
    }

    private static string GetFloorImageFilePrefix(string floorKey) =>
        floorKey switch
        {
            "1f" => "floor-1",
            "2f" => "floor-2",
            _ => $"floor-{floorKey}"
        };

    private static string GetFloorRecognitionFileName(string floorKey) =>
        floorKey switch
        {
            "1f" => FloorOneRecognitionFileName,
            "2f" => FloorTwoRecognitionFileName,
            _ => $"floor-{floorKey}-recognition.png"
        };

    private static string GetFloorOverlayFileName(string floorKey) =>
        floorKey switch
        {
            "1f" => FloorOneOverlayFileName,
            "2f" => FloorTwoOverlayFileName,
            _ => $"floor-{floorKey}-overlay.png"
        };

    private static string GetFloorThumbnailFileName(string floorKey) =>
        floorKey switch
        {
            "1f" => "floor-1-thumbnail.jpg",
            "2f" => "floor-2-thumbnail.jpg",
            _ => $"floor-{floorKey}-thumbnail.jpg"
        };

    private static Task CreateThumbnailAsync(string sourcePath, string destinationPath) =>
        Task.Run(() =>
        {
            using var operation = MemoryJournal.BeginOperation("Thumbnail.Generate", sourcePath);
            Mat? source = null, bgr = null, thumbnail = null;
            try
            {
                source = DecodeImage(sourcePath);
                if (source.Empty())
                    throw new InvalidOperationException($"Image cannot be read: '{sourcePath}'.");
                TrackThumbnailRaster(source, "DecodedInput");
                if (source.Type() == MatType.CV_8UC4)
                {
                    bgr = MapBackgroundProcessor.CompositeExclusiveBgraToBgr(source);
                    var request = MemoryJournal.Request("Thumbnail.PackedBgrView", 0, "OpenCvSharpExtern.dll");
                    MemoryJournal.TrackResource(bgr, "Thumbnail.PackedBgrView", 0, "OpenCvSharpExtern.dll", request);
                }
                else
                {
                    bgr = MapBackgroundProcessor.CompositeToBgr(source);
                    TrackThumbnailRaster(bgr, "CompositeBgr");
                    ReleaseThumbnailRaster(ref source);
                }
                const int maxWidth = 400;
                var width = Math.Min(maxWidth, bgr.Width);
                var height = Math.Max(1, (int)Math.Round(bgr.Height * (width / (double)bgr.Width)));
                thumbnail = new Mat();
                Cv2.Resize(bgr, thumbnail, new Size(width, height), 0, 0, InterpolationFlags.Area);
                TrackThumbnailRaster(thumbnail, "Resized");
                ReleaseThumbnailRaster(ref bgr);
                ReleaseThumbnailRaster(ref source);
                if (!Cv2.ImWrite(destinationPath, thumbnail, [new ImageEncodingParam(ImwriteFlags.JpegQuality, 82)]))
                    throw new InvalidOperationException($"Image cannot be written: '{destinationPath}'.");
            }
            finally
            {
                ReleaseThumbnailRaster(ref thumbnail);
                ReleaseThumbnailRaster(ref bgr);
                ReleaseThumbnailRaster(ref source);
            }
        });

    private static void ReleaseThumbnailRaster(ref Mat? raster)
    {
        if (raster is null) return;
        var released = raster;
        released.Dispose();
        raster = null;
        MemoryJournal.ReleaseResource(released);
    }

    private static void TrackThumbnailRaster(Mat raster, string name)
    {
        var resource = "Thumbnail." + name;
        var bytes = raster.Total() * raster.ElemSize();
        var request = MemoryJournal.Request(resource, bytes, "OpenCvSharpExtern.dll");
        MemoryJournal.TrackResource(raster, resource, bytes, "OpenCvSharpExtern.dll", request);
    }

    private static async Task PopulateThumbnailMetadataAsync(
        FloorDefinition floor,
        string thumbnailPath)
    {
        var metadata = await Task.Run(() => ReadImageMetadataAsync(thumbnailPath));
        floor.ThumbnailFileName = Path.GetFileName(thumbnailPath);
        floor.ThumbnailSha256 = metadata.Sha256;
        floor.ThumbnailWidth = metadata.Width;
        floor.ThumbnailHeight = metadata.Height;
        floor.ThumbnailFileLength = metadata.FileLength;
        floor.ThumbnailLastWriteUtcTicks = metadata.LastWriteUtcTicks;
    }

    private void CopyRepairedMetadata(MapRecord map, FloorDefinition source,
        FloorDefinition destination, RepairMetadataInput input)
    {
        if (input.Image == AssetMetadata.Image(destination)
            && HasMatchingFileStamp(GetFloorImagePath(map, destination.Key), source.ImageFileLength, source.ImageLastWriteUtcTicks))
        {
            destination.ImageSha256 = source.ImageSha256;
            destination.ImageWidth = source.ImageWidth;
            destination.ImageHeight = source.ImageHeight;
            destination.ImageFileLength = source.ImageFileLength;
            destination.ImageLastWriteUtcTicks = source.ImageLastWriteUtcTicks;
        }

        if (input.Thumbnail == AssetMetadata.Thumbnail(destination)
            && input.RecognitionFile == destination.RecognitionFileName
            && input.RecognitionHash == destination.RecognitionSha256
            && HasMatchingFileStamp(GetOriginalFloorThumbnailPath(map, destination.Key), source.ThumbnailFileLength, source.ThumbnailLastWriteUtcTicks))
        {
            destination.ThumbnailFileName = source.ThumbnailFileName;
            destination.ThumbnailSha256 = source.ThumbnailSha256;
            destination.ThumbnailWidth = source.ThumbnailWidth;
            destination.ThumbnailHeight = source.ThumbnailHeight;
            destination.ThumbnailFileLength = source.ThumbnailFileLength;
            destination.ThumbnailLastWriteUtcTicks = source.ThumbnailLastWriteUtcTicks;
        }
    }

    private static async Task PopulateFloorImageMetadataAsync(
        MapRecord record,
        string stagingDirectory,
        IReadOnlyDictionary<string, string> floorImageFileNames)
    {
        foreach (var floor in MapFloorRules.GetOrderedFloors(record))
        {
            if (!floorImageFileNames.TryGetValue(floor.Key, out var fileName))
            {
                throw new InvalidOperationException(
                    $"Floor '{floor.Key}' has no copied local image.");
            }

            var path = Path.Combine(stagingDirectory, fileName);
            var metadata = await ReadImageMetadataAsync(path);
            floor.ImageFileName = fileName;
            floor.ImageSha256 = metadata.Sha256;
            floor.ImageWidth = metadata.Width;
            floor.ImageHeight = metadata.Height;
            floor.ImageFileLength = metadata.FileLength;
            floor.ImageLastWriteUtcTicks = metadata.LastWriteUtcTicks;
        }
    }

    private static bool MatchesStoredDerivedMetadata(
        string path,
        string expectedSha256,
        int expectedWidth,
        int expectedHeight,
        long expectedFileLength,
        long expectedLastWriteUtcTicks,
        string expectedSourceSha256,
        bool requiresFile)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)
            || string.IsNullOrWhiteSpace(expectedSourceSha256)
            || expectedWidth <= 0
            || expectedHeight <= 0)
            return false;
        if (requiresFile && !File.Exists(path))
            return false;
        if (!File.Exists(path))
            return string.Equals(path, expectedSourceSha256, StringComparison.OrdinalIgnoreCase);

        var info = new FileInfo(path);
        if (expectedFileLength > 0
            && expectedLastWriteUtcTicks > 0
            && info.Length == expectedFileLength
            && info.LastWriteTimeUtc.Ticks == expectedLastWriteUtcTicks)
            return true;

        var actual = ReadImageMetadataAsync(path).GetAwaiter().GetResult();
        return string.Equals(actual.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase)
            && actual.Width == expectedWidth
            && actual.Height == expectedHeight;
    }

    private static async Task PopulateDerivedImageMetadataAsync(
        FloorDefinition floor,
        string sourcePath,
        string recognitionPath,
        string overlayPath,
        FloorRecognitionProfile profile,
        bool forceRecognitionPath = false)
    {
        var sourceMetadata = await ReadImageMetadataAsync(sourcePath);
        var recognitionPathForMetadata = !forceRecognitionPath && UsesWholeSourceImage(profile)
            ? sourcePath
            : recognitionPath;
        var recognitionMetadata = await ReadImageMetadataAsync(recognitionPathForMetadata);
        var overlayMetadata = await ReadImageMetadataAsync(overlayPath);

        floor.RecognitionFileName = Path.GetFileName(recognitionPathForMetadata);
        floor.RecognitionSha256 = recognitionMetadata.Sha256;
        floor.RecognitionSourceSha256 = sourceMetadata.Sha256;
        floor.RecognitionWidth = recognitionMetadata.Width;
        floor.RecognitionHeight = recognitionMetadata.Height;
        floor.RecognitionFileLength = recognitionMetadata.FileLength;
        floor.RecognitionLastWriteUtcTicks = recognitionMetadata.LastWriteUtcTicks;
        floor.OverlayFileName = Path.GetFileName(overlayPath);
        floor.OverlaySha256 = overlayMetadata.Sha256;
        floor.OverlaySourceSha256 = recognitionMetadata.Sha256;
        floor.OverlayWidth = overlayMetadata.Width;
        floor.OverlayHeight = overlayMetadata.Height;
        floor.OverlayFileLength = overlayMetadata.FileLength;
        floor.OverlayLastWriteUtcTicks = overlayMetadata.LastWriteUtcTicks;
    }

    private static async Task<FloorImageMetadata> ReadImageMetadataAsync(string path)
    {
        (int Width, int Height) image;
        try { image = MapImageFileValidation.ValidateImageFile(path, CancellationToken.None); }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            throw new InvalidOperationException($"Image cannot be decoded: '{path}'.", exception);
        }

        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        var info = new FileInfo(path);
        return new FloorImageMetadata(
            Convert.ToHexString(hash).ToLowerInvariant(),
            image.Width,
            image.Height,
            info.Length,
            info.LastWriteTimeUtc.Ticks);
    }

    private readonly record struct FloorImageMetadata(
        string Sha256,
        int Width,
        int Height,
        long FileLength,
        long LastWriteUtcTicks);

    private static bool CreateRecognitionAssets(
        string sourcePath,
        string destinationPath,
        FloorRecognitionProfile profile,
        string? overlayPath,
        bool removeBackground = false,
        int backgroundRemovalIntensity = MapBackgroundProcessor.DefaultBackgroundRemovalIntensity,
        string? thumbnailPath = null)
    {
        var needsIndependentRecognition = removeBackground
            || profile.BackgroundLayers.Count > 0
            || !UsesWholeSourceImage(profile);
        if (TryWriteOwnedUnmaskedRegionAssets(sourcePath, destinationPath, profile, overlayPath,
                removeBackground, needsIndependentRecognition, thumbnailPath))
            return needsIndependentRecognition && thumbnailPath is not null;
        using var processed = ProcessRecognitionSource(
            sourcePath,
            profile,
            removeBackground,
            backgroundRemovalIntensity);
        if (needsIndependentRecognition && !Cv2.ImWrite(destinationPath, processed.Recognition))
            throw new InvalidOperationException("无法保存地图识别区域。");
        if (overlayPath is not null && !Cv2.ImWrite(overlayPath, processed.Overlay))
            throw new InvalidOperationException("无法保存透明地图图层。");
        return false;
    }

    // Process returns independent owned pixels. Close the full-size decoded
    // input before PNG encoding allocates its own working buffers.
    private static MapBackgroundProcessingResult ProcessRecognitionSource(
        string sourcePath,
        FloorRecognitionProfile profile,
        bool removeBackground,
        int backgroundRemovalIntensity,
        string decodeFailure = "无法读取地图原图以生成识别区域。")
    {
        if (!removeBackground && profile.BackgroundLayers is null or { Count: 0 }
            && profile.FreeCropPoints is null or { Count: 0 }
            && MapRecognitionRegionDecoder.IsEightBitPng(sourcePath))
        {
            var region = UsesWholeSourceImage(profile)
                ? new NormalizedRectangle { Width = 1d, Height = 1d }
                : profile.GetEffectiveRecognitionRegion();
            return MapBackgroundProcessor.ProcessOwnedUnmaskedRegion(
                MapRecognitionRegionDecoder.Decode(sourcePath, region), profile);
        }
        var source = DecodeImage(sourcePath);
        var tracked = false;
        try
        {
            if (source.Empty()) throw new InvalidOperationException(decodeFailure);
            var bytes = source.Total() * source.ElemSize();
            var request = MemoryJournal.Request("Recognition.DecodedInput", bytes, "OpenCvSharpExtern.dll");
            MemoryJournal.TrackResource(source, "Recognition.DecodedInput", bytes, "OpenCvSharpExtern.dll", request);
            tracked = true;
            return MapBackgroundProcessor.Process(source, profile, removeBackground, backgroundRemovalIntensity);
        }
        finally
        {
            source.Dispose();
            if (tracked) MemoryJournal.ReleaseResource(source);
        }
    }

    private static Mat DecodeImage(string path) =>
        Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Unchanged);

    private static Rect GetPixelRegion(NormalizedRectangle region, int width, int height)
    {
        var left = Math.Clamp((int)Math.Floor(region.X * width), 0, Math.Max(0, width - 1));
        var top = Math.Clamp((int)Math.Floor(region.Y * height), 0, Math.Max(0, height - 1));
        var right = Math.Clamp(
            (int)Math.Ceiling((region.X + region.Width) * width),
            left + 1,
            width);
        var bottom = Math.Clamp(
            (int)Math.Ceiling((region.Y + region.Height) * height),
            top + 1,
            height);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static void CreateWhiteKeyOverlay(Mat source, string destinationPath)
    {
        using var overlay = MapBackgroundProcessor.CreateWhiteKeyOverlay(source);
        if (!Cv2.ImWrite(destinationPath, overlay))
            throw new InvalidOperationException("无法保存透明地图图层。");
    }

    private static bool UsesWholeSourceImage(FloorRecognitionProfile profile)
    {
        var region = profile.RecognitionRegion;
        return region?.IsValid is not true
            || (region.X <= 0.000001d
                && region.Y <= 0.000001d
                && region.X + region.Width >= 0.999999d
                && region.Y + region.Height >= 0.999999d);
    }

    /// <summary>
    /// Repairs file stamps and list thumbnails without blocking catalog reads.
    /// The expensive image work is explicitly scheduled away from the UI thread.
    /// </summary>
    public async Task RepairImageMetadataAsync(CancellationToken cancellationToken)
    {
        // Multiple list/co-creation pages can load while an earlier page's
        // background repair is running. One catalog owns one repair at a time.
        var repairGate = MetadataRepairGates.GetOrAdd(CatalogPath, _ => new SemaphoreSlim(1, 1));
        await repairGate.WaitAsync(cancellationToken);
        try { await RepairImageMetadataCoreAsync(cancellationToken); }
        finally { repairGate.Release(); }
    }

    private async Task RepairImageMetadataCoreAsync(CancellationToken cancellationToken)
    {
        using var operation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Catalog.RepairImageMetadata");
        // A page reopening must not deserialize every editable drawing merely
        // to discover that all source/thumbnail file stamps are still valid.
        var metadataProjection = await GetImageMetadataSnapshotAsync();
        if (ImageMetadataIsCurrent(metadataProjection, cancellationToken)) return;
        await Gate.WaitAsync(cancellationToken);
        MapCatalogDocument catalog;
        MapCatalogRevision originalRevision;
        try
        {
            originalRevision = GetCatalogRevision();
            catalog = await ReadCatalogAsync();
        }
        finally
        {
            Gate.Release();
        }

        var inputs = catalog.Maps.SelectMany(map => map.Floors.Select(floor =>
            (Key: (map.Id, floor.Key), Input: new RepairMetadataInput(AssetMetadata.Image(floor),
                AssetMetadata.Thumbnail(floor), floor.RecognitionFileName, floor.RecognitionSha256))))
            .ToDictionary(entry => entry.Key, entry => entry.Input);
        var changed = false;
        foreach (var map in catalog.Maps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var floor in MapFloorRules.GetOrderedFloors(map))
            {
                if (string.IsNullOrWhiteSpace(floor.ImageFileName))
                    continue;

                var sourcePath = GetSafeMapFilePath(
                    GetMapDirectory(map.Id),
                    floor.ImageFileName);
                if (!File.Exists(sourcePath))
                    continue;

                if (!HasMatchingFileStamp(
                        sourcePath,
                        floor.ImageFileLength,
                        floor.ImageLastWriteUtcTicks)
                    || string.IsNullOrWhiteSpace(floor.ImageSha256)
                    || floor.ImageWidth <= 0
                    || floor.ImageHeight <= 0)
                {
                    var metadata = await Task.Run(
                        () => ReadImageMetadataAsync(sourcePath),
                        cancellationToken);
                    floor.ImageSha256 = metadata.Sha256;
                    floor.ImageWidth = metadata.Width;
                    floor.ImageHeight = metadata.Height;
                    floor.ImageFileLength = metadata.FileLength;
                    floor.ImageLastWriteUtcTicks = metadata.LastWriteUtcTicks;
                    changed = true;
                }

                var recognitionPath = GetFloorRecognitionPath(map, floor.Key);
                if (!File.Exists(recognitionPath))
                    recognitionPath = sourcePath;
                var thumbnailPath = GetOriginalFloorThumbnailPath(map, floor.Key);
                if (File.Exists(recognitionPath)
                    && (!HasMatchingFileStamp(
                            thumbnailPath,
                            floor.ThumbnailFileLength,
                            floor.ThumbnailLastWriteUtcTicks)
                        || floor.ThumbnailWidth <= 0
                        || floor.ThumbnailHeight <= 0))
                {
                    await CreateThumbnailAsync(recognitionPath, thumbnailPath);
                    await PopulateThumbnailMetadataAsync(floor, thumbnailPath);
                    changed = true;
                }
            }
        }

        if (!changed && catalog.StorageSchemaVersion >= CurrentStorageSchemaVersion)
            return;

        await Gate.WaitAsync(cancellationToken);
        try
        {
            var latestRevision = GetCatalogRevision();
            var latest = originalRevision == latestRevision ? catalog : await ReadCatalogAsync();
            var metadataOnly = originalRevision == latestRevision && GetCatalogRevision() == latestRevision;
            foreach (var repairedMap in catalog.Maps)
            {
                var latestMap = latest.Maps.FirstOrDefault(map => map.Id == repairedMap.Id);
                if (latestMap is null || latestMap.UpdatedAt != repairedMap.UpdatedAt)
                    continue;
                foreach (var repairedFloor in repairedMap.Floors)
                {
                    var latestFloor = latestMap.Floors.FirstOrDefault(
                        floor => string.Equals(floor.Key, repairedFloor.Key, StringComparison.Ordinal));
                    if (latestFloor is null
                        || !string.Equals(latestFloor.ImageFileName, repairedFloor.ImageFileName, StringComparison.Ordinal))
                        continue;
                    if (!ReferenceEquals(latest, catalog))
                        CopyRepairedMetadata(latestMap, repairedFloor, latestFloor, inputs[(repairedMap.Id, repairedFloor.Key)]);
                }
            }
            latest.StorageSchemaVersion = CurrentStorageSchemaVersion;
            await WriteCatalogAsync(latest);
            var committedRevision = GetCatalogRevision();
            if (metadataOnly && CanonicalCatalogRevisions.TryGetValue(CatalogPath, out var verified)
                && committedRevision == verified)
                MetadataRevisionAdvances[CatalogPath] = new(originalRevision, committedRevision);
        }
        finally
        {
            Gate.Release();
        }
    }
}
/*
 * 文件职责：MapRepository.Assets。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
