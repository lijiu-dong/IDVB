using System.Numerics;
using System.Runtime.CompilerServices;
using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Core.Collections;

/// <summary>Immutable row-major bits with exact, allocation-free word access.
/// Zero words occupy only one presence bit, without a decompression cache.</summary>
public sealed class CompactBitWords
{
    private readonly ulong[] _values;
    private readonly ulong[]? _presence;
    private readonly int[]? _offsets;
    public int Length { get; }
    public long MemoryBytes => 8L * _values.Length + 12L * (_presence?.Length ?? 0);
    public static CompactBitWords Empty { get; } = new(0);

    private CompactBitWords(int length) { Length = length; _values = []; }
    public static CompactBitWords Zero(int length) => length >= 0 ? new(length) : throw new ArgumentOutOfRangeException(nameof(length));

    public CompactBitWords(ReadOnlySpan<byte> raster, int width, int height, byte threshold, string resource)
    {
        if (width <= 0 || height <= 0 || raster.Length != checked(width * height))
            throw new ArgumentException("Invalid bit raster dimensions.");
        var stride = (width + 63) / 64;
        Length = checked(stride * height);
        var groups = (Length + 63) / 64;
        var request = MemoryJournal.Request(resource + ".Presence", 8L * groups);
        var presence = new ulong[groups];
        MemoryJournal.ObserveManagedArray(presence, resource + ".Presence", request);
        request = MemoryJournal.Request(resource + ".Offsets", 4L * groups);
        var offsets = new int[groups];
        MemoryJournal.ObserveManagedArray(offsets, resource + ".Offsets", request);
        var count = 0;
        for (var index = 0; index < Length; index++)
        {
            if ((index & 63) == 0) offsets[index >> 6] = count;
            if (RasterWord(raster, width, stride, index, threshold) == 0) continue;
            presence[index >> 6] |= 1UL << (index & 63);
            count++;
        }
        // Dense data retains direct indexing rather than paying both formats.
        var sparse = count * 8L + groups * 12L < Length * 8L;
        request = MemoryJournal.Request(resource + ".Words", 8L * (sparse ? count : Length));
        _values = new ulong[sparse ? count : Length];
        MemoryJournal.ObserveManagedArray(_values, resource + ".Words", request);
        if (sparse) { _presence = presence; _offsets = offsets; }
        var at = 0;
        for (var index = 0; index < Length; index++)
        {
            var word = RasterWord(raster, width, stride, index, threshold);
            if (!sparse || word != 0) _values[at++] = word;
        }
    }

    private static ulong RasterWord(ReadOnlySpan<byte> raster, int width, int stride, int index, byte threshold)
    {
        var x = (index % stride) * 64;
        var row = (index / stride) * width;
        ulong value = 0;
        var count = Math.Min(64, width - x);
        for (var bit = 0; bit < count; bit++)
            if (raster[row + x + bit] <= threshold) value |= 1UL << bit;
        return value;
    }

    public ulong this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)index >= (uint)Length) throw new IndexOutOfRangeException();
            if (_presence is null) return _values.Length == 0 ? 0 : _values[index];
            var mask = _presence[index >> 6];
            var bit = 1UL << (index & 63);
            if ((mask & bit) == 0) return 0;
            return _values[_offsets![index >> 6] + BitOperations.PopCount(mask & (bit - 1))];
        }
    }
}
