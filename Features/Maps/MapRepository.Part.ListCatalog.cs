using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    private static readonly JsonSerializerOptions ListCatalogOptions = CreateListCatalogOptions(false);
    private static readonly JsonSerializerOptions TagCatalogOptions = CreateListCatalogOptions(true);
    private static readonly JsonSerializerOptions ImageMetadataOptions = CreateListCatalogOptions(false, true);

    private static JsonSerializerOptions CreateListCatalogOptions(bool tagsOnly, bool imageMetadata = false)
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            var names = imageMetadata && type.Type == typeof(MapRecord)
                ? new[] { nameof(MapRecord.Recognition), nameof(MapRecord.PortableGates), nameof(MapRecord.Tags) }
                : tagsOnly && type.Type == typeof(MapRecord)
                ? new[] { nameof(MapRecord.Recognition), nameof(MapRecord.Floors), nameof(MapRecord.PortableGates) }
                : type.Type == typeof(FloorRecognitionProfile)
                    ? new[] { nameof(FloorRecognitionProfile.FreeCropPoints), nameof(FloorRecognitionProfile.BackgroundLayers) }
                    : [];
            foreach (var name in names)
            {
                var property = type.Properties.Single(property => property.Name == name);
                property.Get = null;
                property.Set = null;
            }
            if (!tagsOnly && !imageMetadata && type.Type == typeof(FloorRecognitionProfile))
                type.Properties.Single(property => property.Name == nameof(FloorRecognitionProfile.Annotations))
                    .CustomConverter = new ListAnnotationReader();
        });
        return new JsonSerializerOptions(SerializerOptions) { TypeInfoResolver = resolver };
    }

    internal Task<MapCatalogSnapshot> GetListCatalogSnapshotAsync() => GetCatalogProjectionAsync(false);
    internal Task<MapCatalogSnapshot> GetTagCatalogSnapshotAsync() => GetCatalogProjectionAsync(true);
    private Task<MapCatalogSnapshot> GetImageMetadataSnapshotAsync() => GetCatalogProjectionAsync(false, true);

    private Task<MapCatalogSnapshot> GetCatalogProjectionAsync(bool tagsOnly, bool imageMetadata = false) => Task.Run(async () =>
    {
        using var operation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation(imageMetadata ? "Catalog.ImageMetadataSnapshot" : tagsOnly ? "Catalog.TagSnapshot" : "Catalog.ListSnapshot", _rootDirectory);
        await Gate.WaitAsync();
        try
        {
            var revision = GetCatalogRevision();
            // Full validation/migration remains mandatory after an external edit.
            // Never normalize or commit a partially read catalog.
            if (!CanonicalCatalogRevisions.TryGetValue(CatalogPath, out var verified) || verified != revision)
                _ = await ReadCatalogAsync();
            revision = GetCatalogRevision();
            if (!File.Exists(CatalogPath)) return new MapCatalogSnapshot([], []);
            MapCatalogDocument catalog;
            await using (var stream = File.OpenRead(CatalogPath))
                catalog = await JsonSerializer.DeserializeAsync<MapCatalogDocument>(stream, imageMetadata ? ImageMetadataOptions : tagsOnly ? TagCatalogOptions : ListCatalogOptions)
                    ?? throw new InvalidDataException("地图目录为空。");
            if (catalog.StorageSchemaVersion != CurrentStorageSchemaVersion || GetCatalogRevision() != revision)
                throw new IOException("地图目录在列表读取期间发生变化，请刷新后重试。");
            foreach (var map in catalog.Maps)
            {
                map.IsListProjection = true;
                if (!tagsOnly && !imageMetadata)
                {
                    BindNavigationStorage(map);
                    map.NormalizeRecognition();
                    ValidateFloorBindingsFast(map);
                }
                DetachFreshCatalogRecord(catalog, map);
                if (!tagsOnly && !imageMetadata)
                    IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedObject(map, "Catalog.ListRecord/" + map.Id.ToString("N"));
            }
            var snapshot = new MapCatalogSnapshot(catalog.Classes.ToArray(), catalog.Maps.OrderBy(map => map.SequenceNumber).ToArray())
            {
                ClassProperties = catalog.ClassProperties.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase),
                VariantGroups = catalog.VariantGroups.Select(group => group.Clone()).ToArray()
            };
            IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedObject(snapshot.Maps, imageMetadata ? "Catalog.ImageMetadata" : tagsOnly ? "Catalog.TagMetadata" : "Catalog.ListMetadata");
            return snapshot;
        }
        finally { Gate.Release(); }
    });
}
