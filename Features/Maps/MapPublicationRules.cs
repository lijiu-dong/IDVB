// Public contract and unavailable implementation. Private sources are selected by the build.
using IDVBuff.UpdateCore;

namespace IDVBuff.Features.Maps;

internal static class MapPublicationRules
{
    internal static bool CanUpdate(IReadOnlyList<MapRecord> maps,
        MapSubscriptionRecord publication, string? publisherHandle)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }
}
