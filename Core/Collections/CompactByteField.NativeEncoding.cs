namespace IDVBuff.Core.Collections;

public sealed partial class CompactByteField
{
    // Planning shares the managed codec's exact tile reader, signature/dedup,
    // affine fit and residual plan. Only its destination is different. Keep the
    // managed constructor as the differential oracle during this opt-in stage.
    private CompactByteField(int width, int height)
    {
        _width = width; _height = height; _tileColumns = (width + 7) / 8;
        _data = [];
    }

    internal static NativeEncodedByteField EncodeNative(ReadOnlySpan<byte> source, int width, int height,
        string resource, bool preferCompactDirectory, Action? afterAllocation, int sourceStride = 0)
    {
        if (sourceStride == 0) sourceStride = width;
        if (width <= 0 || height <= 0 || sourceStride < width || source.Length != checked((height - 1) * sourceStride + width))
            throw new ArgumentException("Invalid raster dimensions.");
        return new CompactByteField(width, height).EncodeNativeCore(source, resource, preferCompactDirectory, afterAllocation, sourceStride);
    }

    private NativeEncodedByteField EncodeNativeCore(ReadOnlySpan<byte> source, string resource,
        bool preferCompactDirectory, Action? afterAllocation, int sourceStride)
    {
        var width = _width; var height = _height;
        var packedLength = checked(width * height);
        var constant = true;
        if (sourceStride == width) constant = source.IndexOfAnyExcept(source[0]) < 0;
        else for (var y = 0; y < height && constant; y++)
            constant = source.Slice(y * sourceStride, width).IndexOfAnyExcept(source[0]) < 0;
        if (constant)
        {
            var filled = new NativeEncodedByteField(new NativeEncodedByteField.EncodedShape(width, height, _tileColumns, 1, 0, 0, true), resource, afterAllocation);
            try { filled.BorrowEncodingTarget().Data[0] = source[0]; filled.CompleteEncoding(); return filled; }
            catch { filled.Dispose(); throw; }
        }
        var count = checked(_tileColumns * ((height + 7) / 8));
        Span<byte> tile = stackalloc byte[64];
        Span<byte> previous = stackalloc byte[64];
        using var metadata = TileDedupWorkspace.Rent(resource);
        var unique = metadata.Workspace.Dictionary;
        long length = 0, compactLength = 0;
        for (var i = 0; i < count; i++)
        {
            ReadTile(source, width, height, i, tile, sourceStride);
            if (FindTile(unique, source, width, height, tile, previous, out var signature, sourceStride) >= 0) continue;
            if (unique.Count < 32768) metadata.Workspace.Add(signature, i);
            var plan = Plan(tile); length += plan.Length;
            if (preferCompactDirectory && !CanInline(plan)) compactLength += plan.Length;
        }
        var raw = length + 4L * count >= packedLength || length >= 1 << 28;
        var compact = !raw && preferCompactDirectory && BitConverter.IsLittleEndian
            && compactLength < 1 << 20 && length <= unique.Count * 32L;
        var dataLength = raw ? packedLength : checked((int)(compact ? compactLength : length));
        var owner = new NativeEncodedByteField(new NativeEncodedByteField.EncodedShape(width, height, _tileColumns, dataLength,
            raw || compact ? 0 : count, compact ? checked(count * 3 + 1) : 0, false), resource, afterAllocation);
        try
        {
            var target = owner.BorrowEncodingTarget();
            if (raw)
            {
                if (sourceStride == width) source.CopyTo(target.Data);
                else for (var y = 0; y < height; y++)
                    source.Slice(y * sourceStride, width).CopyTo(target.Data.Slice(y * width, width));
            }
            else WriteNativeTiles(source, target, compact, count, unique, tile, previous, sourceStride);
            owner.CompleteEncoding();
            return owner;
        }
        catch { owner.Dispose(); throw; }
    }

    private unsafe void WriteNativeTiles(ReadOnlySpan<byte> source, NativeEncodedByteField.WritableEncoding target,
        bool compact, int count, Dictionary<ulong, int> unique, Span<byte> tile, Span<byte> previous, int sourceStride)
    {
        // Layout lengths and every emitted tile are checked before raw writes.
        // The new owner is private to this constructor until CompleteEncoding.
        if (compact ? target.Compact.Length != checked(count * 3 + 1) : target.Tiles.Length != count)
            throw new InvalidOperationException("Native directory length does not match the tile plan.");
        var width = _width; var height = _height; var dataLength = target.Data.Length;
        fixed (byte* data = target.Data)
        fixed (uint* tiles = target.Tiles)
        fixed (byte* directory = target.Compact)
        {
            var offset = 0;
            for (var i = 0; i < count; i++)
            {
                ReadTile(source, width, height, i, tile, sourceStride);
                var shared = FindTile(unique, source, width, height, tile, previous, out _, sourceStride);
                if (shared >= 0 && shared < i)
                {
                    if (!compact) tiles[i] = tiles[shared];
                    else WriteNativeDescriptor(directory, i, ReadNativeDescriptor(directory, shared));
                    continue;
                }
                var plan = Plan(tile);
                var mode = plan.Mode; var dx = plan.Dx; var dy = plan.Dy;
                var minimum = plan.Minimum; var length = plan.Length; var first = tile[0];
                if (compact && mode == 9)
                { WriteNativeDescriptor(directory, i, first | (9u << 20)); continue; }
                if (compact && CanInline(plan))
                {
                    WriteNativeDescriptor(directory, i, first | ((uint)(dx & 63) << 8)
                        | ((uint)(dy & 63) << 14) | (10u << 20));
                    continue;
                }
                if (offset > dataLength - length)
                    throw new InvalidOperationException("Native tile exceeds the planned payload.");
                if (!compact) tiles[i] = (uint)offset | ((uint)mode << 28);
                else WriteNativeDescriptor(directory, i, (uint)offset | ((uint)mode << 20));
                if (mode == 9) data[offset] = first;
                else if (mode == 8) tile.CopyTo(new Span<byte>(data + offset, 64));
                else
                {
                    data[offset] = first; data[offset + 1] = unchecked((byte)dx);
                    data[offset + 2] = unchecked((byte)dy);
                    data[offset + 3] = unchecked((byte)minimum); data[offset + 4] = unchecked((byte)(minimum >> 8));
                    for (var p = 0; p < 64 && mode > 0; p++)
                    {
                        var residual = tile[p] - first - dx * (p & 7) - dy * (p >> 3) - minimum;
                        var bit = p * mode; var packed = residual << (bit & 7);
                        data[offset + 5 + (bit >> 3)] |= (byte)packed;
                        if ((bit & 7) + mode > 8) data[offset + 6 + (bit >> 3)] |= (byte)(packed >> 8);
                    }
                }
                offset += length;
            }
            if (offset != dataLength) throw new InvalidOperationException("Native payload size changed during encoding.");
        }
    }
    private static unsafe uint ReadNativeDescriptor(byte* directory, int index)
    {
        var at = index * 3;
        return (uint)(directory[at] | directory[at + 1] << 8 | directory[at + 2] << 16);
    }
    private static unsafe void WriteNativeDescriptor(byte* directory, int index, uint descriptor)
    {
        var at = index * 3; directory[at] = (byte)descriptor;
        directory[at + 1] = (byte)(descriptor >> 8); directory[at + 2] = (byte)(descriptor >> 16);
    }
}
