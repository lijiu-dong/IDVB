namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private GameMapCloseWarningWindow? _gameMapCloseWarning;
    private int _gameMapCloseWarningVersion;

    private async Task HandleGameMapCloseAsync()
    {
        if (!CanCheckGameMapClose()) return;
        if (!_captureSvc.TryGetForegroundClientBounds(out var boundsObject, out var windowHandle, out var failure)
            || boundsObject is not MapScreenRect bounds)
        {
            ReportInputDecision("game-map-close", "ignored", "game-not-foreground:" + failure);
            return;
        }
        var version = _gameMapToggleState.Version;
        var matchVersion = _matchSession.Snapshot.Version;
        var identityMapId = GetGameMapCloseIdentityId();
        var hasVisibleMap = HasVisibleGameMapAlignment(identityMapId)
            && windowHandle == _lastGameWindowHandle && bounds == _lastGameBounds;
        if (!_gameMapToggleState.IsOpen && !_overlay.HasMap)
        {
            ReportInputDecision("game-map-close", "ignored", "map-already-closed");
            return;
        }
        // Capture before yielding or changing runtime state. Sampling after the
        // close fade would classify every normal Escape close as mouse misuse.
        var absent = identityMapId.HasValue && hasVisibleMap
            ? ProbeGameMapAbsent(GetCurrentMapViewportPresenceReference(), windowHandle, bounds)
            : null;
        var warn = GameMapCloseProbe.ShouldWarnOnEscape(identityMapId.HasValue, hasVisibleMap, absent);
        await _matchLifecycleGate.WaitAsync();
        try
        {
            if (!CanCheckGameMapClose() || version != _gameMapToggleState.Version
                || !IsGameMapCloseTargetCurrent(windowHandle, bounds)) return;
            warn = warn && IsGameMapCloseIdentityCurrent(identityMapId, matchVersion)
                && HasVisibleGameMapAlignment(identityMapId);
            _gameMapToggleState.SetOpenForExternalController(false);
            await EndMapDisplayAsync(warn ? "escape recovered an already closed game map" : "game map closed by escape");
            ReportInputDecision("game-map-close", "applied", warn ? "already-closed-with-overlay" : "map-closed");
        }
        finally { _matchLifecycleGate.Release(); }
        if (warn && identityMapId is { } mapId) await WarnGameMapClosedByMouseAsync(bounds, mapId, matchVersion);
    }

    private bool CanCheckGameMapClose() =>
        !_disposed && !IsMatchEnding && _settings is { IsEnabled: true } && _matchSession.Snapshot.IsStarted;

    private Guid? GetGameMapCloseIdentityId() =>
        GameMapCloseProbe.GetCurrentIdentity(_matchSession.Snapshot, _mapLease,
            (_pendingAlignmentIdentity ?? _lastRecognition)?.Map.Id);

    private bool IsGameMapCloseIdentityCurrent(Guid? mapId, int matchVersion) =>
        CanCheckGameMapClose() && mapId.HasValue && _matchSession.Snapshot.Version == matchVersion
        && GetGameMapCloseIdentityId() == mapId;

    private string? GameMapCloseFloor =>
        _currentFloorKey ?? (_pendingAlignmentIdentity ?? _lastRecognition)?.Result.Floor;

    private bool HasVisibleGameMapAlignment(Guid? mapId) =>
        !_alignmentResultHidden && mapId is { } currentMap && GameMapCloseFloor is { } floor
        && _overlay.IsMapContentVisible(currentMap, floor);

    // Called on the hook thread: only read identity/presentation state here.
    // Never capture, wait, dispatch or decide absence from this predicate.
    private bool CanWatchGameMapClick() => CanCheckGameMapClose() && _gameMapToggleState.IsOpen
        && HasVisibleGameMapAlignment(GetGameMapCloseIdentityId())
        && _lastGameWindowHandle != IntPtr.Zero && GetForegroundWindow() == _lastGameWindowHandle;

    private bool IsGameMapCloseTargetCurrent(IntPtr window, MapScreenRect bounds) =>
        _captureSvc.TryGetForegroundClientBounds(out var currentBounds, out var currentWindow, out _)
        && currentWindow == window && currentBounds is MapScreenRect rectangle && rectangle == bounds;

    private bool? ProbeGameMapAbsent(MapViewportColorSignature? reference,
        IntPtr expectedWindow, MapScreenRect expectedBounds)
    {
        var viewport = ResolveMapViewportForCurrentWindow();
        if (!_captureSvc.TryCaptureViewport(viewport, out var frameObject, out _)
            || frameObject is not CapturedGameFrame frame) return null;
        using (frame)
        {
            if (frame.WindowHandle != expectedWindow || frame.ClientBounds != expectedBounds) return null;
            var signature = MapViewportPresenceDetector.CreateSignature(frame.Image, includeStructure: false);
            return MapViewportPresenceDetector.IsMapAbsent(signature, reference);
        }
    }

    private async Task WarnGameMapClosedByMouseAsync(MapScreenRect bounds, Guid mapId, int matchVersion)
    {
        // Recheck after EndMapDisplayAsync: rescan, survey or match shutdown may
        // have released the identity while the close consumer was awaiting.
        if (!IsGameMapCloseIdentityCurrent(mapId, matchVersion)) return;
        _statusMessage = "禁止使用鼠标左键关闭地图。请使用开图快捷键或 Esc。";
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (_headless || _disposed) return;
        var version = ++_gameMapCloseWarningVersion;
        (_gameMapCloseWarning ??= new(_captureProtection)).Show(bounds);
        // Every violation shows the popup again and restarts its full duration.
        // There is no per-match, per-session or lifetime warning quota.
        try { await Task.Delay(TimeSpan.FromSeconds(6), _lifetimeCts.Token); }
        catch (OperationCanceledException) { return; }
        if (!_disposed && version == _gameMapCloseWarningVersion) _gameMapCloseWarning.Hide();
    }

    private void HideGameMapCloseWarning()
    {
        _gameMapClickProbe = null;
        ++_gameMapCloseWarningVersion;
        _gameMapCloseWarning?.Hide();
    }
}
