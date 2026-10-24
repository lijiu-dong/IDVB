namespace IDVBuff.UpdateCore;

public interface IOnlineServiceConfiguration
{
    Uri Resolve(string operation, params (string Name, string Value)[] parameters);
    Uri ServiceRoot(string service);
    HttpRequestMessage CreateRequest(string operation, params (string Name, string Value)[] parameters);
    HttpClient CreateHttpClient(string operation, HttpMessageHandler? handler = null);
    bool Boolean(string section, string key);
    string Value(string section, string key);
    long Integer(string section, string key);
    Uri UpdateRoot { get; }
    bool IsKnownSubscriptionFeed(Uri uri, Guid publicationId);
}
