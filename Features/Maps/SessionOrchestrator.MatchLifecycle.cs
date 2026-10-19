using IDVBuff.Diagnostics;
using IDVBuff.Features.Notifications;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private readonly SemaphoreSlim _matchLifecycleGate = new(1, 1);
    private CancellationTokenSource? _matchCancellation;
    private readonly MapOpenCancellationOwner _mapOpenCancellationOwner = new();
    private int _matchEnding;
    private long _currentMapOpenGeneration;

    // A user-confirmed map remains useful evidence even when its first
    // alignment attempt fails. Keep that identity and its scan seed separate
    // from _lastRecognition so an unverified transform is never rendered.
    private RuntimeMapRecognition? _pendingAlignmentIdentity;
    private MapAlignmentSession? _pendingAlignmentSeed;

    private bool IsMatchEnding => Volatile.Read(ref _matchEnding) != 0;

    private CancellationToken CurrentMatchCancellationToken =>
        _matchCancellation?.Token ?? new CancellationToken(canceled: true);

    private bool IsCurrentMatchOperation(MapMatchSnapshot operationMatch) =>
        !IsMatchEnding && _matchSession.IsCurrent(operationMatch);

    private MapOpenOperationContext CaptureMapOpenOperationContext(
        MapGameToggleTransition toggle,
        MapMatchSnapshot operationMatch,
        RuntimeMapRecognition locked,
        CancellationToken cancellationToken,
        string? currentFloorKey = null,
        IntPtr windowHandle = default,
        MapScreenRect clientBounds = default)
    {
        var generation = Interlocked.Increment(ref _currentMapOpenGeneration);
        return new MapOpenOperationContext
        {
            OperationMatch = operationMatch,
            MapId = locked.Map.Id,
            MapUpdatedAt = locked.Map.UpdatedAt,
            MapToggleVersion = toggle.Version,
            OperationGeneration = generation,
            // A current/previous floor is fallback state, not a manual lock.
            ManualFloorKey = _settings?.DisableAutoFloor == true
                ? currentFloorKey ?? MapFloorRules.GetPrimaryFloorKey(locked.Map)
                : null,
            WindowHandle = windowHandle,
            ClientBounds = clientBounds,
            CancellationToken = cancellationToken
        };
    }

    private bool IsMapOpenOperationCurrent(MapOpenOperationContext context)
    {
        if (!IsCurrentMatchOperation(context.OperationMatch))
            return false;

        var currentLocked = _pendingAlignmentIdentity ?? _lastRecognition;
        return context.MatchesCurrentOperation(
            _matchSession.Snapshot,
            _gameMapToggleState.IsOpen,
            _gameMapToggleState.Version,
            Volatile.Read(ref _currentMapOpenGeneration),
            currentLocked?.Map.Id,
            currentLocked?.Map.UpdatedAt,
            _currentFloorKey);
    }

    private void InvalidateActiveMapOpenOperation(string reason = "operation-invalidated",
        CancellationToken continuingOwner = default)
    {
        _mapOpenCancellationOwner.CancelUnlessOwnedBy(continuingOwner);
        Interlocked.Increment(ref _currentMapOpenGeneration);
        _lowStructureRecoveryCursor.Reset();
        _alignmentCommitGuard.Invalidate();
    }

    private void StartMatchCancellationScope()
    {
        _matchCancellation?.Cancel();
        _matchCancellation?.Dispose();
        _matchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCts.Token);
        var nativeMiniMapToken = _matchCancellation.Token;
        _nativeMiniMapCaptureTask = Task.Run(() => RunNativeMiniMapCaptureAsync(nativeMiniMapToken));
    }

    private void CancelMatchOperations(bool preserveMapToggleState = false)
    {
        HideGameMapCloseWarning();
        CancelMapObservation();
        EndAdaptiveMapOpen("match lifecycle changed");
        CancelOrbTracking("match lifecycle changed");
        InvalidateActiveMapOpenOperation("match lifecycle changed");
        _lowStructureRecoveryCursor.Reset();
        try
        {
            _matchCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposal and match shutdown can race during application exit.
        }
        _alignmentCommitGuard.Invalidate();
        if (!preserveMapToggleState)
            _gameMapToggleState.Reset();
    }

    private void QueueSelectedMapResourcePreparation(Guid mapId)
    {
        var map = (_pendingAlignmentIdentity ?? _lastRecognition)?.Map;
        if (_matchSession.Snapshot.IsStarted && map?.Id == mapId)
            _ = PrepareSelectedMapResourcesAsync(map);
    }

    private async Task PrepareSelectedMapResourcesAsync(MapRecord map)
    {
        try { await _recognition.PrepareSelectedMapFloorsAsync(map); }
        catch (Exception exception)
        {
            _logCollector.Append(MapLogCategory.StructureRegistration, MapLogLevel.Error,
                "选中地图楼层资源准备失败", details: new() { ["mapId"] = map.Id, ["error"] = exception.ToString() });
        }
    }
    internal void StopForMemoryFailure()
    {
        _lifetimeCts.Cancel();
        CancelMatchOperations();
    }

    private CancellationTokenSource BeginMapOpenCancellationScope()
    {
        return _mapOpenCancellationOwner.Begin(
            CurrentMatchCancellationToken, ExternalOperationCancellation.Value);
    }

    private void CompleteMapOpenCancellationScope(
        CancellationTokenSource scope)
    {
        _mapOpenCancellationOwner.Complete(scope);
    }

    private void CancelMapOpenAlignment()
    {
        _mapOpenCancellationOwner.CancelUnlessOwnedBy();
        _lowStructureRecoveryCursor.Reset();
    }

    private async Task DrainMatchOperationsAsync()
    {
        await _observationTask;
        await _nativeMiniMapCaptureTask;
        await DrainOrbTrackingAsync();
        await _scanGate.WaitAsync();
        _scanGate.Release();
    }

    /// <summary>
    /// A quick scan is an explicit request to identify the map again. Release
    /// every map-scoped lock before the new scan starts so a previous wrong
    /// choice cannot constrain the result or remain visible when rescanning
    /// fails or is cancelled.
    /// </summary>
    private void UnlockMapForRescan()
    {
        _mapOpenFrameReuseCache.Reset();
        HideGameMapCloseWarning();
        _hasCompletedQuickScanAlignment = false;
        CancelMapObservation(clearPreview: true);
        _overlay.ClearMap();
        _overlayStatus.Clear();
        RefreshMiniMapForCurrentFloor();
        // 再次快捷扫描是显式请求重新识别：作废尚未消费的后台扫描结果。
        ClearPendingBackgroundScan();
        _lowStructureRecoveryCursor.Reset();
        EndAdaptiveMapOpen("map rescan requested");
        InvalidateActiveMapOpenOperation("map rescan requested");
        var previousMapId = _lastRecognition?.Map.Id
            ?? _pendingAlignmentIdentity?.Map.Id;
        if (previousMapId is null
            && _lastAlignmentSession is null
            && _primaryFloorAlignmentSession is null)
        {
            return;
        }

        _overlayStatus.Clear();
        _overlay.Clear();
        // Identity and presentation are already cleared. Reusing unchanged,
        // bounded raster owners avoids decoding and reallocating the same map
        // on every rescan; changed files and match end still retire the owners.
        MapOverlayBitmapRenderer.PrepareImageCacheForRescan();
        _mapOpenSession.Close("quick scan restarted");
        _candidateStability.Reset();
        _alignmentCommitGuard.Invalidate();
        _recognition.ResetForRescan();
        // Capture storage belongs to the window/geometry, not the identified
        // map. WGC already rejects frames older than each request's timestamp;
        // its geometry gate replaces resized/moved windows. Preserve that
        // owner during a rescan instead of rebuilding a healthy D3D device.
        // Match end/start still resets every capture owner below.

        _currentFloorKey = null;
        _mapLease.Clear();
        _lastRecognition = null;
        _pendingAlignmentIdentity = null;
        _pendingAlignmentSeed = null;
        _lastAlignmentSession = null;
        _primaryFloorAlignmentSession = null;
        ClearAdaptiveSessionKeys();
        _lastFloorRecognition = null;
        _lastTrustedPlayerPoint = null;
        _alignmentTrackingMode = MapAlignmentTrackingMode.None;
        _lastGameBounds = default;
        _lastGameWindowHandle = IntPtr.Zero;

        lock (_reliableFloorAlignmentGate)
        {
            _reliableFloorAlignments.Clear();
        }
        ClearManualFloorScaleLocks();
        ClearMapViewportPresenceReferences();

        // Do not allow samples collected for a wrongly selected map to be
        // persisted after a later scan corrects the identity.
        ResetAutomaticMapCacheSamples();

        _logCollector.Append(
            MapLogCategory.Session,
            MapLogLevel.Info,
            $"重新扫描已解除地图锁定 · previousMap={previousMapId?.ToString() ?? "<none>"}");
    }

    private void ResetMatchTransientState(bool resetAutomaticCacheSamples)
    {
        _mapOpenFrameReuseCache.Reset();
        CancelMapObservation(clearPreview: true);
        // 对局结束：作废尚未消费的后台扫描结果，下一局重新开始。
        ClearPendingBackgroundScan();
        ClearOptimisticPresentation();
        _lowStructureRecoveryCursor.Reset();
        EndAdaptiveMapOpen("match transient state reset");
        InvalidateActiveMapOpenOperation("match transient state reset");
        _overlayStatus.Clear();
        _alignmentResultHidden = false;
        ClearHiddenRealtimeTransform();
        _overlay.SetMapContentVisible(true);
        _overlay.Clear();
        MapOverlayBitmapRenderer.InvalidateImageCache();
        _mapOpenSession.Close("match lifecycle reset");
        _candidateStability.Reset();
        _alignmentCommitGuard.Invalidate();
        _gameMapToggleState.Reset();
        _recognition.ResetMatchState();
        _captureSvc.Reset();

        _activeCandidateSelector = null;
        _lastCandidateChoices = [];
        ClearMapLearningContext();
        _manualSelectionActive = false;
        _currentFloorKey = null;
        _mapLease.Clear();
        _lastRecognition = null;
        _pendingAlignmentIdentity = null;
        _pendingAlignmentSeed = null;
        _lastAlignmentSession = null;
        _primaryFloorAlignmentSession = null;
        ClearAdaptiveSessionKeys();
        _lastDiagnostics = null;
        // Diagnostic results also own candidate maps and scan indices. Clearing
        // the recognition service alone leaves these graphs reachable from the
        // session after end/start/dispose. Operations have been drained here.
        Interlocked.Exchange(ref _lastSideEntranceScan, null)?.Dispose();
        _lastScanPipelineContext = null;
        _lastScanPhaseTimings = null;
        _lastAlignmentPhaseTimings = null;
        _lastScanOperationTrace = null;
        _lastAlignmentOperationTrace = null;
        _lastCandidateOperationTrace = null;
        _lastStableCaptureFailureReason = null;
        _lastFloorRecognition = null;
        _lastTrustedPlayerPoint = null;
        _alignmentTrackingMode = MapAlignmentTrackingMode.None;
        _lastGameBounds = default;
        _lastGameWindowHandle = IntPtr.Zero;
        lock (_reliableFloorAlignmentGate)
            _reliableFloorAlignments.Clear();
        ClearManualFloorScaleLocks();
        ClearMapViewportPresenceReferences();
        lock (_notifiedVpsg3DegradationKeys)
            _notifiedVpsg3DegradationKeys.Clear();

        if (resetAutomaticCacheSamples)
            ResetAutomaticMapCacheSamples();

        Vpsg3SolverScratch.ReleaseSharedScratch();
        IDVBuff.Core.Collections.CompactByteField.ReleaseSharedScratch();

        // This reset runs both when a match starts and when it ends. Keep the
        // lifecycle probe observational: a blocking compacting Gen2 collection
        // plus EmptyWorkingSet here made "Start match" page out the whole
        // process and disguised resident startup caches as released memory.
        RealtimePerformanceTracker.AuditLifecycle(
            "ResetMatchTransientState",
            detail: $"resetAutoSamples={resetAutomaticCacheSamples}");
    }

    // ════════════════ ISessionOrchestrator ════════════════

    public Task BeginMatchAsync() => Task.CompletedTask;
    public async Task RunScanAsync() => await Task.CompletedTask;

    public Task BeginMatchAsync(string mapClass) => BeginMatchAsync(mapClass, Task.CompletedTask);

    private async Task BeginMatchAsync(string mapClass, Task pendingSettingsSave)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_headless && _captureSvc.TryGetForegroundClientBounds(out var bounds, out _, out _)
            && bounds is MapScreenRect gameBounds)
            OverlayNotificationCenter.UpdateGameBounds(gameBounds);
        using var loading = new MatchLoadingNotification(_headless ? null
            : OverlayNotificationCenter.Notice("正在加载…\n准备开始对局 · 0%", 0, Timeout.InfiniteTimeSpan));
        using var progress = MapOperationProgress.Begin(loading);
        try
        {
            await pendingSettingsSave;
            MapOperationProgress.Report(.02, "同步地图库");
            await MapOperationProgress.RunAsync(.02, .15, EnsureMapCacheSynchronizedAsync);
            MapOperationProgress.Report(.15, "等待对局初始化");
            await _matchLifecycleGate.WaitAsync();
            try
            {
                await MapOperationProgress.RunAsync(.15, .99, () => BeginMatchCoreAsync(mapClass));
                if (!_disposed && _matchSession.Snapshot.IsStarted)
                    loading.Complete();
            }
            finally { _matchLifecycleGate.Release(); }
        }
        catch (Exception exception)
        {
            loading.Dispose();
            if (!_headless)
                OverlayNotificationCenter.Error($"进入对局失败：{exception.Message}");
            throw;
        }
    }

    private async Task BeginMatchCoreAsync(string mapClass)
    {
            IDVBuff.Core.Diagnostics.MemoryBudget.Process.ThrowIfExceeded();
            using var memoryOperation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Match.Begin", mapClass);
            if (_disposed)
                return;
            if (_matchSession.Snapshot.IsStarted)
                throw new InvalidOperationException("A match is already in progress.");
            ResetMatchTransientState(resetAutomaticCacheSamples: true);
            _statusMessage = $"正在准备对局地图 · {mapClass}";
            StateChanged?.Invoke(this, EventArgs.Empty);
            MapOperationProgress.Report(.05, "重置对局状态");
            await MapOperationProgress.RunAsync(.05, .92, () => _recognition.PrepareMatchResourcesAsync(mapClass));
            if (_disposed)
                return;
            _matchPluginsActivated = false;
            StartMatchCancellationScope();
            var match = _matchSession.Begin(mapClass);
            if (_settings?.DiagnosticModeEnabled is true)
                MapDiagnosticModeCapture.BeginMatch();
            MapOperationProgress.Report(.94, "激活对局插件");
            await SetMatchPluginsActivatedCoreAsync(true);
            _statusMessage = $"对局已开始 · {mapClass}";
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"进入对局 · version={match.Version} · class={match.MapClass}");
            StateChanged?.Invoke(this, EventArgs.Empty);

            // Native capture is acquired by its actual consumers. Starting a WGC
            // session here records every game frame even when heading is disabled
            // and recognition is using GDI, until the match is reset.
    }

    [Obsolete("Player slots are no longer used. Call BeginMatchAsync(mapClass).")]
    public Task BeginMatchAsync(PlayerSlot playerSlot, string mapClass) =>
        BeginMatchAsync(mapClass);
    public Task EndMatchAsync() => EndMatchAsync(saveAutomaticMapCache: false);
}

