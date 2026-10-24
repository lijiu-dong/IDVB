namespace IDVBuff.Core.Contracts.Online;

public sealed record AccountIdentity(string DisplayName, string PublisherHandle, string? AvatarUrl,
    bool IsOfficial, bool IsBuilder)
{
    public string? Subject { get; init; }
}

public sealed record AccountSessionSnapshot(string? Token, AccountIdentity? Identity, long Generation,
    CancellationToken Lifetime = default);
public sealed record StoredAccountSession(string Token, AccountIdentity Identity, string? ProductVersion = null);

public interface IAccountService
{
    string? PublishToken { get; }
    AccountIdentity? Identity { get; }
    event EventHandler? Changed;
    AccountSessionSnapshot Capture();
    bool IsCurrent(AccountSessionSnapshot snapshot);
    Task<AccountIdentity> LoginAsync(CancellationToken cancellationToken = default);
    void Set(string token, AccountIdentity identity);
    void Clear();
    void ClearIfCurrent(string? token);
    void ClearIfCurrent(AccountSessionSnapshot snapshot);
    Task LogoutAsync();
    Task<AccountIdentity> RequirePublishAccessAsync(CancellationToken cancellationToken = default);
    Task RefreshAsync();
}

public interface IAccountCredentialStore
{
    StoredAccountSession? Load();
    void Save(StoredAccountSession session);
    void Clear();
}

public interface IOnlineBrowser
{
    Task<bool> LaunchAsync(Uri uri, CancellationToken cancellationToken);
}

public sealed class OnlineServiceUnavailableException(string operation)
    : InvalidOperationException($"在线服务未配置，无法执行 {operation}。")
{
    public string Operation { get; } = operation;
}
