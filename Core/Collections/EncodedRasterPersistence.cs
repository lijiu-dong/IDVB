using System.Runtime.InteropServices;

namespace IDVBuff.Core.Collections;

// The caller supplies dimensions from a validated source identity. Check lengths
// before allocation and every descriptor before publishing pointer-based readers.
internal static class EncodedRasterPersistence
{
    internal static void Write(BinaryWriter writer, CompactByteField.EncodedStorage storage) =>
        Write(writer, storage.Width, storage.Height, storage.Constant, storage.Data,
            storage.Tiles, storage.CompactTiles);

    internal static void Write(BinaryWriter writer, int width, int height, bool constant,
        ReadOnlySpan<byte> data, ReadOnlySpan<uint> tiles, ReadOnlySpan<byte> compact)
    {
        writer.Write(0x31574643); writer.Write(width); writer.Write(height); writer.Write(constant);
        writer.Write(data.Length); writer.Write(tiles.Length); writer.Write(compact.Length);
        writer.BaseStream.Write(data);
        writer.BaseStream.Write(MemoryMarshal.AsBytes(tiles));
        writer.BaseStream.Write(compact);
    }

    internal static NativeEncodedByteField.EncodedShape ReadShape(BinaryReader reader, int width, int height)
    {
        if (width <= 0 || height <= 0 || reader.ReadInt32() != 0x31574643
            || reader.ReadInt32() != width || reader.ReadInt32() != height)
            throw new InvalidDataException("Encoded raster identity does not match its source.");
        var constant = reader.ReadBoolean();
        var data = reader.ReadInt32(); var tiles = reader.ReadInt32(); var compact = reader.ReadInt32();
        var columns = checked((width + 7) / 8);
        var count = checked(columns * ((height + 7) / 8));
        if (data < 0 || data > checked(width * height + 64) || tiles < 0 || compact < 0
            || (tiles != 0 && tiles != count) || (compact != 0 && compact != checked(count * 3 + 1))
            || (tiles != 0 && compact != 0) || (constant && (data != 1 || tiles != 0 || compact != 0))
            || (!constant && tiles == 0 && compact == 0 && data != checked(width * height)))
            throw new InvalidDataException("Invalid encoded raster lengths.");
        var total = checked((long)data + tiles * 4L + compact);
        if (reader.BaseStream.CanSeek && total > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new EndOfStreamException();
        return new(width, height, columns, data, tiles, compact, constant);
    }

    internal static CompactByteField.EncodedStorage Read(BinaryReader reader, int width, int height)
    {
        var shape = ReadShape(reader, width, height);
        var data = new byte[shape.DataLength];
        var tiles = shape.TilesLength == 0 ? null : new uint[shape.TilesLength];
        var compact = shape.CompactLength == 0 ? null : new byte[shape.CompactLength];
        reader.BaseStream.ReadExactly(data);
        if (tiles is not null) reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(tiles.AsSpan()));
        if (compact is not null) reader.BaseStream.ReadExactly(compact);
        NativeEncodedByteField.ValidateEncoding(width, height, shape.Columns, shape.Constant,
            data, tiles, compact, tiles is not null, compact is not null);
        return new(data, tiles, compact, width, height, shape.Columns, shape.Constant);
    }
}

public sealed partial class NativeEncodedByteField
{
    public static NativeEncodedByteField ReadFrom(BinaryReader reader, int width, int height, string resource)
    {
        var shape = EncodedRasterPersistence.ReadShape(reader, width, height);
        var owner = new NativeEncodedByteField(shape, resource, null);
        try
        {
            var target = owner.BorrowEncodingTarget();
            reader.BaseStream.ReadExactly(target.Data);
            reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(target.Tiles));
            reader.BaseStream.ReadExactly(target.Compact);
            owner.CompleteEncoding();
            return owner;
        }
        catch { owner.Dispose(); throw; }
    }
}
