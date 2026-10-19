using IDVBuff.Core.Contracts.Online;

namespace IDVBuff.Services.Online;

public sealed class UnavailableAccountService : IAccountService
{
    public string? PublishToken => null;
    public AccountIdentity? Identity => null;
    public event EventHandler? Changed { add { } remove { } }
    public AccountSessionSnapshot Capture() => new(null, null, 0);
    public bool IsCurrent(AccountSessionSnapshot snapshot) => snapshot == Capture();
    public Task<AccountIdentity> LoginAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<AccountIdentity>(new OnlineServiceUnavailableException("accounts.login"));
    public void Set(string token, AccountIdentity identity) => throw new OnlineServiceUnavailableException("accounts.set");
    public void Clear() { }
    public void ClearIfCurrent(string? token) { }
    public void ClearIfCurrent(AccountSessionSnapshot snapshot) { }
    public Task LogoutAsync() => Task.CompletedTask;
    public Task<AccountIdentity> RequirePublishAccessAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<AccountIdentity>(new OnlineServiceUnavailableException("accounts.token.validate"));
    public Task RefreshAsync() => Task.CompletedTask;
}
