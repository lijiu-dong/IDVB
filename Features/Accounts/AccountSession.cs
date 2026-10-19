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
    public static Task<AccountIdentity> LoginAsync(CancellationToken cancellationToken = default) => Service.LoginAsync(cancellationToken);
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
