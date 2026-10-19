namespace IDVBuff.Core.Contracts.Online.Security;

/// <summary>Reserved control-plane operations. No protected-resource authority is implied by an HTTP response.</summary>
public enum SecurityOperation
{
    Capabilities, DeviceChallenge, RegisterDevice, RevokeDevice, RecoverDevice, CreateSession, RevokeSession,
    IssueRuntimeLicense, RenewRuntimeLicense, IssueOfflineAllowance, CreateResourceGrant,
    RedeemResourceGrant, ReadArtifactManifest, ReadResourceChunk, ReadKeyEnvelope,
    ReadPolicySnapshot, ListAppeals, SubmitAppeal, IssueSubscriptionGrant
}

public static class SecurityOperationNames
{
    public static string Id(this SecurityOperation operation) => operation switch
    {
        SecurityOperation.Capabilities => "security.capabilities",
        SecurityOperation.DeviceChallenge => "security.devices.challenge",
        SecurityOperation.RegisterDevice => "security.devices.register",
        SecurityOperation.RevokeDevice => "security.devices.revoke",
        SecurityOperation.RecoverDevice => "security.devices.recover",
        SecurityOperation.CreateSession => "security.sessions.create",
        SecurityOperation.RevokeSession => "security.sessions.revoke",
        SecurityOperation.IssueRuntimeLicense => "security.licenses.issue",
        SecurityOperation.RenewRuntimeLicense => "security.licenses.renew",
        SecurityOperation.IssueOfflineAllowance => "security.offline_allowances.issue",
        SecurityOperation.CreateResourceGrant => "security.resources.grant",
        SecurityOperation.RedeemResourceGrant => "security.resources.redeem",
        SecurityOperation.ReadArtifactManifest => "security.resources.manifest",
        SecurityOperation.ReadResourceChunk => "security.resources.chunk",
        SecurityOperation.ReadKeyEnvelope => "security.resources.key_envelope",
        SecurityOperation.ReadPolicySnapshot => "security.policy.snapshot",
        SecurityOperation.ListAppeals => "security.appeals.list",
        SecurityOperation.SubmitAppeal => "security.appeals.submit",
        SecurityOperation.IssueSubscriptionGrant => "subscriptions.grant.issue",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };
}

/// <summary>The caller owns Body until SendAsync completes. Bodies and proof material must never be logged.</summary>
public sealed class SecurityTransportRequest
{
    public required SecurityOperation Operation { get; init; }
    public required Guid RequestId { get; init; }
    public Guid? IdempotencyKey { get; init; }
    public IReadOnlyDictionary<string, string> PathParameters { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> QueryParameters { get; init; } =
        new Dictionary<string, string>();
    public ReadOnlyMemory<byte> Body { get; init; }
    public ISecurityRequestProofProvider? ProofProvider { get; init; }
    public string? ServerNonce { get; init; }
    public override string ToString() => $"{Operation.Id()} [{RequestId:D}]";
}

/// <summary>Built from the final configured target. Resource/body authorization remains a server responsibility.</summary>
public sealed record SecurityRequestTarget(SecurityOperation Operation, string Method, Uri Uri,
    Guid RequestId, Guid? IdempotencyKey, ReadOnlyMemory<byte> BodySha256, string? ServerNonce = null);

/// <summary>Custom proof is not RFC 9449 DPoP. Implementations must reject unsupported enum values.</summary>
public enum SecurityProofFormat { CustomPopV1, DpopRfc9449 }

/// <summary>Implemented by SecurityRuntime/PlatformSecurity; never exports device private keys.</summary>
public interface ISecurityRequestProofProvider
{
    ValueTask<ISecurityRequestProofLease> CreateAsync(SecurityRequestTarget target, CancellationToken cancellationToken);
}

/// <summary>Owner clears sensitive buffers on disposal. A new proof is required for each network attempt.</summary>
public interface ISecurityRequestProofLease : IDisposable
{
    SecurityProofFormat Format { get; }
    ReadOnlyMemory<byte> AuthorizationUtf8 { get; }
    ReadOnlyMemory<byte> PossessionProofUtf8 { get; }
}

/// <summary>Unverified wire data. SecurityRuntime must verify type, issuer, audience, signature and bindings before use.</summary>
public interface IUnverifiedSecurityResponseLease : IDisposable
{
    Guid RequestId { get; }
    int StatusCode { get; }
    ReadOnlyMemory<byte> Body { get; }
    string? ServerNonce { get; }
    TimeSpan? RetryAfter { get; }
    string? ProblemCode { get; }
    string? CaseId { get; }
}

/// <summary>Control-plane transport only. Never call from scan, alignment, rendering or per-frame code.</summary>
public interface IOnlineSecurityTransport
{
    bool IsConfigured { get; }
    ValueTask<IUnverifiedSecurityResponseLease> SendAsync(SecurityTransportRequest request, CancellationToken cancellationToken);
}

public sealed record DesktopSecurityState(bool IsConfigured, bool ShadowMode, long AccountGeneration,
    string Phase, string? ProblemCode = null);

/// <summary>Account-scoped control-plane lifecycle; it never gates local application features.</summary>
public interface IDesktopSecurityRuntime : IDisposable
{
    DesktopSecurityState State { get; }
    void Start();
    Task SynchronizeAsync(CancellationToken cancellationToken = default);
}
