using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static partial class MapStructureScaleEstimator
{
    private static Mat Resize(Mat source, int factor)
    {
        var result = new Mat();
        try
        {
            Cv2.Resize(source, result, ReducedSize(source.Size(), factor),
                interpolation: InterpolationFlags.Area);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static Size ReducedSize(Size source, int factor) =>
        new(Math.Max(1, source.Width / factor), Math.Max(1, source.Height / factor));

    private static LowStructureScaleSelection RefineNativeBasins(
        IReadOnlyList<ScaleScore> coarse, double[] coarseGrid,
        Mat? distance, Mat edges, Mat patch, double clip, bool asymmetric,
        MapStructureScaleHint? hint, double minimumScale, double maximumScale,
        SemanticScaleSearch? semantic)
    {
        using var halfDistance = distance is null ? null : Resize(distance, 2);
        using var halfEdges = semantic is null ? Resize(edges, 2) : null;
        var refined = new List<ScaleScore>();
        var tested = coarseGrid.Length;
        var broadBasins = new List<ScaleScore[]>(2);
        foreach (var basin in coarse.Take(2))
        {
            var index = Array.FindIndex(coarseGrid, scale => Math.Abs(scale - basin.Scale) < 1e-9);
            // Coarse distance averaging can move the winner into an adjacent
            // scale bin. Search both full neighbouring bins, not their midpoints.
            var broadGrid = BuildFineScaleGrid(coarseGrid, index, .02d);
            var broad = ScoreScales(broadGrid, 2, ReducedSize(edges.Size(), 2), halfDistance, halfEdges, patch, clip, asymmetric, semantic, basin, 48)
                .OrderBy(item => item.Cost).ToArray();
            tested += broadGrid.Count;
            if (broad.Length == 0) continue;
            broadBasins.Add(broad);
        }
        // Both half-resolution basins are independent. Finish their scoring
        // before native refinement, then retire pixels no subsequent consumer
        // reads instead of retaining both resolutions through the fine search.
        semantic?.ReleaseDistanceLevel(2);
        foreach (var broad in broadBasins)
        {
            var center = broad[0].Scale;
            var grid = Enumerable.Range(-4, 9)
                .Select(i => center * Math.Exp(i * .005d))
                .Where(s => s >= minimumScale && s <= maximumScale).ToArray();
            var scores = ScoreScales(grid, 1, edges.Size(), distance, edges, patch, clip, asymmetric, semantic, broad[0], 16)
                .OrderBy(item => item.Cost).ToArray();
            tested += grid.Length;
            if (scores.Length == 0) continue;
            var fine = Enumerable.Range(-3, 7)
                .Select(i => scores[0].Scale * Math.Exp(i * .001d))
                .Where(s => s >= minimumScale && s <= maximumScale).ToArray();
            var exact = ScoreScales(fine, 1, edges.Size(), distance, edges, patch, clip, asymmetric, semantic, scores[0], 8)
                .OrderBy(item => item.Cost).ToArray();
            tested += fine.Length;
            if (exact.Length == 0) continue;
            var fitted = FitLogScaleMinimum(exact, fine, exact[0].Scale);
            refined.Add(new(fitted.Scale, fitted.Cost));
        }
        var ordered = refined.OrderBy(item => item.Cost)
            .DistinctBy(item => Math.Round(item.Scale, 3)).ToArray();
        if (ordered.Length == 0)
            return new([], 0, tested, true);
        return new(ordered.Select(item => item.Scale).ToArray(), .001d, tested,
            IsAmbiguous(ordered), BestCost: ordered[0].Cost,
            SecondCost: ordered.ElementAtOrDefault(1)?.Cost ?? double.PositiveInfinity,
            HintScale: hint?.Scale, HintConfidence: hint?.Confidence ?? 0,
            SearchMinimumScale: minimumScale, SearchMaximumScale: maximumScale,
            TopBasinScales: ordered.Select(item => item.Scale).ToArray());
    }

    private static (double Scale, double Cost) FitLogScaleMinimum(
        IReadOnlyList<ScaleScore> scores,
        IReadOnlyList<double> grid,
        double fallbackScale)
    {
        var orderedGrid = grid.OrderBy(scale => scale).ToArray();
        var centerIndex = Array.FindIndex(
            orderedGrid,
            scale => Math.Abs(scale - fallbackScale) < 1e-9d);
        if (centerIndex <= 0 || centerIndex + 1 >= orderedGrid.Length)
            return (fallbackScale, scores[0].Cost);

        var leftScale = orderedGrid[centerIndex - 1];
        var centerScale = orderedGrid[centerIndex];
        var rightScale = orderedGrid[centerIndex + 1];
        var leftCost = scores.FirstOrDefault(score =>
            Math.Abs(score.Scale - leftScale) < 1e-9d)?.Cost ?? double.NaN;
        var centerCost = scores.FirstOrDefault(score =>
            Math.Abs(score.Scale - centerScale) < 1e-9d)?.Cost ?? double.NaN;
        var rightCost = scores.FirstOrDefault(score =>
            Math.Abs(score.Scale - rightScale) < 1e-9d)?.Cost ?? double.NaN;
        if (!double.IsFinite(leftCost)
            || !double.IsFinite(centerCost)
            || !double.IsFinite(rightCost))
        {
            return (fallbackScale, scores[0].Cost);
        }

        var leftLog = Math.Log(leftScale / centerScale);
        var rightLog = Math.Log(rightScale / centerScale);
        var leftSlope = (centerCost - leftCost) / -leftLog;
        var rightSlope = (rightCost - centerCost) / rightLog;
        var curvature = (rightSlope - leftSlope) / (rightLog - leftLog);
        if (!double.IsFinite(curvature) || curvature <= 1e-9d)
            return (fallbackScale, scores[0].Cost);

        var linear = leftSlope - (curvature * leftLog);
        var fittedLogOffset = Math.Clamp(
            -linear / (2d * curvature), leftLog, rightLog);
        var fittedScale = centerScale * Math.Exp(fittedLogOffset);
        var fittedCost = (curvature * fittedLogOffset * fittedLogOffset)
            + (linear * fittedLogOffset) + centerCost;
        return double.IsFinite(fittedScale) && double.IsFinite(fittedCost)
            ? (fittedScale, fittedCost)
            : (fallbackScale, scores[0].Cost);
    }
    private static double PreferredScaleDistance(
        double scale,
        double? preferredScale) =>
        preferredScale is { } preferred
            && double.IsFinite(preferred)
            && preferred > 0d
            ? Math.Abs(Math.Log(scale / preferred))
            : Math.Abs(Math.Log(scale));

}
