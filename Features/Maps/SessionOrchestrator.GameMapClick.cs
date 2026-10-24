using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private GameMapClickProbeContext? _gameMapClickProbe;
    private sealed record GameMapClickProbeContext(int Version, int MatchVersion, Guid MapId, string? Floor,
        IntPtr Window, MapScreenRect Bounds, MapViewportColorSignature? Reference, GameMapCloseProbe Timing);
    private static double GameMapCloseNow => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;

    private async Task HandleGameMapClickAsync()
    {
        if (!CanCheckGameMapClose() || !_gameMapToggleState.IsOpen) return;
        if (GetGameMapCloseIdentityId() is not { } mapId)
        {
            ReportInputDecision("game-map-click", "ignored", "identity-not-locked");
            return;
        }
        if (!HasVisibleGameMapAlignment(mapId))
        {
            ReportInputDecision("game-map-click", "ignored", "no-visible-alignment");
            return;
        }
        if (!_captureSvc.TryGetForegroundClientBounds(out var boundsObject, out var window, out _)
            || boundsObject is not MapScreenRect bounds) return;
        if (window != _lastGameWindowHandle || bounds != _lastGameBounds)
        {
            ReportInputDecision("game-map-click", "ignored", "alignment-target-changed");
            return;
        }
        var now = GameMapCloseNow;
        var openedAt = _gameMapToggleState.OpenedAtTimestamp * 1000d / Stopwatch.Frequency;
        if (_gameMapClickProbe is { } active && IsGameMapClickProbeCurrent(active)
            && active.Window == window && active.Bounds == bounds)
        {
            active.Timing.Arm(now, openedAt);
            ReportInputDecision("game-map-click", "observing", "extended-after-click");
            return;
        }
        var timing = new GameMapCloseProbe();
        timing.Arm(now, openedAt);
        var probe = new GameMapClickProbeContext(_gameMapToggleState.Version,
            _matchSession.Snapshot.Version, mapId, GameMapCloseFloor,
            window, bounds, GetCurrentMapViewportPresenceReference(), timing);
        _gameMapClickProbe = probe;
        try
        {
            while (IsGameMapClickProbeCurrent(probe) && GameMapCloseNow <= timing.Deadline)
            {
                await Task.Delay(GameMapCloseProbe.SampleIntervalMilliseconds, CurrentMatchCancellationToken);
                if (!IsGameMapClickProbeCurrent(probe)) return;
                if (!timing.CanSample(GameMapCloseNow)) continue;
                // A visible frame or failed capture resets confirmation, but does
                // not stop watching: the game's close can arrive much later.
                var absent = ProbeGameMapAbsent(probe.Reference, probe.Window, probe.Bounds);
                if (!timing.Observe(GameMapCloseNow, absent)) continue;
                await _matchLifecycleGate.WaitAsync();
                try
                {
                    if (!IsGameMapClickProbeCurrent(probe)
                        || !IsGameMapCloseTargetCurrent(probe.Window, probe.Bounds)) return;
                    _gameMapToggleState.SetOpenForExternalController(false);
                    await EndMapDisplayAsync("game map closed by mouse");
                    ReportInputDecision("game-map-click", "applied", "map-closed-by-mouse");
                }
                finally { _matchLifecycleGate.Release(); }
                // Release probe ownership before waiting for the popup duration.
                // A new open/click starts immediately without sharing old work.
                if (ReferenceEquals(_gameMapClickProbe, probe)) _gameMapClickProbe = null;
                await WarnGameMapClosedByMouseAsync(probe.Bounds, probe.MapId, probe.MatchVersion);
                return;
            }
            ReportInputDecision("game-map-click", "ignored", "observation-ended-without-confirmed-close");
        }
        finally
        {
            if (ReferenceEquals(_gameMapClickProbe, probe)) _gameMapClickProbe = null;
        }
    }

    private bool IsGameMapClickProbeCurrent(GameMapClickProbeContext probe) =>
        ReferenceEquals(_gameMapClickProbe, probe) && CanCheckGameMapClose()
        && _gameMapToggleState.IsOpen && _gameMapToggleState.Version == probe.Version
        && IsGameMapCloseIdentityCurrent(probe.MapId, probe.MatchVersion)
        && GameMapCloseFloor == probe.Floor && HasVisibleGameMapAlignment(probe.MapId);
}
