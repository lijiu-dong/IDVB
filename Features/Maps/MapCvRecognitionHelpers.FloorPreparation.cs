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
        try
        {
            using var prepared = FloorResourcePreparation.Prepare(repository, map, floorKey, true,
                deterministicScanStorage, deterministicSemanticStorage);
            var scan = prepared.TakeScan();
            if (scan is not null) IDVBuff.Core.Diagnostics.MemoryJournal.TrackResource(scan,
                $"ScanIndex/{map.Id:N}/{floorKey}", scan.RetainedMemoryBytes);
            return scan;
        }
        catch (Exception error)
        {
            IDVBuff.Core.Diagnostics.MemoryJournal.Current?.Record("resource-request-failed",
                new { map.Id, floor = floorKey, error = error.ToString() });
            return null;
        }
    }
}
