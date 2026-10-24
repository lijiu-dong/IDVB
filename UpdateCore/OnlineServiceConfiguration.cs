using IDVBuff.Core.Contracts.Online;

namespace IDVBuff.UpdateCore;


public sealed class OnlineServiceConfiguration : IOnlineServiceConfiguration
{
    public static OnlineServiceConfiguration Current { get; } = new();
    public Uri Resolve(string operation, params (string Name, string Value)[] parameters) => throw Unavailable();
    public Uri ServiceRoot(string service) => throw Unavailable();
    public HttpRequestMessage CreateRequest(string operation, params (string Name, string Value)[] parameters) => throw Unavailable();
    public HttpClient CreateHttpClient(string operation, HttpMessageHandler? handler = null) => throw Unavailable();
    public bool Boolean(string section, string key) => throw Unavailable();
    public string Value(string section, string key) => throw Unavailable();
    public long Integer(string section, string key) => throw Unavailable();
    public Uri UpdateRoot => throw Unavailable();
    public bool IsKnownSubscriptionFeed(Uri uri, Guid publicationId) => false;
    private static OnlineServiceUnavailableException Unavailable() => new("configuration");
}
