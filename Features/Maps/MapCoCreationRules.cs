using System.Text.Json;
using IDVBuff.UpdateCore;

namespace IDVBuff.Features.Maps;

public static class MapCoCreationRules
{
    public static MapDraft CreateEditingDraft(MapDraft source)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }

    public static bool NeedsCloudPackage(MapCoCreationOwnedPublication? installed) => throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services");

    public static MapRecord? ResolveSavedMap(IEnumerable<MapRecord> maps, MapSubscriptionRecord publication, Guid? sourceMapId)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }

    public static IReadOnlyList<MapCoCreationOwnedPublication> GetOwnedPublications(
        IEnumerable<MapRecord> maps, IEnumerable<MapSubscriptionRecord> publications, string? publisherHandle)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }
    public static IReadOnlyList<MapCoCreationOwnedClass> GetOwnedClasses(
        IEnumerable<MapRecord> maps, IEnumerable<MapSubscriptionRecord> publications, string? publisherHandle) => throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services");
}
