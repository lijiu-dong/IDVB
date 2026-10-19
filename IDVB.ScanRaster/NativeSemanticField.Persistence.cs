using IDVBuff.Core.Collections;

namespace IDVBuff.Core.Imaging;

public sealed unsafe partial class NativeSemanticField
{
    public void WriteTo(BinaryWriter writer, NativeSemanticField? reuse = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (reuse is not null && ReferenceEquals(_identity, reuse._identity)) { writer.Write((byte)0); return; }
        if (_basis is not null)
        {
            if (reuse is null || !ReferenceEquals(_basis._identity, reuse._identity))
                throw new InvalidOperationException("A native semantic patch requires its same-floor base.");
            writer.Write((byte)1); writer.Write(_left); writer.Write(_top);
            writer.Write(_patch!.Width); writer.Write(_patch.Height); _patch.WriteTo(writer); return;
        }
        writer.Write((byte)2); writer.Write(_constant);
        if (_constant) writer.Write(_value); else _reader!.WriteTo(writer);
    }
    public static NativeSemanticField ReadFrom(BinaryReader reader, int width, int height,
        string resource, NativeSemanticField? reuse = null)
    {
        var mode = reader.ReadByte();
        if (mode == 0) return reuse?.Clone() ?? throw new InvalidDataException("Missing native semantic base.");
        if (mode == 2)
        {
            if (reader.ReadBoolean()) return Filled(width, height, reader.ReadByte());
            using var owner = NativeEncodedByteField.ReadFrom(reader, width, height, resource);
            var lease = owner.TryRent() ?? throw new InvalidOperationException("New native cache reader was retired.");
            try { return new(lease); } catch { lease.Dispose(); throw; }
        }
        if (mode != 1 || reuse is null || reuse._basis is not null) throw new InvalidDataException("Invalid native semantic base.");
        var left = reader.ReadInt32(); var top = reader.ReadInt32();
        var w = reader.ReadInt32(); var h = reader.ReadInt32();
        if (left < 0 || top < 0 || w <= 0 || h <= 0 || w > width || h > height
            || left > width - w || top > height - h || (long)w * h > (long)width * height / 4)
            throw new InvalidDataException("Invalid native semantic patch bounds.");
        var patch = ReadFrom(reader, w, h, resource + ".Patch");
        NativeSemanticField? basis = null;
        try { basis = reuse.Clone(); return new(basis, patch, left, top, null); }
        catch { basis?.Dispose(); patch.Dispose(); throw; }
    }
}
