using System.Diagnostics.CodeAnalysis;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    internal const string Vpsg3ScaleUnobservableReason = "Vpsg3ScaleUnobservable";
    internal const double ScaleObservabilityStep = .08d;
    private static readonly double[] ScaleHarmonics = [2d, 1.5d, .5d, 2d / 3d, 3d, 1d / 3d];

    // The 1D pitch estimate can lock onto a harmonic of the room pitch: half,
    // two thirds and triple the floor scale were seen on first floor visits.
    // Re-solve this frame's own estimate at its harmonics before the slower
    // structure scale search. The winner still faces the observability check.
    internal static bool TrySolveScaleHarmonic(Vpsg3LiveObservation observation, Vpsg3PreparedFloor floor,
        double estimate, [NotNullWhen(true)] out Vpsg3BootstrapResult? best, out int tested)
    {
        best = null;
        tested = 0;
        if (!double.IsFinite(estimate) || estimate <= 0d)
            return false;
        foreach (var factor in ScaleHarmonics)
        {
            var scale = estimate * factor;
            // An out-of-range seed would make the solver estimate scale itself.
            if (scale < Vpsg3TuningConfig.Default.MinSupportedScale
                || scale > Vpsg3TuningConfig.Default.MaxSupportedScale)
                continue;
            tested++;
            var candidate = Vpsg3FastBootstrapSolver.TrySolve(observation, floor, knownScaleSeed: scale);
            if (candidate.IsAccepted && (best is null || candidate.Confidence > best.Confidence))
                best = candidate;
        }
        if (best is null)
            return false;
        // The harmonic inherits the coarse estimate's error (about 1%); a
        // locked solve cannot move it, so sample the basin around it.
        var center = best.Scale;
        foreach (var step in new[] { -.02d, -.01d, .01d, .02d })
        {
            var scale = center * Math.Exp(step);
            if (scale < Vpsg3TuningConfig.Default.MinSupportedScale
                || scale > Vpsg3TuningConfig.Default.MaxSupportedScale)
                continue;
            tested++;
            var candidate = Vpsg3FastBootstrapSolver.TrySolve(observation, floor, knownScaleSeed: scale);
            if (candidate.IsAccepted && candidate.Confidence > best.Confidence)
                best = candidate;
        }
        return true;
    }

    // A sparse view, such as one corridor beside a room corner, fits almost any
    // scale. Without a validated scale for this floor, the fit must collapse
    // 8% away from the accepted scale; otherwise a wrong scale would become
    // this floor's lock. Correct first-visit frames lose >=0.2 confidence there.
    internal static bool IsVpsg3ScaleObservable(Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor floor, Vpsg3BootstrapResult accepted)
    {
        foreach (var step in new[] { -ScaleObservabilityStep, ScaleObservabilityStep })
        {
            var scale = accepted.Scale * Math.Exp(step);
            // An out-of-range seed would make the solver estimate scale itself.
            if (scale < Vpsg3TuningConfig.Default.MinSupportedScale
                || scale > Vpsg3TuningConfig.Default.MaxSupportedScale)
                continue;
            var probe = Vpsg3FastBootstrapSolver.TrySolve(observation, floor, knownScaleSeed: scale);
            if (probe.IsAccepted && probe.Confidence >= accepted.Confidence - .10d)
                return false;
        }
        return true;
    }
    /// <summary>
    /// 尝试使用 VPSG 3.0 进行极速结构对齐（向后兼容重载）。
    /// </summary>
    public bool TryAlignWithVpsg3(
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey,
        double identityPriorConfidence,
        [NotNullWhen(true)] out MapRecognitionAttempt? attempt,
        double? knownScaleSeed = null) =>
        TryAlignWithVpsg3(
            frame,
            map,
            floorKey,
            identityPriorConfidence,
            out attempt,
            out _,
            knownScaleSeed);


}
