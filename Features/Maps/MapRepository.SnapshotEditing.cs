using System.Runtime.CompilerServices;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    // Remember provenance without retaining any snapshot/map after its owner
    // closes. Startup hands its detached snapshot to another repository instance
    // for the same catalog. Provenance follows that object, not that instance.
    // Image bindings and navigation are still verified before editing.
    private sealed record EditableSnapshotRevision(string CatalogPath, MapCatalogRevision Revision, Guid Id);
    private static readonly ConditionalWeakTable<MapRecord, EditableSnapshotRevision> EditableSnapshots = new();

    private void RememberEditableSnapshot(MapCatalogSnapshot snapshot, MapCatalogRevision revision)
    {
        if (GetCatalogRevision() != revision) return; // Migration/external replacement: read again when editing.
        foreach (var record in snapshot.Maps)
        {
            if (record.IsListProjection) continue;
            EditableSnapshots.Add(record, new(Path.GetFullPath(CatalogPath), revision, record.Id));
        }
    }

    internal Task<MapDraft?> CreateDraftFromSnapshotAsync(MapRecord record) => Task.Run(async () =>
    {
        using var operation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Catalog.EditDraft", record.Id.ToString());
        await Gate.WaitAsync();
        try
        {
            var revision = GetCatalogRevision();
            var known = EditableSnapshots.TryGetValue(record, out var source);
            var sameCatalog = known && string.Equals(source!.CatalogPath, Path.GetFullPath(CatalogPath), StringComparison.OrdinalIgnoreCase);
            if (!record.IsListProjection && sameCatalog && source!.Id == record.Id && revision == source.Revision)
            {
                await VerifyFloorImageBindingsAsync(record);
                if (GetCatalogRevision() == source.Revision)
                {
                    IDVBuff.Core.Diagnostics.MemoryJournal.Current?.Record("draft-source", new { record.Id, source = "unchanged-owned-snapshot", revision });
                    return CreateRoutePlanningDraft(record);
                }
            }
            // Unknown origin, saved/deleted map, changed class or concurrent
            // replacement must resolve the current record by id under the gate.
            IDVBuff.Core.Diagnostics.MemoryJournal.Current?.Record("draft-source", new { record.Id, source = "current-catalog",
                reason = record.IsListProjection ? "list-projection" : !known ? "unknown-origin" : !sameCatalog ? "different-catalog" : source!.Id != record.Id ? "changed-id" : "changed-revision",
                snapshotRevision = source?.Revision, currentRevision = GetCatalogRevision() });
            return await CreateDraftCoreAsync(record.Id, gateAlreadyHeld: true);
        }
        finally { Gate.Release(); }
    });
}
