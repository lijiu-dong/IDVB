using IDVBuff.Core.Contracts.Online;

namespace IDVBuff.Services.Online;

public sealed class UnavailableVersionPolicyService : IVersionPolicyService
{
    public bool Enabled => false;
    public string DownloadUrl => throw new OnlineServiceUnavailableException("downloads.page");
    public string? ReadToken() => null;
    public void ClearProof() { }
    public Task<VersionAccessResult> CheckAsync(string productVersion, CancellationToken cancellationToken = default) =>
        Task.FromResult(VersionAccessResult.FeatureDisabled);
    public Task MonitorHeadlessAsync(string productVersion, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class UnavailableApplicationUpdateService : IApplicationUpdateService
{
    public bool Available => false;
    public void TryLaunch() { }
}
