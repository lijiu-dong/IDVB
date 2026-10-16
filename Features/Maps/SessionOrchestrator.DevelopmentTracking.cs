#if IDVB_DEVELOPMENT_BUILD
namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    internal object CaptureDevelopmentTrackingSnapshot() => new
    {
        trackingGeneration = Volatile.Read(ref _orbTrackingGeneration),
        trackingRunning = _orbTrackingTask is { IsCompleted: false },
        trackingCancellationRequested = _orbTrackingCancellation?.IsCancellationRequested,
        realtime = _realtimeMapTransformPublisher.Snapshot(reset: false),
        overlay = (_overlay as Adapters.OverlayWindowAdapter)?.CaptureDevelopmentTrackingSnapshot()
    };
}
#endif
