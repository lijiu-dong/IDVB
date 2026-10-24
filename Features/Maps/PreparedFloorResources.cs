using System.Security.Cryptography;
using System.Text;
using IDVBuff.Core.Diagnostics;
using IDVBuff.Core.Imaging;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal sealed class PreparedFloorResources : IDisposable
{
    private ScanStructureIndex? _scan;
    private Vpsg3PreparedFloor? _alignment;
    internal PreparedFloorResources(ScanStructureIndex? scan, Vpsg3PreparedFloor alignment) { _scan = scan; _alignment = alignment; }
    internal ScanStructureIndex? TakeScan() => Interlocked.Exchange(ref _scan, null);
    internal Vpsg3PreparedFloor TakeAlignment() => Interlocked.Exchange(ref _alignment, null)
        ?? throw new InvalidOperationException("Prepared alignment ownership was already transferred.");
    public void Dispose() { Interlocked.Exchange(ref _scan, null)?.Dispose(); Interlocked.Exchange(ref _alignment, null)?.Dispose(); }
}

internal static class FloorResourcePreparation
{
    internal static Vpsg3IndexCacheKey RuntimeKey(MapRecord map, FloorDefinition floor, string contentFingerprint) => new(map.Id, floor.Key,
        contentFingerprint, map.UpdatedAt,
        Vpsg3IndexCacheKey.CreatePrebuiltGenerationIdentity(floor.PrebuiltStructureLine!), 1);

    internal static PreparedFloorResources Prepare(MapRepository repository, MapRecord map, string floorKey,
        bool prepareScan, bool nativeDistance, bool nativeSemantic, CancellationToken cancellationToken = default,
        Vpsg3IndexCacheKey? runtimeKey = null)
    {
        var floor = map.Floors.Single(f => string.Equals(f.Key, floorKey, StringComparison.OrdinalIgnoreCase));
        floorKey = floor.Key;
        var asset = floor.PrebuiltStructureLine ?? throw new InvalidDataException("楼层缺少预制轮廓。");
        var path = repository.GetPrebuiltStructureLinePath(map, floorKey);
        using var operation = MemoryJournal.BeginOperation("Floor.PrepareResources", $"map={map.Id:N};floor={floorKey}");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        var actualHash = Convert.ToHexString(SHA256.HashData(input));
        if (!string.Equals(actualHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("楼层预制轮廓与登记的 SHA-256 不一致。");
        input.Position = 0;
        var width = asset.Width; var height = asset.Height;
        if (width <= 0 || height <= 0 || (long)width * height > int.MaxValue / 4)
            throw new InvalidDataException("楼层预制轮廓尺寸无效。");
        var mask = Mask(map, floorKey, width, height);
        var identity = $"floor-artifact-v2|{map.Id:N}|{floorKey.Trim().ToLowerInvariant()}|{actualHash}|{width}x{height}|"
            + $"{asset.AlgorithmSha256}|{asset.AlgorithmSchemaVersion}|{asset.EngineRevision}|nativeSemantic={nativeSemantic}";
        var alignmentKey = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var scanKey = SHA256.HashData(Encoding.UTF8.GetBytes($"{identity}|scan={mask.X},{mask.Y},{mask.Width},{mask.Height}|nativeDistance={nativeDistance}"));
        var directory = repository.PreparedFloorCacheDirectory;
        var alignmentPath = Path.Combine(directory, Convert.ToHexString(alignmentKey) + ".align");
        var scanPath = Path.Combine(directory, Convert.ToHexString(scanKey) + ".scan");
        using var protection = AppDataPaths.ProtectCachePath(directory);
        using var producer = PreparationGate.Enter(alignmentPath);
        cancellationToken.ThrowIfCancellationRequested();
        // Scan-only consumers never publish this temporary alignment owner.
        // Registry callers pass the current session's full runtime identity.
        var key = runtimeKey ?? RuntimeKey(map, floor, actualHash);
        var limit = checked((long)width * height * 16 + 1024 * 1024);
        Vpsg3PreparedFloor? alignment = null; ScanStructureIndex? scan = null; Mat? labels = null;
        try
        {
            alignment = PreparedFloorArtifactCache.Read(alignmentPath, alignmentKey, limit,
                reader => Vpsg3PreparedFloor.ReadFrom(reader, key, width, height, nativeSemantic));
            if (alignment is not null && prepareScan)
                scan = PreparedFloorArtifactCache.Read(scanPath, scanKey, limit,
                    reader => ScanStructureIndex.ReadFrom(reader, width, height, nativeDistance, nativeSemantic, alignment.SemanticIndex));
            if (alignment is null || (prepareScan && scan is null))
            {
                if (!PrebuiltContourLabels.TryRead(input, out labels, out var semantic, $"Floor.Labels/{map.Id:N}/{floorKey}"))
                {
                    input.Position = 0;
                    if (input.Length > int.MaxValue) throw new InvalidDataException("楼层轮廓文件过大。");
                    var png = new byte[checked((int)input.Length)]; input.ReadExactly(png);
                    using var image = Cv2.ImDecode(png, ImreadModes.Unchanged);
                    semantic = image.Channels() == 3; labels = SemanticStructureContours.Decode(image);
                    MemoryJournal.TrackResource(labels, $"Floor.Labels/{map.Id:N}/{floorKey}", labels.Total(), "OpenCvSharpExtern.dll");
                }
                if (labels.Width != width || labels.Height != height) throw new InvalidDataException("楼层轮廓尺寸与源身份不一致。");
                if (alignment is null)
                {
                    alignment = Vpsg3PreparedIndexBuilder.BuildFromPreparedLabels(labels, semantic, key, nativeSemanticStorage: nativeSemantic);
                    PreparedFloorArtifactCache.Write(alignmentPath, alignmentKey, alignment.WriteTo);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (prepareScan && scan is null)
                {
                    byte erasedKinds = 0;
                    if (mask.Width > 0 && mask.Height > 0)
                    {
                        using var region = new Mat(labels, mask);
                        using var warm = new Mat(); using var cool = new Mat();
                        Cv2.InRange(region, new Scalar(127), new Scalar(127), warm);
                        Cv2.InRange(region, new Scalar(255), new Scalar(255), cool);
                        if (Cv2.CountNonZero(warm) > 0) erasedKinds |= 1;
                        if (Cv2.CountNonZero(cool) > 0) erasedKinds |= 2;
                        region.SetTo(Scalar.Black);
                    }
                    using var scanOperation = MemoryJournal.BeginOperation("Scan.PrepareFloor", $"map={map.Id:N};floor={floorKey}");
                    scan = ScanStructureIndex.FromMaskedLabels(labels, semantic, nativeDistance, nativeSemantic, alignment.SemanticIndex, erasedKinds);
                    try { scan.PrepareLocalGeometryFromLabels(labels); }
                    catch (Exception error)
                    {
                        MapLogCollector.Instance.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Warning,
                            "DeepScan 局部索引预热失败，其他扫描模式仍可使用。",
                            details: new() { ["exception"] = error.ToString() });
                    }
                    // Keep a transient DeepScan failure retryable next match.
                    // Ordinary scanning can use this owner without persisting
                    // an incomplete artifact as a permanent cache hit.
                    if (scan.LocalGeometry is not null)
                        PreparedFloorArtifactCache.Write(scanPath, scanKey, writer => scan.WriteTo(writer, alignment.SemanticIndex));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var anchors = MapFloorRules.GetFloorProfile(map, floorKey)?.Anchors
                .Select(anchor => anchor.Clone()).ToArray() ?? [];
            alignment.AutomaticEntryIndex = MapFrontEntryReferenceIndexBuilder.Build(map, floorKey,
                alignment, key, repository.GetCatalogRevision(), anchors, cancellationToken).Index;
            var result = new PreparedFloorResources(scan, alignment);
            scan = null; alignment = null;
            return result;
        }
        finally
        {
            scan?.Dispose(); alignment?.Dispose();
            if (labels is not null) { labels.Dispose(); MemoryJournal.ReleaseResource(labels); }
        }
    }

    internal static Rect Mask(MapRecord map, string floorKey, int width, int height)
    {
        var bounds = MapScanFloorRules.GetScanFeatureAnchor(map, floorKey)?.Bounds;
        return bounds?.IsValid != true ? default : new Rect((int)Math.Floor(bounds.X * width), (int)Math.Floor(bounds.Y * height),
            (int)Math.Ceiling(bounds.Width * width), (int)Math.Ceiling(bounds.Height * height)).Intersect(new(0, 0, width, height));
    }
}
