using System.Diagnostics;
using IDVBuff.Core.Models;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private async Task RunVpsg3_5TrackingLoopAsync(
        OrbTrackingContext context,
        RuntimeMapRecognition initialRecognition,
        MapOverlayTransform initialTransform,
        CancellationToken cancellationToken)
    {
        var currentRecognition = initialRecognition;
        var lockedScale = (initialTransform.ScaleX + initialTransform.ScaleY) / 2d;
        var trackingConfig = Vpsg3_5TrackingConfig.Default;
        var motion = new Vpsg3_5DragMotion(initialTransform.OffsetX, initialTransform.OffsetY, trackingConfig);
        SetNativeDragPresentation(context.Generation, motion);
        using var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var opticalFlow = new Vpsg3_5OpticalFlowTracker();
        var visualEpoch = 0L;
        var releaseEpoch = 0L;
        var visualFrameTx = initialTransform.OffsetX;
        var visualFrameTy = initialTransform.OffsetY;
        var hasVisualFrameAnchor = false;
        var previousViewport = default(MapScreenRect);
        var afterSystemTicks = SystemRelativeClock.GetTicks();
        var lastAbsoluteCorrection = 0L;
        var weakFrames = 0;
        var sessionStarted = Stopwatch.GetTimestamp();
        var lastTelemetryLog = sessionStarted;
        var captureCount = 0;
        var gdiFallbackCount = 0;
        var visualAttempts = 0;
        var acceptedCount = 0;
        var rejectedCount = 0;
        var opticalAttempts = 0;
        var opticalAccepted = 0;
        var droppedFrames = 0;
        var captureAgeTotalMs = 0d;
        var readbackTotalMs = 0d;
        var preprocessTotalMs = 0d;
        var trackTotalMs = 0d;
        var endToEndTotalMs = 0d;
        var solveSamples = new List<double>();

        _realtimeMapTransformPublisher.Snapshot(reset: true);
        _logCollector.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
            $"VPSG 3.5 hybrid tracking loop entered · map={context.MapId} · floor={context.FloorKey} · scale={lockedScale:F4} · initialTx={initialTransform.OffsetX:F1}, initialTy={initialTransform.OffsetY:F1}");

        TimeBeginPeriod(1);
        // This is the only realtime pose publisher. Capture, LK, absolute solving
        // and release settling must never run on the cursor sampling task.
        var inputTask = Task.Run(() => RunVpsg3_5InputLoopAsync(
            context, lockedScale, motion, inputCancellation.Token));
        try
        {
            while (!cancellationToken.IsCancellationRequested && IsOrbTrackingContextCurrent(context))
            {
                if (inputTask.IsCompleted)
                    break;
                var snapshot = motion.Snapshot();
                if (!snapshot.IsDragging)
                {
                    if (snapshot.ReleasedAt > 0 && releaseEpoch != snapshot.Epoch)
                    {
                        releaseEpoch = snapshot.Epoch;
                        var snap = await PerformReleaseSnapAsync(context, currentRecognition,
                            lockedScale, snapshot.Tx, snapshot.Ty, trackingConfig, cancellationToken)
                            .ConfigureAwait(false);
                        // A quick re-grab invalidates this snap even if the same map
                        // session is still current. Never pull the new drag backwards.
                        if (snap is { IsAccepted: true } && IsOrbTrackingContextCurrent(context)
                            && motion.TryReleaseSnap(snapshot.Epoch, snap.OffsetX, snap.OffsetY,
                                SystemRelativeClock.GetTicks(), snap.Confidence))
                        {
                            currentRecognition = CommitVpsg3_5Motion(context, currentRecognition,
                                lockedScale, motion.Snapshot());
                        }
                        opticalFlow.Reset();
                        hasVisualFrameAnchor = false;
                        weakFrames = 0;
                        _alignmentTrackingMode = MapAlignmentTrackingMode.VpsgTracking;
                    }
                    await Task.Delay(2, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (visualEpoch != snapshot.Epoch)
                {
                    visualEpoch = snapshot.Epoch;
                    opticalFlow.Reset();
                    hasVisualFrameAnchor = false;
                    afterSystemTicks = snapshot.StartedAt;
                    lastAbsoluteCorrection = 0;
                    _captureSvc.PrepareViewportCapture();
                }

                var captureStarted = Stopwatch.GetTimestamp();
                object? frameObject = await _captureSvc.CaptureNextViewportAsync(
                    ResolveMapViewportForCurrentWindow(), afterSystemTicks,
                    TimeSpan.FromMilliseconds(trackingConfig.FrameCaptureWaitMs), cancellationToken)
                    .ConfigureAwait(false);
                var fallbackTimestamp = SystemRelativeClock.GetTicks();
                if (frameObject is not CapturedGameFrame)
                {
                    if (_captureSvc.TryCaptureViewport(ResolveMapViewportForCurrentWindow(),
                            out frameObject, out _))
                        gdiFallbackCount++;
                }
                if (frameObject is not CapturedGameFrame frame)
                {
                    droppedFrames++;
                    await Task.Delay(2, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                using (frame)
                {
                    var timestamp = frame.CaptureSystemRelativeTicks > 0
                        ? frame.CaptureSystemRelativeTicks : fallbackTimestamp;
                    afterSystemTicks = Math.Max(afterSystemTicks, timestamp);
                    // The mouse task may have released or started another drag while
                    // capture/readback was pending. Dispose those pixels without using them.
                    if (!IsOrbTrackingContextCurrent(context)
                        || !motion.TryPredictFrame(visualEpoch, timestamp, out var predictedTx, out var predictedTy))
                        continue;
                    captureCount++;
                    droppedFrames += frame.CaptureDroppedFrames;
                    captureAgeTotalMs += SystemRelativeClock.AgeMilliseconds(timestamp);
                    readbackTotalMs += frame.CaptureReadbackMilliseconds > 0d
                        ? frame.CaptureReadbackMilliseconds
                        : Stopwatch.GetElapsedTime(captureStarted).TotalMilliseconds;

                    if (previousViewport.Width != frame.ViewportBounds.Width
                        || previousViewport.Height != frame.ViewportBounds.Height)
                    {
                        opticalFlow.Reset();
                        hasVisualFrameAnchor = false;
                    }
                    opticalAttempts++;
                    var hadVisualFrameAnchor = hasVisualFrameAnchor;
                    var flow = hasVisualFrameAnchor
                        ? opticalFlow.Track(frame.Image,
                            predictedTx - visualFrameTx - (frame.ViewportBounds.X - previousViewport.X),
                            predictedTy - visualFrameTy - (frame.ViewportBounds.Y - previousViewport.Y))
                        : opticalFlow.Track(frame.Image);
                    preprocessTotalMs += flow.PreprocessMilliseconds;
                    trackTotalMs += flow.TrackMilliseconds;
                    if (hasVisualFrameAnchor && flow.Accepted)
                    {
                        opticalAccepted++;
                        visualFrameTx += flow.DeltaX + frame.ViewportBounds.X - previousViewport.X;
                        visualFrameTy += flow.DeltaY + frame.ViewportBounds.Y - previousViewport.Y;
                        motion.ObserveVisual(visualEpoch, timestamp,
                            visualFrameTx, visualFrameTy, flow.InlierRatio);
                    }
                    else
                    {
                        visualFrameTx = predictedTx;
                        visualFrameTy = predictedTy;
                        hasVisualFrameAnchor = true;
                    }
                    previousViewport = frame.ViewportBounds;

                    var correctionAgeMs = ElapsedMilliseconds(lastAbsoluteCorrection);
                    if (correctionAgeMs >= trackingConfig.VisualVerificationIntervalMs
                        || (hadVisualFrameAnchor && !flow.Accepted && correctionAgeMs >= 32d))
                    {
                        lastAbsoluteCorrection = Stopwatch.GetTimestamp();
                        visualAttempts++;
                        var preprocessStarted = Stopwatch.GetTimestamp();
                        using var observation = Vpsg3FastLiveExtractor.Extract(frame.Image, frame.ViewportBounds);
                        preprocessTotalMs += Stopwatch.GetElapsedTime(preprocessStarted).TotalMilliseconds;
                        if (observation.SparseEdgePoints.Count >= 8
                            && _recognition.TryGetVpsg3FloorLease(currentRecognition.Map, context.FloorKey, out var lease))
                        {
                            Vpsg3_5TrackingResult result;
                            using (lease)
                                result = Vpsg3_5TrackingSolver.TryTrack(observation, lease.Floor,
                                    lockedScale, visualFrameTx, visualFrameTy, trackingConfig);
                            solveSamples.Add(result.Timing.TotalMs);
                            if (result.IsAccepted && IsOrbTrackingContextCurrent(context)
                                && motion.ObserveVisual(visualEpoch, timestamp,
                                    result.OffsetX, result.OffsetY, result.Confidence))
                            {
                                acceptedCount++;
                                weakFrames = 0;
                                visualFrameTx = result.OffsetX;
                                visualFrameTy = result.OffsetY;
                                _alignmentTrackingMode = MapAlignmentTrackingMode.VpsgTracking;
                                currentRecognition = CommitVpsg3_5Motion(context, currentRecognition,
                                    lockedScale, motion.Snapshot());
                            }
                            else
                            {
                                rejectedCount++;
                                HandleWeakTrackingFrame(++weakFrames);
                            }
                        }
                        else
                        {
                            rejectedCount++;
                            HandleWeakTrackingFrame(++weakFrames);
                        }
                    }
                    endToEndTotalMs += SystemRelativeClock.AgeMilliseconds(timestamp);
                }

                if (ElapsedMilliseconds(lastTelemetryLog) >= 3000)
                {
                    lastTelemetryLog = Stopwatch.GetTimestamp();
                    var realtime = _realtimeMapTransformPublisher.Snapshot(reset: false);
                    var input = motion.Telemetry();
                    _logCollector.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                        $"VPSG 3.5 hybrid tracking telemetry · optical={opticalAccepted}/{opticalAttempts} · accepted={acceptedCount}/{visualAttempts} · mode={_alignmentTrackingMode}",
                        details: new()
                        {
                            ["generation"] = context.Generation,
                            ["captureCount"] = captureCount,
                            ["visualAttempts"] = visualAttempts,
                            ["accepted"] = acceptedCount,
                            ["rejected"] = rejectedCount,
                            ["opticalAttempts"] = opticalAttempts,
                            ["opticalAccepted"] = opticalAccepted,
                            ["inputSamples"] = input.InputSamples,
                            ["maximumInputGapMs"] = input.MaximumInputGapMs,
                            ["mouseGainX"] = input.MouseGainX,
                            ["mouseGainY"] = input.MouseGainY,
                            ["nativeInputDelayMs"] = input.NativeInputDelayMs,
                            ["nativeFrameErrorPixels"] = input.NativeFrameErrorPixels,
                            ["maximumNativeFrameErrorPixels"] = input.MaximumNativeFrameErrorPixels,
                            ["dispatcherQueueP95Ms"] = realtime.P95DispatcherQueueMs,
                            ["renderP95Ms"] = realtime.P95RenderMs,
                            ["endToEndP95Ms"] = realtime.P95EndToEndMs,
                            ["coalescedTransforms"] = realtime.CoalescedTransforms
                        });
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logCollector.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                "VPSG 3.5 tracking loop stopped after an unexpected error.",
                details: new() { ["generation"] = context.Generation, ["exception"] = exception.ToString() });
        }
        finally
        {
            inputCancellation.Cancel();
            try { await inputTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (inputCancellation.IsCancellationRequested) { }
            catch (Exception exception)
            {
                _logCollector.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                    "VPSG 3.5 cursor sampling stopped after an unexpected error.",
                    details: new() { ["generation"] = context.Generation, ["exception"] = exception.ToString() });
            }
            finally
            {
                ClearNativeDragPresentation(context.Generation);
                LogVpsg3_5SessionEnd(context, Stopwatch.GetElapsedTime(sessionStarted).TotalMilliseconds,
                    motion.Telemetry().DragDurationMs, captureCount, gdiFallbackCount, visualAttempts, acceptedCount, rejectedCount,
                    opticalAttempts, droppedFrames, captureAgeTotalMs, readbackTotalMs, preprocessTotalMs,
                    trackTotalMs, endToEndTotalMs, solveSamples, motion);
                TimeEndPeriod(1);
                lock (_orbTrackingGate)
                {
                    if (_orbTrackingGeneration == context.Generation)
                    {
                        _orbTrackingCancellation?.Dispose();
                        _orbTrackingCancellation = null;
                        _orbTrackingTask = null;
                    }
                }
            }
        }
    }

    private RuntimeMapRecognition CommitVpsg3_5Motion(OrbTrackingContext context,
        RuntimeMapRecognition recognition, double scale, Vpsg3_5DragSnapshot snapshot)
    {
        var transform = MapCanonicalTransformMath.BuildOverlayTransform(scale, scale, snapshot.Tx, snapshot.Ty,
            recognition.Result.OverlayTransform?.ReferenceWidth ?? 1000,
            recognition.Result.OverlayTransform?.ReferenceHeight ?? 1000,
            residualPixels: 0d, orientationDegrees: recognition.Result.OrientationDegrees,
            alignmentMode: MapOverlayAlignmentMode.Uniform);
        var updated = MapCvRecognitionBuilders.ReplaceTransformAndSource(recognition, transform,
            MapRecognitionSource.VpsgTracking);
        EnqueueOrbTrackingCommit(context, updated, 0.05d, updateOverlay: false);
        return updated;
    }
}
