namespace IDVBuff.UpdateCore;

/// <summary>Identity read from an already signature-verified publication, never from an HTTP status.</summary>
public sealed record SubscriptionKeyRequest(Guid PublicationId, string PublisherKeyId, uint KeyEpoch = 0);

public interface ISubscriptionContentKeyLease : IDisposable
{
    ReadOnlyMemory<byte> Key { get; }
    string? GrantId => null;
}

public interface ISubscriptionSecurity
{
    string? Subject { get; }
    ValueTask<ISubscriptionContentKeyLease> AcquireKeyAsync(SubscriptionKeyRequest request, CancellationToken cancellationToken);
}

public interface ISignedSubscriptionFeedObserver
{
    void ObserveVerifiedFeed(Guid publicationId, string publisherKeyId, ReadOnlyMemory<byte> signedPayload);
}
