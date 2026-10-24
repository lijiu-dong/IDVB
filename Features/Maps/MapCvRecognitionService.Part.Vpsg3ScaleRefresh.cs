namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    internal static bool CanUseVpsg3(
        MapAlignmentChannel channel,
        double? scaleSeed,
        bool hasValidatedFloorScale) =>
        channel == MapAlignmentChannel.Standard
        || (hasValidatedFloorScale && scaleSeed is { } scale
            && double.IsFinite(scale)
            && scale >= Vpsg3TuningConfig.Default.MinSupportedScale
            && scale <= Vpsg3TuningConfig.Default.MaxSupportedScale);

    internal static Vpsg3ScaleRefreshComparison CompareVpsg3ScaleRefresh(
        Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor floor,
        Vpsg3BootstrapResult baseline,
        double prior)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var baselineError = ScaleResidual(observation, floor, baseline);
        var testedScaleCount = baseline.TestedScaleHypotheses;
        // Coverage merely asks us to inspect the fit. A good incumbent needs
        // no independent solve, regardless of the milestone just crossed.
        if (baseline.IsAccepted && baselineError <= 1d)
            return new(baseline, baseline, baseline, true, 0, testedScaleCount);

        var selected = baseline;
        if (baseline.IsAccepted)
        {
            var refined = Vpsg3LocalRefiner.Refine(observation.SparseEdgePoints, floor,
                prior, baseline.OffsetX, baseline.OffsetY, observation.ViewportBounds,
                observation.Width, observation.Height);
            var localError = ScaleResidual(observation, floor, refined.RefinedScale,
                refined.RefinedX, refined.RefinedY);
            if (Math.Abs(refined.RefinedScale / prior - 1d) > .005d
                && localError < baselineError * .70d && baselineError - localError >= .5d
                && refined.BestScore >= baseline.Confidence)
                Consider(Vpsg3FastBootstrapSolver.TrySolve(observation, floor,
                    knownScaleSeed: refined.RefinedScale));
        }
        if (ReferenceEquals(selected, baseline)
            && (!baseline.IsAccepted || (baselineError >= 3d && baseline.Confidence < .65d)))
            Consider(Vpsg3FastBootstrapSolver.TrySolve(observation, floor));
        return new(selected, baseline, baseline, ReferenceEquals(selected, baseline),
            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, testedScaleCount);

        void Consider(Vpsg3BootstrapResult candidate)
        {
            testedScaleCount += candidate.TestedScaleHypotheses;
            if (!candidate.IsAccepted || Math.Abs(candidate.Scale / prior - 1d) <= .005d) return;
            var error = ScaleResidual(observation, floor, candidate);
            // Use the same dense class-labelled support for both scales. Sparse
            // vote noise alone cannot replace an accepted floor-local baseline.
            if (!baseline.IsAccepted || (error < baselineError * .70d
                && baselineError - error >= .5d && candidate.Confidence >= baseline.Confidence))
                selected = candidate;
        }
    }

    private static double ScaleResidual(Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor floor, Vpsg3BootstrapResult result)
    {
        if (!result.IsAccepted) return double.PositiveInfinity;
        if (floor.SemanticIndex is not { } index) return result.Confidence >= .75d ? 0d : 6d;
        return ScaleResidual(observation, floor, result.Scale, result.OffsetX, result.OffsetY);
    }

    private static double ScaleResidual(Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor floor, double scale, double offsetX, double offsetY)
    {
        if (floor.SemanticIndex is not { } index) return double.PositiveInfinity;
        var sum = 0d; var count = 0;
        for (var cls = 0; cls < 2; cls++)
        foreach (var p in observation.SemanticSamples.GetPoints(cls))
        {
            var x = (p.X + observation.ViewportBounds.X - offsetX) / scale;
            var y = (p.Y + observation.ViewportBounds.Y - offsetY) / scale;
            sum += Math.Min(6d, index.Distance(x, y,
                cls == 0 ? SemanticStructureContours.Cool : SemanticStructureContours.Warm) * scale);
            count++;
        }
        return count >= 24 ? sum / count : double.PositiveInfinity;
    }
}

internal sealed record Vpsg3ScaleRefreshComparison(
    Vpsg3BootstrapResult Selected,
    Vpsg3BootstrapResult? Baseline,
    Vpsg3BootstrapResult? BestPrior,
    bool SelectedPriorHypothesis,
    double AdditionalMilliseconds,
    int TestedScaleCount);
