using System.Runtime.CompilerServices;
using IDVBuff.Features.Maps.AdaptiveScaleAlignment;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    internal Func<bool> ScaleLockingAllowed { get; set; } = static () => true;
    private readonly object _scalePolicyGate = new();
    private readonly Dictionary<AdaptiveScaleKey, AdaptiveScaleCoverageMilestones> _coverageMilestones = [];
    private readonly Dictionary<(Guid MapId, string Floor, string Source), AdaptiveScaleCoverageReference> _coverageReferences = [];
    private readonly ConditionalWeakTable<CapturedGameFrame, ScaleRefreshFrame> _scaleRefreshFrames = new();
    private readonly ConditionalWeakTable<CapturedGameFrame, CoverageFrame> _alignmentCoverage = new();
    private sealed record CoverageFrame(AdaptiveScaleKey Key, long Generation, int Hits, int Total,
        MapOverlayTransform Transform);
    private long _scalePolicyGeneration;
    private sealed record ScaleRefreshFrame(AdaptiveScaleKey Key, long Generation, double PriorScale)
    {
        public bool Completed { get; set; }
        public bool ScaleChanged { get; set; }
    }

    internal bool CanReuseScale(CapturedGameFrame frame, Guid mapId, string floor)
    {
        if (!ScaleLockingAllowed())
            return false;
        var map = TryGetMap(mapId);
        if (map is null)
            return false;
        // A coverage milestone requests an evidence check, not invalidation of
        // this floor's trusted scale. Failed alignment still takes the recovery route.
        return true;
    }

    private void CompleteVpsgScaleRefresh(CapturedGameFrame frame, double finalScale)
    {
        lock (_scalePolicyGate)
            if (_scaleRefreshFrames.TryGetValue(frame, out var value))
            {
                value.Completed = true;
                value.ScaleChanged = Math.Abs(finalScale / value.PriorScale - 1d) > .005d;
            }
    }

    private (double? Lock, double? RefreshPrior) ResolveVpsgScaleLock(
        CapturedGameFrame frame, MapRecord map, string floor, double? seed)
    {
        var key = AdaptiveScaleKey.Create(map, floor, frame.ClientBounds, frame.ViewportBounds);
        lock (_scalePolicyGate)
        {
            if (!ScaleLockingAllowed())
            {
                _scaleRefreshFrames.Remove(frame);
                return (null, null);
            }
            if (seed is { } scale && double.IsFinite(scale) && scale > 0d)
            {
                // Validation of recovery candidates re-enters this method. Keep
                // the original incumbent so the final commit invalidates its cache.
                if (!_scaleRefreshFrames.TryGetValue(frame, out var existing)
                    || existing.Key != key || existing.Generation != _scalePolicyGeneration)
                {
                    _scaleRefreshFrames.Remove(frame);
                    _scaleRefreshFrames.Add(frame, new(key, _scalePolicyGeneration, scale));
                }
                return (seed, seed);
            }
            return (seed, null);
        }
    }

    internal bool WasScaleRefresh(CapturedGameFrame frame, MapRecord map, string floor)
    {
        var key = AdaptiveScaleKey.Create(map, floor, frame.ClientBounds, frame.ViewportBounds);
        lock (_scalePolicyGate)
            return _scaleRefreshFrames.TryGetValue(frame, out var value)
                && value.Completed && value.Key == key && value.Generation == _scalePolicyGeneration;
    }

    internal bool DidChangeScaleOnRefresh(CapturedGameFrame frame, MapRecord map, string floor)
    {
        var key = AdaptiveScaleKey.Create(map, floor, frame.ClientBounds, frame.ViewportBounds);
        lock (_scalePolicyGate)
            return _scaleRefreshFrames.TryGetValue(frame, out var value)
                && value.Completed && value.ScaleChanged
                && value.Key == key && value.Generation == _scalePolicyGeneration;
    }

    private CoverageFrame? MeasureAlignmentCoverage(CapturedGameFrame frame, RuntimeMapRecognition recognition,
        Vpsg3LiveObservation? observation = null)
    {
        if (recognition.Result.OverlayTransform is not { } transform)
            return null;
        var key = AdaptiveScaleKey.Create(recognition.Map, recognition.Result.Floor, frame.ClientBounds, frame.ViewportBounds);
        var sourceFloor = recognition.Map.Floors.FirstOrDefault(f => f.Key == recognition.Result.Floor);
        var referenceKey = (recognition.Map.Id, recognition.Result.Floor,
            sourceFloor?.PrebuiltStructureLine is { } source ? Vpsg3IndexCacheKey.CreatePrebuiltGenerationIdentity(source) : string.Empty);
        long generation;
        AdaptiveScaleCoverageReference.Lease lease;
        lock (_scalePolicyGate)
        {
            // A late measurement must not recreate native cache storage after
            // Dispose has retired the last owner.
            if (_disposed) return null;
            generation = _scalePolicyGeneration;
            if (_alignmentCoverage.TryGetValue(frame, out var cached)
                && cached.Key == key && cached.Generation == generation
                && ReferenceEquals(cached.Transform, transform))
                return cached;
            if (!_coverageReferences.TryGetValue(referenceKey, out var reference))
            {
                // Coverage is an advisory post-alignment measurement.  A resident VPSG3 index
                // may still be valid while its on-disk source is being repaired or has changed;
                // that condition must not turn an already accepted alignment into an exception.
                if (!_repository.HasPrebuiltStructureLine(recognition.Map, recognition.Result.Floor))
                    return null;
                string path;
                try
                {
                    path = _repository.GetPrebuiltStructureLinePath(recognition.Map, recognition.Result.Floor);
                }
                catch (InvalidDataException)
                {
                    return null;
                }
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
                    return null;
                using var lines = ReadCoverageGeometry(recognition.Map, recognition.Result.Floor);
                if (lines.Empty())
                    return null;
                reference = AdaptiveScaleCoverageReference.Create(lines);
                try { _coverageReferences.Add(referenceKey, reference); }
                catch { reference.Dispose(); throw; }
            }
            lease = reference.Borrow();
        }
        using var referenceLease = lease;
        var points = lease.Points;
        if (points.Length == 0)
            return null;
        var live = observation ?? frame.GetOrCreateVpsg3Observation();
        var hits = AdaptiveScaleCoverageMilestones.CountCoveredPoints(
            points, live.ObservedEdges, transform, frame.ViewportBounds);
        var measured = new CoverageFrame(key, generation, hits, points.Length, transform);
        lock (_scalePolicyGate)
        {
            if (generation != _scalePolicyGeneration)
                return null;
            _alignmentCoverage.Remove(frame);
            _alignmentCoverage.Add(frame, measured);
        }
        return measured;
    }

    internal void ObserveAlignmentCoverage(CapturedGameFrame frame, RuntimeMapRecognition recognition)
    {
        var measured = MeasureAlignmentCoverage(frame, recognition);
        if (measured is null)
            return;
        var key = measured.Key;
        var coverage = (double)measured.Hits / measured.Total;
        lock (_scalePolicyGate)
        {
            if (measured.Generation != _scalePolicyGeneration)
                return;
            if (!_coverageMilestones.TryGetValue(key, out var state))
                _coverageMilestones[key] = state = new();
            var refreshed = WasScaleRefresh(frame, recognition.Map, recognition.Result.Floor);
            var changed = DidChangeScaleOnRefresh(frame, recognition.Map, recognition.Result.Floor);
            state.Observe(coverage, refreshed);
            _scaleRefreshFrames.Remove(frame);
            MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                "floor scale coverage milestone", details: new()
                {
                    ["mapId"] = key.MapId, ["floor"] = key.FloorKey,
                    ["totalCoverage"] = coverage, ["coveredReferencePoints"] = measured.Hits,
                    ["totalReferencePoints"] = measured.Total, ["milestonePercent"] = state.ReachedPercent,
                    ["refreshNextAlignment"] = state.RefreshPending, ["scaleChecked"] = refreshed,
                    ["scaleRefreshed"] = changed
                });
        }
    }

    private void ResetScaleCoverage()
    {
        lock (_scalePolicyGate)
        {
            _scalePolicyGeneration++;
            _coverageMilestones.Clear();
            foreach (var reference in _coverageReferences.Values) reference.Dispose();
            _coverageReferences.Clear();
            _scaleRefreshFrames.Clear();
            _alignmentCoverage.Clear();
        }
    }
}
