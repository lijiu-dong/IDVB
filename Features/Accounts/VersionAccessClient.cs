using IDVBuff.Core.Contracts.Online;
using IDVBuff.Services.Online;

namespace IDVBuff.Features.Accounts;

internal static partial class VersionAccessClient
{
    private static readonly Lazy<IVersionPolicyService> Instance = new(Create);
    internal static IVersionPolicyService Service => Instance.Value;
    internal static bool Enabled => Service.Enabled;
    internal static string DownloadUrl => Service.DownloadUrl;
    internal static string? ReadToken() => Service.ReadToken();
    internal static void ClearProof() => Service.ClearProof();
    internal static Task<VersionAccessResult> CheckAsync(string productVersion, CancellationToken cancellationToken = default) => Service.CheckAsync(productVersion, cancellationToken);
    internal static Task MonitorHeadlessAsync(string productVersion, CancellationToken cancellationToken = default) => Service.MonitorHeadlessAsync(productVersion, cancellationToken);
    private static IVersionPolicyService Create()
    {
        IVersionPolicyService service = new UnavailableVersionPolicyService();
        ConfigurePrivate(ref service);
        return service;
    }
    static partial void ConfigurePrivate(ref IVersionPolicyService service);
}