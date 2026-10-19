using IDVBuff.Core.Contracts.Online;
using IDVBuff.UpdateCore;

namespace IDVBuff.Features.Maps;

public sealed record MapSubscriptionCheckResult(
    int CheckedCount,
    int UpToDateCount,
    int AppliedCount,
    int FailedCount);

public sealed record MapSubscriptionReconciliationResult(
    int RemovedSubscriptionCount,
    int IncompleteSubscriptionCount);

public sealed partial class MapSubscriptionService : IMapSubscriptionService
{
    private readonly IMapSubscriptionService _service;
    public bool Available => _service.Available;
    public MapSubscriptionService(MapRepository repository, string? dataRoot = null)
        : this(new SubscriptionMapInstallation(repository), dataRoot) { }
    public MapSubscriptionService(ISubscriptionMapInstallation installation, string? dataRoot = null)
    {
        IMapSubscriptionService service = new UnavailableMapSubscriptionService();
        ConfigurePrivate(installation, dataRoot ?? AppDataPaths.RootDirectory, ref service);
        _service = service;
    }
    public IReadOnlyList<MapSubscriptionRecord> GetSubscriptions() => _service.GetSubscriptions();
    public Task RegisterPublishedAsync(MapPublicationResult publication, string subscriptionLink, IReadOnlyList<MapRecord> maps, CancellationToken cancellationToken = default) => _service.RegisterPublishedAsync(publication, subscriptionLink, maps, cancellationToken);
    public Task<MapSubscriptionReconciliationResult> ReconcileInstalledMapsAsync(CancellationToken cancellationToken = default) => _service.ReconcileInstalledMapsAsync(cancellationToken);
    public Task RegisterCoCreationEditAsync(MapSubscriptionRecord record, MapPublicationPayload publication, MapRecord map, string? sourceClassName = null, Guid? sourceMapId = null) => _service.RegisterCoCreationEditAsync(record, publication, map, sourceClassName, sourceMapId);
    public Task<MapSubscriptionRecord> AddAsync(string linkText, CancellationToken cancellationToken = default) => _service.AddAsync(linkText, cancellationToken);
    public Task SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default) => _service.SetEnabledAsync(id, enabled, cancellationToken);
    public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) => _service.RemoveAsync(id, cancellationToken);
    public Task<MapSubscriptionCheckResult> CheckAndApplyAsync(CancellationToken cancellationToken = default) => _service.CheckAndApplyAsync(cancellationToken);
    public Task RegisterMetadataAsync(MapPublicationMetadataUpdate update, CancellationToken cancellationToken = default) => _service.RegisterMetadataAsync(update, cancellationToken);
    static partial void ConfigurePrivate(ISubscriptionMapInstallation installation, string dataRoot, ref IMapSubscriptionService service);
}

internal sealed class UnavailableMapSubscriptionService : IMapSubscriptionService
{
    public bool Available => false;
    private static OnlineServiceUnavailableException Unavailable() => new("subscriptions");
    public IReadOnlyList<MapSubscriptionRecord> GetSubscriptions() => throw Unavailable();
    public Task RegisterPublishedAsync(MapPublicationResult publication, string subscriptionLink, IReadOnlyList<MapRecord> maps, CancellationToken cancellationToken = default) => Task.FromException(Unavailable());
    public Task<MapSubscriptionReconciliationResult> ReconcileInstalledMapsAsync(CancellationToken cancellationToken = default) => Task.FromException<MapSubscriptionReconciliationResult>(Unavailable());
    public Task RegisterCoCreationEditAsync(MapSubscriptionRecord record, MapPublicationPayload publication, MapRecord map, string? sourceClassName = null, Guid? sourceMapId = null) => Task.FromException(Unavailable());
    public Task<MapSubscriptionRecord> AddAsync(string linkText, CancellationToken cancellationToken = default) => Task.FromException<MapSubscriptionRecord>(Unavailable());
    public Task SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default) => Task.FromException(Unavailable());
    public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromException(Unavailable());
    public Task<MapSubscriptionCheckResult> CheckAndApplyAsync(CancellationToken cancellationToken = default) => Task.FromException<MapSubscriptionCheckResult>(Unavailable());
    public Task RegisterMetadataAsync(MapPublicationMetadataUpdate update, CancellationToken cancellationToken = default) => Task.FromException(Unavailable());
}
