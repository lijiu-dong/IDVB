using System.Text.Json;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private readonly MapOpenFrameReuseCache _mapOpenFrameReuseCache = new();

    private MapOpenFrameReuseKey CreateMapOpenFrameReuseKey(
        MapMatchSnapshot match, CapturedGameFrame frame, MapRecord map, string floorKey) => new(
            CreateAlignmentContextKey(match, frame, map, floorKey),
            _recognition.CatalogRevision,
            JsonSerializer.Serialize(new
            {
                _settings!.OverlayAlignmentMode,
                _settings.DisableAutoFloor,
                _settings.RecognitionTuning,
                _settings.StructureRegistrationTuning,
                _settings.SessionTuning
            }));

    private void RememberMapOpenFrameForReuse(
        MapMatchSnapshot match, RuntimeMapRecognition recognition,
        MapAlignmentSession session, CapturedGameFrame frame, bool isScaleReliable)
    {
        if (!isScaleReliable || !_scaleLockOptions.CanLockScale)
            return;
        _mapOpenFrameReuseCache.Remember(
            CreateMapOpenFrameReuseKey(match, frame, recognition.Map, recognition.Result.Floor),
            frame, recognition, session);
    }

    private MapOpenFrameReuseEntry? TryGetUnchangedMapOpenFrame(
        MapGameToggleTransition toggle, RuntimeMapRecognition locked,
        string floorKey, CapturedGameFrame frame)
    {
        if (!_scaleLockOptions.CanLockScale || _pendingAlignmentIdentity is not null
            || !_gameMapToggleState.IsCurrent(toggle)
            || !string.Equals(_currentFloorKey ?? locked.Result.Floor,
                floorKey, StringComparison.OrdinalIgnoreCase)
            || (frame.DetectedFloorKey is { } detectedFloor
                && !string.Equals(detectedFloor, floorKey, StringComparison.OrdinalIgnoreCase)))
            return null;

        using var comparison = ActiveOperationTrace?.StartChild(
            "unchanged_frame_compare", MapOperationWaitKind.Compute,
            mapId: locked.Map.Id.ToString("D"), floorKey: floorKey);
        return _mapOpenFrameReuseCache.TryGet(CreateMapOpenFrameReuseKey(
            _matchSession.Snapshot, frame, locked.Map, floorKey), frame);
    }

    private async Task PublishUnchangedMapOpenFrameAsync(
        MapOpenFrameReuseEntry cached, CapturedGameFrame frame,
        MapOpenOperationContext context, double captureMilliseconds)
    {
        var trace = ActiveOperationTrace;
        if (!IsMapOpenOperationCurrent(context))
        {
            trace?.SetTerminal("superseded", "map-operation-version-changed");
            return;
        }

        var previous = cached.Recognition.Result;
        var recognition = new RuntimeMapRecognition
        {
            Map = cached.Recognition.Map,
            FloorImagePath = cached.Recognition.FloorImagePath,
            Result = new MapRecognitionResult
            {
                MapId = previous.MapId,
                Floor = previous.Floor,
                OrientationDegrees = previous.OrientationDegrees,
                Confidence = previous.Confidence,
                IdentityConfidence = previous.IdentityConfidence,
                LocalizationConfidence = previous.LocalizationConfidence,
                OverlayTransform = previous.OverlayTransform,
                EvidenceKind = previous.EvidenceKind,
                StructureDisposition = previous.StructureDisposition,
                StructureBestScore = previous.StructureBestScore,
                StructureSecondScore = previous.StructureSecondScore,
                StructureCandidateMargin = previous.StructureCandidateMargin,
                Source = MapRecognitionSource.ReusedLastTransform,
                ReusedLastTransform = true
            }
        };
        trace?.SetContext(route: "unchanged-frame-reuse");
        var compute = trace?.StartTopLevel("alignment_compute", MapOperationWaitKind.Compute);
        compute?.Complete(MapOperationSpanStatus.Skipped, "unchanged-validated-frame");
        _lastDiagnostics = new MapScanDiagnostics
        {
            AlignmentClass = "Reused",
            WarmStateHit = true,
            StableViewportWaitMilliseconds = captureMilliseconds,
            StableViewportMode = "unchanged-validated-frame",
            InputToFirstCaptureMilliseconds = captureMilliseconds
        };
        _lastAlignmentPhaseTimings = BuildAlignmentPhaseTimings(_lastDiagnostics, 0d);
        _mapOpenSession.LockAlignedMap(recognition.Map.Id, previous.Floor,
            MapSimilarityTransform.FromOverlay(previous.OverlayTransform!),
            previous.EvidenceKind switch
            {
                MapAlignmentEvidenceKind.DualGate => MapLocationMethod.DualAnchor,
                MapAlignmentEvidenceKind.SingleGateAndAuxiliary => MapLocationMethod.SingleAnchor,
                MapAlignmentEvidenceKind.AuxiliaryConsensus => MapLocationMethod.AuxiliaryAnchor,
                MapAlignmentEvidenceKind.Structure => MapLocationMethod.StructureTranslation,
                _ => MapLocationMethod.Manual
            }, previous.LocalizationConfidence);
        _currentFloorKey = previous.Floor;
        _lastRecognition = recognition;
        _lastAlignmentSession = cached.Session;
        _alignmentTrackingMode = MapAlignmentTrackingMode.HoldingLastTransform;
        _mapLease.Bind(context.OperationMatch, recognition.Map.Id);
        RememberPrimaryFloorSession(recognition, cached.Session);
        _lastGameBounds = frame.ClientBounds;
        _lastGameWindowHandle = frame.WindowHandle;
        _statusMessage = $"画面未变化，已直接显示：{recognition.Map.DisplayName} · {previous.Floor.ToUpperInvariant()}";

        using (var publish = trace?.StartTopLevel("overlay_publish", MapOperationWaitKind.Compute))
        {
            using var present = _overlay.DeferPresent();
            _overlayStatus.Clear();
            _overlay.SetMainContentVisible(true);
            _overlay.UpdateMap(recognition, frame.ClientBounds, frame.WindowHandle,
                _settings!.ShowOverlayStatus, frame.ViewportBounds);
            _overlay.Show();
        }
        _logCollector.Append(MapLogCategory.Session, MapLogLevel.Info,
            "静止重开图命中，直接显示可信结果，已跳过轮廓提取与对齐",
            details: new()
            {
                ["mapId"] = recognition.Map.Id,
                ["floor"] = previous.Floor,
                ["alignmentSkipped"] = true,
                ["route"] = "unchanged-frame-reuse",
                ["toggleVersion"] = context.MapToggleVersion
            });

        // A cache hit is presentation, not another independent scale sample.
        // Resume interaction tracking without writing adaptive/cache evidence.
        PublishMiniMapAfterMainPresent(recognition, previous.Floor, false);
        StartLivePlayerTracking(recognition, frame);
        await StartOrbTrackingAsync(recognition, frame);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
