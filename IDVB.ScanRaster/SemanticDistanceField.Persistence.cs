using IDVBuff.Core.Collections;

namespace IDVBuff.Core.Imaging;

public sealed partial class SemanticDistanceField
{
    public void WriteTo(BinaryWriter writer, SemanticDistanceField? reuse = null)
    {
        if (ReferenceEquals(this, reuse)) { writer.Write((byte)0); return; }
        if (reuse is not null && ReferenceEquals(_basis, reuse))
        {
            writer.Write((byte)1); writer.Write(_left); writer.Write(_top);
            writer.Write(_patchWidth); writer.Write(_patchHeight);
            EncodedRasterPersistence.Write(writer, _patch); return;
        }
        // Standalone cache entries cannot retain a hidden chain of other files.
        if (_basis is not null) throw new InvalidOperationException("A semantic patch requires its same-floor base.");
        writer.Write((byte)2); EncodedRasterPersistence.Write(writer, _storage);
    }
    public static SemanticDistanceField ReadFrom(BinaryReader reader, int width, int height,
        string resource, SemanticDistanceField? reuse = null)
    {
        var mode = reader.ReadByte();
        if (mode == 0) return reuse ?? throw new InvalidDataException("Missing semantic base.");
        if (mode == 2) return new(CompactByteField.ReadFrom(reader, width, height, resource));
        if (mode != 1 || reuse is null || reuse._basis is not null) throw new InvalidDataException("Invalid semantic base.");
        var left = reader.ReadInt32(); var top = reader.ReadInt32();
        var w = reader.ReadInt32(); var h = reader.ReadInt32();
        if (left < 0 || top < 0 || w <= 0 || h <= 0 || w > width || h > height
            || left > width - w || top > height - h || (long)w * h > (long)width * height / 4)
            throw new InvalidDataException("Invalid semantic patch bounds.");
        return new(reuse, CompactByteField.ReadFrom(reader, w, h, resource + ".Patch"), left, top);
    }
}
