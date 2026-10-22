using IDVBuff.Core.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    // The lifecycle/cache gate is held; no match consumer can see a partial
    // scan publication. Each worker releases its labels before taking a new floor.
    private void PrepareResidentFloors(IReadOnlyList<MapRecord> maps, Action<double, string> report)
    {
        using var operation = MemoryJournal.BeginOperation("Match.PrepareFloorResources", _residentMapClass);
        var targets = maps.Select(map => (Map: map, Floor: MapScanFloorRules.ResolveScanFloorKey(map)))
            .Where(item => _repository.TryGetValidSideEntranceFeaturePath(item.Map, item.Floor, out _, out _)).ToArray();
        var scans = new ScanStructureIndex?[targets.Length];
        var completed = 0; var progressGate = new object();
        try
        {
            Parallel.For(0, targets.Length, new ParallelOptions { MaxDegreeOfParallelism = MapCvRecognitionHelpers.FloorPreparationWorkerCount }, i =>
            {
                var (map, floor) = targets[i];
                var key = FloorResourcePreparation.RuntimeKey(map, map.Floors.Single(f => f.Key == floor),
                    MapFeatureCacheRules.ComputeContentFingerprint(map));
                try
                {
                    using var prepared = FloorResourcePreparation.Prepare(_repository, map, floor, true,
                        _deterministicScanStorage, _deterministicSemanticStorage, runtimeKey: key);
                    var alignment = prepared.TakeAlignment();
                    try
                    {
                        if (!_vpsg3Registry.TryBeginBuild(key) || !TryPublishSelectedFloor(key, alignment, null))
                            throw new InvalidOperationException("当前楼层资源已过期，未发布对局资源。");
                    }
                    catch { alignment.Dispose(); throw; }
                    scans[i] = prepared.TakeScan();
                }
                catch (Exception error)
                {
                    RecordSelectedFloorBuildFailure(key, error.Message, null);
                    MapLogCollector.Instance.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Warning,
                        "楼层资源准备失败，跳过该楼层。", details: new()
                        { ["mapId"] = map.Id, ["floor"] = floor, ["error"] = error.ToString() });
                }
                finally
                {
                    lock (progressGate) report(.2 + .75 * ++completed / Math.Max(1, targets.Length), "正在准备楼层扫描与对齐资源");
                }
            });
            var cache = new Dictionary<(Guid, string), ScanStructureIndex>();
            for (var i = 0; i < targets.Length; i++)
            {
                if (scans[i] is not { } scan) continue;
                cache.Add((targets[i].Map.Id, targets[i].Floor), scan);
                MemoryJournal.TrackResource(scan, $"ScanIndex/{targets[i].Map.Id:N}/{targets[i].Floor}", scan.RetainedMemoryBytes);
            }
            // Publish takes ownership even if it fails. Do not retire it twice.
            Array.Clear(scans);
            _sideEntranceFeatureCache.Publish(cache);
        }
        finally { ScanIndexCache.RetireUnpublished(scans.OfType<ScanStructureIndex>()); }
    }

    private Mat ReadAlignmentReference(MapRecord map, string floor, MapStructureRegistrationTuning tuning)
    {
        if (GetReferenceProfile(map, floor, tuning, MapStructurePreprocessingProfile.EdgesAndFeatures)
            == MapStructurePreprocessingProfile.PrebuiltStructureLine
            && TryGetVpsg3FloorLease(map, floor, out var lease))
        {
            using (lease)
                if (lease.Floor.CreateReferenceImage() is { } image) return image;
        }
        return Cv2.ImRead(GetAlignmentReferencePath(map, floor, tuning), ImreadModes.Unchanged);
    }

    private Mat ReadCoverageGeometry(MapRecord map, string floor)
    {
        if (TryGetVpsg3FloorLease(map, floor, out var lease))
        {
            using (lease)
                if (lease.Floor.CreateGeometryImage() is { } image) return image;
        }
        using var input = Cv2.ImRead(_repository.GetPrebuiltStructureLinePath(map, floor), ImreadModes.Unchanged);
        return SemanticStructureContours.Geometry(input);
    }
}
