using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using IDVBuff.Core.Collections;
using IDVBuff.Core.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class Vpsg3PreparedFloor
{
    internal void WriteTo(BinaryWriter writer)
    {
        writer.Write(EdgePixelCount); writer.Write(_deriveGeometryK3); writer.Write(_deriveGeometryK5);
        writer.Write(ScalePrior.SeedScale); writer.Write(ScalePrior.PeakRatio); writer.Write(ScalePrior.FastPathEligible);
        writer.Write(ScalePrior.ReferencePitch); writer.Write(ScalePrior.ReferencePeakRatio);
        var reason = Encoding.UTF8.GetBytes(ScalePrior.RejectReason);
        writer.Write(reason.Length); writer.BaseStream.Write(reason);
        // Derived K3/K5 remain lazy, even if a caller already materialized them.
        WriteWords(writer, _deriveGeometryK5 ? [] : _dilatedBitsetK5!);
        WriteWords(writer, _deriveGeometryK3 ? [] : _dilatedBitsetK3!);
        writer.Write(SemanticIndex is not null); SemanticIndex?.WriteTo(writer);
        writer.Write(ReferenceSeeds is not null); ReferenceSeeds?.WriteTo(writer);
    }
    private static void WriteWords(BinaryWriter writer, ReadOnlySpan<ulong> words)
    {
        writer.Write(words.Length); writer.BaseStream.Write(MemoryMarshal.AsBytes(words));
    }
    private static ulong[] ReadWords(BinaryReader reader, int expected)
    {
        if (reader.ReadInt32() != expected) throw new InvalidDataException("Cached geometry word dimensions differ.");
        var words = new ulong[expected]; reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(words.AsSpan()));
        if (words.Length > 0) MemoryJournal.ObserveManagedArray(words, "VPSG3.Cache.Geometry");
        return words;
    }
    internal static Vpsg3PreparedFloor ReadFrom(BinaryReader reader, Vpsg3IndexCacheKey key, int width, int height, bool nativeSemantic)
    {
        var edgeCount = reader.ReadInt32(); var derive3 = reader.ReadBoolean(); var derive5 = reader.ReadBoolean();
        var scale = reader.ReadDouble(); var ratio = reader.ReadDouble(); var fast = reader.ReadBoolean();
        var pitch = reader.ReadDouble(); var peak = reader.ReadDouble(); var reasonLength = reader.ReadInt32();
        if (edgeCount < 0 || edgeCount > (long)width * height || !double.IsFinite(scale) || scale <= 0
            || !double.IsFinite(ratio) || !double.IsFinite(pitch) || !double.IsFinite(peak)
            || reasonLength < 0 || reasonLength > 2048 || derive3 != derive5)
            throw new InvalidDataException("Invalid cached floor statistics.");
        var reason = new byte[reasonLength]; reader.BaseStream.ReadExactly(reason);
        var stride = checked((width + 63) / 64); var count = checked(stride * height);
        var k5 = ReadWords(reader, derive5 ? 0 : count); var k3 = ReadWords(reader, derive3 ? 0 : count);
        var floor = new Vpsg3PreparedFloor(key, width, height, edgeCount,
            new(scale, ratio, fast, Encoding.UTF8.GetString(reason), pitch, peak), stride, k5, k3,
            160L + 8L * (k5.Length + k3.Length) + (derive3 ? 0 : 48), deriveGeometryK3: derive3, deriveGeometryK5: derive5);
        try
        {
            if (reader.ReadBoolean()) floor.SetSemanticIndex(SemanticStructureIndex.ReadFrom(reader, width, height, nativeSemantic, true));
            if (reader.ReadBoolean()) floor.ReferenceSeeds = CompactBitWords.ReadFrom(reader, count, "VPSG3.Cache.Seeds");
            if ((floor.SemanticIndex is null) == (floor.ReferenceSeeds is null)
                || (floor.SemanticIndex is not null && !derive3))
                throw new InvalidDataException("Cached source geometry is missing.");
            return floor;
        }
        catch { floor.Dispose(); throw; }
    }

    // Reconstitute the exact source categories from immutable resident fields.
    // One caller owns this temporary Mat; the floor retains no decoded raster.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal Mat? CreateReferenceImage()
    {
        if (IsDisposed) return null;
        if (SemanticIndex is not null) return SemanticIndex.CreateReferenceImage();
        if (ReferenceSeeds is not { } seeds) return null;
        var binary = new Mat(ReferenceHeight, ReferenceWidth, MatType.CV_8UC1);
        try
        {
            for (var y = 0; y < ReferenceHeight; y++)
            {
                var row = MemoryMarshal.CreateSpan(ref binary.At<byte>(y, 0), ReferenceWidth);
                for (var x = 0; x < ReferenceWidth; x++) row[x] = (seeds[y * WordsPerRow + (x >> 6)] & (1UL << (x & 63))) != 0 ? (byte)255 : (byte)0;
            }
            return binary;
        }
        catch { binary.Dispose(); throw; }
    }
}
