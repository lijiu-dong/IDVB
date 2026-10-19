// Public contract and unavailable implementation. Private sources are selected by the build.
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace IDVBuff.UpdateCore;

public sealed record MapSubscriptionUpdateProgress(double Fraction, string Operation);

public sealed record MapSubscriptionUpdateSummary(int Checked, int Prepared, int Failed);

public sealed class MapSubscriptionUpdateEngine
{

    public MapSubscriptionUpdateEngine(HttpClient? httpClient = null, OnlineServiceConfiguration? configuration = null, ISubscriptionSecurity? security = null) => throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services");

    public  Task<MapSubscriptionUpdateSummary> UpdateAllAsync(
        string subscriptionRoot,
        string officialPublicKeyPem,
        CancellationToken cancellationToken = default,
        Action<MapSubscriptionUpdateProgress>? progress = null)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }
}
