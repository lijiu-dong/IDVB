using IDVBuff.UpdateCore;

namespace IDVBuff.Features.Maps;

public interface IMapSubscriptionService
{
    bool Available { get; }
    IReadOnlyList<MapSubscriptionRecord> GetSubscriptions();
    Task RegisterPublishedAsync(MapPublicationResult publication, string subscriptionLink,
        IReadOnlyList<MapRecord> maps, CancellationToken cancellationToken = default);
    Task<MapSubscriptionReconciliationResult> ReconcileInstalledMapsAsync(CancellationToken cancellationToken = default);
    Task RegisterCoCreationEditAsync(MapSubscriptionRecord verifiedRecord, MapPublicationPayload publication,
        MapRecord map, string? sourceClassName = null, Guid? sourceMapId = null);
    Task<MapSubscriptionRecord> AddAsync(string linkText, CancellationToken cancellationToken = default);
    Task SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
    Task<MapSubscriptionCheckResult> CheckAndApplyAsync(CancellationToken cancellationToken = default);
    Task<MapSubscriptionCheckResult> CheckAndApplyAsync(Guid subscriptionId, CancellationToken cancellationToken = default);
    Task RegisterMetadataAsync(MapPublicationMetadataUpdate update, CancellationToken cancellationToken = default);
}

public interface IMapPublicationService
{
    bool Available { get; }
    Task<MapPublicationResult> PublishAsync(IdvmExportScope scope, string? className,
        string outputParentDirectory, string publisherHandle, string publisherDisplayName,
        bool isOfficialPublisher, bool isBuilderPublisher, bool intendedForOfficialWebsite,
        string packageName, string coverPath, Guid? publicationId = null, byte[]? existingContentKey = null,
        CancellationToken cancellationToken = default, bool usesAutomaticCover = false);
    Task<MapPublicationMetadataUpdate> PrepareMetadataAsync(Guid publicationId, string publisherHandle,
        string publisherDisplayName, bool isOfficialPublisher, bool isBuilderPublisher,
        string packageName, string? coverPath, CancellationToken cancellationToken = default);
}

public interface ISubscriptionMapInstallation
{
    Task DetachUnregisteredCopiesAsync(IReadOnlyList<MapSubscriptionRecord> records, CancellationToken cancellationToken);
    Task<IReadOnlySet<Guid>> GetInstalledMapIdsAsync();
    Task<MapSubscriptionPromotionResult> ImportAsync(MapSubscriptionRecord record, MapPublicationPayload publication,
        string packagePath, CancellationToken cancellationToken);
}
