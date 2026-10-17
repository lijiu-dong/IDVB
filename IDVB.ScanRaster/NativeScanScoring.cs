using System;
using System.Runtime.CompilerServices;
using IDVBuff.Core.Collections;
using OpenCvSharp;

namespace IDVBuff.Core.Imaging;

/// <summary>One synchronous pose score over independently held native readers.
/// The caller keeps its scan/floor lease until this batch returns.</summary>
public static class NativeScanScoring
{
    // Deadline-bound scans must not spend their first request in quick JIT code.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static double Score(ReadOnlySpan<Point> points, double scale, double x, double y,
        double minimumCompetitiveScore, byte[]? semanticLabels, Rect unknownBounds,
        double supportTolerance, int width, int height, NativeEncodedByteField.ReadLease distances,
        NativeSemanticField? warm, NativeSemanticField? cool, ReadOnlySpan<double> decode)
    {
        if (points.IsEmpty) return 0;
        var pixels = distances.BorrowPixelKernel();
        var sum = 0d;
        var known = 0;
        var semanticRadius = Math.Max(2d, 2d / scale);
        var unknownRadius = supportTolerance / scale;
        try
        {
            for (var i = 0; i < points.Length; i++)
            {
                var p = points[i];
                var rx = (p.X - x) / scale;
                var ry = (p.Y - y) / scale;
                if (unknownBounds.Width > 0 && unknownBounds.Height > 0)
                {
                    var dx = Math.Max(0, Math.Max(unknownBounds.X - rx, rx - (unknownBounds.Right - 1)));
                    var dy = Math.Max(0, Math.Max(unknownBounds.Y - ry, ry - (unknownBounds.Bottom - 1)));
                    if (dx * dx + dy * dy <= unknownRadius * unknownRadius) continue;
                }
                var d = warm is not null && semanticLabels is not null && semanticLabels[i] != 0
                    && !IsHit((int)Math.Round(rx), (int)Math.Round(ry), semanticLabels[i], semanticRadius, width, height, warm, cool!)
                    ? 50d : Distance(rx, ry, scale, width, height, in pixels, decode);
                known++;
                sum += d <= .8 ? 1 : d <= 2.5 ? .7 : d <= 5.5 ? .4 : 0;
                var remaining = points.Length - i - 1;
                if (sum + remaining + 1e-9 < minimumCompetitiveScore * (known + remaining)) return 0;
            }
            return known > 0 && known >= Math.Min(80, points.Length) && known >= points.Length * .65
                ? sum / known : 0;
        }
        finally
        {
            GC.KeepAlive(distances); GC.KeepAlive(warm); GC.KeepAlive(cool);
        }

    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    private static bool IsHit(int px, int py, byte kind, double radius, int width, int height,
        NativeSemanticField warm, NativeSemanticField cool)
    {
        if ((uint)px >= width || (uint)py >= height || kind is not (127 or 255)) return false;
        var value = (kind == 127 ? warm : cool).AtCompact(px, py);
        return value < byte.MaxValue && value <= radius;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    private static double Distance(double px, double py, double scale, int width, int height,
        in NativeEncodedByteField.PixelKernel storage, ReadOnlySpan<double> decode)
    {
        if (!double.IsFinite(px) || !double.IsFinite(py) || px < 0 || py < 0 || px >= width - 1 || py >= height - 1)
            return 50d;
        var ix = (int)px; var iy = (int)py;
        var fx = px - ix; var fy = py - iy;
        var pixels = NativeSemanticField.ReadEncodedQuad(in storage, ix, iy);
        return ((decode[(byte)pixels] * (1 - fx) + decode[(byte)(pixels >> 8)] * fx) * (1 - fy)
            + (decode[(byte)(pixels >> 16)] * (1 - fx) + decode[(byte)(pixels >> 24)] * fx) * fy) * scale;
    }
}
