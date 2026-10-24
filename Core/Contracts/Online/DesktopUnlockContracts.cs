namespace IDVBuff.Core.Contracts.Online;

/// <summary>Identity of immutable encrypted content. A licence renewal never changes the content.</summary>
public sealed record DesktopUnlockArtifact(string ArtifactId, long KeyEpoch, string Digest);

/// <summary>Required items are never silently dropped. Excess prefetch items are returned as deferred.</summary>
public sealed record DesktopUnlockRequest(string ClientVersion, long BootstrapEpoch,
    IReadOnlyList<DesktopUnlockArtifact> Required, IReadOnlyList<DesktopUnlockArtifact> Prefetch);

public interface IDesktopResourceKeyLease : IDisposable
{
    ReadOnlyMemory<byte> Key { get; }
}

/// <summary>Owns short-lived content keys. Callers must dispose both this result and acquired key leases.</summary>
public interface IDesktopUnlockLease : IDisposable
{
    DesktopAccessResult Access { get; }
    /// <summary>The unlock route is absent. Only a build with no protected bootstrap may use legacy signed access.</summary>
    bool EndpointUnavailable { get; }
    IReadOnlyList<DesktopUnlockArtifact> Deferred { get; }
    IReadOnlyList<string> Denied { get; }
    IDesktopResourceKeyLease AcquireKey(DesktopUnlockArtifact artifact);
}

/// <summary>Independent capability; existing login and subscription interfaces remain compatible.</summary>
public interface IDesktopResourceUnlock
{
    Task<IDesktopUnlockLease> UnlockAsync(DesktopUnlockRequest request, CancellationToken cancellationToken = default);
}

public sealed class UnavailableDesktopResourceUnlock : IDesktopResourceUnlock
{
    public Task<IDesktopUnlockLease> UnlockAsync(DesktopUnlockRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<IDesktopUnlockLease>(new InvalidOperationException("资源解锁服务未配置。"));
}
