using IDVBuff.Core.Contracts.Online;
using IDVBuff.Features.Maps;

namespace IDVBuff.Services.Online;

public sealed class UnavailableCommunityService : ICommunityService
{
    public Task<IReadOnlyList<MapCoCreationPublication>> GetOwnedAsync(CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<MapCoCreationPublication>>(new OnlineServiceUnavailableException("community.mine"));
    public Task<MapCoCreationCommunityCatalog> GetCatalogAsync(CancellationToken cancellationToken) =>
        Task.FromException<MapCoCreationCommunityCatalog>(new OnlineServiceUnavailableException("community.catalog"));
    public Task<MapCoCreationCommunityCatalog> GetAcquiredAsync(CancellationToken cancellationToken) =>
        Task.FromException<MapCoCreationCommunityCatalog>(new OnlineServiceUnavailableException("community.acquired.list"));
    public Task AcquireAsync(Guid? publicationId, string? filename, CancellationToken cancellationToken) =>
        Task.FromException(new OnlineServiceUnavailableException("community.acquired.add"));
    public Task DownloadPackageAsync(MapCoCreationDownload download, Stream destination,
        Action<double>? reportProgress, CancellationToken cancellationToken) =>
        Task.FromException(new OnlineServiceUnavailableException("downloads.maps.package"));
    public Task<string> PublishAsync(CommunityPublicationUpload publication, CancellationToken cancellationToken) =>
        Task.FromException<string>(new OnlineServiceUnavailableException("community.publish"));
    public Task UpdateMetadataAsync(CommunityMetadataUpdate update, CancellationToken cancellationToken) =>
        Task.FromException(new OnlineServiceUnavailableException("community.metadata.update"));
}
