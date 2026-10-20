using IDVBuff.Core.Contracts.Online;
using IDVBuff.Features.Maps;
using IDVBuff.Services.Online;
using IDVBuff.Core.Contracts.Online.Security;

namespace IDVBuff.Features.Accounts;

internal static partial class AccountSession
{
    private static readonly Lazy<IAccountService> Instance = new(Create);
    internal static IAccountService Service => Instance.Value;
    public static string? PublishToken => Service.PublishToken;
    public static AccountIdentity? Identity => Service.Identity;
    public static event EventHandler? Changed { add => Service.Changed += value; remove => Service.Changed -= value; }
    /// <summary>
    /// Set by the desktop host to the native login window (1.7.1-preview.3). When absent (CLI, tests, public
    /// build) login falls back to the configured account service.
    /// </summary>
    internal static Func<CancellationToken, Task<AccountIdentity>>? InteractiveLogin { get; set; }
    public static Task<AccountIdentity> LoginAsync(CancellationToken cancellationToken = default) =>
        InteractiveLogin is { } login && DesktopLoginGateHost.Gate.IsConfigured ? login(cancellationToken) : Service.LoginAsync(cancellationToken);
    public static void Set(string token, AccountIdentity identity) => Service.Set(token, identity);
    public static void Clear() => Service.Clear();
    internal static void ClearIfCurrent(string? token) => Service.ClearIfCurrent(token);
    public static Task LogoutAsync() => Service.LogoutAsync();
    public static Task<AccountIdentity> RequirePublishAccessAsync(CancellationToken cancellationToken = default) => Service.RequirePublishAccessAsync(cancellationToken);
    public static Task RefreshAsync() => Service.RefreshAsync();
    public static Task<string> UploadPublicationAsync(MapPublicationResult publication, CancellationToken cancellationToken = default) =>
        CoCreationPublicationClient.Service.PublishAsync(new(publication.PublicationId, publication.PublisherHandle,
            publication.SubscriptionLink, publication.OutputDirectory, publication.PackagePath, publication.PackageName,
            publication.CoverPath), cancellationToken);
    public static Task UpdatePublicationMetadataAsync(MapPublicationMetadataUpdate update, CancellationToken cancellationToken = default) =>
        CoCreationPublicationClient.Service.UpdateMetadataAsync(new(update.PublicationId, update.PublisherHandle,
            update.SubscriptionLink, update.PackageName, update.CoverPath, update.FeedBytes, update.ExpectedFeedSha256), cancellationToken);
    private static IAccountService Create()
    {
        IAccountService service = new UnavailableAccountService();
        ConfigurePrivate(ref service);
        return service;
    }
    static partial void ConfigurePrivate(ref IAccountService service);
}

internal static partial class DesktopSecurityRuntimeHost
{
    private sealed record Services(IOnlineSecurityTransport Transport, IDesktopSecurityRuntime Runtime);
    private static readonly Lazy<Services> Instance = new(Create);
    internal static IOnlineSecurityTransport Transport => Instance.Value.Transport;
    internal static IDesktopSecurityRuntime Runtime => Instance.Value.Runtime;
    internal static void Stop() { if (Instance.IsValueCreated) Instance.Value.Runtime.Dispose(); }
    private static Services Create()
    {
        IOnlineSecurityTransport transport = new UnavailableOnlineSecurityTransport();
        IDesktopSecurityRuntime runtime = new UnavailableDesktopSecurityRuntime();
        ConfigurePrivate(ref transport, ref runtime);
        var services = new Services(transport, runtime);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => services.Runtime.Dispose();
        return services;
    }
    static partial void ConfigurePrivate(ref IOnlineSecurityTransport transport, ref IDesktopSecurityRuntime runtime);
}

/// <summary>Native desktop login gate; the public build has none and starts without it.</summary>
internal static partial class DesktopLoginGateHost
{
    private static readonly Lazy<IDesktopLoginGate> Instance = new(Create);
    internal static IDesktopLoginGate Gate => Instance.Value;
    private static IDesktopLoginGate Create()
    {
        IDesktopLoginGate gate = new UnavailableDesktopLoginGate();
        ConfigurePrivate(ref gate);
        return gate;
    }
    static partial void ConfigurePrivate(ref IDesktopLoginGate gate);
}

/// <summary>Optional resource capability. Public builds explicitly report unavailable.</summary>
internal static partial class DesktopResourceUnlockHost
{
    private static readonly Lazy<IDesktopResourceUnlock> Instance = new(Create);
    internal static IDesktopResourceUnlock Service => Instance.Value;
    private static IDesktopResourceUnlock Create()
    {
        IDesktopResourceUnlock service = new UnavailableDesktopResourceUnlock();
        ConfigurePrivate(ref service);
        if (service is IDisposable disposable) AppDomain.CurrentDomain.ProcessExit += (_, _) => disposable.Dispose();
        return service;
    }
    static partial void ConfigurePrivate(ref IDesktopResourceUnlock service);
}
