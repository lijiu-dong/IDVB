namespace IDVBuff.Core.Contracts.Online;

public enum UsagePoolKind { Daily, WeeklyExtra, FreeDaily }

/// <summary>One quota pool. A null limit means unlimited; a zero limit means the tier has no such pool.</summary>
public sealed record UsagePool(UsagePoolKind Kind, long UsedSeconds, long? LimitSeconds, DateTimeOffset ResetsAt)
{
    public double UsedFraction => LimitSeconds is > 0 and var limit ? Math.Clamp((double)UsedSeconds / limit, 0, 1) : 0;
}

/// <summary>Registered tier as the server reports it. The key is data; callers never branch on it.</summary>
public sealed record UsageTier(string Key, string DisplayName, int Rank, bool Unlimited);

/// <summary>Server view of the account's quota. Enforced is false while the master switch is off.</summary>
public sealed record UsageSnapshot(bool Enforced, UsageTier? Tier, IReadOnlyList<UsagePool> Pools,
    long? RemainingSeconds, DateTimeOffset ServerTime, UsagePoints? Points = null);

public sealed record UsagePoints(decimal FreePoints, decimal PaidPoints, decimal HoursPerPoint);

public enum UsageStartOutcome
{
    /// <summary>Metered by the server.</summary>
    Started,
    /// <summary>Not limited: metering is off, unconfigured, or not reachable while not enforced.</summary>
    Unmetered,
    /// <summary>The quota is used up; no ticket was issued.</summary>
    Exhausted,
    /// <summary>Metering is enforced but the server could not be reached or refused the request.</summary>
    Unavailable,
}

public enum UsageEndReason { Exhausted, LeaseLost }

/// <summary>Outcome of starting a metered activity. Ticket is present for Started and Unmetered.</summary>
public sealed record UsageStartResult(UsageStartOutcome Outcome, IUsageTicket? Ticket, UsageSnapshot? Usage, string? Message = null);

/// <summary>One running activity. Disposing it stops timing; Ended fires when the quota or the lease runs out.</summary>
public interface IUsageTicket : IAsyncDisposable
{
    CancellationToken Ended { get; }
    UsageEndReason? EndReason { get; }
}

/// <summary>Play-time metering. Concurrent tickets share one server session, so overlapping time counts once.</summary>
public interface IUsageMeter
{
    UsageSnapshot? Current { get; }
    event EventHandler<UsageSnapshot>? Changed;
    Task<UsageStartResult> BeginAsync(string activity, CancellationToken cancellationToken = default);
    Task<UsageSnapshot?> RefreshAsync(CancellationToken cancellationToken = default);
}

/// <summary>Public builds and hosts without online services: nothing is metered or limited.</summary>
public sealed class UnavailableUsageMeter : IUsageMeter
{
    public UsageSnapshot? Current => null;
    public event EventHandler<UsageSnapshot>? Changed { add { } remove { } }
    public Task<UsageStartResult> BeginAsync(string activity, CancellationToken cancellationToken = default) =>
        Task.FromResult(new UsageStartResult(UsageStartOutcome.Unmetered, UnmeteredUsageTicket.Instance, null));
    public Task<UsageSnapshot?> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult<UsageSnapshot?>(null);
}

public sealed class UnmeteredUsageTicket : IUsageTicket
{
    public static UnmeteredUsageTicket Instance { get; } = new();
    public CancellationToken Ended => CancellationToken.None;
    public UsageEndReason? EndReason => null;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
