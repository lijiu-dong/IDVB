using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    // Revision metadata only. No map/profile/navigation objects are cached here.
    private static readonly ConcurrentDictionary<string, MapCatalogRevision> CanonicalCatalogRevisions
        = new(StringComparer.OrdinalIgnoreCase);
    private sealed record MetadataRevisionAdvance(MapCatalogRevision From, MapCatalogRevision To);
    private static readonly ConcurrentDictionary<string, MetadataRevisionAdvance> MetadataRevisionAdvances
        = new(StringComparer.OrdinalIgnoreCase);

    internal bool IsMetadataOnlyRevisionAdvance(MapCatalogRevision from, MapCatalogRevision to) =>
        MetadataRevisionAdvances.TryGetValue(CatalogPath, out var advance) && advance.From == from && advance.To == to;

    private Task<MapCatalogDocument> ReadClassCatalogAsync(string className) => ReadSelectedCatalogAsync(className, null);

    private Task<MapCatalogDocument> ReadMapCatalogAsync(Guid id) => ReadSelectedCatalogAsync(null, id);

    internal async Task<IReadOnlyDictionary<string, MapClassProperties>> GetClassPropertiesSnapshotAsync()
    {
        await Gate.WaitAsync();
        try
        {
            var catalog = await ReadSelectedCatalogAsync(null, null);
            return catalog.ClassProperties.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        }
        finally { Gate.Release(); }
    }

    private async Task<MapCatalogDocument> ReadSelectedCatalogAsync(string? className, Guid? id)
    {
        var revision = GetCatalogRevision();
        if (!CanonicalCatalogRevisions.TryGetValue(CatalogPath, out var verified) || verified != revision)
            return await ReadCatalogAsync();

        using var memory = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation(id is not null ? "Catalog.ReadMap"
            : className is not null ? "Catalog.ReadClass" : "Catalog.ReadProperties", className ?? id?.ToString());
        MapCatalogDocument catalog;
        var previousSelection = SelectedCatalogContext.Value;
        SelectedCatalogContext.Value = new SelectedMapReader(className, id);
        try
        {
            await using var stream = File.OpenRead(CatalogPath);
            catalog = await JsonSerializer.DeserializeAsync<MapCatalogDocument>(stream, SelectedCatalogOptions.Value)
                ?? throw new InvalidDataException("地图目录为空。");
        }
        finally { SelectedCatalogContext.Value = previousSelection; }
        // Never normalize/write a partial catalog. Any format or external
        // revision change returns to the complete migration/validation path.
        if (catalog.StorageSchemaVersion != CurrentStorageSchemaVersion || GetCatalogRevision() != revision)
            return await ReadCatalogAsync();
        catalog.Maps.RemoveAll(map => map is null);
        foreach (var map in catalog.Maps)
        {
            BindNavigationStorage(map);
            map.NormalizeRecognition();
            ValidateFloorBindingsFast(map);
        }
        return catalog;
    }

    // Cache serializer metadata only. The async selection contains a class
    // name/Guid and is restored even on failed reads; no map graph is cached.
    // Lazy creation avoids depending on static field order across partial files.
    private static readonly AsyncLocal<SelectedMapReader?> SelectedCatalogContext = new();
    private static readonly Lazy<JsonSerializerOptions> SelectedCatalogOptions = new(() =>
    {
        var options = new JsonSerializerOptions(SerializerOptions);
        options.Converters.Add(new SharedSelectedMapReader());
        return options;
    });

    private sealed class SharedSelectedMapReader : JsonConverter<MapRecord>
    {
        public override MapRecord? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => (SelectedCatalogContext.Value ?? throw new InvalidOperationException("地图选择读取上下文缺失。"))
                .Read(ref reader, typeToConvert, options);

        public override void Write(Utf8JsonWriter writer, MapRecord value, JsonSerializerOptions options)
            => throw new NotSupportedException("按类读取器不能写入地图目录。");
    }

    private sealed class SelectedMapReader(string? selectedClass, Guid? selectedId) : JsonConverter<MapRecord>
    {
        public override MapRecord? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // Inspect an independent reader cursor and skip unrelated objects.
            // Even a temporary JsonDocument per map copies the complete nested
            // recognition JSON; selecting one map should not allocate those DOMs.
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("地图记录必须是对象。");
            if (selectedClass is null && selectedId is null)
            {
                if (!reader.TrySkip()) throw new JsonException("地图记录不完整。");
                return null;
            }
            var probe = reader;
            var objectDepth = reader.CurrentDepth;
            string? mapClass = null;
            Guid? mapId = null;
            while (probe.Read())
            {
                if (probe.TokenType == JsonTokenType.EndObject && probe.CurrentDepth == objectDepth) break;
                if (probe.TokenType != JsonTokenType.PropertyName || probe.CurrentDepth != objectDepth + 1)
                    throw new JsonException("地图记录属性格式错误。");
                var name = probe.GetString();
                if (!probe.Read()) throw new JsonException("地图记录属性值不完整。");
                if (string.Equals(name, "Class", StringComparison.OrdinalIgnoreCase)) mapClass = probe.GetString();
                else if (string.Equals(name, "Id", StringComparison.OrdinalIgnoreCase)) mapId = probe.GetGuid();
                if (!probe.TrySkip()) throw new JsonException("地图记录属性值不完整。");
            }
            if (selectedId is { } id ? mapId == id : string.Equals(mapClass, selectedClass, StringComparison.OrdinalIgnoreCase))
                return JsonSerializer.Deserialize<MapRecord>(ref reader, SerializerOptions);
            if (!reader.TrySkip()) throw new JsonException("地图记录不完整。");
            return null;
        }

        public override void Write(Utf8JsonWriter writer, MapRecord value, JsonSerializerOptions options)
            => throw new NotSupportedException("按类读取器不能写入地图目录。");
    }
}
