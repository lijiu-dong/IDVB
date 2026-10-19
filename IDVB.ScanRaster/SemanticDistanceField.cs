using System.Runtime.CompilerServices;
using IDVBuff.Core.Collections;
using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Core.Imaging;

/// <summary>Immutable process-local semantic distance pixels. A bounded patch
/// can borrow a same-floor field without duplicating its complete directory.</summary>
public sealed partial class SemanticDistanceField
{
    private readonly CompactByteField.EncodedStorage _storage, _patch;
    private readonly SemanticDistanceField? _basis;
    private readonly int _left, _top, _patchWidth, _patchHeight;
    public int Width { get; }
    public int Height { get; }
    public long MemoryBytes { get; }

    private SemanticDistanceField(CompactByteField field)
    {
        _storage = field.GetEncodedStorage();
        Width = _storage.Width; Height = _storage.Height;
        MemoryBytes = field.MemoryBytes;
    }

    private SemanticDistanceField(SemanticDistanceField basis, CompactByteField patch, int left, int top)
    {
        _basis = basis; _storage = basis._storage; _patch = patch.GetEncodedStorage();
        Width = basis.Width; Height = basis.Height;
        _left = left; _top = top; _patchWidth = _patch.Width; _patchHeight = _patch.Height;
        MemoryBytes = basis.MemoryBytes + patch.MemoryBytes;
    }

    public static SemanticDistanceField Filled(int width, int height, byte value, string resource) =>
        new(CompactByteField.Filled(width, height, value, resource));

    // Only exact pixels from a matching caller-selected floor may be shared.
    // A patch never chains to another patch, bounding both lookup and retention.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static SemanticDistanceField Create(ReadOnlySpan<byte> pixels, int width, int height,
        string resource, SemanticDistanceField? reuse = null)
    {
        if (width <= 0 || height <= 0 || pixels.Length != checked(width * height))
            throw new ArgumentException("Invalid semantic raster dimensions.");
        if (reuse is null || reuse.Width != width || reuse.Height != height)
            return new(new CompactByteField(pixels, width, height, resource, true));
        if (pixels.IndexOfAnyExcept(pixels[0]) < 0)
            return reuse.HasSameConstantPixelsAs(pixels[0], width, height)
                ? reuse : Filled(width, height, pixels[0], resource);
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (pixels[y * width + x] == reuse.AtCompact(x, y)) continue;
            left = Math.Min(left, x); top = Math.Min(top, y);
            right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            // Dense changes do not need another retained base. Stop comparing
            // as soon as even the patch rectangle exceeds the bounded scope.
            if ((long)(right - left + 1) * (bottom - top + 1) > pixels.Length / 4)
                return new(new CompactByteField(pixels, width, height, resource, true));
        }
        if (right < 0) return reuse;
        if (reuse._basis is not null)
            return new(new CompactByteField(pixels, width, height, resource, true));
        // Encode directly from the native raster's row stride. No temporary
        // managed rectangle or full-field decompression buffer is allocated.
        var patch = CompactByteField.CreateWindow(pixels, width, height, left, top,
            right - left + 1, bottom - top + 1, resource + ".Patch");
        if (patch.MemoryBytes >= reuse.MemoryBytes)
            return new(new CompactByteField(pixels, width, height, resource, true));
        MemoryJournal.Current?.Record("managed-storage-patch", new { resource, width, height, left, top,
            patchWidth = right - left + 1, patchHeight = bottom - top + 1,
            patchBytes = patch.MemoryBytes, sharedBytes = reuse.MemoryBytes,
            meaning = "exact immutable same-floor pixels; no pose, scale or source Mat retained" });
        return new(reuse, patch, left, top);
    }

    public long SharedBytesFrom(SemanticDistanceField other) =>
        ReferenceEquals(this, other) || ReferenceEquals(_basis, other) ? other.MemoryBytes : 0;

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    public byte AtCompact(int x, int y)
    {
        if (_basis is not null && (uint)(x - _left) < _patchWidth && (uint)(y - _top) < _patchHeight)
            return Read(_patch, x - _left, y - _top);
        return Read(_storage, x, y);
    }

    public bool HasSameConstantPixelsAs(byte value, int width, int height) =>
        Width == width && Height == height && _basis is null && _storage.Constant && _storage.Data[0] == value;

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    private static byte Read(in CompactByteField.EncodedStorage storage, int x, int y)
    {
        var data = storage.Data;
        if (storage.Constant) return data[0];
        var compact = storage.CompactTiles;
        if (storage.Tiles is null && compact is null) return data[y * storage.Width + x];
        var index = (y >> 3) * storage.TileColumns + (x >> 3);
        int offset, mode;
        if (compact is not null)
        {
            var at = index * 3;
            var descriptor = (uint)(compact[at] | compact[at + 1] << 8 | compact[at + 2] << 16);
            offset = (int)(descriptor & 0xfffff); mode = (int)(descriptor >> 20);
            if (mode == 9) return (byte)offset;
            if (mode == 10) return (byte)((byte)offset + ((offset << 18) >> 26) * (x & 7) + ((offset << 12) >> 26) * (y & 7));
        }
        else
        {
            var descriptor = storage.Tiles![index];
            offset = (int)(descriptor & 0xfffffff); mode = (int)(descriptor >> 28);
            if (mode == 9) return data[offset];
        }
        var pixel = (y & 7) * 8 + (x & 7);
        if (mode == 8) return data[offset + pixel];
        var minimum = (short)(data[offset + 3] | data[offset + 4] << 8);
        var residual = 0;
        if (mode > 0)
        {
            var bit = pixel * mode; var at = offset + 5 + (bit >> 3); var packed = (int)data[at];
            if ((bit & 7) + mode > 8) packed |= data[at + 1] << 8;
            residual = (packed >> (bit & 7)) & ((1 << mode) - 1);
        }
        return (byte)(data[offset] + (sbyte)data[offset + 1] * (x & 7)
            + (sbyte)data[offset + 2] * (y & 7) + minimum + residual);
    }
}
