using System.Runtime.InteropServices;
using IDVBuff.Core.Collections;

namespace IDVBuff.Core.Imaging;

public sealed unsafe partial class NativeCompactBitWords
{
    public void WriteTo(BinaryWriter writer)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        CompactBitWords.Write(writer, Length,
            _reader is null ? [] : new ReadOnlySpan<ulong>(_values, checked((int)(MemoryBytes - _valuesOffset) / 8)),
            _sparse ? new ReadOnlySpan<ulong>(_presence, _groups) : []);
        GC.KeepAlive(this);
    }
    public static NativeCompactBitWords ReadFrom(BinaryReader reader, int length, string resource)
    {
        // Only encoded words are staged, never a full pixel raster. The managed
        // directory becomes unreachable as soon as the final native owner exists.
        var managed = CompactBitWords.ReadFrom(reader, length, resource + ".Read");
        var encoded = managed.EncodedWords;
        if (encoded.Values.Length == 0) return Zero(length);
        var sparse = encoded.Presence is not null;
        var groups = sparse ? encoded.Presence!.Length : 0;
        var offset = sparse ? checked((groups * 12 + 7) & ~7) : 0;
        var bytes = checked(offset + encoded.Values.Length * 8);
        using var owner = new NativeEncodedByteField(new NativeEncodedByteField.EncodedShape(
            bytes, 1, (bytes + 7) / 8, bytes, 0, 0, false), resource, null);
        var target = owner.BorrowEncodingTarget().Data;
        MemoryMarshal.AsBytes(encoded.Values.AsSpan()).CopyTo(target[offset..]);
        if (sparse)
        {
            MemoryMarshal.AsBytes(encoded.Presence.AsSpan()).CopyTo(target);
            MemoryMarshal.AsBytes(encoded.Offsets.AsSpan()).CopyTo(target[(groups * 8)..]);
        }
        owner.CompleteEncoding();
        var lease = owner.TryRent() ?? throw new InvalidOperationException("New native bit cache was retired.");
        try { return new(lease, length, groups, offset, sparse); } catch { lease.Dispose(); throw; }
    }
}
