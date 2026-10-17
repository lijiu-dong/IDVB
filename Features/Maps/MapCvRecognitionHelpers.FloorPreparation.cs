using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static partial class MapCvRecognitionHelpers
{
    /// <summary>
    /// Builds the side-entrance feature cache: loads the valid entry/scan-floor
    /// feature image for every registered map as a grayscale <see cref="Mat"/>.
    /// Other floors can participate in post-lock alignment, but never in the
    /// initial identity scan and therefore must not retain a full distance index.
    /// </summary>
    internal static Dictionary<(Guid, string), ScanStructureIndex> BuildSideEntranceFeatureCache(
        MapRepository repository,
        IReadOnlyList<MapRecord> maps, bool deterministicScanStorage = false, bool deterministicSemanticStorage = false)
    {
        // Selecting floors only reads metadata and checks files. Each selected
        // floor then owns independent decode/encode scratch, so a bounded number
        // of workers prepares them; publication keeps the catalog order.
        var floors = new List<(MapRecord Map, string FloorKey)>();
        foreach (var map in maps)
        {
            var scanFloorKey = MapScanFloorRules.ResolveScanFloorKey(map);
            foreach (var floorDef in MapFloorRules.GetOrderedFloors(map))
            {
                if (!string.Equals(
                        MapScanFloorRules.NormalizeFloorIdentity(floorDef.Key),
                        MapScanFloorRules.NormalizeFloorIdentity(scanFloorKey),
                        StringComparison.Ordinal))
                    continue;

                var profile = MapFloorRules.GetFloorProfile(map, floorDef.Key);
                if (profile is null
                    || !repository.TryGetValidSideEntranceFeaturePath(
                        map,
                        floorDef.Key,
                        out _,
                        out _))
                    continue;
                floors.Add((map, floorDef.Key));
            }
        }

        var built = new ScanStructureIndex?[floors.Count];
        var cache = new Dictionary<(Guid, string), ScanStructureIndex>();
        var transferred = false;
        try
        {
            var completed = 0;
            var progressGate = new object();
            Parallel.For(0, floors.Count,
                new ParallelOptions { MaxDegreeOfParallelism = FloorPreparationWorkerCount }, i =>
                {
                    var (map, floorKey) = floors[i];
                    built[i] = PrepareScanFloor(repository, map, floorKey,
                        deterministicScanStorage, deterministicSemanticStorage);
                    // Keep reported progress monotonic across workers.
                    lock (progressGate)
                        MapOperationProgress.Report((double)++completed / floors.Count,
                            $"正在加载识别轮廓：{map.DisplayName}……");
                });
            for (var i = 0; i < built.Length; i++)
            {
                if (built[i] is not { } index) continue;
                var key = (floors[i].Map.Id, floors[i].FloorKey);
                cache.TryGetValue(key, out var replaced);
                cache[key] = index;
                built[i] = null;
                if (replaced is not null) ScanIndexCache.RetireUnpublished([replaced]);
            }
            MapOperationProgress.Report(1, "识别轮廓加载完成");
            transferred = true;
            return cache;
        }
        finally
        {
            if (!transferred)
            {
                ScanIndexCache.RetireUnpublished(cache.Values);
                ScanIndexCache.RetireUnpublished(built.OfType<ScanStructureIndex>());
            }
        }
    }

    // Floor preparation owns full-resolution decode/encode scratch. Bound the
    // workers so match start uses spare cores without multiplying the peak by
    // every core, and leave cores for the game running in the foreground.
    internal static int FloorPreparationWorkerCount { get; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    private static ScanStructureIndex? PrepareScanFloor(MapRepository repository, MapRecord map, string floorKey,
        bool deterministicScanStorage, bool deterministicSemanticStorage)
    {
        ScanStructureIndex? unpublishedIndex = null;
        Mat? loaded = null;
        Mat? labels = null;
        long memoryRequest = 0;
        try
        {
            using var memoryOperation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation(
                "Scan.PrepareFloor", $"map={map.Id:N};floor={floorKey}");
            // Retain the derived-feature integrity gate, but search the entire authored floor.
            var path = repository.GetPrebuiltStructureLinePath(map, floorKey);
            if (!IDVBuff.Core.Imaging.PrebuiltContourLabels.TryRead(path, out labels, out var semantic,
                $"ScanLabels/{map.Id:N}/{floorKey}"))
            {
                // Unsupported PNG encodings retain the original decoder
                // and palette gate, without accepting a gray downgrade.
                memoryRequest = IDVBuff.Core.Diagnostics.MemoryJournal.Request(
                    $"ScanContour/{map.Id:N}/{floorKey}", nativeModule: "OpenCvSharpExtern.dll");
                var mat = loaded = Cv2.ImRead(path, ImreadModes.Unchanged);
                if (mat.Empty()) return null;
                IDVBuff.Core.Diagnostics.MemoryJournal.TrackResource(mat,
                    $"ScanContour/{map.Id:N}/{floorKey}", mat.Total() * mat.ElemSize(),
                    "OpenCvSharpExtern.dll", memoryRequest);
                semantic = mat.Channels() == 3;
                labels = SemanticStructureContours.Decode(mat);
                IDVBuff.Core.Diagnostics.MemoryJournal.TrackResource(labels,
                    $"ScanLabels/{map.Id:N}/{floorKey}", labels.Total() * labels.ElemSize(),
                    "OpenCvSharpExtern.dll");
                mat.Dispose();
                IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(mat);
                loaded = null;
            }
            // Both decoders validate every original pixel before the
            // anchor is erased. The direct path never owns an RGB Mat.
            var anchor = MapScanFloorRules.GetScanFeatureAnchor(map, floorKey);
            if (anchor?.Bounds?.IsValid is true)
            {
                var rect = new Rect((int)Math.Floor(anchor.Bounds.X * labels.Width),
                    (int)Math.Floor(anchor.Bounds.Y * labels.Height),
                    (int)Math.Ceiling(anchor.Bounds.Width * labels.Width),
                    (int)Math.Ceiling(anchor.Bounds.Height * labels.Height))
                    .Intersect(new Rect(0, 0, labels.Width, labels.Height));
                if (rect.Width > 0 && rect.Height > 0) Cv2.Rectangle(labels, rect, Scalar.Black, -1);
            }
            var index = unpublishedIndex = ScanStructureIndex.FromPreparedLabels(labels, semantic, deterministicScanStorage, deterministicSemanticStorage);
            try { index.PrepareLocalGeometryFromLabels(labels); }
            catch (Exception ex)
            {
                MapLogCollector.Instance.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Warning,
                    "DeepScan 局部索引预热失败，其他扫描模式仍可使用。",
                    details: new() { ["exception"] = ex.ToString() });
            }
            IDVBuff.Core.Diagnostics.MemoryJournal.TrackResource(index,
                $"ScanIndex/{map.Id:N}/{floorKey}", index.RetainedMemoryBytes);
            unpublishedIndex = null;
            // Both indices own immutable storage. Labels end in finally,
            // including failure paths, and are never weak-table cache keys.
            return index;
        }
        catch (Exception ex)
        {
            IDVBuff.Core.Diagnostics.MemoryJournal.Current?.Record("resource-request-failed",
                new { id = memoryRequest, map.Id, floor = floorKey, error = ex.ToString() });
            // 单张地图加载失败不影响其他地图
            return null;
        }
        finally
        {
            try { if (unpublishedIndex is not null) ScanIndexCache.RetireUnpublished([unpublishedIndex]); }
            finally
            {
                if (labels is not null)
                {
                    labels.Dispose();
                    IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(labels);
                }
                if (loaded is not null)
                {
                    loaded.Dispose();
                    IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(loaded);
                }
            }
        }
    }
}
