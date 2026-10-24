using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static partial class MapStructureScaleEstimator
{
    private static IReadOnlyList<ScaleScore>? ScoreLocalSemanticGrid(IEnumerable<double> candidates,
        Size referenceSize, Mat livePatch, SemanticScaleSearch semantic, ScaleScore anchor, int radius)
    {
        var requests = new List<(double Scale, Size Size, Rect Region)>();
        foreach (var scale in candidates)
        {
            var size = new Size(Math.Max(1, (int)Math.Round(livePatch.Width / scale)),
                Math.Max(1, (int)Math.Round(livePatch.Height / scale)));
            if (size.Width < 3 || size.Height < 3 || size.Width >= referenceSize.Width || size.Height >= referenceSize.Height)
                continue;
            if (SemanticSearchRegion(anchor, livePatch.Size(), size, referenceSize, 1, radius) is not { } region)
                return null;
            requests.Add((scale, size, region));
        }
        if (!semantic.TryScoreLocalGrid(requests.Select(request => (request.Size, request.Region)).ToArray(), out var results))
            return null;
        var scores = new List<ScaleScore>(requests.Count);
        for (var i = 0; i < requests.Count; i++)
            if (double.IsFinite(results[i].Cost))
                scores.Add(new(requests[i].Scale, results[i].Cost, results[i].Location.X, results[i].Location.Y));
        return scores;
    }

    private sealed partial class SemanticScaleSearch
    {
        // Each candidate keeps only its small accumulator. Process channels in
        // the same warm/cool/geometry addition order, expanding one source plane
        // at a time rather than retaining all three full-resolution regions.
        public bool TryScoreLocalGrid(IReadOnlyList<(Size Size, Rect Region)> requests,
            out IReadOnlyList<(double Cost, Point Location)> results)
        {
            results = [];
            if (requests.Count == 0) return true;
            var full = _channels[0].Distance;
            var left = Math.Max(0, requests.Min(request => request.Region.Left) - 32);
            var top = Math.Max(0, requests.Min(request => request.Region.Top) - 32);
            var right = Math.Min(full.Width, requests.Max(request => request.Region.Right) + 32);
            var bottom = Math.Min(full.Height, requests.Max(request => request.Region.Bottom) + 32);
            var envelope = new Rect(left, top, right - left, bottom - top);
            var extents = requests.Select(request => new Size(request.Region.Width - request.Size.Width + 1,
                request.Region.Height - request.Size.Height + 1)).ToArray();
            if (extents.Any(size => size.Width <= 0 || size.Height <= 0)) return false;
            var capacities = extents.Select(size => new Size(checked((size.Width + 63) / 64 * 64),
                checked((size.Height + 63) / 64 * 64))).ToArray();
            var accumulatorBytes = capacities.Sum(size => checked((long)size.Width * size.Height * sizeof(float) * 2));
            // Do not turn a large grid of output buffers into a larger peak than
            // the existing three-region path. Global coarse scoring is unchanged.
            if (accumulatorBytes >= checked((long)envelope.Width * envelope.Height * sizeof(float) * 2)) return false;
            var workspaces = new List<ScoreWorkspace>(requests.Count);
            var masses = new double[requests.Count];
            try
            {
                foreach (var size in capacities) workspaces.Add(new(size.Width, size.Height, 1));
                for (var channelIndex = 0; channelIndex < _channels.Count; channelIndex++)
                {
                    var channel = _channels[channelIndex];
                    var expanded = channel.Distance.Read(envelope);
                    try
                    {
                        // Candidates own disjoint accumulators and only read the
                        // shared region and patch, so they are scored concurrently.
                        RunParallel(requests.Count, i =>
                        {
                            var request = requests[i];
                            using var reduced = new Mat();
                            using var weights = new Mat();
                            Cv2.Resize(channel.Patch, reduced, request.Size, interpolation: InterpolationFlags.Area);
                            reduced.ConvertTo(weights, MatType.CV_32FC1, 1d / 255d);
                            masses[i] += Cv2.Sum(weights).Val0;
                            var roi = new Rect(request.Region.Left - left, request.Region.Top - top,
                                request.Region.Width, request.Region.Height);
                            using var referenceRegion = new Mat(expanded, roi);
                            var output = new Rect(0, 0, extents[i].Width, extents[i].Height);
                            using var combined = new Mat(workspaces[i].Combined, output);
                            using var scores = new Mat(workspaces[i].Scores, output);
                            Cv2.MatchTemplate(referenceRegion, weights, channelIndex == 0 ? combined : scores, TemplateMatchModes.CCorr);
                            if (channelIndex > 0) Cv2.Add(combined, scores, combined);
                        });
                    }
                    finally { DisposeDistance(expanded); }
                }
                var collected = new List<(double, Point)>(requests.Count);
                for (var i = 0; i < requests.Count; i++)
                {
                    using var combined = new Mat(workspaces[i].Combined, new Rect(0, 0, extents[i].Width, extents[i].Height));
                    Cv2.MinMaxLoc(combined, out double minimum, out double _, out var location, out _);
                    collected.Add((masses[i] >= 1d ? minimum / masses[i] : double.PositiveInfinity,
                        location + requests[i].Region.TopLeft));
                }
                results = collected;
                return true;
            }
            finally { foreach (var workspace in workspaces) workspace.Dispose(); }
        }
    }
}
