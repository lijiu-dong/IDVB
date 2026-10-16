using OpenCvSharp;
using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Features.Maps;

internal static partial class MapStructureScaleEstimator
{
    private static Rect? SemanticSearchRegion(ScaleScore? anchor, Size patch, Size size,
        Size reference, int factor, int radius)
    {
        if (anchor is null || !double.IsFinite(anchor.X)) return null;
        var cx = (anchor.X + patch.Width / anchor.Scale / 2d) / factor - size.Width / 2d;
        var cy = (anchor.Y + patch.Height / anchor.Scale / 2d) / factor - size.Height / 2d;
        var r = Math.Max(2, radius / factor);
        var left = Math.Clamp((int)Math.Floor(cx) - r, 0, reference.Width - size.Width);
        var top = Math.Clamp((int)Math.Floor(cy) - r, 0, reference.Height - size.Height);
        var right = Math.Clamp((int)Math.Ceiling(cx) + r, left, reference.Width - size.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(cy) + r, top, reference.Height - size.Height);
        return new Rect(left, top, right - left + size.Width, bottom - top + size.Height);
    }

    private static IEnumerable<double> ReserveSemanticGrid(
        IEnumerable<double> candidates, int factor, Size referenceSize,
        Mat livePatch, SemanticScaleSearch semantic)
    {
        // A correlation output is smaller than its reference by the patch
        // extent. Reserve once for this complete grid, preserving order
        // and avoiding both whole-floor buffers and per-scale growth.
        var grid = candidates as IReadOnlyList<double> ?? candidates.ToArray();
        var maximumWidth = 0;
        var maximumHeight = 0;
        foreach (var scale in grid)
        {
            var width = Math.Max(1, (int)Math.Round(livePatch.Width / scale / factor));
            var height = Math.Max(1, (int)Math.Round(livePatch.Height / scale / factor));
            if (width < 3 || height < 3 || width >= referenceSize.Width || height >= referenceSize.Height) continue;
            maximumWidth = Math.Max(maximumWidth, referenceSize.Width - width + 1);
            maximumHeight = Math.Max(maximumHeight, referenceSize.Height - height + 1);
        }
        semantic.ReserveScoreWorkspace(factor, maximumWidth, maximumHeight);
        return grid;
    }

    private sealed partial class SemanticScaleSearch : IDisposable
    {
        private readonly List<(ClippedFloatPlane Distance, Mat Patch)> _channels = [];
        private readonly Dictionary<int, Mat[]> _levels = [];
        private readonly List<Mat> _pendingCoarse = [];
        private readonly Dictionary<int, ScoreWorkspace> _scoreWorkspaces = [];
        private readonly List<SemanticDistanceOwner.Lease> _referenceLeases = [];
        private readonly HashSet<ClippedFloatPlane> _borrowedPlanes = [];
        private Mat[]? _nativeRegion;
        private Rect _cachedRegion;

        public static SemanticScaleSearch? Create(Mat? reference, Mat? live,
            Mat edges, Rect bounds, double clip)
            => CreateCore(reference, live, edges, bounds, clip, null);

        public static SemanticScaleSearch? CreateCached(MapStructureFeatures reference, Mat? live,
            Mat edges, Rect bounds, double clip)
            => CreateCore(reference.SemanticLabels, live, edges, bounds, clip, reference);

        private static SemanticScaleSearch? CreateCore(Mat? reference, Mat? live,
            Mat edges, Rect bounds, double clip, MapStructureFeatures? owner)
        {
            if (reference is null || live is null) return null;
            using var operation = MemoryJournal.BeginOperation("Structure.SemanticScale.Create", $"reference={reference.Width}x{reference.Height};clip={clip:R}");
            var search = new SemanticScaleSearch();
            try
            {
                foreach (var kind in new byte[] { SemanticStructureContours.Warm, SemanticStructureContours.Cool, 0 })
                {
                    using var liveMask = new Mat();
                    Cv2.InRange(live, new Scalar(kind), new Scalar(kind), liveMask);
                    Cv2.BitwiseAnd(liveMask, edges, liveMask);
                    using var roi = new Mat(liveMask, bounds);
                    if (Cv2.CountNonZero(roi) == 0) continue;
                    if (owner is not null)
                    {
                        var lease = owner.RentSemanticDistance(kind, clip);
                        try { search._referenceLeases.Add(lease); }
                        catch { lease.Dispose(); throw; }
                        search._borrowedPlanes.Add(lease.Distance);
                        // Recreate the original area resize from exact full pixels.
                        // Keeping its odd-dimension floats packed was larger than
                        // the full-resolution packed field itself.
                        var coarse = Resize(lease.Distance, 2);
                        try { search._pendingCoarse.Add(coarse); }
                        catch { DisposeDistance(coarse); throw; }
                        var patch = roi.Clone();
                        try { search._channels.Add((lease.Distance, patch)); }
                        catch { patch.Dispose(); throw; }
                        continue;
                    }
                    using var referenceMask = new Mat();
                    if (kind == 0) Cv2.Threshold(reference, referenceMask, 0, 255, ThresholdTypes.Binary);
                    else Cv2.InRange(reference, new Scalar(kind), new Scalar(kind), referenceMask);
                    var request = MemoryJournal.Request($"Structure.SemanticScale.Distance/{kind}", reference.Total() * sizeof(float), "OpenCvSharpExtern.dll");
                    var distance = new NativeDistanceMat(reference.Size(), MatType.CV_32FC1, new Scalar(clip));
                    try
                    {
                        MemoryJournal.TrackResource(distance, $"Structure.SemanticScale.Distance/{kind}", distance.Total() * sizeof(float), "OpenCvSharpExtern.dll", request);
                        if (Cv2.CountNonZero(referenceMask) > 0)
                        {
                            using var inverse = new Mat();
                            Cv2.BitwiseNot(referenceMask, inverse);
                            Cv2.DistanceTransform(inverse, distance, DistanceTypes.L2, DistanceTransformMasks.Mask3);
                            Cv2.Threshold(distance, distance, clip, clip, ThresholdTypes.Trunc);
                        }
                        // Resize the original full plane before packing: odd
                        // dimensions make area weights cross tile boundaries.
                        var coarse = ResizeFull(distance, 2);
                        try { search._pendingCoarse.Add(coarse); }
                        catch { DisposeDistance(coarse); throw; }
                        var patch = roi.Clone();
                        ClippedFloatPlane? packed = null;
                        try
                        {
                            packed = new ClippedFloatPlane(distance, (float)clip);
                            search._channels.Add((packed, patch));
                        }
                        catch { packed?.Dispose(); patch.Dispose(); throw; }
                    }
                    finally { DisposeDistance(distance); }
                }
                search._levels[2] = search._pendingCoarse.ToArray();
                search._pendingCoarse.Clear();
                return search;
            }
            catch { search.Dispose(); throw; }
        }

        public void ReserveScoreWorkspace(int factor, int maximumWidth, int maximumHeight)
        {
            if (maximumWidth <= 0 || maximumHeight <= 0) return;
            if (_scoreWorkspaces.TryGetValue(factor, out var previous))
            {
                if (previous.Fits(maximumWidth, maximumHeight)) return;
                previous.Dispose();
                _scoreWorkspaces.Remove(factor);
            }
            _scoreWorkspaces[factor] = new(maximumWidth, maximumHeight, factor);
        }

        public double Score(Size size, int factor, Rect? region, out Point location)
        {
            var origin = new Point();
            Mat[] distances;
            Rect roi;
            if (factor == 1)
            {
                var full = _channels[0].Distance;
                var requested = region ?? new Rect(0, 0, full.Width, full.Height);
                if (_nativeRegion is null || requested.Left < _cachedRegion.Left
                    || requested.Top < _cachedRegion.Top || requested.Right > _cachedRegion.Right
                    || requested.Bottom > _cachedRegion.Bottom)
                {
                    if (_nativeRegion is not null)
                        foreach (var previous in _nativeRegion) DisposeDistance(previous);
                    _nativeRegion = null;
                    var left = Math.Max(0, requested.Left - 32);
                    var top = Math.Max(0, requested.Top - 32);
                    _cachedRegion = new Rect(left, top,
                        Math.Min(full.Width, requested.Right + 32) - left,
                        Math.Min(full.Height, requested.Bottom + 32) - top);
                    var planes = new List<Mat>();
                    try
                    {
                        foreach (var channel in _channels)
                        {
                            var plane = channel.Distance.Read(_cachedRegion);
                            try { planes.Add(plane); }
                            catch { DisposeDistance(plane); throw; }
                        }
                        _nativeRegion = planes.ToArray();
                    }
                    catch { foreach (var plane in planes) DisposeDistance(plane); throw; }
                }
                distances = _nativeRegion;
                roi = new Rect(requested.Left - _cachedRegion.Left, requested.Top - _cachedRegion.Top,
                    requested.Width, requested.Height);
                origin = _cachedRegion.TopLeft;
            }
            else
            {
                if (!_levels.TryGetValue(factor, out distances!)) _levels[factor] = distances = CreateDistanceLevel(factor);
                roi = region ?? new Rect(0, 0, distances[0].Width, distances[0].Height);
            }
            var width = roi.Width - size.Width + 1;
            var height = roi.Height - size.Height + 1;
            if (!_scoreWorkspaces.TryGetValue(factor, out var workspace)
                || !workspace.Fits(width, height))
            {
                workspace?.Dispose();
                _scoreWorkspaces.Remove(factor);
                // Global coarse scores share the level's bounded raster. Local
                // refinement uses small buckets instead of a full-floor buffer.
                var capacityWidth = region is null ? distances[0].Width : checked((width + 63) / 64 * 64);
                var capacityHeight = region is null ? distances[0].Height : checked((height + 63) / 64 * 64);
                _scoreWorkspaces[factor] = workspace = new(capacityWidth, capacityHeight, factor);
            }
            return ScoreCore(distances, roi, origin, size, workspace, out location);
        }

        // Scores independent sizes of one reduced level concurrently. The level
        // and patches are read-only during the batch; each worker owns one
        // accumulator sized for the largest request, released before return.
        public double[] ScoreBatch(IReadOnlyList<(Size Size, Rect? Region)> requests, int factor,
            out Point[] locations)
        {
            if (factor == 1) throw new ArgumentOutOfRangeException(nameof(factor));
            var costs = new double[requests.Count];
            var found = new Point[requests.Count];
            locations = found;
            if (requests.Count == 0) return costs;
            if (!_levels.TryGetValue(factor, out var distances)) _levels[factor] = distances = CreateDistanceLevel(factor);
            var full = new Rect(0, 0, distances[0].Width, distances[0].Height);
            var capacityWidth = 0;
            var capacityHeight = 0;
            foreach (var (size, region) in requests)
            {
                var roi = region ?? full;
                capacityWidth = Math.Max(capacityWidth, roi.Width - size.Width + 1);
                capacityHeight = Math.Max(capacityHeight, roi.Height - size.Height + 1);
            }
            var pool = new System.Collections.Concurrent.ConcurrentBag<ScoreWorkspace>();
            var created = new System.Collections.Concurrent.ConcurrentQueue<ScoreWorkspace>();
            try
            {
                RunParallel(requests.Count, index =>
                {
                    if (!pool.TryTake(out var workspace))
                    {
                        workspace = new ScoreWorkspace(capacityWidth, capacityHeight, factor);
                        created.Enqueue(workspace);
                    }
                    try
                    {
                        var (size, region) = requests[index];
                        costs[index] = ScoreCore(distances, region ?? full, new Point(), size, workspace,
                            out found[index]);
                    }
                    finally { pool.Add(workspace); }
                });
            }
            finally { foreach (var workspace in created) workspace.Dispose(); }
            return costs;
        }

        private double ScoreCore(Mat[] distances, Rect roi, Point origin, Size size,
            ScoreWorkspace workspace, out Point location)
        {
            var width = roi.Width - size.Width + 1;
            var height = roi.Height - size.Height + 1;
            using var combined = new Mat(workspace.Combined, new Rect(0, 0, width, height));
            using var scores = new Mat(workspace.Scores, new Rect(0, 0, width, height));
            // First channel overwrites the complete accumulator.
            var mass = 0d;
            for (var i = 0; i < _channels.Count; i++)
            {
                using var reduced = new Mat();
                using var weights = new Mat();
                Cv2.Resize(_channels[i].Patch, reduced, size, interpolation: InterpolationFlags.Area);
                reduced.ConvertTo(weights, MatType.CV_32FC1, 1d / 255d);
                mass += Cv2.Sum(weights).Val0;
                using var referenceRegion = new Mat(distances[i], roi);
                Cv2.MatchTemplate(referenceRegion, weights, i == 0 ? combined : scores, TemplateMatchModes.CCorr);
                if (i > 0) Cv2.Add(combined, scores, combined);
            }
            Cv2.MinMaxLoc(combined, out double minimum, out double _, out location, out _);
            location += roi.TopLeft + origin;
            return mass >= 1d ? minimum / mass : double.PositiveInfinity;
        }

        public void Dispose()
        {
            foreach (var level in _pendingCoarse) DisposeDistance(level);
            if (_nativeRegion is not null)
                foreach (var previous in _nativeRegion) DisposeDistance(previous);
            foreach (var channel in _channels) { if (!_borrowedPlanes.Contains(channel.Distance)) channel.Distance.Dispose(); channel.Patch.Dispose(); }
            foreach (var level in _levels.Values) foreach (var distance in level) DisposeDistance(distance);
            foreach (var workspace in _scoreWorkspaces.Values) workspace.Dispose();
            foreach (var lease in _referenceLeases) lease.Dispose();
        }

        public void ReleaseDistanceLevel(int factor)
        {
            if (_levels.Remove(factor, out var level)) foreach (var distance in level) DisposeDistance(distance);
            if (_scoreWorkspaces.Remove(factor, out var workspace)) workspace.Dispose();
        }

        private sealed class ScoreWorkspace : IDisposable
        {
            public Mat Combined { get; }
            public Mat Scores { get; }
            public bool Fits(int width, int height) => width <= Combined.Width && height <= Combined.Height;
            public ScoreWorkspace(int width, int height, int factor)
            {
                Combined = Create(width, height, factor);
                try { Scores = Create(width, height, factor); }
                catch { DisposeDistance(Combined); throw; }
            }
            private static Mat Create(int width, int height, int factor)
            {
                var bytes = checked((long)width * height * sizeof(float));
                var request = MemoryJournal.Request($"Structure.SemanticScale.ScoreWorkspace/{factor}", bytes, "OpenCvSharpExtern.dll");
                var buffer = new NativeDistanceMat(height, width, MatType.CV_32FC1);
                MemoryJournal.TrackResource(buffer, $"Structure.SemanticScale.ScoreWorkspace/{factor}", bytes, "OpenCvSharpExtern.dll", request);
                return buffer;
            }
            public void Dispose() { DisposeDistance(Combined); DisposeDistance(Scores); }
        }

        private Mat[] CreateDistanceLevel(int factor)
        {
            using var operation = MemoryJournal.BeginOperation("Structure.SemanticScale.Level", $"factor={factor}");
            var created = new List<Mat>(_channels.Count);
            try
            {
                foreach (var channel in _channels)
                {
                    var level = Resize(channel.Distance, factor);
                    try { created.Add(level); }
                    catch { DisposeDistance(level); throw; }
                }
                return created.ToArray();
            }
            catch
            {
                foreach (var level in created) DisposeDistance(level);
                throw;
            }
        }

        private static void DisposeDistance(Mat distance)
        {
            distance.Dispose();
            MemoryJournal.ReleaseResource(distance);
        }
        private static Mat Resize(ClippedFloatPlane source, int factor)
        {
            var full = source.Read(new Rect(0, 0, source.Width, source.Height));
            try { return ResizeFull(full, factor); }
            finally { DisposeDistance(full); }
        }

        private static Mat ResizeFull(Mat source, int factor)
        {
            var result = ClippedFloatPlane.Allocate(Math.Max(1, source.Height / factor),
                Math.Max(1, source.Width / factor), MatType.CV_32FC1, "Structure.SemanticScale.Level");
            try
            {
                Cv2.Resize(source, result, result.Size(), interpolation: InterpolationFlags.Area);
                return result;
            }
            catch { DisposeDistance(result); throw; }
        }
    }
}

