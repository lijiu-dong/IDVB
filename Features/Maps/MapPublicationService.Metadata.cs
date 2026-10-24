// Public contract and unavailable implementation. Private sources are selected by the build.
using IDVBuff.UpdateCore;

namespace IDVBuff.Features.Maps;

public sealed record MapPublicationMetadataUpdate(Guid PublicationId, string Version, string PublisherHandle,
    string SubscriptionLink, string PackageName, string? CoverPath, byte[] FeedBytes, string ExpectedFeedSha256);
