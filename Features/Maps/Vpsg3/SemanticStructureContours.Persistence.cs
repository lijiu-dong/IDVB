using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using IDVBuff.Core.Collections;
using IDVBuff.Core.Imaging;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class SemanticStructureIndex
{
    // K3 contains every source seed. Decode distances only inside those words;
    // the rest is known background, without rebuilding an RGB raster first.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal Mat CreateGeometryImage()
    {
        var image = new Mat(Height, Width, MatType.CV_8UC1, Scalar.Black);
        try
        {
            var stride = (Width + 63) / 64;
            var hasBits = _warmK3.Length == stride * Height && _coolK3.Length == stride * Height;
            for (var y = 0; y < Height; y++)
            {
                var row = MemoryMarshal.CreateSpan(ref image.At<byte>(y, 0), Width);
                if (!hasBits)
                {
                    // Scan-only indices may intentionally omit correlation bits.
                    for (var x = 0; x < Width; x++)
                        if (ReadDistancePixel(x, y, SemanticStructureContours.Warm) == 0
                            || ReadDistancePixel(x, y, SemanticStructureContours.Cool) == 0)
                            row[x] = 255;
                    continue;
                }
                for (var column = 0; column < stride; column++)
                {
                    var warm = _warmK3[y * stride + column];
                    var cool = _coolK3[y * stride + column];
                    var remaining = warm | cool;
                    while (remaining != 0)
                    {
                        var bit = BitOperations.TrailingZeroCount(remaining);
                        var mask = 1UL << bit;
                        var x = column * 64 + bit;
                        if (x < Width
                            && (((warm & mask) != 0 && ReadDistancePixel(x, y, SemanticStructureContours.Warm) == 0)
                                || ((cool & mask) != 0 && ReadDistancePixel(x, y, SemanticStructureContours.Cool) == 0)))
                            row[x] = 255;
                        remaining &= remaining - 1;
                    }
                }
            }
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    // The producer computed this mask while erasing this exact source Mat.
    // Callers cannot infer equality from filenames or from another floor.
    internal static SemanticStructureIndex FromMaskedLabels(Mat labels, SemanticStructureIndex source,
        byte erasedKinds, bool nativeStorage) => new(labels, false, source, nativeStorage, (byte)(3 & ~erasedKinds));

    private SemanticStructureIndex(BinaryReader reader, int width, int height, bool native,
        bool correlation, SemanticStructureIndex? reuse)
    {
        Width = width; Height = height;
        try
        {
            if (native)
            {
                _nativeWarm = NativeSemanticField.ReadFrom(reader, width, height, "Semantic.Cache.Warm", reuse?._nativeWarm);
                _nativeCool = NativeSemanticField.ReadFrom(reader, width, height, "Semantic.Cache.Cool", reuse?._nativeCool);
            }
            else
            {
                _warm = SemanticDistanceField.ReadFrom(reader, width, height, "Semantic.Cache.Warm", reuse?._warm);
                _cool = SemanticDistanceField.ReadFrom(reader, width, height, "Semantic.Cache.Cool", reuse?._cool);
            }
            var length = correlation ? checked(((width + 63) / 64) * height) : 0;
            _warmK3 = SemanticCorrelationBits.ReadFrom(reader, length, native, "Semantic.Cache.Warm.K3");
            _coolK3 = SemanticCorrelationBits.ReadFrom(reader, length, native, "Semantic.Cache.Cool.K3");
            if (reuse is not null)
                SharedDistanceBytes = native ? _nativeWarm!.SharedBytesFrom(reuse._nativeWarm!) + _nativeCool!.SharedBytesFrom(reuse._nativeCool!)
                    : _warm!.SharedBytesFrom(reuse._warm!) + _cool!.SharedBytesFrom(reuse._cool!);
        }
        catch { _nativeWarm?.Dispose(); _nativeCool?.Dispose(); _warmK3?.Dispose(); _coolK3?.Dispose(); throw; }
    }
    internal void WriteTo(BinaryWriter writer, SemanticStructureIndex? reuse = null)
    {
        if (UsesNativeStorage) { _nativeWarm!.WriteTo(writer, reuse?._nativeWarm); _nativeCool!.WriteTo(writer, reuse?._nativeCool); }
        else { _warm!.WriteTo(writer, reuse?._warm); _cool!.WriteTo(writer, reuse?._cool); }
        _warmK3.WriteTo(writer); _coolK3.WriteTo(writer);
    }
    internal static SemanticStructureIndex ReadFrom(BinaryReader reader, int width, int height, bool native,
        bool correlation, SemanticStructureIndex? reuse = null) => new(reader, width, height, native, correlation, reuse);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal Mat CreateReferenceImage()
    {
        var image = new Mat(Height, Width, MatType.CV_8UC3);
        try
        {
            for (var y = 0; y < Height; y++)
            {
                var row = MemoryMarshal.CreateSpan(ref image.At<Vec3b>(y, 0), Width);
                for (var x = 0; x < Width; x++)
                    row[x] = ReadDistancePixel(x, y, SemanticStructureContours.Warm) == 0 ? new(0, 140, 255)
                        : ReadDistancePixel(x, y, SemanticStructureContours.Cool) == 0 ? new(255, 255, 255) : new(0, 0, 0);
            }
            return image;
        }
        catch { image.Dispose(); throw; }
    }
}

internal sealed partial class SemanticCorrelationBits
{
    internal void WriteTo(BinaryWriter writer)
    {
        if (_native is not null) _native.WriteTo(writer); else _managed!.WriteTo(writer);
    }
    internal static SemanticCorrelationBits ReadFrom(BinaryReader reader, int length, bool native, string resource) => native
        ? new(NativeCompactBitWords.ReadFrom(reader, length, resource)) : new(CompactBitWords.ReadFrom(reader, length, resource));
}
