using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    // The list consumes only Any(IsValid), via MapRouteRules. Keep one actual
    // normalized valid annotation per floor; skip the remaining drawing JSON.
    // Editing, copy and export read complete current records separately.
    private sealed class ListAnnotationReader : JsonConverter<List<MapAnnotation>>
    {
        public override List<MapAnnotation> Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("地图标注必须为数组。");
            var selected = new List<MapAnnotation>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) return selected;
                if (selected.Count != 0)
                {
                    if (!reader.TrySkip()) throw new JsonException("地图标注不完整。");
                    continue;
                }
                var annotation = JsonSerializer.Deserialize<MapAnnotation>(ref reader, SerializerOptions)
                    ?? throw new JsonException("地图标注不能为 null。");
                // Validity depends on legacy color normalization, exactly as
                // in the full catalog. Do not invent a capability sentinel.
                MapRecognitionProfile.NormalizeAnnotationColor(annotation);
                if (annotation.IsValid) selected.Add(annotation);
            }
            throw new JsonException("地图标注数组不完整。");
        }

        public override void Write(Utf8JsonWriter writer, List<MapAnnotation> value,
            JsonSerializerOptions options) => throw new NotSupportedException("列表标注投影不能写入地图目录。");
    }
}
