namespace IDVBuff.Core.Contracts.Online;

public sealed record VersionAccessResult(bool Allowed, bool LoginRequired, bool UpgradeRequired, string Message)
{
    public bool Disabled { get; init; }
    public static VersionAccessResult FeatureDisabled => new(true, false, false, "") { Disabled = true };
}

public interface IVersionPolicyService
{
    bool Enabled { get; }
    string DownloadUrl { get; }
    string? ReadToken();
    void ClearProof();
    Task<VersionAccessResult> CheckAsync(string productVersion, CancellationToken cancellationToken = default);
    Task MonitorHeadlessAsync(string productVersion, CancellationToken cancellationToken = default);
}

public interface IApplicationUpdateService
{
    bool Available { get; }
    void TryLaunch();
}
