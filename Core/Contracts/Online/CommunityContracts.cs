using IDVBuff.Features.Maps;

namespace IDVBuff.Core.Contracts.Online;

public sealed record CommunityPublicationUpload(Guid PublicationId, string PublisherHandle,
    string SubscriptionLink, string OutputDirectory, string PackagePath, string PackageName, string CoverPath);

public sealed record CommunityMetadataUpdate(Guid PublicationId, string PublisherHandle,
    string SubscriptionLink, string PackageName, string? CoverPath, byte[] FeedBytes, string ExpectedFeedSha256);

public interface ICommunityService
{
    Task<IReadOnlyList<MapCoCreationPublication>> GetOwnedAsync(CancellationToken cancellationToken);
    Task<MapCoCreationCommunityCatalog> GetCatalogAsync(CancellationToken cancellationToken);
    Task<MapCoCreationCommunityCatalog> GetAcquiredAsync(CancellationToken cancellationToken);
    Task AcquireAsync(Guid? publicationId, string? filename, CancellationToken cancellationToken);
    Task DownloadPackageAsync(MapCoCreationDownload download, Stream destination,
        Action<double>? reportProgress, CancellationToken cancellationToken);
    Task<string> PublishAsync(CommunityPublicationUpload publication, CancellationToken cancellationToken);
    Task UpdateMetadataAsync(CommunityMetadataUpdate update, CancellationToken cancellationToken);
}
