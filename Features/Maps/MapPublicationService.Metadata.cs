using IDVBuff.UpdateCore;
using System.Security.Cryptography;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed record MapPublicationMetadataUpdate(Guid PublicationId, string Version, string PublisherHandle,
    string SubscriptionLink, string PackageName, string? CoverPath, byte[] FeedBytes, string ExpectedFeedSha256);

public sealed partial class MapPublicationService
{
    private static readonly HttpClient MetadataHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 2 * 1024 * 1024
    };

    public async Task<MapPublicationMetadataUpdate> PrepareMetadataAsync(Guid publicationId,
        string publisherHandle, string publisherDisplayName, bool isOfficialPublisher, bool isBuilderPublisher,
        string packageName, string? coverPath, CancellationToken cancellationToken = default)
    {
        var handle = MapSubscriptionProtocol.NormalizePublisherHandle(publisherHandle);
        var name = packageName.Trim();
        if (name.Length is < 1 or > 80) throw new InvalidOperationException("地图包名称须为 1 至 80 个字符。");
        MapSubscriptionRecord record;
        await MapSubscriptionOperations.Gate.WaitAsync(cancellationToken);
        try
        {
            record = _subscriptions.Load().SingleOrDefault(item => item.Id == publicationId
                && string.Equals(item.PublisherHandle, handle, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("只有原发布者能够修改地图包资料。");
        }
        finally { MapSubscriptionOperations.Gate.Release(); }
        var link = MapSubscriptionLink.Parse(record.Link);
        if (link.PublisherKeyId != record.PublisherKeyId)
            throw new CryptographicException("发布记录中的签名身份与订阅链接不一致。");
        // Fetch only the current small feed. No map snapshot, archive, encrypted
        // package or local content changes are needed to edit community metadata.
        var bytes = link.FeedUri.IsFile
            ? await File.ReadAllBytesAsync(link.FeedUri.LocalPath, cancellationToken)
            : await MetadataHttp.GetByteArrayAsync(link.FeedUri, cancellationToken);
        if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("地图订阅清单过大。");
        var envelope = JsonSerializer.Deserialize<SignedMapPublicationEnvelope>(bytes, MapSubscriptionProtocol.JsonOptions)
            ?? throw new InvalidDataException("地图订阅清单为空。");
        var officialKey = MapSubscriptionTrust.LoadOfficialPublicKey(AppContext.BaseDirectory);
        var payload = MapSubscriptionCrypto.Verify(envelope, link.PublisherKeyId, officialKey);
        var credential = _credentials.GetOrCreate(handle, officialKey);
        if (payload.PublicationId != publicationId || payload.PublisherHandle != handle || credential.KeyId != link.PublisherKeyId)
            throw new CryptographicException("官网地图包与当前发布身份不一致。");
        var updated = payload with { PackageName = name, PublisherDisplayName = publisherDisplayName.Trim(),
            IsOfficialPublisher = isOfficialPublisher, IsBuilderPublisher = isBuilderPublisher };
        var signed = MapSubscriptionCrypto.Sign(updated, credential.PrivateKeyPem);
        return new(publicationId, payload.Version, handle, record.Link, name, coverPath,
            JsonSerializer.SerializeToUtf8Bytes(signed, MapSubscriptionProtocol.JsonOptions),
            Convert.ToHexString(SHA256.HashData(bytes)));
    }
}
