using IDVBuff.Core.Collections;

namespace IDVBuff.Features.Maps;

internal sealed partial class ScanStructureIndex
{
    internal static ScanStructureIndex FromMaskedLabels(OpenCvSharp.Mat labels, bool semantic, bool nativeDistance,
        bool nativeSemantic, SemanticStructureIndex? source, byte erasedKinds) =>
        new(labels, semantic, nativeDistance, nativeSemantic, source, erasedKinds);

    internal void WriteTo(BinaryWriter writer, SemanticStructureIndex? source)
    {
        if (_distanceReader is not null) _distanceReader.WriteTo(writer); else _distances!.WriteTo(writer);
        writer.Write(SemanticIndex is not null);
        SemanticIndex?.WriteTo(writer, source);
        writer.Write(LocalGeometry is not null);
        LocalGeometry?.WriteTo(writer);
    }
    private ScanStructureIndex(BinaryReader reader, int width, int height, bool nativeDistance,
        bool nativeSemantic, SemanticStructureIndex? source)
    {
        Width = width; Height = height;
        try
        {
            if (nativeDistance)
            {
                using var owner = NativeEncodedByteField.ReadFrom(reader, width, height, "Scan.Cache.Distance");
                _distanceReader = owner.TryRent() ?? throw new InvalidOperationException("New scan cache owner retired.");
                _ownsDistanceReader = true; _distanceBytes = _distanceReader.PayloadBytes;
            }
            else
            {
                _distances = CompactByteField.ReadFrom(reader, width, height, "Scan.Cache.Distance");
                _distanceBytes = _distances.MemoryBytes;
            }
            if (reader.ReadBoolean()) SemanticIndex = SemanticStructureIndex.ReadFrom(reader, width, height, nativeSemantic, false, source);
            _ownsSemanticReader = SemanticIndex?.UsesNativeStorage == true;
            if (!reader.ReadBoolean()) throw new InvalidDataException("Cached local geometry is incomplete.");
            LocalGeometry = DeepScanStructureIndex.ReadFrom(reader, this);
        }
        catch { _distanceReader?.Dispose(); SemanticIndex?.Dispose(); throw; }
    }
    internal static ScanStructureIndex ReadFrom(BinaryReader reader, int width, int height, bool nativeDistance,
        bool nativeSemantic, SemanticStructureIndex? source) => new(reader, width, height, nativeDistance, nativeSemantic, source);
}

internal sealed partial class DeepScanStructureIndex
{
    internal void WriteTo(BinaryWriter writer)
    {
        var corners = _corners.Values.SelectMany(c => c).ToArray();
        writer.Write(corners.Length);
        foreach (var c in corners)
        {
            writer.Write(c.A.X); writer.Write(c.A.Y); writer.Write(c.B.X); writer.Write(c.B.Y); writer.Write(c.C.X); writer.Write(c.C.Y);
        }
    }
    internal static DeepScanStructureIndex ReadFrom(BinaryReader reader, ScanStructureIndex distances)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > Math.Min(1_000_000L, (long)distances.Width * distances.Height))
            throw new InvalidDataException("Invalid cached corner count.");
        var corners = new Corner[count];
        OpenCvSharp.Point Point()
        {
            var x = reader.ReadInt32(); var y = reader.ReadInt32();
            if ((uint)x >= distances.Width || (uint)y >= distances.Height) throw new InvalidDataException("Invalid cached corner.");
            return new(x, y);
        }
        for (var i = 0; i < count; i++) corners[i] = new(Point(), Point(), Point());
        return new(distances, corners);
    }
}
