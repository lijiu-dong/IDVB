// Public contract and unavailable implementation. Private sources are selected by the build.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IDVBuff.Features.Accounts;

internal sealed record VersionAccessEnvelope(string Payload, string Signature);
internal sealed record VersionAccessClaims(string Audience, string Version, string Nonce, string TokenHash,
    bool Allowed, bool OfflineExempt, long IssuedAt, long ExpiresAt, string Message, string DownloadUrl);

internal static class VersionAccessProof
{
    internal const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEcpBrflF6R5HFD7rm7B4iSa3xQaLw
        AQrIqHhSsvLGRUNwLpcrynOnVTYZkKV0yPbIrxKheSdCahrGg+n1hku0og==
        -----END PUBLIC KEY-----
        """;
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    internal static string HashToken(string token) => throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services");

    internal static VersionAccessClaims Verify(VersionAccessEnvelope envelope, string token, string version,
        string? nonce, DateTimeOffset now, string publicKey = PublicKey)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }
}
