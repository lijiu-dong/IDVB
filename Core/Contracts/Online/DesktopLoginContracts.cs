namespace IDVBuff.Core.Contracts.Online;

/// <summary>Result of the startup access decision. Only the server decides; the client verifies its signature.</summary>
public enum DesktopAccessOutcome
{
    /// <summary>Fresh server-signed access for this installation.</summary>
    Granted,
    /// <summary>Server unreachable; a previously signed offline allowance for this installation is still valid.</summary>
    GrantedOffline,
    /// <summary>No usable account session, or this installation must complete an email code.</summary>
    LoginRequired,
    /// <summary>The account is disabled by the server.</summary>
    Disabled,
    /// <summary>Server unreachable and no valid offline allowance.</summary>
    Unavailable,
    /// <summary>Public build without the private online services: no gate.</summary>
    NotConfigured,
    /// <summary>The product version is no longer allowed to start.</summary>
    UpdateRequired,
}

public sealed record DesktopAccessResult(DesktopAccessOutcome Outcome, string? Message = null,
    DateTimeOffset? OfflineUntil = null, AccountIdentity? Identity = null)
{
    public const string VersionUpdateMessage = "当前版本已停用，请更新后继续使用 IDVB。";
}

public enum DesktopAuthStepKind { SignedIn, VerificationRequired, CodeSent, PasswordChanged, Failed, CodeVerified }

/// <summary>One step of the native login / registration flow. Field errors are keyed by email, password, code, displayName.</summary>
public sealed record DesktopAuthStep(DesktopAuthStepKind Kind, string? Message = null, string? MaskedEmail = null,
    IReadOnlyDictionary<string, string>? FieldErrors = null, AccountIdentity? Identity = null);

/// <summary>Native desktop login (1.7.1-preview.3). Replaces the browser OAuth flow for the desktop application.</summary>
public interface IDesktopLoginGate
{
    bool IsConfigured { get; }
    /// <summary>Email domains a new account may use; the registration page offers only these.</summary>
    IReadOnlyList<string> RegistrationDomains { get; }
    Task<DesktopAccessResult> CheckAccessAsync(CancellationToken cancellationToken = default);
    Task<DesktopAuthStep> LoginAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<DesktopAuthStep> ConfirmLoginAsync(string email, string password, string code, CancellationToken cancellationToken = default);
    Task<DesktopAuthStep> StartRegistrationAsync(string email, CancellationToken cancellationToken = default);
    Task<DesktopAuthStep> VerifyRegistrationCodeAsync(string email, string code, CancellationToken cancellationToken = default);
    Task<DesktopAuthStep> ConfirmRegistrationAsync(string email, string code, string password, string displayName,
        CancellationToken cancellationToken = default);
    Task<DesktopAuthStep> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default);
    Task<DesktopAuthStep> VerifyPasswordResetCodeAsync(string email, string code, CancellationToken cancellationToken = default);
    Task<DesktopAuthStep> ConfirmPasswordResetAsync(string email, string code, string newPassword,
        CancellationToken cancellationToken = default);
}

/// <summary>Public build: the private online services are absent, so there is nothing to gate.</summary>
public sealed class UnavailableDesktopLoginGate : IDesktopLoginGate
{
    public bool IsConfigured => false;
    public IReadOnlyList<string> RegistrationDomains { get; } = [];
    public Task<DesktopAccessResult> CheckAccessAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new DesktopAccessResult(DesktopAccessOutcome.NotConfigured));
    private static Task<DesktopAuthStep> Unavailable() =>
        Task.FromResult(new DesktopAuthStep(DesktopAuthStepKind.Failed, "在线服务未配置。"));
    public Task<DesktopAuthStep> LoginAsync(string email, string password, CancellationToken cancellationToken = default) => Unavailable();
    public Task<DesktopAuthStep> ConfirmLoginAsync(string email, string password, string code, CancellationToken cancellationToken = default) => Unavailable();
    public Task<DesktopAuthStep> StartRegistrationAsync(string email, CancellationToken cancellationToken = default) => Unavailable();
    public Task<DesktopAuthStep> VerifyRegistrationCodeAsync(string email, string code, CancellationToken cancellationToken = default) => Unavailable();
    public Task<DesktopAuthStep> ConfirmRegistrationAsync(string email, string code, string password, string displayName,
        CancellationToken cancellationToken = default) => Unavailable();
    public Task<DesktopAuthStep> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default) => Unavailable();
    public Task<DesktopAuthStep> VerifyPasswordResetCodeAsync(string email, string code, CancellationToken cancellationToken = default) => Unavailable();
    public Task<DesktopAuthStep> ConfirmPasswordResetAsync(string email, string code, string newPassword,
        CancellationToken cancellationToken = default) => Unavailable();
}
