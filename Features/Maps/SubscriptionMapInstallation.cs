// Public contract and unavailable implementation. Private sources are selected by the build.
using IDVBuff.UpdateCore;

namespace IDVBuff.Features.Maps;

/// <summary>Local IDVM installation port; it performs no network or subscription-feed processing.</summary>
public sealed class SubscriptionMapInstallation : ISubscriptionMapInstallation
{
    public SubscriptionMapInstallation(MapRepository repository) { }
    public Task DetachUnregisteredCopiesAsync(IReadOnlyList<MapSubscriptionRecord> records, CancellationToken cancellationToken) => throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services");

    public  Task<IReadOnlySet<Guid>> GetInstalledMapIdsAsync() => throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services");

    public  Task<MapSubscriptionPromotionResult> ImportAsync(MapSubscriptionRecord record,
        MapPublicationPayload publication, string packagePath, CancellationToken cancellationToken)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }
}
