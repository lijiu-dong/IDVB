using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IDVBuff.Core.Collections;

namespace IDVBuff.Core.Imaging;

/// <summary>Exact immutable row-major words with independently owned native readers.
/// A reader must finish its reads before disposing itself; other clones may retire.
/// Empty/zero fields are inline, and the last nonzero reader releases the allocation.</summary>
public sealed unsafe partial class NativeCompactBitWords : IDisposable
{
    private NativeEncodedByteField.ReadLease? _reader;
    private readonly ulong* _values;
    private readonly ulong* _presence;
    private readonly int* _offsets;
    private readonly int _groups, _valuesOffset;
    private readonly bool _sparse;
    private int _disposed;
    public int Length { get; }
    public long MemoryBytes { get; }

    private NativeCompactBitWords(int length) => Length = length;
    private NativeCompactBitWords(NativeEncodedByteField.ReadLease reader,
        int length, int groups, int valuesOffset, bool sparse)
    {
        var pixels = reader.BorrowPixelKernel();
        if (pixels._constant || pixels._hasTiles || pixels._hasCompact || pixels._height != 1)
            throw new InvalidOperationException("Bit words require exact contiguous storage.");
        _reader = reader; Length = length; MemoryBytes = reader.PayloadBytes;
        _groups = groups; _valuesOffset = valuesOffset; _sparse = sparse;
        _values = (ulong*)(pixels._data + valuesOffset);
        if (sparse) { _presence = (ulong*)pixels._data; _offsets = (int*)(pixels._data + groups * 8); }
    }

    public static NativeCompactBitWords Zero(int length) => length >= 0
        ? new(length) : throw new ArgumentOutOfRangeException(nameof(length));

    public static NativeCompactBitWords Create(ReadOnlySpan<byte> raster,
        int width, int height, byte threshold, string resource) =>
        Create(raster, width, height, threshold, resource, null);

    internal static NativeCompactBitWords Create(ReadOnlySpan<byte> raster,
        int width, int height, byte threshold, string resource, Action? afterAllocation)
    {
        if (width <= 0 || height <= 0 || raster.Length != checked(width * height))
            throw new ArgumentException("Invalid bit raster dimensions.");
        var stride = checked((width + 63) / 64);
        var length = checked(stride * height);
        var groups = checked((length + 63) / 64);
        var count = 0;
        for (var i = 0; i < length; i++)
            if (RasterWord(raster, width, stride, i, threshold) != 0) count++;
        if (count == 0) return Zero(length);
        var sparse = count * 8L + groups * 12L < length * 8L;
        var valuesOffset = sparse ? checked((groups * 12 + 7) & ~7) : 0;
        var bytes = checked(valuesOffset + (sparse ? count : length) * 8);
        using var owner = new NativeEncodedByteField(
            new NativeEncodedByteField.EncodedShape(bytes, 1, checked((bytes + 7) / 8), bytes, 0, 0, false), resource, afterAllocation);
        var target = owner.BorrowEncodingTarget().Data;
        var values = MemoryMarshal.Cast<byte, ulong>(target.Slice(valuesOffset));
        var presence = sparse ? MemoryMarshal.Cast<byte, ulong>(target.Slice(0, groups * 8)) : Span<ulong>.Empty;
        var offsets = sparse ? MemoryMarshal.Cast<byte, int>(target.Slice(groups * 8, groups * 4)) : Span<int>.Empty;
        var at = 0;
        for (var i = 0; i < length; i++)
        {
            if (sparse && (i & 63) == 0) offsets[i >> 6] = at;
            var word = RasterWord(raster, width, stride, i, threshold);
            if (!sparse) values[at++] = word;
            else if (word != 0) { presence[i >> 6] |= 1UL << (i & 63); values[at++] = word; }
        }
        if (at != values.Length) throw new InvalidOperationException("Bit raster changed during construction.");
        owner.CompleteEncoding();
        var reader = owner.TryRent() ?? throw new InvalidOperationException("New bit word storage retired.");
        try { return new(reader, length, groups, valuesOffset, sparse); }
        catch { reader.Dispose(); throw; }
    }

    private static ulong RasterWord(ReadOnlySpan<byte> raster, int width, int stride, int index, byte threshold)
    {
        var x = (index % stride) * 64; var row = (index / stride) * width;
        var count = Math.Min(64, width - x); ulong value = 0;
        for (var bit = 0; bit < count; bit++)
            if (raster[row + x + bit] <= threshold) value |= 1UL << bit;
        return value;
    }

    public ulong this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if ((uint)index >= (uint)Length) throw new IndexOutOfRangeException();
            var reader = _reader;
            if (reader is null) return 0;
            ulong value;
            if (!_sparse) value = _values[index];
            else
            {
                var mask = _presence[index >> 6]; var bit = 1UL << (index & 63);
                value = (mask & bit) == 0 ? 0 : _values[_offsets[index >> 6] + BitOperations.PopCount(mask & (bit - 1))];
            }
            GC.KeepAlive(reader);
            return value;
        }
    }

    // The owning reader must remain alive and undisposed for the whole batch.
    // Independent sibling/root readers may retire while this reader is borrowed.
    internal WordKernel BorrowWordKernel()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return new(_values, _presence, _offsets, _sparse);
    }

    internal readonly struct WordKernel(ulong* values, ulong* presence, int* offsets, bool sparse)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ulong At(int index)
        {
            if (values == null) return 0;
            if (!sparse) return values[index];
            var mask = presence[index >> 6]; var bit = 1UL << (index & 63);
            return (mask & bit) == 0 ? 0 : values[offsets[index >> 6] + BitOperations.PopCount(mask & (bit - 1))];
        }
    }

    public NativeCompactBitWords Clone()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (MemoryBytes == 0) return Zero(Length);
        var source = Volatile.Read(ref _reader) ?? throw new ObjectDisposedException(nameof(NativeCompactBitWords));
        var reader = source.Clone();
        try { return new(reader, Length, _groups, _valuesOffset, _sparse); }
        catch { reader.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            Interlocked.Exchange(ref _reader, null)?.Dispose();
    }
}
