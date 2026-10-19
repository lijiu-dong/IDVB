using System.Collections.Concurrent;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    private sealed record ScanFloorAdvance(MapCatalogRevision From, MapCatalogRevision To,
        string ClassName, string? FloorKey);
    private static readonly ConcurrentDictionary<string, ScanFloorAdvance> ScanFloorAdvances
        = new(StringComparer.OrdinalIgnoreCase);

    internal bool TryGetScanFloorAdvance(MapCatalogRevision from, MapCatalogRevision to,
        out string className, out string? floorKey)
    {
        if (ScanFloorAdvances.TryGetValue(CatalogPath, out var advance)
            && advance.From == from && advance.To == to && GetCatalogRevision() == to)
        {
            className = advance.ClassName;
            floorKey = advance.FloorKey;
            return true;
        }
        className = string.Empty;
        floorKey = null;
        return false;
    }

    // The caller holds Gate and has validated a canonical, selected-class read.
    // Copy unchanged JSON bytes instead of constructing every unrelated map.
    private async Task WriteScanFloorPropertiesAsync(MapCatalogDocument selected,
        MapCatalogRevision revision, string className, string? floorKey, CancellationToken cancellation)
    {
        using var memory = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Catalog.WriteScanFloor", className);
        var temporary = $"{CatalogPath}.scan-floor-{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var source = File.OpenRead(CatalogPath))
            {
                var range = await Task.Run(() => FindClassPropertiesRange(source), cancellation);
                if (GetCatalogRevision() != revision)
                    throw new IOException("地图目录在修改期间发生变化，请刷新后重试。");
                source.Position = 0;
                await using var target = File.Create(temporary);
                var buffer = new byte[64 * 1024];
                var remaining = range.Start;
                while (remaining > 0)
                {
                    var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellation);
                    if (count == 0) throw new EndOfStreamException("地图目录不完整。");
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellation);
                    remaining -= count;
                }
                await JsonSerializer.SerializeAsync(target, selected.ClassProperties, CatalogWriteOptions, cancellation);
                source.Position = range.End;
                await source.CopyToAsync(target, 64 * 1024, cancellation);
                await target.FlushAsync(cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            if (GetCatalogRevision() != revision)
                throw new IOException("地图目录在修改期间发生变化，请刷新后重试。");
            var committedRevision = await CommitCatalogReplacementAsync(temporary);
            MetadataRevisionAdvances.TryRemove(CatalogPath, out _);
            ScanFloorAdvances.TryRemove(CatalogPath, out _);
            if (committedRevision is { } written)
            {
                ClassNamesCache[CatalogPath] = (written, selected.Classes.ToArray());
                AdvanceMapCount(revision, written);
                CanonicalCatalogRevisions[CatalogPath] = written;
                ScanFloorAdvances[CatalogPath] = new(revision, written, className, floorKey);
            }
        }
        finally
        {
            // Only our own unfinished staging file; the original stays intact.
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static (long Start, long End) FindClassPropertiesRange(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        stream.ReadExactly(buffer.AsSpan(0, 3));
        var hasBom = buffer[0] == 0xef && buffer[1] == 0xbb && buffer[2] == 0xbf;
        var buffered = hasBom ? 0 : 3;
        long offset = hasBom ? 3 : 0, start = -1, end = -1;
        var state = new JsonReaderState();
        var awaitingValue = false;
        var propertyCount = 0;
        while (true)
        {
            if (buffered == buffer.Length) Array.Resize(ref buffer, checked(buffer.Length * 2));
            var count = stream.Read(buffer, buffered, buffer.Length - buffered);
            var final = count == 0;
            var length = buffered + count;
            var reader = new Utf8JsonReader(buffer.AsSpan(0, length), final, state);
            while (reader.Read())
            {
                if (awaitingValue)
                {
                    if (reader.TokenType != JsonTokenType.StartObject)
                        throw new InvalidDataException("地图类属性必须是对象。");
                    start = offset + reader.TokenStartIndex;
                    awaitingValue = false;
                }
                if (start >= 0 && end < 0 && reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 1)
                    end = offset + reader.BytesConsumed;
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1
                    && string.Equals(reader.GetString(), "ClassProperties", StringComparison.OrdinalIgnoreCase))
                {
                    propertyCount++;
                    awaitingValue = true;
                }
            }
            var consumed = checked((int)reader.BytesConsumed);
            state = reader.CurrentState;
            offset += consumed;
            buffered = length - consumed;
            buffer.AsSpan(consumed, buffered).CopyTo(buffer);
            if (final) break;
        }
        if (propertyCount != 1 || start < 0 || end <= start)
            throw new InvalidDataException("地图目录缺少唯一的地图类属性对象。");
        return (start, end);
    }
}
