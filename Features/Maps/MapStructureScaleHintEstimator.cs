using OpenCvSharp;
using System.Runtime.InteropServices;
using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Features.Maps;

internal sealed record MapStructureScaleHint(
    double Scale,
    double Confidence,
    int ReferencePairCount,
    int LivePairCount);

/// <summary>
/// Estimates a scale range from edge spacing only. It is a search hint, never
/// an alignment decision.
/// </summary>
internal static class MapStructureScaleHintEstimator
{
    private const int MaximumSamplePoints = 192;
    private const int MinimumPairCount = 80;
    private const int HistogramBinCount = 48;
    private const int HintScaleCount = 65;

    internal static bool TryEstimate(
        MapStructureFeatures reference,
        MapStructureFeatures live,
        double minimumScale,
        double maximumScale,
        out MapStructureScaleHint hint)
    {
        hint = new(0d, 0d, 0, 0);
        if (reference.Edges.Empty() || live.Edges.Empty())
            return false;
        using var operation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation(
            "Structure.ScaleHint", $"reference={reference.Edges.Width}x{reference.Edges.Height};live={live.Edges.Width}x{live.Edges.Height}");

        using var referenceLogs = BuildPairDistanceLogs(reference.Edges);
        using var liveLogs = BuildPairDistanceLogs(live.Edges);
        if (referenceLogs.Length < MinimumPairCount
            || liveLogs.Length < MinimumPairCount)
        {
            return false;
        }

        minimumScale = Math.Max(0.05d, minimumScale);
        maximumScale = Math.Max(minimumScale, maximumScale);
        var minimumLog = Math.Min(
            referenceLogs.Minimum,
            liveLogs.Minimum - Math.Log(maximumScale));
        var maximumLog = Math.Max(
            referenceLogs.Maximum,
            liveLogs.Maximum - Math.Log(minimumScale));
        var binWidth = (maximumLog - minimumLog) / HistogramBinCount;
        if (!double.IsFinite(binWidth) || binWidth <= 1e-6d)
            return false;

        var referenceHistogram = BuildHistogram(
            referenceLogs.Values,
            minimumLog,
            binWidth);
        var scales = Enumerable.Range(0, HintScaleCount)
            .Select(index => minimumScale * Math.Exp(
                Math.Log(maximumScale / minimumScale)
                    * index / (HintScaleCount - 1d)))
            .ToArray();
        var scores = scales
            .Select(scale => Correlate(
                referenceHistogram,
                BuildHistogram(
                    liveLogs.Values,
                    minimumLog,
                    binWidth,
                    Math.Log(scale))))
            .ToArray();
        var bestIndex = Enumerable.Range(0, scores.Length)
            .MaxBy(index => scores[index]);
        var bestScore = scores[bestIndex];
        var secondScore = scores
            .Where((_, index) => Math.Abs(index - bestIndex) > 2)
            .DefaultIfEmpty(0d)
            .Max();
        var margin = Math.Max(0d, bestScore - secondScore);
        var confidence = Math.Clamp(
            0.20d + (bestScore * 0.35d) + (margin * 3d),
            0d,
            0.98d);

        var logScale = Math.Log(scales[bestIndex]);
        if (bestIndex > 0 && bestIndex + 1 < scores.Length)
        {
            var curvature = scores[bestIndex - 1]
                - (2d * scores[bestIndex])
                + scores[bestIndex + 1];
            if (curvature < -1e-9d)
            {
                var offset = (scores[bestIndex - 1] - scores[bestIndex + 1])
                    / (2d * curvature);
                logScale += Math.Clamp(
                    offset,
                    -1d,
                    1d) * Math.Log(maximumScale / minimumScale)
                    / (HintScaleCount - 1d);
            }
        }

        var estimatedScale = Math.Clamp(
            Math.Exp(logScale),
            minimumScale,
            maximumScale);
        hint = new(
            estimatedScale,
            confidence,
            referenceLogs.Length,
            liveLogs.Length);
        return double.IsFinite(hint.Scale);
    }

    internal static PairDistanceLogs BuildPairDistanceLogs(Mat edges)
    {
        // This consumer needs at most 192 locations. Keep OpenCV's original
        // ordering and rounding without first copying every edge into the GC heap.
        Point[] sampled;
        using (var locations = new Mat())
        {
            var request = MemoryJournal.Request("Structure.ScaleHint.EdgeLocations", nativeModule: "OpenCvSharpExtern.dll");
            Cv2.FindNonZero(edges, locations);
            if (locations.Empty()) return new(0);
            MemoryJournal.TrackResource(locations, "Structure.ScaleHint.EdgeLocations",
                checked(locations.Total() * locations.ElemSize()), "OpenCvSharpExtern.dll", request);
            try
            {
                var count = checked((int)locations.Total());
                var points = MemoryMarshal.CreateReadOnlySpan(ref locations.At<Point>(0, 0), count);
                var sampledRequest = MemoryJournal.Request("Structure.ScaleHint.SampledPoints",
                    Math.Min(count, MaximumSamplePoints) * 8L);
                sampled = new Point[Math.Min(count, MaximumSamplePoints)];
                MemoryJournal.ObserveManagedArray(sampled, "Structure.ScaleHint.SampledPoints", sampledRequest);
                if (count > MaximumSamplePoints)
                {
                    var stride = (count - 1d) / (MaximumSamplePoints - 1d);
                    for (var index = 0; index < sampled.Length; index++)
                        sampled[index] = points[(int)Math.Round(index * stride)];
                }
                else points.CopyTo(sampled);
            }
            finally
            {
                locations.Dispose();
                MemoryJournal.ReleaseResource(locations);
            }
        }

        // ponytail: bounded O(n²) pairs keep this optional hint cheap; replace
        // with a spatial sampler only if real captures show it is a bottleneck.
        // Exact upper bound, in one closeable in-process scratch allocation.
        // Histograms borrow the same doubles; filtering changes only the length,
        // without a second large managed array or a resized copy.
        var capacity = sampled.Length * (sampled.Length - 1) / 2;
        var distances = new PairDistanceLogs(capacity);
        try
        {
            var values = distances.WritableValues;
            var used = 0;
            for (var first = 0; first < sampled.Length; first++)
            {
                for (var second = first + 1; second < sampled.Length; second++)
                {
                    var dx = sampled[first].X - sampled[second].X;
                    var dy = sampled[first].Y - sampled[second].Y;
                    var distance = Math.Sqrt((dx * dx) + (dy * dy));
                    if (double.IsFinite(distance) && distance > 1.5d)
                        values[used++] = Math.Log(distance);
                }
            }
            distances.Complete(used);
            return distances;
        }
        catch { distances.Dispose(); throw; }
    }

    internal static double[] BuildHistogram(
        ReadOnlySpan<double> values,
        double minimumLog,
        double binWidth,
        double logScale = 0d)
    {
        var histogram = new double[HistogramBinCount];
        foreach (var value in values)
        {
            // Apply the same subtraction in the same order, without copying
            // all pair distances for each of the 65 candidate scales.
            var index = (int)Math.Floor(((value - logScale) - minimumLog) / binWidth);
            if (index == HistogramBinCount)
                index--;
            if ((uint)index < HistogramBinCount)
                histogram[index]++;
        }

        var smoothed = new double[HistogramBinCount];
        for (var index = 0; index < histogram.Length; index++)
        {
            var previous = index > 0 ? histogram[index - 1] : 0d;
            var next = index + 1 < histogram.Length
                ? histogram[index + 1]
                : 0d;
            smoothed[index] = (previous + (2d * histogram[index]) + next) / 4d;
        }
        return smoothed;
    }

    // Borrowed spans remain valid only within this owner's using scope. No
    // pointer, Mat or full pair list escapes into a result/cache/async callback.
    internal sealed class PairDistanceLogs : IDisposable
    {
        private Mat? _storage;
        private readonly int _capacity;
        private bool _disposed;
        internal int Length { get; private set; }
        internal double Minimum { get; private set; } = double.PositiveInfinity;
        internal double Maximum { get; private set; } = double.NegativeInfinity;
        internal PairDistanceLogs(int capacity)
        {
            _capacity = capacity;
            if (capacity == 0) return;
            var request = MemoryJournal.Request("Structure.ScaleHint.PairLogs", capacity * sizeof(double), "OpenCvSharpExtern.dll");
            var storage = new Mat(1, capacity, MatType.CV_64FC1);
            try { MemoryJournal.TrackResource(storage, "Structure.ScaleHint.PairLogs", capacity * sizeof(double), "OpenCvSharpExtern.dll", request); }
            catch { storage.Dispose(); throw; }
            _storage = storage;
        }
        internal Span<double> WritableValues
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _storage is null ? [] : MemoryMarshal.CreateSpan(ref _storage.At<double>(0, 0), _capacity);
            }
        }
        internal ReadOnlySpan<double> Values => WritableValues[..Length];
        internal void Complete(int used)
        {
            Length = used;
            foreach (var value in Values) { Minimum = Math.Min(Minimum, value); Maximum = Math.Max(Maximum, value); }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var storage = Interlocked.Exchange(ref _storage, null);
            if (storage is null) return;
            try { storage.Dispose(); }
            finally { MemoryJournal.ReleaseResource(storage); }
        }
    }

    private static double Correlate(
        IReadOnlyList<double> first,
        IReadOnlyList<double> second)
    {
        var firstNorm = Math.Sqrt(first.Sum(value => value * value));
        var secondNorm = Math.Sqrt(second.Sum(value => value * value));
        if (firstNorm <= 1e-9d || secondNorm <= 1e-9d)
            return 0d;
        var dot = 0d;
        for (var index = 0; index < first.Count; index++)
            dot += first[index] * second[index];
        return Math.Clamp(dot / (firstNorm * secondNorm), 0d, 1d);
    }
}
