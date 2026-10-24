using System.Numerics;
using System.Runtime.InteropServices;
using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Core.Collections;

public sealed partial class CompactBitWords
{
    internal (ulong[] Values, ulong[]? Presence, int[]? Offsets) EncodedWords => (_values, _presence, _offsets);
    private CompactBitWords(int length, ulong[] values, ulong[]? presence, int[]? offsets, string resource)
    {
        Length = length; _values = values; _presence = presence; _offsets = offsets;
        MemoryJournal.ObserveManagedArray(values, resource + ".Words");
        if (presence is not null) MemoryJournal.ObserveManagedArray(presence, resource + ".Presence");
        if (offsets is not null) MemoryJournal.ObserveManagedArray(offsets, resource + ".Offsets");
    }
    public void WriteTo(BinaryWriter writer) => Write(writer, Length, _values, _presence);
    internal static void Write(BinaryWriter writer, int length, ReadOnlySpan<ulong> values, ReadOnlySpan<ulong> presence)
    {
        writer.Write(length); writer.Write(values.Length); writer.Write(presence.Length);
        writer.BaseStream.Write(MemoryMarshal.AsBytes(values));
        writer.BaseStream.Write(MemoryMarshal.AsBytes(presence));
    }
    public static CompactBitWords ReadFrom(BinaryReader reader, int length, string resource)
    {
        if (reader.ReadInt32() != length || length < 0) throw new InvalidDataException("Bit word dimensions differ.");
        var count = reader.ReadInt32(); var groups = reader.ReadInt32();
        if (count < 0 || count > length || groups < 0 || (groups != 0 && groups != (length + 63) / 64)
            || (groups == 0 && count != 0 && count != length)) throw new InvalidDataException("Invalid bit word lengths.");
        var values = new ulong[count];
        var presence = groups == 0 ? null : new ulong[groups];
        var offsets = groups == 0 ? null : new int[groups];
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
        if (presence is not null)
        {
            reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(presence.AsSpan()));
            var total = 0;
            for (var i = 0; i < groups; i++)
            {
                offsets![i] = total; total = checked(total + BitOperations.PopCount(presence[i]));
            }
            if (total != count || ((length & 63) != 0 && (presence[^1] >> (length & 63)) != 0))
                throw new InvalidDataException("Invalid sparse bit word directory.");
        }
        return new(length, values, presence, offsets, resource);
    }
}
