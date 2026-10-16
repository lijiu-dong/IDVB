using System.Text.Json;

namespace IDVBuff.Features.Maps;

public static partial class MapNavigationRules
{
    private static void CheckShape(byte[] bytes)
    {
        // Validate unknown properties too, without a second complete DOM.
        // Sibling objects reuse a set at their own depth; property names are
        // shared within this read and never retained in a global cache.
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 32 });
        var names = new HashSet<string>?[33];
        var arrays = new bool[33];
        var counts = new int[33];
        var cache = new Dictionary<ulong, List<string>>();
        var cachedNames = 0;
        while (reader.Read())
        {
            var depth = reader.CurrentDepth;
            if (depth > 0 && arrays[depth - 1]
                && reader.TokenType is not (JsonTokenType.EndObject or JsonTokenType.EndArray))
                if (++counts[depth - 1] > 100000) Fail("导航数组过长。");
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                arrays[depth] = false;
                (names[depth] ??= new HashSet<string>(StringComparer.Ordinal)).Clear();
            }
            else if (reader.TokenType == JsonTokenType.StartArray)
            {
                arrays[depth] = true;
                counts[depth] = 0;
            }
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                string? name = null;
                if (!reader.ValueIsEscaped)
                {
                    ulong hash = 14695981039346656037;
                    foreach (var value in reader.ValueSpan)
                        hash = unchecked((hash ^ value) * 1099511628211);
                    cache.TryGetValue(hash, out var bucket);
                    // Always compare names after hashing: collisions cannot
                    // weaken duplicate checks or merge distinct properties.
                    if (bucket is not null)
                        foreach (var previous in bucket)
                            if (reader.ValueTextEquals(previous)) { name = previous; break; }
                    if (name is null)
                    {
                        name = reader.GetString()!;
                        // Unknown wide objects keep the full duplicate check,
                        // without also growing an unbounded interning table.
                        if (cachedNames < 256)
                        {
                            if (bucket is null) cache[hash] = bucket = [];
                            bucket.Add(name);
                            cachedNames++;
                        }
                    }
                }
                else name = reader.GetString()!;
                if (!names[depth - 1]!.Add(name)) Fail("导航 JSON 含重复字段。");
            }
        }
    }
}
