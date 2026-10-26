using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public static partial class Vpsg3LocalRefiner
{
    private static bool PreferColdCandidate(double score, IReadOnlyList<Point> points, Vpsg3PreparedFloor floor,
        double scale, double x, double y, MapScreenRect bounds, ref double bestScore, ref int bestTightSupport)
    {
        if (score < bestScore) return false;
        var tightSupport = CountTightSupport(points, floor, scale, x, y, bounds);
        if (score == bestScore && tightSupport <= bestTightSupport) return false;
        bestScore = score;
        bestTightSupport = tightSupport;
        return true;
    }

    // K3/K5 admit a flat maximum around a wall. Prefer its supported center
    // rather than whichever scale/translation happened to be enumerated first.
    // This only breaks primary-score ties; acceptance gates and returned scores
    // still use exactly the existing K3/K5 equation. The locked-scale path is unchanged.
    private static int CountTightSupport(IReadOnlyList<Point> points, Vpsg3PreparedFloor floor,
        double scale, double x, double y, MapScreenRect bounds)
    {
        var words = floor.DilatedBitsetK3Span;
        if (words.IsEmpty) return 0;
        var inverseScale = 1d / scale;
        var semantic = points as Vpsg3SemanticPoints;
        var hits = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var rx = (int)Math.Round((bounds.X + point.X - x) * inverseScale);
            var ry = (int)Math.Round((bounds.Y + point.Y - y) * inverseScale);
            if (semantic is not null && floor.SemanticIndex is { } index
                && semantic.Classes[i] is SemanticStructureContours.Warm or SemanticStructureContours.Cool)
            {
                if (index.IsHit(rx, ry, semantic.Classes[i], 0)) hits++;
                continue;
            }
            // Erode the resident K3 support at this point. No full-frame raster,
            // resampling, distance field allocation or additional scale seed is needed.
            var supported = rx > 0 && ry > 0 && rx < floor.ReferenceWidth - 1 && ry < floor.ReferenceHeight - 1;
            for (var dy = -1; supported && dy <= 1; dy++)
            for (var dx = -1; supported && dx <= 1; dx++)
                supported = (words[(ry + dy) * floor.WordsPerRow + ((rx + dx) >> 6)]
                    & (1UL << ((rx + dx) & 63))) != 0;
            if (supported) hits++;
        }
        return hits;
    }
}
