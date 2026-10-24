using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Keeps FirstFloor/SecondFloor readable from legacy catalogs while writing a
/// single canonical Floors dictionary in new catalogs.
/// </summary>
public sealed class MapRecognitionProfileJsonConverter : JsonConverter<MapRecognitionProfile>
{
    public override MapRecognitionProfile? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return new MapRecognitionProfile();
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("地图识别配置必须为对象。");
        // Keep cursors for the last occurrence of each known property. This
        // preserves JsonElement.TryGetProperty's duplicate-property behavior
        // without renting a DOM buffer for the complete drawing/profile.
        Utf8JsonReader schema = default, whole = default, first = default, second = default, floors = default;
        bool hasSchema = false, hasWhole = false, hasFirst = false, hasSecond = false, hasFloors = false;
        var ended = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) { ended = true; break; }
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("地图识别配置属性无效。");
            var property = reader.ValueTextEquals("SchemaVersion"u8) ? 1
                : reader.ValueTextEquals("WholeImage"u8) ? 2
                : reader.ValueTextEquals("FirstFloor"u8) ? 3
                : reader.ValueTextEquals("SecondFloor"u8) ? 4
                : reader.ValueTextEquals("Floors"u8) ? 5 : 0;
            if (!reader.Read()) throw new JsonException("地图识别配置属性值不完整。");
            switch (property)
            {
                case 1: schema = reader; hasSchema = true; break;
                case 2: whole = reader; hasWhole = true; break;
                case 3: first = reader; hasFirst = true; break;
                case 4: second = reader; hasSecond = true; break;
                case 5: floors = reader; hasFloors = true; break;
            }
            if (!reader.TrySkip()) throw new JsonException("地图识别配置属性值不完整。");
        }
        if (!ended) throw new JsonException("地图识别配置不完整。");
        var profile = new MapRecognitionProfile();
        if (hasSchema && schema.TryGetInt32(out var version))
            profile.SchemaVersion = version;
        if (hasWhole)
            profile.WholeImage = JsonSerializer.Deserialize<WholeImageRecognitionSettings>(ref whole, options)
                ?? new WholeImageRecognitionSettings();
        if (hasFirst)
            profile.FirstFloor = JsonSerializer.Deserialize<FloorRecognitionProfile>(ref first, options)
                ?? new FloorRecognitionProfile { Floor = MapFloor.First };
        if (hasSecond)
            profile.SecondFloor = JsonSerializer.Deserialize<FloorRecognitionProfile>(ref second, options)
                ?? new FloorRecognitionProfile { Floor = MapFloor.Second };
        if (hasFloors)
            profile.Floors = JsonSerializer.Deserialize<Dictionary<string, FloorRecognitionProfile>>(ref floors, options) ?? [];

        profile.EnsureStandardAnchors();
        return profile;
    }

    public override void Write(
        Utf8JsonWriter writer,
        MapRecognitionProfile value,
        JsonSerializerOptions options)
    {
        value.EnsureStandardAnchors();
        writer.WriteStartObject();
        writer.WriteNumber("SchemaVersion", Math.Max(8, value.SchemaVersion));
        writer.WritePropertyName("WholeImage");
        JsonSerializer.Serialize(writer, value.WholeImage, options);
        writer.WritePropertyName("Floors");
        JsonSerializer.Serialize(writer, value.Floors, options);
        writer.WriteEndObject();
    }

}
