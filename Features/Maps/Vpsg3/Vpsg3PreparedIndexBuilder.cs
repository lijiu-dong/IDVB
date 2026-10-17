using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Prepares immutable, resident in-memory VPSG 3.0 floor indices from structural edge images.
/// Extracts dominant pitch priors, builds dilated binary bitsets for fast translation correlation,
/// and computes memory footprints.
/// </summary>
public static class Vpsg3PreparedIndexBuilder
{
    private static SemanticStructureIndex CreateSemanticIndex(Mat input, SemanticStructureIndex? reuseDistancesFrom, bool nativeSemanticStorage)
    {
        using var labels = SemanticStructureContours.Decode(input);
        return new(labels, reuseDistancesFrom: reuseDistancesFrom, nativeStorage: nativeSemanticStorage);
    }

    private const double Epsilon = 1e-9d;

    internal static Vpsg3PreparedFloor? BuildFromPrebuiltFile(string path, Vpsg3IndexCacheKey cacheKey,
        Vpsg3TuningConfig? tuning = null, bool preparePrecision = false,
        SemanticStructureIndex? reuseSemanticDistancesFrom = null, bool nativeSemanticStorage = false)
    {
        using var operation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("VPSG3.BuildPrebuiltFile", cacheKey.ToString());
        if (IDVBuff.Core.Imaging.PrebuiltContourLabels.TryRead(path, out var labels, out var semantic,
            $"VPSG3.Labels/{cacheKey}"))
        {
            try { return BuildFromPreparedLabels(labels, semantic, cacheKey, tuning, preparePrecision, reuseSemanticDistancesFrom, nativeSemanticStorage); }
            finally { labels.Dispose(); IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(labels); }
        }
        var resource = $"VPSG3.SourceContour/{cacheKey}";
        var request = IDVBuff.Core.Diagnostics.MemoryJournal.Request(resource, nativeModule: "OpenCvSharpExtern.dll");
        Mat? image = null;
        try
        {
            image = Cv2.ImRead(path, ImreadModes.Unchanged);
            IDVBuff.Core.Diagnostics.MemoryJournal.TrackResource(image, resource, image.Total() * image.ElemSize(),
                "OpenCvSharpExtern.dll", request);
            return image.Empty() ? null : BuildFromMat(image, cacheKey, tuning, preparePrecision, reuseSemanticDistancesFrom, nativeSemanticStorage);
        }
        finally
        {
            if (image is not null) { image.Dispose(); IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(image); }
        }
    }

    // Labels are borrowed from the original validated decoder, without scan
    // anchor erasure. Geometry and semantic fields own independent storage.
    internal static Vpsg3PreparedFloor BuildFromPreparedLabels(Mat labels, bool semantic,
        Vpsg3IndexCacheKey cacheKey, Vpsg3TuningConfig? tuning = null, bool preparePrecision = false,
        SemanticStructureIndex? reuseSemanticDistancesFrom = null, bool nativeSemanticStorage = false)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (labels.Empty() || labels.Type() != MatType.CV_8UC1)
            throw new ArgumentException("Prepared labels must be nonempty single-channel bytes.", nameof(labels));
        var geometryResource = $"VPSG3.Geometry/{cacheKey}";
        var geometryRequest = IDVBuff.Core.Diagnostics.MemoryJournal.Request(geometryResource, labels.Total(), "OpenCvSharpExtern.dll");
        using var edgeImage = new Mat();
        try
        {
            Cv2.Threshold(labels, edgeImage, 0, 255, ThresholdTypes.Binary);
            IDVBuff.Core.Diagnostics.MemoryJournal.TrackResource(edgeImage, geometryResource, edgeImage.Total(),
                "OpenCvSharpExtern.dll", geometryRequest);
            return BuildFromGeometry(edgeImage, cacheKey, semantic,
                () => semantic ? new SemanticStructureIndex(labels, reuseDistancesFrom: reuseSemanticDistancesFrom, nativeStorage: nativeSemanticStorage) : null,
                tuning, preparePrecision);
        }
        finally { edgeImage.Dispose(); IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(edgeImage); }
    }

    /// <summary>
    /// Builds a prepared VPSG 3.0 floor index synchronously from a reference edge image.
    /// </summary>
    public static Vpsg3PreparedFloor BuildFromMat(
        Mat inputImage,
        Vpsg3IndexCacheKey cacheKey,
        Vpsg3TuningConfig? tuning = null,
        bool preparePrecision = false,
        SemanticStructureIndex? reuseSemanticDistancesFrom = null, bool nativeSemanticStorage = false)
    {
        ArgumentNullException.ThrowIfNull(inputImage);
        if (inputImage.Empty())
            throw new ArgumentException("Edge image cannot be empty.", nameof(inputImage));
        using var edgeImage = SemanticStructureContours.Geometry(inputImage);
        return BuildFromGeometry(edgeImage, cacheKey, inputImage.Channels() == 3,
            () => inputImage.Channels() == 3 ? CreateSemanticIndex(inputImage, reuseSemanticDistancesFrom, nativeSemanticStorage) : null,
            tuning, preparePrecision);
    }

    // Owns the caller-created geometry scratch, which ends before the semantic
    // callback. Neither geometry nor the callback is retained by the floor.
    private static Vpsg3PreparedFloor BuildFromGeometry(Mat edgeImage, Vpsg3IndexCacheKey cacheKey,
        bool deriveGeometryK3, Func<SemanticStructureIndex?> buildSemantic, Vpsg3TuningConfig? tuning, bool preparePrecision)
    {
        using var memoryOperation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("VPSG3.BuildFloor", cacheKey.ToString());
        if (edgeImage.Type() != MatType.CV_8UC1)
            throw new ArgumentException("Edge image must be single-channel 8-bit.", nameof(edgeImage));

        var cfg = tuning ?? Vpsg3TuningConfig.Default;
        var width = edgeImage.Width;
        var height = edgeImage.Height;

        // 1. Edge pixel count
        var edgePixelCount = Cv2.CountNonZero(edgeImage);

        // 2. Compute reference scale prior via normalized autocorrelation
        var scalePrior = ComputeReferenceScalePrior(edgeImage, edgePixelCount, cfg);

        // 3. Morphological dilation for structural matching tolerance (K5 for 5x5 / +/-2px and K3 for 3x3 / +/-1px)
        using var dilatedK5 = new Mat();
        using var dilatedK3 = new Mat();
        using var kernel5 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        using var kernel3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        if (!deriveGeometryK3) Cv2.Dilate(edgeImage, dilatedK5, kernel5);
        if (!deriveGeometryK3) Cv2.Dilate(edgeImage, dilatedK3, kernel3);

        // 4. Pack into 64-bit row-major bitsets
        var wordsPerRow = (width + 63) / 64;
        var memoryRequest = IDVBuff.Core.Diagnostics.MemoryJournal.Request($"VPSG3.Base/{cacheKey}",
            (long)height * wordsPerRow * (deriveGeometryK3 ? 0 : 16) + (deriveGeometryK3 ? 160 : 208));
        var bitsetK5 = deriveGeometryK3 ? [] : new ulong[height * wordsPerRow];
        var bitsetK3 = deriveGeometryK3 ? [] : new ulong[height * wordsPerRow];
        if (!deriveGeometryK3)
        {
            IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedArray(bitsetK5, "VPSG3.K5");
            IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedArray(bitsetK3, "VPSG3.K3");
        } // RGB uses shared Array.Empty here; there was no payload allocation.

        for (var y = 0; !deriveGeometryK3 && y < height; y++)
        {
            var rowOffset = y * wordsPerRow;
            for (var x = 0; x < width; x++)
            {
                if (dilatedK5.At<byte>(y, x) > 128)
                {
                    bitsetK5[rowOffset + (x >> 6)] |= 1UL << (x & 63);
                }
                if (!deriveGeometryK3 && dilatedK3.At<byte>(y, x) > 128)
                {
                    bitsetK3[rowOffset + (x >> 6)] |= 1UL << (x & 63);
                }
            }
        }

        // 5. Calculate memory footprint
        var objectOverhead = 160L;
        var bitsetBytes = ((bitsetK5.Length + bitsetK3.Length) * 8L) + (deriveGeometryK3 ? 0L : 48L);
        float[]? precisionDistance = null;
        if (preparePrecision)
        {
            precisionDistance = Vpsg3PreparedFloor.CreatePrecisionDistance(edgeImage);
            IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedArray(precisionDistance, "VPSG3.Precision");
        }
        var totalBytes = objectOverhead + bitsetBytes
            + (precisionDistance is null ? 0L : precisionDistance.LongLength * sizeof(float) + 24L);

        // Geometry bitsets already own their data. End native scratch lifetimes
        // before constructing semantic distance transforms for this floor.
        dilatedK5.Dispose();
        dilatedK3.Dispose();
        edgeImage.Dispose();
        IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(edgeImage);

        // Construct all dependent data before publishing cache ownership. If
        // semantic preparation throws, no unreachable floor owns the bitsets.
        var semanticIndex = buildSemantic();

        try
        {
            return new Vpsg3PreparedFloor(
                cacheKey,
                width,
                height,
                edgePixelCount,
                scalePrior,
                wordsPerRow,
                bitsetK5,
                bitsetK3,
                totalBytes,
                precisionDistance, memoryRequest, deriveGeometryK3, deriveGeometryK5: deriveGeometryK3) { SemanticIndex = semanticIndex };
        }
        catch { semanticIndex?.Dispose(); throw; }
    }

    /// <summary>
    /// Builds a prepared VPSG 3.0 floor index asynchronously with execution timing.
    /// Synchronously clones the input Mat before background dispatch to guarantee caller lifecycle isolation.
    /// Outer lifecycle ensures ownedMat is always disposed even if cancellation occurs before task dispatch.
    /// </summary>
    public static async Task<Vpsg3IndexBuildResult> BuildFromMatAsync(
        Mat inputImage,
        Vpsg3IndexCacheKey cacheKey,
        Vpsg3TuningConfig? tuning = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputImage);
        if (inputImage.Empty())
            throw new ArgumentException("Edge image cannot be empty.", nameof(inputImage));
        // Keep the existing synchronous validation, without retaining its
        // unused full-size geometry throughout the background build.
        using (SemanticStructureContours.Geometry(inputImage)) { }

        if (cancellationToken.IsCancellationRequested)
        {
            return new Vpsg3IndexBuildResult(
                Success: false,
                Floor: null,
                ErrorMessage: "Operation was canceled.",
                BuildMilliseconds: 0);
        }

        var ownedMat = inputImage.Clone();
        var sw = Stopwatch.StartNew();
        var taskExecuted = false;

        try
        {
            return await Task.Run(() =>
            {
                taskExecuted = true;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var floor = BuildFromMat(ownedMat, cacheKey, tuning);
                    sw.Stop();
                    return new Vpsg3IndexBuildResult(
                        Success: true,
                        Floor: floor,
                        ErrorMessage: null,
                        BuildMilliseconds: sw.Elapsed.TotalMilliseconds);
                }
                finally
                {
                    ownedMat.Dispose();
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new Vpsg3IndexBuildResult(
                Success: false,
                Floor: null,
                ErrorMessage: "Operation was canceled.",
                BuildMilliseconds: sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new Vpsg3IndexBuildResult(
                Success: false,
                Floor: null,
                ErrorMessage: ex.Message,
                BuildMilliseconds: sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            if (!taskExecuted && !ownedMat.IsDisposed)
            {
                ownedMat.Dispose();
            }
        }
    }

    /// <summary>
    /// Decodes a reference image from disk in the background and constructs a prepared floor index.
    /// Manages the full lifecycle of the loaded Mat entirely within the background task.
    /// </summary>
    public static async Task<Vpsg3IndexBuildResult> BuildFromFileAsync(
        string imagePath,
        Vpsg3IndexCacheKey cacheKey,
        Vpsg3TuningConfig? tuning = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var image = Cv2.ImRead(imagePath, ImreadModes.Unchanged);
                if (image.Empty())
                {
                    sw.Stop();
                    return new Vpsg3IndexBuildResult(
                        Success: false,
                        Floor: null,
                        ErrorMessage: $"Failed to decode reference image from '{imagePath}'.",
                        BuildMilliseconds: sw.Elapsed.TotalMilliseconds);
                }

                var floor = BuildFromMat(image, cacheKey, tuning);
                sw.Stop();
                return new Vpsg3IndexBuildResult(
                    Success: true,
                    Floor: floor,
                    ErrorMessage: null,
                    BuildMilliseconds: sw.Elapsed.TotalMilliseconds);
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            return new Vpsg3IndexBuildResult(
                Success: false,
                Floor: null,
                ErrorMessage: ex.Message,
                BuildMilliseconds: sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Computes dominant pitch and peak ratio from edge projections.
    /// </summary>
    public static Vpsg3ScalePrior ComputeReferenceScalePrior(
        Mat edgeImage,
        int edgePixelCount,
        Vpsg3TuningConfig cfg)
    {
        if (edgePixelCount < cfg.MinEdgePixels)
        {
            return Vpsg3ScalePrior.Ineligible(
                $"EdgePixelCountBelowThreshold ({edgePixelCount} < {cfg.MinEdgePixels})");
        }

        var projX = Compute1DProjection(edgeImage, axis: 0);
        var (pitchX, ratioX) = FindDominantPitchNormalized(projX);

        if (pitchX <= 5.0 || ratioX < cfg.PeakRatioThreshold)
        {
            return Vpsg3ScalePrior.Ineligible(
                $"ReferencePeakRatioBelowThreshold ({ratioX:F2} < {cfg.PeakRatioThreshold:F2})",
                pitchX,
                ratioX);
        }

        return new Vpsg3ScalePrior(
            SeedScale: 1.0d,
            PeakRatio: ratioX,
            FastPathEligible: true,
            RejectReason: string.Empty,
            ReferencePitch: pitchX,
            ReferencePeakRatio: ratioX);
    }

    /// <summary>
    /// Computes 1D projection signal along the chosen axis.
    /// </summary>
    public static double[] Compute1DProjection(Mat edgeMask, int axis)
    {
        var width = edgeMask.Width;
        var height = edgeMask.Height;

        if (axis == 0) // Column projection along X
        {
            var proj = new double[width];
            for (var x = 0; x < width; x++)
            {
                var sum = 0.0;
                for (var y = 0; y < height; y++)
                {
                    if (edgeMask.At<byte>(y, x) > 128)
                        sum++;
                }
                proj[x] = sum;
            }
            return proj;
        }
        else // Row projection along Y
        {
            var proj = new double[height];
            for (var y = 0; y < height; y++)
            {
                var sum = 0.0;
                for (var x = 0; x < width; x++)
                {
                    if (edgeMask.At<byte>(y, x) > 128)
                        sum++;
                }
                proj[y] = sum;
            }
            return proj;
        }
    }

    /// <summary>
    /// Extracts dominant pitch and peak-to-median ratio via normalized autocorrelation.
    /// </summary>
    public static (double Pitch, double PeakRatio) FindDominantPitchNormalized(double[] signal)
    {
        var n = signal.Length;
        if (n < 40)
            return (0.0, 0.0);

        var sum = 0.0;
        for (var i = 0; i < n; i++)
            sum += signal[i];
        var mean = sum / n;

        var centered = new double[n];
        var variance = 0.0;
        for (var i = 0; i < n; i++)
        {
            centered[i] = signal[i] - mean;
            variance += centered[i] * centered[i];
        }

        if (variance < Epsilon)
            return (0.0, 0.0);

        const int minLag = 12;
        var maxLag = n / 2;
        if (maxLag <= minLag)
            return (0.0, 0.0);

        var bestLag = 0;
        var maxR = -1.0;
        var rValues = new List<double>(maxLag - minLag);

        for (var lag = minLag; lag < maxLag; lag++)
        {
            var dot = 0.0;
            var limit = n - lag;
            for (var i = 0; i < limit; i++)
            {
                dot += centered[i] * centered[i + lag];
            }

            var r = dot / variance;
            rValues.Add(r);

            if (r > maxR)
            {
                maxR = r;
                bestLag = lag;
            }
        }

        if (rValues.Count == 0 || maxR <= 0.05)
            return (0.0, 0.0);

        // Harmonic ambiguity resolution: prioritize fundamental pitch over harmonic multiples
        // If a smaller local peak exists with r >= 0.80 * maxR and bestLag is roughly an integer multiple of it,
        // select the smaller fundamental pitch to prevent locking into higher harmonics.
        if (bestLag > minLag)
        {
            for (var lag = minLag + 1; lag < bestLag - 1; lag++)
            {
                var idx = lag - minLag;
                if (idx <= 0 || idx >= rValues.Count - 1) continue;
                var r = rValues[idx];
                if (r > rValues[idx - 1] && r > rValues[idx + 1] && r >= 0.80 * maxR)
                {
                    var ratio = (double)bestLag / lag;
                    var k = Math.Round(ratio);
                    if (k >= 2 && Math.Abs(ratio - k) < 0.15)
                    {
                        bestLag = lag;
                        maxR = r;
                        break;
                    }
                }
            }
        }

        var sortedR = rValues.Select(Math.Abs).OrderBy(x => x).ToList();
        var medianR = sortedR[sortedR.Count / 2];
        var peakRatio = maxR / Math.Max(0.01, medianR);

        return (bestLag, peakRatio);
    }
}
