using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace IDVBuff.Features.Maps;

/// <summary>Exact tile encoding for caller-owned clipped float planes.
/// The caller retains all storage and the source for these synchronous calls.</summary>
public static class ClippedFloatTileEncoder
{
    private const int TileSize = 16;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int BuildIndex(ReadOnlySpan<float> pixels, Span<int> index,
        Span<int> directory, int imageWidth, int imageHeight, float constant)
    {
        var columns = (imageWidth + TileSize - 1) / TileSize;
        var rows = (imageHeight + TileSize - 1) / TileSize;
        var maximumUnique = Math.Min(columns * rows, 32768);
        var capacity = directory.Length / 2;
        var count = 0;
        var uniqueCount = 0;

        for (var ty = 0; ty < rows; ty++)
        for (var tx = 0; tx < columns; tx++)
        {
            var x = tx * TileSize;
            var y = ty * TileSize;
            var width = Math.Min(TileSize, imageWidth - x);
            var height = Math.Min(TileSize, imageHeight - y);
            var same = true;
            for (var row = 0; row < height && same; row++)
                same = pixels.Slice((y + row) * imageWidth + x, width).IndexOfAnyExcept(constant) < 0;
            
            var tileId = ty * columns + tx;
            if (same) { index[tileId] = -1; continue; }
            var signature = new HashCode(); signature.Add(width); signature.Add(height);
            for (var row = 0; row < height; row++)
                signature.AddBytes(MemoryMarshal.AsBytes(pixels.Slice((y + row) * imageWidth + x, width)));
            var hash = signature.ToHashCode();
            var slot = (hash & int.MaxValue) & (capacity - 1);
            var shared = -1;
            while (directory[slot * 2 + 1] != 0)
            {
                var first = directory[slot * 2 + 1] - 1;
                if (directory[slot * 2] == hash)
                {
                    var px = first % columns * TileSize; var py = first / columns * TileSize;
                    var equal = Math.Min(TileSize, imageWidth - px) == width
                        && Math.Min(TileSize, imageHeight - py) == height;
                    for (var row = 0; row < height && equal; row++)
                        equal = MemoryMarshal.AsBytes(pixels.Slice((y + row) * imageWidth + x, width)).SequenceEqual(
                            MemoryMarshal.AsBytes(pixels.Slice((py + row) * imageWidth + px, width)));
                    if (equal) { shared = first; break; }
                }
                slot = (slot + 1) & (capacity - 1);
            }
            if (shared >= 0) { index[tileId] = -2 - shared; continue; }
            if (uniqueCount < maximumUnique)
            {
                directory[slot * 2] = hash;
                directory[slot * 2 + 1] = tileId + 1;
                uniqueCount++;
            }
            index[tileId] = count;

            if (!same) count = checked(count + width * height);
        }
        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void CopyPayload(ReadOnlySpan<float> pixels, Span<int> index,
        Span<float> target, int imageWidth, int imageHeight)
    {
        var columns = (imageWidth + TileSize - 1) / TileSize;
        var rows = (imageHeight + TileSize - 1) / TileSize;
        for (var ty = 0; ty < rows; ty++)
        for (var tx = 0; tx < columns; tx++)
        {
            var offset = index[ty * columns + tx];
            if (offset <= -2) { index[ty * columns + tx] = index[-offset - 2]; continue; }
            if (offset < 0) continue;
            var x = tx * TileSize;
            var y = ty * TileSize;
            var width = Math.Min(TileSize, imageWidth - x);
            var height = Math.Min(TileSize, imageHeight - y);
            for (var row = 0; row < height; row++)
                pixels.Slice((y + row) * imageWidth + x, width).CopyTo(target.Slice(offset + row * width, width));
        }
    }
}
