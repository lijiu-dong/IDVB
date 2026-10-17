using System.Runtime.InteropServices;
using IDVBuff.Core.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Core.Imaging;

/// <summary>Exact Euclidean distance encoding within the existing 71.5px
/// saturation band. Borrowed labels and native scratch never outlive a build.</summary>
public static class ScanDistanceRaster
{
    // Any seed farther than 71 pixels vertically is outside the encoded band.
    // Keep 72 as an absent/out-of-band sentinel, without inventing a seed.
    private const int VerticalLimit = 72;
    private const int MaximumSquaredDistance = 5112; // floor(71.5 * 71.5)
    private static readonly byte[] Encodings = CreateEncodings();

    public sealed class EncodedRaster(Mat image) : IDisposable
    {
        public Mat Image { get; } = image;
        public void Dispose()
        {
            Image.Dispose();
            MemoryJournal.ReleaseResource(Image);
        }
    }

    private sealed class RowScratch
    {
        internal readonly int[] Centers;
        internal readonly double[] Cuts;
        internal RowScratch(int width)
        {
            Centers = new int[width];
            Cuts = new double[width + 1];
            MemoryJournal.ObserveManagedArray(Centers, "ScanStructureIndex.RowCenters");
            MemoryJournal.ObserveManagedArray(Cuts, "ScanStructureIndex.RowCuts");
        }
    }

    private static byte[] CreateEncodings()
    {
        // Preserve OpenCV's float sqrt and byte conversion rounding, including
        // the fine/coarse boundary and saturation. The immutable 5KiB table
        // replaces per-floor float rasters; it contains no floor pose or scale.
        using var distance = new Mat(1, MaximumSquaredDistance + 1, MatType.CV_32FC1);
        for (var i = 0; i <= MaximumSquaredDistance; i++)
            distance.Set(0, i, (float)Math.Sqrt(i));
        using var encoded = new Mat();
        using var coarse = new Mat();
        using var mask = new Mat();
        distance.ConvertTo(encoded, MatType.CV_8UC1, 16);
        distance.ConvertTo(coarse, MatType.CV_8UC1, 2, 112);
        Cv2.Compare(distance, 8, mask, CmpTypes.GT);
        coarse.CopyTo(encoded, mask);
        var result = MemoryMarshal.CreateReadOnlySpan(ref encoded.At<byte>(0, 0), MaximumSquaredDistance + 1).ToArray();
        MemoryJournal.ObserveManagedArray(result, "ScanStructureIndex.DistanceEncodingTable");
        return result;
    }

    public static EncodedRaster Encode(Mat labels)
    {
        if (labels.Empty() || labels.Type() != MatType.CV_8UC1)
            throw new ArgumentException("扫描标签必须为非空单通道图。", nameof(labels));
        var width = labels.Width;
        var height = labels.Height;
        var pixels = checked((long)width * height);
        using var operation = MemoryJournal.BeginOperation("ScanStructureIndex.BuildDistance", $"{width}x{height}");
        var verticalRequest = MemoryJournal.Request("ScanStructureIndex.VerticalDistance", pixels, "OpenCvSharpExtern.dll");
        using var vertical = new Mat(height, width, MatType.CV_8UC1);
        MemoryJournal.TrackResource(vertical, "ScanStructureIndex.VerticalDistance", pixels, "OpenCvSharpExtern.dll", verticalRequest);
        Mat? encoded = null;
        try
        {
            var encodedRequest = MemoryJournal.Request("ScanStructureIndex.EncodedDistance", pixels, "OpenCvSharpExtern.dll");
            encoded = new Mat(height, width, MatType.CV_8UC1);
            MemoryJournal.TrackResource(encoded, "ScanStructureIndex.EncodedDistance", pixels, "OpenCvSharpExtern.dll", encodedRequest);
            for (var y = 0; y < height; y++)
            {
                var input = MemoryMarshal.CreateReadOnlySpan(ref labels.At<byte>(y, 0), width);
                var row = MemoryMarshal.CreateSpan(ref vertical.At<byte>(y, 0), width);
                var previous = y > 0 ? MemoryMarshal.CreateReadOnlySpan(ref vertical.At<byte>(y - 1, 0), width)
                    : ReadOnlySpan<byte>.Empty;
                for (var x = 0; x < width; x++)
                    row[x] = input[x] != 0 ? (byte)0 : previous.IsEmpty ? (byte)VerticalLimit
                        : (byte)Math.Min(VerticalLimit, previous[x] + 1);
            }
            for (var y = height - 2; y >= 0; y--)
            {
                var row = MemoryMarshal.CreateSpan(ref vertical.At<byte>(y, 0), width);
                var next = MemoryMarshal.CreateReadOnlySpan(ref vertical.At<byte>(y + 1, 0), width);
                for (var x = 0; x < width; x++)
                    row[x] = (byte)Math.Min(row[x], Math.Min(VerticalLimit, next[x] + 1));
            }
            // Rows have independent envelopes and disjoint outputs. Bound
            // worker scratch and wait for every row before returning ownership.
            Parallel.For(0, height, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) },
                () => new RowScratch(width),
                (y, _, scratch) =>
                {
                    FillRow(MemoryMarshal.CreateReadOnlySpan(ref vertical.At<byte>(y, 0), width),
                        MemoryMarshal.CreateSpan(ref encoded.At<byte>(y, 0), width), scratch);
                    return scratch;
                }, _ => { });
            return new(encoded);
        }
        catch
        {
            if (encoded is not null)
            {
                encoded.Dispose();
                MemoryJournal.ReleaseResource(encoded);
            }
            throw;
        }
        finally
        {
            vertical.Dispose();
            MemoryJournal.ReleaseResource(vertical);
        }
    }

    /// <summary>Exact saturated Chebyshev distance for a binary class mask.
    /// The mask is borrowed; the returned raster owns its native pixels.</summary>
    public static EncodedRaster EncodeChebyshev(Mat mask, string resource = "Semantic.ByteDistance")
    {
        if (mask.Empty() || mask.Type() != MatType.CV_8UC1)
            throw new ArgumentException("语义距离种子必须为非空单通道掩码。", nameof(mask));
        var width = mask.Width;
        var height = mask.Height;
        var pixels = checked((long)width * height);
        using var operation = MemoryJournal.BeginOperation("Semantic.BuildByteDistance", $"{resource};{width}x{height}");
        var request = MemoryJournal.Request(resource, pixels, "OpenCvSharpExtern.dll");
        Mat? encoded = null;
        try
        {
            encoded = new Mat(mask.Size(), MatType.CV_8UC1);
            MemoryJournal.TrackResource(encoded, resource, pixels, "OpenCvSharpExtern.dll", request);
            for (var y = 0; y < height; y++)
            {
                var seeds = MemoryMarshal.CreateReadOnlySpan(ref mask.At<byte>(y, 0), width);
                var row = MemoryMarshal.CreateSpan(ref encoded.At<byte>(y, 0), width);
                var above = y > 0 ? MemoryMarshal.CreateReadOnlySpan(ref encoded.At<byte>(y - 1, 0), width)
                    : ReadOnlySpan<byte>.Empty;
                var left = 255;
                for (var x = 0; x < width; x++)
                {
                    if (seeds[x] != 0) row[x] = 0;
                    else
                    {
                        var best = left;
                        if (!above.IsEmpty)
                        {
                            best = Math.Min(best, above[x]);
                            if (x > 0) best = Math.Min(best, above[x - 1]);
                            if (x + 1 < width) best = Math.Min(best, above[x + 1]);
                        }
                        row[x] = (byte)Math.Min(255, best + 1);
                    }
                    left = row[x];
                }
            }
            for (var y = height - 1; y >= 0; y--)
            {
                var row = MemoryMarshal.CreateSpan(ref encoded.At<byte>(y, 0), width);
                var below = y + 1 < height ? MemoryMarshal.CreateReadOnlySpan(ref encoded.At<byte>(y + 1, 0), width)
                    : ReadOnlySpan<byte>.Empty;
                var right = 255;
                for (var x = width - 1; x >= 0; x--)
                {
                    var best = right;
                    if (!below.IsEmpty)
                    {
                        best = Math.Min(best, below[x]);
                        if (x > 0) best = Math.Min(best, below[x - 1]);
                        if (x + 1 < width) best = Math.Min(best, below[x + 1]);
                    }
                    row[x] = (byte)Math.Min(row[x], Math.Min(255, best + 1));
                    right = row[x];
                }
            }
            return new(encoded);
        }
        catch
        {
            if (encoded is not null)
            {
                encoded.Dispose();
                MemoryJournal.ReleaseResource(encoded);
            }
            throw;
        }
    }

    private static void FillRow(ReadOnlySpan<byte> vertical, Span<byte> result, RowScratch scratch)
    {
        var width = vertical.Length;
        var centers = scratch.Centers;
        var cuts = scratch.Cuts;
        var last = -1;
        // Lower envelope of exact integer squared-distance parabolas. A
        // saturated vertical column cannot contribute within the output band.
        for (var q = 0; q < width; q++)
        {
            if (vertical[q] >= VerticalLimit) continue;
            var f = vertical[q] * vertical[q];
            var cut = double.NegativeInfinity;
            while (last >= 0)
            {
                var v = centers[last];
                cut = ((long)q * q + f - (long)v * v - vertical[v] * vertical[v]) / (2d * (q - v));
                if (cut > cuts[last]) break;
                last--;
            }
            centers[++last] = q;
            cuts[last] = last == 0 ? double.NegativeInfinity : cut;
            cuts[last + 1] = double.PositiveInfinity;
        }
        if (last < 0)
        {
            // No seed in range: never convert OpenCV's huge float sentinel
            // through an overflowing integer conversion that would become 0.
            result.Fill(byte.MaxValue);
            return;
        }
        var selected = 0;
        for (var x = 0; x < width; x++)
        {
            while (cuts[selected + 1] < x) selected++;
            var v = centers[selected];
            var dx = x - v;
            var squared = (long)dx * dx + vertical[v] * vertical[v];
            result[x] = squared <= MaximumSquaredDistance ? Encodings[(int)squared] : byte.MaxValue;
        }
    }
}
