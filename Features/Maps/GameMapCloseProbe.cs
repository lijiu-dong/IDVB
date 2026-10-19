namespace IDVBuff.Features.Maps;

/// <summary>A click starts an observation window, rather than deciding from its first frame.</summary>
internal sealed class GameMapCloseProbe
{
    internal const int SampleIntervalMilliseconds = 100;
    internal const int OpeningGraceMilliseconds = 1500;
    internal const int ObservationMilliseconds = 2500;
    internal const int ConfirmationMilliseconds = 300;
    private double _notBefore;
    private double? _absentSince;
    internal double Deadline { get; private set; }

    internal void Arm(double now, double openedAt)
    {
        _notBefore = Math.Max(now + 200, openedAt + OpeningGraceMilliseconds);
        Deadline = _notBefore + ObservationMilliseconds;
        _absentSince = null;
    }

    internal bool CanSample(double now) => now >= _notBefore && now <= Deadline;

    internal bool Observe(double now, bool? absent)
    {
        if (!CanSample(now) || absent is not true)
        {
            _absentSince = null;
            return false;
        }
        _absentSince ??= now;
        return now - _absentSince.Value >= ConfirmationMilliseconds;
    }

    internal static Guid? GetCurrentIdentity(MapMatchSnapshot match, MapMatchMapLease lease, Guid? identityMapId) =>
        identityMapId is { } mapId && lease.IsCurrent(match, mapId) ? mapId : null;

    internal static bool ShouldWarnOnEscape(bool hasCurrentIdentity, bool hasVisibleMap, bool? absent) =>
        hasCurrentIdentity && hasVisibleMap && absent is true;
}
