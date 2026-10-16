namespace IDVBuff.Core.Collections;

public sealed partial class NativeEncodedByteField
{
    /// <summary>Encode directly into the owned native allocation. No intermediate
    /// managed payload or directory is retained or queued for garbage collection.</summary>
    public static NativeEncodedByteField Encode(ReadOnlySpan<byte> source, int width, int height,
        string resource, bool preferCompactDirectory = false) =>
        CompactByteField.EncodeNative(source, width, height, resource, preferCompactDirectory, null);

    internal static NativeEncodedByteField Encode(ReadOnlySpan<byte> source, int width, int height,
        string resource, bool preferCompactDirectory, Action afterAllocation) =>
        CompactByteField.EncodeNative(source, width, height, resource, preferCompactDirectory, afterAllocation);

    internal static NativeEncodedByteField EncodeWindow(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        int left, int top, int width, int height, string resource)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || source.Length != checked(sourceWidth * sourceHeight)
            || left < 0 || top < 0 || width <= 0 || height <= 0 || left > sourceWidth - width || top > sourceHeight - height)
            throw new ArgumentException("Invalid raster window.");
        var start = checked(top * sourceWidth + left);
        var length = checked((height - 1) * sourceWidth + width);
        return CompactByteField.EncodeNative(source.Slice(start, length), width, height, resource, true, null, sourceWidth);
    }

    internal readonly record struct EncodedShape(int Width, int Height, int Columns,
        int DataLength, int TilesLength, int CompactLength, bool Constant);

    internal NativeEncodedByteField(EncodedShape shape, string resource, Action? afterAllocation)
    {
        _owner = new Storage(shape, resource, afterAllocation);
    }

    internal WritableEncoding BorrowEncodingTarget() =>
        (_owner ?? throw new ObjectDisposedException(nameof(NativeEncodedByteField))).BorrowEncodingTarget();

    internal void CompleteEncoding()
    {
        var storage = _owner ?? throw new ObjectDisposedException(nameof(NativeEncodedByteField));
        storage.ValidateWrittenEncoding();
        GC.KeepAlive(this);
    }

    internal readonly ref struct WritableEncoding
    {
        internal readonly Span<byte> Data;
        internal readonly Span<uint> Tiles;
        internal readonly Span<byte> Compact;
        internal WritableEncoding(Span<byte> data, Span<uint> tiles, Span<byte> compact)
        { Data = data; Tiles = tiles; Compact = compact; }
    }

    private static EncodedShape ValidatedShape(CompactByteField.EncodedStorage encoded)
    {
        ValidateEncoding(encoded);
        return new(encoded.Width, encoded.Height, encoded.TileColumns, encoded.Data.Length,
            encoded.Tiles?.Length ?? 0, encoded.CompactTiles?.Length ?? 0, encoded.Constant);
    }
}
