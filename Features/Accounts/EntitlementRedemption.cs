using IDVBuff.Core.Contracts.Online;

namespace IDVBuff.Features.Accounts;

internal static partial class DesktopEntitlementRedemptionHost
{
    private static readonly Lazy<IEntitlementRedemptionService> Instance = new(Create);
    internal static IEntitlementRedemptionService Service => Instance.Value;
    private static IEntitlementRedemptionService Create()
    {
        IEntitlementRedemptionService service = new UnavailableEntitlementRedemptionService();
        ConfigurePrivate(ref service);
        if (service is IDisposable disposable) AppDomain.CurrentDomain.ProcessExit += (_, _) => disposable.Dispose();
        return service;
    }
    static partial void ConfigurePrivate(ref IEntitlementRedemptionService service);
}
