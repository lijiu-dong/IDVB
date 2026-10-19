using IDVBuff.Core.Contracts.Online;
using IDVBuff.Features.Maps;
using IDVBuff.Services.Online;

namespace IDVBuff.Features.Accounts;

internal static partial class CoCreationPublicationClient
{
    private static readonly Lazy<ICommunityService> Instance = new(Create);
    internal static ICommunityService Service => Instance.Value;
    internal static Task<IReadOnlyList<MapCoCreationPublication>> GetAsync(CancellationToken cancellationToken) => Service.GetOwnedAsync(cancellationToken);
    internal static Task<MapCoCreationCommunityCatalog> GetCommunityAsync(CancellationToken cancellationToken) => Service.GetCatalogAsync(cancellationToken);
    internal static Task<MapCoCreationCommunityCatalog> GetAcquiredAsync(CancellationToken cancellationToken) => Service.GetAcquiredAsync(cancellationToken);
    internal static Task AcquireAsync(Guid? publicationId, string? filename, CancellationToken cancellationToken) => Service.AcquireAsync(publicationId, filename, cancellationToken);
    internal static Task DownloadPackageAsync(MapCoCreationDownload download, Stream destination,
        Action<double>? reportProgress, CancellationToken cancellationToken) =>
        Service.DownloadPackageAsync(download, destination, reportProgress, cancellationToken);
    private static ICommunityService Create()
    {
        ICommunityService service = new UnavailableCommunityService();
        ConfigurePrivate(ref service);
        return service;
    }
    static partial void ConfigurePrivate(ref ICommunityService service);
}
