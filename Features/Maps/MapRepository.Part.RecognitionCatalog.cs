using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    private static readonly JsonSerializerOptions RecognitionAssetOptions = CreateRecognitionAssetOptions();

    private static JsonSerializerOptions CreateRecognitionAssetOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            if (type.Type != typeof(FloorRecognitionProfile)) return;
            var annotations = type.Properties.Single(property => property.Name == nameof(FloorRecognitionProfile.Annotations));
            annotations.Get = null;
            annotations.Set = null;
        });
        return new JsonSerializerOptions(SerializerOptions) { TypeInfoResolver = resolver };
    }

    // Annotation drawings are consumed by the selected match/editor, not by
    // derived image repair or catalog fingerprinting. Keep every crop stroke,
    // background layer, anchor and file binding needed for those operations.
    // A partial record cannot be committed or returned as an editable map.
    internal Task<MapCatalogSnapshot> GetRecognitionCatalogSnapshotAsync(string? residentClass, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        using var operation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Catalog.RecognitionSnapshot", residentClass);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = GetCatalogRevision();
            MapCatalogDocument catalog;
            // A running match keeps the former full read path. It must neither
            // probe/decode each record twice nor lose current-class drawings.
            if (residentClass is not null || !CanonicalCatalogRevisions.TryGetValue(CatalogPath, out var verified) || verified != revision)
                catalog = await ReadCatalogAsync();
            else
            {
                using var read = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Catalog.ReadRecognition", residentClass);
                await using (var stream = File.OpenRead(CatalogPath))
                    catalog = await JsonSerializer.DeserializeAsync<MapCatalogDocument>(stream, RecognitionAssetOptions, cancellationToken)
                        ?? throw new InvalidDataException("地图目录为空。");
                // A partial graph must never normalize/write an external edit.
                if (catalog.StorageSchemaVersion != CurrentStorageSchemaVersion || GetCatalogRevision() != revision)
                    catalog = await ReadCatalogAsync();
                else
                    foreach (var map in catalog.Maps)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        BindNavigationStorage(map);
                        map.NormalizeRecognition();
                        ValidateFloorBindingsFast(map);
                    }
            }
            foreach (var map in catalog.Maps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(map.Class, residentClass, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var profile in map.Recognition.Floors.Values.Distinct())
                        if (profile.Annotations.Count != 0) profile.Annotations = [];
                    map.IsListProjection = true;
                }
                DetachFreshCatalogRecord(catalog, map);
            }
            var snapshot = new MapCatalogSnapshot(catalog.Classes.ToArray(), catalog.Maps.OrderBy(map => map.SequenceNumber).ToArray())
            {
                ClassProperties = catalog.ClassProperties.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase),
                VariantGroups = catalog.VariantGroups.Select(group => group.Clone()).ToArray()
            };
            RememberEditableSnapshot(snapshot, revision);
            IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedObject(snapshot.Maps, "Catalog.RecognitionMaps");
            return snapshot;
        }
        finally { Gate.Release(); }
    });

}
