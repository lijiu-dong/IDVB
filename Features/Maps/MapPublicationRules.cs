using IDVBuff.UpdateCore;

namespace IDVBuff.Features.Maps;

internal static class MapSubscriptionOperations
{
    internal static SemaphoreSlim Gate { get; } = new(1, 1);
}

internal static class MapPublicationRules
{
    internal static bool CanUpdate(IReadOnlyList<MapRecord> maps,
        MapSubscriptionRecord publication, string? publisherHandle)
    {
        if (string.IsNullOrWhiteSpace(publisherHandle)
            || !string.Equals(publication.PublisherHandle, publisherHandle, StringComparison.OrdinalIgnoreCase))
            return false;
        var subscribed = maps.Where(map => map.AcquisitionKind == MapAcquisitionKind.Subscription).ToArray();
        var registered = publication.InstalledMapIds.ToHashSet();
        // A copied SubscriptionId is not proof that these maps belong to the
        // publication. New local maps may still be added to a registered class.
        return subscribed.Length > 0 && subscribed.All(map =>
            map.SubscriptionId == publication.Id && registered.Contains(map.Id));
    }
}
