using System.Diagnostics;
using IDVBuff.Core.Models;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private sealed record OrbTrackingCommit(
        OrbTrackingContext Context,
        RuntimeMapRecognition Recognition,
        double MaximumBaselineScaleChangeRatio,
        bool UpdateOverlay);

    private void EnqueueOrbTrackingCommit(
        OrbTrackingContext context,
        RuntimeMapRecognition recognition,
        double maximumBaselineScaleChangeRatio,
        bool updateOverlay = true)
    {
        Volatile.Write(ref _pendingOrbTrackingCommit,
            new OrbTrackingCommit(context, recognition, maximumBaselineScaleChangeRatio, updateOverlay));
        if (Interlocked.CompareExchange(ref _orbCommitQueued, 1, 0) != 0)
            return;

        void DrainPendingCommits()
        {
            try
            {
                while (Interlocked.Exchange(ref _pendingOrbTrackingCommit, null) is { } pending)
                {
                    ApplyOrbTrackingCommit(pending.Context, pending.Recognition,
                        pending.MaximumBaselineScaleChangeRatio, pending.UpdateOverlay);
                }
            }
            finally
            {
                Volatile.Write(ref _orbCommitQueued, 0);
                if (Volatile.Read(ref _pendingOrbTrackingCommit) is not null
                    && Interlocked.CompareExchange(ref _orbCommitQueued, 1, 0) == 0)
                {
                    _dispatcher.TryEnqueue(DrainPendingCommits);
                }
            }
        }

        if (!_dispatcher.TryEnqueue(DrainPendingCommits))
        {
            Volatile.Write(ref _orbCommitQueued, 0);
        }
    }

    private void ApplyOrbTrackingCommit(
        OrbTrackingContext context,
        RuntimeMapRecognition recognition,
        double maximumBaselineScaleChangeRatio,
        bool updateOverlay)
    {
        try
        {
            if (!IsOrbTrackingContextCurrent(context) || recognition.Result.OverlayTransform is not { } transform || _lastAlignmentSession is not { } session)
                return;
            var effectiveScaleLimit = IsAdaptiveTransformConfirmed(context, transform) ? 0.50d : maximumBaselineScaleChangeRatio;
            var advanced = session.Advance(recognition.Map, recognition.Result, effectiveScaleLimit);
            _lastRecognition = recognition;
            _mapLease.Bind(_matchSession.Snapshot, recognition.Map.Id);
            _lastAlignmentSession = advanced;
            if (CanUseAdaptiveReliableSession(advanced, context.AdaptiveKey))
                RememberPrimaryFloorSession(recognition, advanced);
            _alignmentTrackingMode = recognition.Result.Source switch
            {
                MapRecognitionSource.VpsgTracking => MapAlignmentTrackingMode.VpsgTracking,
                MapRecognitionSource.OrbTracking => MapAlignmentTrackingMode.OrbTracking,
                _ => MapAlignmentTrackingMode.StructureMatched
            };
            // VPSG 3.5 has a single realtime presentation owner. A delayed
            // business commit must not repaint an older cursor position.
            if (!updateOverlay)
                return;
            var renderTimer = Stopwatch.StartNew();
            _overlay.UpdateMapTransform(transform, preservePlayer: true);
            renderTimer.Stop();
            var previousRenderLog = Volatile.Read(ref _lastOrbRenderMetricsTimestamp);
            if (ElapsedMilliseconds(previousRenderLog) >= 5000)
            {
                Volatile.Write(ref _lastOrbRenderMetricsTimestamp, Stopwatch.GetTimestamp());
                _logCollector.Append(MapLogCategory.OrbTracking, MapLogLevel.Info, "ORB tracking render sample",
                    elapsedMs: renderTimer.Elapsed.TotalMilliseconds,
                    details: new() { ["generation"] = context.Generation, ["renderMs"] = renderTimer.Elapsed.TotalMilliseconds, ["overlayVisible"] = _overlay.IsVisible });
            }
        }
        catch (InvalidOperationException exception)
        {
            _logCollector.Append(MapLogCategory.OrbTracking, MapLogLevel.Warning,
                "A continuous tracking observation was rejected by the alignment session.",
                details: new() { ["generation"] = context.Generation, ["failureReason"] = exception.Message });
        }
    }

}
