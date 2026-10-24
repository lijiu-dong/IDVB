using IDVBuff.UpdateCore;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    internal async Task<int> DetachUnregisteredSubscriptionCopiesAsync(
        IReadOnlyList<MapSubscriptionRecord> subscriptions, CancellationToken cancellationToken = default)
    {
        var registered = subscriptions.Where(record => record.InstalledMapIds.Count > 0)
            .ToDictionary(record => record.Id, record => record.InstalledMapIds.ToHashSet());
        if (registered.Count == 0) return 0;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var catalog = await ReadCatalogAsync();
            var copies = catalog.Maps.Where(map => map.AcquisitionKind == MapAcquisitionKind.Subscription
                && map.SubscriptionId is { } id && registered.TryGetValue(id, out var ids)
                && !ids.Contains(map.Id)).ToArray();
            if (copies.Length == 0) return 0;
            // Older class copies inherited subscription metadata but were never
            // registered. Keep all artwork/edits and detach only that identity.
            foreach (var copy in copies) copy.DetachSubscription();
            await WriteCatalogAsync(catalog);
            return copies.Length;
        }
        finally { Gate.Release(); }
    }

    internal bool IsGeneratedPublicationCover(string path)
    {
        var relative = Path.GetRelativePath(_rootDirectory, Path.GetFullPath(path));
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length == 2 && (parts[0].Equals(".appearance", StringComparison.OrdinalIgnoreCase)
            || Guid.TryParseExact(parts[0], "N", out _) && (parts[1].Contains("-thumbnail.", StringComparison.OrdinalIgnoreCase)
                || parts[1].Contains("-overlay.", StringComparison.OrdinalIgnoreCase)));
    }
}
