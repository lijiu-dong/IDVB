using System.Diagnostics;
using IDVBuff.Pipeline;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    // Called only by the map-open owner while it holds _scanGate. The solver
    // has no session writers; this UI-thread transaction is its only consumer.
    private async Task RunFrontEntranceMapOpenIdentityAsync(MapGameToggleTransition toggle,
        MapMatchSnapshot match, CancellationToken cancellationToken)
    {
        ActiveOperationTrace?.SetContext(route: "front-entrance-automatic");
        bool IsCurrent() => !cancellationToken.IsCancellationRequested
            && !_disposed && _settings is { IsEnabled: true, SelectMapByTagsEnabled: false }
            && IsCurrentMatchOperation(match) && _gameMapToggleState.IsCurrent(toggle)
            && _lastRecognition is null && _pendingAlignmentIdentity is null
            && !_manualSelectionActive;

        await EnsureMapCacheSynchronizedAsync();
        if (!IsCurrent()) return;
        CapturedGameFrame? captured;
        using (ActiveOperationTrace?.StartTopLevel("stable_viewport", MapOperationWaitKind.Capture))
            captured = await CaptureStableViewportAsync("正门自动识别", cancellationToken,
                shouldContinue: IsCurrent, allowPartialMap: true);
        using var capturedOwner = captured;
        if (captured is null || !IsCurrent())
        {
            ActiveOperationTrace?.SetTerminal("failed", "front-entrance-capture-unavailable");
            return;
        }
        using var frame = CropAutomaticIdentityCanvas(captured);

        var timer = Stopwatch.StartNew();
        var timeout = _settings!.SessionTuning.OpeningTimeoutMilliseconds;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var budget = MapNoDoorAlignmentBudgetContext.Enter(
            () => Math.Max(0, timeout - (int)Math.Min(int.MaxValue, timer.ElapsedMilliseconds)));
        var recognitionTuning = CreateInitialAlignmentRecognitionTuning();
        var structureTuning = MapScaleSeedResolver.CreateStrictInitialIdentityValidationTuning(
            CreateScanVerificationTuning(CreateInitialAlignmentStructureTuning()));
        MapAutomaticIdentityAttempt attempt;
        try
        {
            using var compute = ActiveOperationTrace?.StartTopLevel("alignment_compute", MapOperationWaitKind.Compute);
            attempt = await Task.Run(() => _recognition.RecognizeAutomaticIdentity(frame,
                match.MapClass, floorKey: null, recognitionTuning, structureTuning, deadline.Token),
                deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            if (IsCurrent())
            {
                _statusMessage = "正门自动识别超时，请重新开图或使用快捷扫描。";
                ActiveOperationTrace?.SetTerminal("failed", "front-entrance-timeout");
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        if (!IsCurrent() || !IsCurrentCaptureTarget(frame)) return;
        _lastDiagnostics = attempt.Diagnostics;
        _logCollector.Append(MapLogCategory.ScanLifecycle,
            attempt.Accepted ? MapLogLevel.Info : MapLogLevel.Warning,
            $"正门自动识别 · status={attempt.Status} · candidates={attempt.CandidateCount}",
            elapsedMs: timer.Elapsed.TotalMilliseconds,
            details: new()
            {
                ["route"] = "front-entrance-automatic",
                ["reason"] = attempt.FailureReason,
                ["catalogRevision"] = attempt.CatalogRevision.ToString(),
                ["candidates"] = attempt.CandidateDiagnostics
            });

        var recognition = attempt.Recognition;
        if (!attempt.Accepted || attempt.AlignmentAttempt?.StructureAccepted != true
            || recognition?.Result.OverlayTransform is null
            || recognition.Result.WasForcedBestResult
            || deadline.IsCancellationRequested || MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0
            || attempt.CatalogRevision != _recognition.CatalogRevision
            || attempt.CatalogRevision != _mapRepository.GetCatalogRevision()
            || !string.Equals(recognition.Map.Class, match.MapClass, StringComparison.OrdinalIgnoreCase))
        {
            ActiveOperationTrace?.SetTerminal("failed", "front-entrance-" + attempt.Status);
            _statusMessage = string.IsNullOrWhiteSpace(attempt.FailureReason)
                ? "正门结构尚未确认，请重新开图或使用快捷扫描。" : attempt.FailureReason;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Identity belongs to the match. A display pose belongs to the current
        // picture: never publish a several-second-old pose after a pan or zoom.
        using var currentCapture = await CaptureSurveyViewportOnceAsync("正门结果确认", cancellationToken);
        if (currentCapture is null || !IsCurrent()) return;
        using var current = CropAutomaticIdentityCanvas(currentCapture);
        var revalidatedCurrentPose = !HasSameAutomaticIdentityPixels(frame, current);
        if (revalidatedCurrentPose)
        {
            var poseTimer = Stopwatch.StartNew();
            using var poseBudget = MapNoDoorAlignmentBudgetContext.Enter(
                () => Math.Max(0, timeout - (int)Math.Min(int.MaxValue, poseTimer.ElapsedMilliseconds)));
            var pose = await Task.Run(() => _recognition.AlignAutomaticIdentityCurrentFrame(current,
                recognition.Map.Id, recognition.Result.Floor!, recognitionTuning,
                structureTuning, cancellationToken), cancellationToken);
            if (!pose.StructureAccepted || pose.Recognition?.Result.OverlayTransform is null
                || MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
            {
                ActiveOperationTrace?.SetTerminal("failed", "front-entrance-current-pose-rejected");
                if (IsCurrent())
                {
                    _statusMessage = "地图身份已通过比较，但当前画面尚未完成贴合，请重新开图。";
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }
                return;
            }
            recognition = pose.Recognition;
        }
        // No await between the final current-frame/catalog checks and the common
        // identity/transform/display transaction. Closing a map revokes its owner.
        if (!IsCurrent() || !IsCurrentCaptureTarget(current)
            || attempt.CatalogRevision != _recognition.CatalogRevision
            || attempt.CatalogRevision != _mapRepository.GetCatalogRevision()) return;
        PublishAutomaticRecognition(recognition, null, current, match,
            $"已自动识别 {recognition.Map.DisplayName}");
        _logCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info,
            "正门身份及当前帧贴合已提交", details: new()
            {
                ["mapId"] = recognition.Map.Id, ["floor"] = recognition.Result.Floor,
                ["currentPoseRevalidated"] = revalidatedCurrentPose,
                ["identityLocked"] = true, ["alignmentCommitted"] = true
            });
        ActiveOperationTrace?.SetContext(route: "front-entrance-automatic",
            mapId: recognition.Map.Id.ToString("D"), floorKey: recognition.Result.Floor);
        await StartOrbTrackingAsync(recognition, current);
    }

    private static bool HasSameAutomaticIdentityPixels(CapturedGameFrame previous, CapturedGameFrame current)
    {
        if (previous.WindowHandle != current.WindowHandle || previous.ClientBounds != current.ClientBounds
            || previous.ViewportBounds != current.ViewportBounds || previous.Image.Type() != current.Image.Type()
            || previous.Image.Size() != current.Image.Size())
            return false;
        // Use the host's existing unchanged-frame tolerances: capture intensity
        // quantization may vary by two levels, but wall geometry may not move.
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        using var walls = new Mat();
        Cv2.Dilate(previous.GetOrCreateAutomaticIdentityObservation().ObservedEdges, walls, kernel);
        return Cv2.Norm(previous.Image, current.Image, NormTypes.INF, walls) <= 2d
            && Cv2.Norm(previous.Image, current.Image, NormTypes.L1)
                / (previous.Image.Total() * previous.Image.Channels()) <= 1d;
    }

    private static CapturedGameFrame CropAutomaticIdentityCanvas(CapturedGameFrame captured)
    {
        captured.AutomaticIdentityUiExclusions = MapFrameUiExclusion.AutomaticCanvas(captured.ClientBounds);
        using var points = captured.GetOrCreateAutomaticIdentityObservation().ProposalEdges.FindNonZero();
        var crop = new Rect(0, 0, captured.Image.Width, captured.Image.Height);
        if (!points.Empty())
        {
            var walls = Cv2.BoundingRect(points);
            var padding = Math.Max(8, (int)Math.Round(captured.ClientBounds.Width * .03));
            crop = new Rect(walls.X - padding, walls.Y - padding,
                walls.Width + padding * 2, walls.Height + padding * 2) & crop;
        }
        return captured.CreateSubview(crop, new MapScreenRect(
            captured.ViewportBounds.X + crop.X, captured.ViewportBounds.Y + crop.Y,
            crop.Width, crop.Height));
    }
}
