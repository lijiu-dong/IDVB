// Public contract and unavailable implementation. Private sources are selected by the build.
using IDVBuff.UpdateCore;
using System.Security.Cryptography;

namespace IDVBuff.Features.Maps;

internal sealed class MapPublisherCredentialStore
{

    public MapPublisherCredentialStore(string? rootDirectory = null) => throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services");

    public (string PrivateKeyPem, string PublicKeyPem, string KeyId) GetOrCreate(
        string publisherHandle,
        string officialPublicKeyPem)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }
}
