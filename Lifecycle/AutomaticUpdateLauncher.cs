using IDVBuff.Core.Contracts.Online;
using IDVBuff.Services.Online;

namespace IDVBuff.Lifecycle;

internal static partial class AutomaticUpdateLauncher
{
    private static readonly Lazy<IApplicationUpdateService> Instance = new(Create);
    internal static IApplicationUpdateService Service => Instance.Value;
    public static void TryLaunch() => Service.TryLaunch();
    private static IApplicationUpdateService Create()
    {
        IApplicationUpdateService service = new UnavailableApplicationUpdateService();
        ConfigurePrivate(ref service);
        return service;
    }
    static partial void ConfigurePrivate(ref IApplicationUpdateService service);
}