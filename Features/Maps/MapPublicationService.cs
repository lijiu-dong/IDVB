using IDVBuff.Core.Contracts.Online;

namespace IDVBuff.Features.Maps;

public sealed record MapPublicationResult(
    Guid PublicationId,
    string Version,
    string PublisherHandle,
    string PublisherDisplayName,
    bool IsOfficialPublisher,
    bool IsBuilderPublisher,
    string OutputDirectory,
    string SubscriptionLink,
    string PackageName,
    string CoverPath,
    string PackagePath,
    IReadOnlyList<MapRecord> ExportedMaps,
    bool UsesAutomaticCover = false);

public sealed partial class MapPublicationService : IMapPublicationService
{
    private readonly IMapPublicationService _service;
    public bool Available => _service.Available;
    public MapPublicationService(MapRepository repository, string? dataRoot = null)
    {
        IMapPublicationService service = new UnavailableMapPublicationService();
        ConfigurePrivate(repository, dataRoot ?? AppDataPaths.RootDirectory, ref service);
        _service = service;
    }
    public Task<MapPublicationResult> PublishAsync(IdvmExportScope scope, string? className,
        string outputParentDirectory, string publisherHandle, string publisherDisplayName,
        bool isOfficialPublisher, bool isBuilderPublisher, bool intendedForOfficialWebsite,
        string packageName, string coverPath, Guid? publicationId = null, byte[]? existingContentKey = null,
        CancellationToken cancellationToken = default, bool usesAutomaticCover = false) =>
        _service.PublishAsync(scope, className, outputParentDirectory, publisherHandle, publisherDisplayName,
            isOfficialPublisher, isBuilderPublisher, intendedForOfficialWebsite, packageName, coverPath,
            publicationId, existingContentKey, cancellationToken, usesAutomaticCover);
    public Task<MapPublicationMetadataUpdate> PrepareMetadataAsync(Guid publicationId, string publisherHandle,
        string publisherDisplayName, bool isOfficialPublisher, bool isBuilderPublisher,
        string packageName, string? coverPath, CancellationToken cancellationToken = default) =>
        _service.PrepareMetadataAsync(publicationId, publisherHandle, publisherDisplayName,
            isOfficialPublisher, isBuilderPublisher, packageName, coverPath, cancellationToken);
    static partial void ConfigurePrivate(MapRepository repository, string dataRoot, ref IMapPublicationService service);
}

internal sealed class UnavailableMapPublicationService : IMapPublicationService
{
    public bool Available => false;
    public Task<MapPublicationResult> PublishAsync(IdvmExportScope scope, string? className,
        string outputParentDirectory, string publisherHandle, string publisherDisplayName,
        bool isOfficialPublisher, bool isBuilderPublisher, bool intendedForOfficialWebsite,
        string packageName, string coverPath, Guid? publicationId = null, byte[]? existingContentKey = null,
        CancellationToken cancellationToken = default, bool usesAutomaticCover = false) =>
        Task.FromException<MapPublicationResult>(new OnlineServiceUnavailableException("community.publish"));
    public Task<MapPublicationMetadataUpdate> PrepareMetadataAsync(Guid publicationId, string publisherHandle,
        string publisherDisplayName, bool isOfficialPublisher, bool isBuilderPublisher,
        string packageName, string? coverPath, CancellationToken cancellationToken = default) =>
        Task.FromException<MapPublicationMetadataUpdate>(new OnlineServiceUnavailableException("community.metadata.update"));
}
