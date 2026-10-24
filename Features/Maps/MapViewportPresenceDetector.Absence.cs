namespace IDVBuff.Features.Maps;

public static partial class MapViewportPresenceDetector
{
    /// <summary>
    /// Conservative closed-map test for a probe the player's own click caused.
    /// The map counts as gone only when neither the aligned floor's reference
    /// nor the blue-gray fallback still finds it; a bluish scene that fools the
    /// fallback merely skips the probe instead of closing an open map.
    /// Pass a signature built without structure: neither check reads it.
    /// </summary>
    public static bool IsMapAbsent(
        MapViewportColorSignature candidate,
        MapViewportColorSignature? reference)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Histogram.Count == 0)
            return false;
        bool IsPresent(MapViewportColorSignature? against) =>
            EvaluateCore(candidate, against, previousFrame: null,
                MinimumReferenceSimilarity, MinimumBlueGrayFraction,
                brightnessTolerance: double.MaxValue,
                requireBlueGrayBrightnessStability: false).IsPresent;
        return !IsPresent(null) && (reference is null || !IsPresent(reference));
    }
}
