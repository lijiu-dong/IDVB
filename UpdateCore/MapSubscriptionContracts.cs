// Public contract and unavailable implementation. Private sources are selected by the build.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.UpdateCore;

public static class MapSubscriptionProtocol
{
    public const int SchemaVersion = 1;
    public const string OfficialPublisherHandle = "@xigefuli";
    public const string OfficialTrustFileName = "idvb-update-2026-01.pem";
    public const long MaximumEncryptedPackageBytes = 384L * 1024 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string NormalizePublisherHandle(string? value)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }
}

public static class IdvmPlatformCompatibility
{
    public const string Windows = "windows";
    public const string Android = "android";
    public const string Ios = "ios";
    public const string Web = "web";

    public static IReadOnlyList<string> All { get; } =
        [Windows, Android, Ios, Web];

    public static bool IsKnown(string value) => throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services");

    public static bool SupportsWindows(IReadOnlyCollection<string>? platforms) => throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services");
}

public sealed record MapSubscriptionLink(
    Uri FeedUri,
    byte[] ContentKey,
    string PublisherKeyId)
{
    public static MapSubscriptionLink Parse(string value)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }

    public string ToUriString()
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }
}

public sealed record MapPublicationPayload(
    int SchemaVersion,
    Guid PublicationId,
    string PublisherHandle,
    string PublisherKeyId,
    string Version,
    DateTimeOffset PublishedAtUtc,
    string Scope,
    bool IntendedForOfficialWebsite,
    string PackageUri,
    long EncryptedLength,
    string EncryptedSha256,
    long PlaintextLength,
    string PlaintextSha256,
    string? PublisherDisplayName = null,
    bool IsOfficialPublisher = false,
    bool IsBuilderPublisher = false,
    string? PackageName = null,
    IReadOnlyList<string>? SupportedPlatforms = null);

public sealed record SignedMapPublicationEnvelope(
    int SchemaVersion,
    string Payload,
    string Signature,
    string PublisherPublicKeyPem);

public sealed class MapSubscriptionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Link { get; set; } = string.Empty;
    public string FeedUri { get; set; } = string.Empty;
    public string PublisherKeyId { get; set; } = string.Empty;
    public string? PublisherHandle { get; set; }
    public string? PublisherDisplayName { get; set; }
    public bool IsOfficialPublisher { get; set; }
    public bool IsBuilderPublisher { get; set; }
    public string? PackageName { get; set; }
    public string? CoverPath { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset AddedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastCheckedAtUtc { get; set; }
    public DateTimeOffset? LastAppliedAtUtc { get; set; }
    public DateTimeOffset? LastPublishedAtUtc { get; set; }
    public string? LastAppliedPlaintextSha256 { get; set; }
    public string? LastAppliedVersion { get; set; }
    public Dictionary<string, string> ClassBindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Own-publication source map IDs mapped to individually saved local drafts.</summary>
    public Dictionary<Guid, Guid> CoCreationMapBindings { get; set; } = [];
    public List<Guid> InstalledMapIds { get; set; } = [];
    public string? LastError { get; set; }
}

public sealed record PendingMapSubscriptionUpdate(
    int SchemaVersion,
    Guid SubscriptionId,
    MapPublicationPayload Publication,
    string PackagePath,
    DateTimeOffset PreparedAtUtc)
{
    public string? AuthorizationSubject { get; init; }
    public SignedMapPublicationEnvelope? SignedFeed { get; init; }
}

public sealed class MapSubscriptionStore
{
    public MapSubscriptionStore(string rootDirectory) => throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services");

    public IReadOnlyList<MapSubscriptionRecord> Load()
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }

    public void Save(IEnumerable<MapSubscriptionRecord> records)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }
}

internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> value) => throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services");

    public static byte[] Decode(string value)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }
}
