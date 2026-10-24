// Public contract and unavailable implementation. Private sources are selected by the build.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IDVBuff.UpdateCore;

public static class MapSubscriptionCrypto
{

    public static (string PrivateKeyPem, string PublicKeyPem, string KeyId) CreatePublisherKey()
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }

    public static string ComputeKeyId(ECDsa key) => throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services");

    public static string ComputeKeyId(string publicKeyPem)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }

    public static SignedMapPublicationEnvelope Sign(
        MapPublicationPayload payload,
        string privateKeyPem)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }

    public static MapPublicationPayload Verify(
        SignedMapPublicationEnvelope envelope,
        string pinnedPublisherKeyId,
        string officialPublicKeyPem)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }

    public static byte[] EncryptIdvm(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> contentKey,
        Guid publicationId)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }

    public static byte[] DecryptIdvm(
        ReadOnlySpan<byte> encrypted,
        ReadOnlySpan<byte> contentKey,
        Guid expectedPublicationId,
        string expectedPlaintextSha256)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }
}
