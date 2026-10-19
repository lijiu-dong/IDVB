using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    // The game's open animation starts with the toggle. A click during it sees
    // a half-faded map, which must not read as "closed".
    private const double GameMapClickOpeningGraceMilliseconds = 1500d;
    // Let the game's close fade finish before sampling, then confirm once more
    // so one transient frame (floor switch, effect) never closes an open map.
    private const int GameMapClickSettleMilliseconds = 250;
    private const int GameMapClickConfirmMilliseconds = 300;

    private int _gameMapClickProbeActive;

    /// <summary>
    /// Escape closes the game's own map. Mirror only that close: an Escape while
    /// the map is already closed, or while another window owns the keyboard,
    /// must never flip the toggle state into "open".
    /// </summary>
    private async Task HandleGameMapCloseAsync()
    {
        await _matchLifecycleGate.WaitAsync();
        try
        {
            if (_disposed || _settings is not { IsEnabled: true }
                || !_matchSession.Snapshot.IsStarted)
            {
                ReportInputDecision("game-map-close", "rejected", _disposed ? "disposed"
                    : _settings is not { IsEnabled: true } ? "runtime-disabled" : "match-not-started");
                return;
            }

            if (!_gameMapToggleState.IsOpen)
            {
                ReportInputDecision("game-map-close", "ignored", "map-already-closed");
                return;
            }

            if (!_captureSvc.TryGetForegroundClientBounds(out _, out _, out var targetFailure))
            {
                ReportInputDecision("game-map-close", "ignored", "game-not-foreground:" + targetFailure);
                return;
            }

            _gameMapToggleState.Toggle();
            await EndMapDisplayAsync("game map closed by escape");
            ReportInputDecision("game-map-close", "applied", "map-closed");
        }
        finally
        {
            _matchLifecycleGate.Release();
        }
    }

    /// <summary>
    /// A left click while the map is open may have hit the game's own close
    /// button, which the runtime cannot see. Probe the map viewport once the
    /// close fade has settled; when the map is really gone, warn the player and
    /// close the runtime state so the next game-map key opens instead of closing.
    /// </summary>
    private async Task HandleGameMapClickAsync()
    {
        if (_disposed || _settings is not { IsEnabled: true }
            || !_matchSession.Snapshot.IsStarted || !_gameMapToggleState.IsOpen)
        {
            ReportInputDecision("game-map-click", "ignored", "map-not-open");
            return;
        }
        if (Stopwatch.GetElapsedTime(_gameMapToggleState.OpenedAtTimestamp).TotalMilliseconds
            < GameMapClickOpeningGraceMilliseconds)
        {
            ReportInputDecision("game-map-click", "ignored", "opening-animation");
            return;
        }
        // Rapid clicks share the probe already running.
        if (Interlocked.Exchange(ref _gameMapClickProbeActive, 1) == 1)
        {
            ReportInputDecision("game-map-click", "ignored", "probe-in-flight");
            return;
        }

        try
        {
            var version = _gameMapToggleState.Version;
            var reference = GetCurrentMapViewportPresenceReference();
            var probeWatch = Stopwatch.StartNew();
            foreach (var delay in new[] { GameMapClickSettleMilliseconds, GameMapClickConfirmMilliseconds })
            {
                await Task.Delay(delay);
                if (!IsGameMapClickProbeCurrent(version))
                {
                    ReportInputDecision("game-map-click", "ignored", "superseded");
                    return;
                }
                var absent = await Task.Run(() => ProbeGameMapAbsent(reference));
                if (absent is not true)
                {
                    ReportInputDecision("game-map-click", "ignored",
                        absent is null ? "probe-capture-failed" : "map-still-open");
                    return;
                }
            }

            await _matchLifecycleGate.WaitAsync();
            try
            {
                if (!IsGameMapClickProbeCurrent(version))
                {
                    ReportInputDecision("game-map-click", "ignored", "superseded");
                    return;
                }
                _gameMapToggleState.Toggle();
                await EndMapDisplayAsync("game map closed by mouse");
                WarnGameMapClosedByMouse();
                ReportInputDecision("game-map-click", "applied",
                    $"map-closed-by-mouse:{probeWatch.ElapsedMilliseconds}ms");
            }
            finally
            {
                _matchLifecycleGate.Release();
            }
        }
        finally
        {
            Volatile.Write(ref _gameMapClickProbeActive, 0);
        }
    }

    private bool IsGameMapClickProbeCurrent(int version) =>
        !_disposed && _gameMapToggleState.IsOpen && _gameMapToggleState.Version == version;

    /// <summary>
    /// Captures the calibrated map viewport once. Returns null when the frame
    /// cannot be captured, e.g. after the click focused another window.
    /// </summary>
    private bool? ProbeGameMapAbsent(MapViewportColorSignature? reference)
    {
        var viewport = ResolveMapViewportForCurrentWindow();
        if (!_captureSvc.TryCaptureViewport(viewport, out var frameObject, out _)
            || frameObject is not CapturedGameFrame frame)
        {
            return null;
        }
        using (frame)
        {
            var signature = MapViewportPresenceDetector.CreateSignature(frame.Image, includeStructure: false);
            return MapViewportPresenceDetector.IsMapAbsent(signature, reference);
        }
    }

    private void WarnGameMapClosedByMouse()
    {
        _statusMessage = "检测到用鼠标关闭了地图，IDVB 已同步关图。请使用开图键或 Esc 关闭地图。";
        if (!_lastGameBounds.IsValid || _lastGameWindowHandle == IntPtr.Zero)
            return;
        ShowTransientOverlayStatus(
            MapOverlayStatusLevel.Warning,
            "请勿用鼠标关闭地图",
            "检测到地图已被鼠标点击关闭，IDVB 已同步关图。",
            "请使用开图键或 Esc 关闭地图，否则叠加层可能与游戏地图状态不同步。",
            _lastGameBounds,
            _lastGameWindowHandle);
        _overlay.Show();
    }
}
