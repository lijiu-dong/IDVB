using System.Collections.Concurrent;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    // Counts belong to the same validated file revision as the catalog. Keep
    // only scalar metadata; a home-page metric needs no detached map graph.
    private static readonly ConcurrentDictionary<string, (MapCatalogRevision Revision, int Count)>
        MapCounts = new(StringComparer.OrdinalIgnoreCase);

    internal Task<int> GetMapCountAsync() => Task.Run(async () =>
    {
        using var memory = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Catalog.Count", _rootDirectory);
        await Gate.WaitAsync();
        try
        {
            var revision = GetCatalogRevision();
            if (MapCounts.TryGetValue(CatalogPath, out var cached) && cached.Revision == revision)
                return cached.Count;
            // Unknown or externally edited storage still takes the complete
            // validation/migration path, without cloning records for a caller.
            return (await ReadCatalogAsync()).Maps.Count;
        }
        finally { Gate.Release(); }
    });

    private void RememberMapCount(MapCatalogRevision revision, int count) =>
        MapCounts[CatalogPath] = (revision, count);

    private void AdvanceMapCount(MapCatalogRevision from, MapCatalogRevision to)
    {
        if (MapCounts.TryGetValue(CatalogPath, out var cached) && cached.Revision == from)
            MapCounts[CatalogPath] = (to, cached.Count);
    }
}
