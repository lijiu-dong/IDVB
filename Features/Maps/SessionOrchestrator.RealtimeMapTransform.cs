namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private int _realtimeTransformReferenceWidth;
    private int _realtimeTransformReferenceHeight;
    private int _realtimeTransformOrientationDegrees;
    private readonly LatestRealtimeTransformBuffer _hiddenRealtimeTransform = new();
    private sealed record NativeDragPresentation(long Generation, Vpsg3_5DragMotion Motion);
    private NativeDragPresentation? _nativeDragPresentation;
    private long _nativePresentationCostTicks = 2 * TimeSpan.TicksPerMillisecond;

    private void SetNativeDragPresentation(long generation, Vpsg3_5DragMotion motion)
    {
        var replacement = new NativeDragPresentation(generation, motion);
        while (generation == Volatile.Read(ref _orbTrackingGeneration))
        {
            var previous = Volatile.Read(ref _nativeDragPresentation);
            if (previous is not null && previous.Generation > generation) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref _nativeDragPresentation, replacement, previous), previous)) return;
        }
    }

    private void ClearNativeDragPresentation(long generation)
    {
        var previous = Volatile.Read(ref _nativeDragPresentation);
        if (previous?.Generation == generation)
            Interlocked.CompareExchange(ref _nativeDragPresentation, null, previous);
    }

    private void PublishRealtimeMapTransform(
        OrbTrackingContext context,
        double scale,
        double tx,
        double ty,
        long timestamp,
        double confidence)
    {
        var state = new RealtimeTransformState(
            scale,
            tx,
            ty,
            timestamp,
            confidence,
            context.Generation);
        if (_alignmentResultHidden)
        {
            _hiddenRealtimeTransform.Hold(state);
            if (_alignmentResultHidden)
                return;
            _hiddenRealtimeTransform.DiscardIfLatest(state);
        }
        _realtimeMapTransformPublisher.Publish(state);
    }

    private bool ApplyRealtimeMapTransform(RealtimeTransformState state)
    {
        if (_disposed
            || state.Generation != Volatile.Read(ref _orbTrackingGeneration)
            || !_gameMapToggleState.IsOpen
            || _realtimeTransformReferenceWidth <= 0
            || _realtimeTransformReferenceHeight <= 0)
        {
            return false;
        }
        if (_alignmentResultHidden)
        {
            _hiddenRealtimeTransform.Hold(state);
            if (_alignmentResultHidden)
                return true;
            _hiddenRealtimeTransform.DiscardIfLatest(state);
        }

        var renderStarted = SystemRelativeClock.GetTicks();
        var presentation = Volatile.Read(ref _nativeDragPresentation);
        if (presentation?.Generation == state.Generation)
        {
            // Sample for the time this native move completes, instead of drawing
            // the cursor pose that was queued several milliseconds earlier.
            // Positive learned game delay lets this use already-recorded input;
            // unknown future cursor movement is always clamped by the history.
            var latest = presentation.Motion.SnapshotForPresentation(renderStarted + _nativePresentationCostTicks);
            state = state with { Tx = latest.Tx, Ty = latest.Ty, Timestamp = latest.Timestamp, Confidence = latest.Confidence };
        }
        var transform = MapCanonicalTransformMath.BuildOverlayTransform(
            state.Scale,
            state.Scale,
            state.Tx,
            state.Ty,
            _realtimeTransformReferenceWidth,
            _realtimeTransformReferenceHeight,
            residualPixels: 0d,
            orientationDegrees: _realtimeTransformOrientationDegrees,
            alignmentMode: MapOverlayAlignmentMode.Uniform);
        _overlay.UpdateMapTransform(transform, preservePlayer: true);
        var cost = Math.Clamp(SystemRelativeClock.GetTicks() - renderStarted, 0, 10 * TimeSpan.TicksPerMillisecond);
        _nativePresentationCostTicks = (_nativePresentationCostTicks * 7 + cost) / 8;
        return true;
    }

    private void ApplyLatestHiddenRealtimeTransform()
    {
        if (_hiddenRealtimeTransform.TryTake(out var latest))
            ApplyRealtimeMapTransform(latest);
    }

    private void ClearHiddenRealtimeTransform() =>
        _hiddenRealtimeTransform.Clear();
}
