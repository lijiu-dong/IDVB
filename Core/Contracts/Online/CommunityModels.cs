// Namespace retained for source and consumer compatibility.
namespace IDVBuff.Features.Maps;

public sealed record MapCoCreationPublication(Guid Id, string Name, string SubscriptionLink,
    string PublisherHandle, DateTimeOffset? PublishedAt, string? CoverUrl = null, string? PublisherName = null,
    string? Version = null);
public sealed record MapCoCreationDownload(string Name, string Filename, string DownloadUrl, long Size,
    DateTimeOffset? Uploaded, string? ResourceKey = null);
public sealed record MapCoCreationCommunityCatalog(IReadOnlyList<MapCoCreationPublication> Publications,
    IReadOnlyList<MapCoCreationDownload> Maps);
