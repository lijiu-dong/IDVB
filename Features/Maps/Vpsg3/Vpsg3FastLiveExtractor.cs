using System.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Production-grade fast native structure line extractor for live game viewport frames (VPSG 3.0 Phase 2.1).
/// Fully migrates the Phase 0.5 A-4 ablation winner (Cheap Dynamic Exclusion + Pure Morphology)
/// while generating a semantically correct ValidMask with steady-state scratch buffer reuse.
/// </summary>
public static partial class Vpsg3FastLiveExtractor
{
    private const double ContourMinPerimeter = 30d;
    private const double HoleMinArea = 900d;
    private const double ApproxEpsilon = 0.55d;

    private static readonly object ScratchGate = new();
    private static Vpsg3LiveExtractorScratch? s_sharedScratch;
    private static int s_scratchGeneration;

    /// <summary>
    /// Extracts live structural edges and valid observation mask from a query game frame or cropped viewport.
    /// </summary>
    /// <param name="source">Source frame (BGRA, BGR, or Grayscale).</param>
    /// <param name="viewportBounds">Screen coordinate bounds of the source image, if known.</param>
    /// <param name="maxSparsePoints">Maximum number of sparse edge points to sample on-demand for downstream verification.</param>
    /// <param name="scratch">Optional session/worker-owned scratch buffers to eliminate intermediate Mat allocations.</param>
    public static Vpsg3LiveObservation Extract(
        Mat source,
        MapScreenRect? viewportBounds = null,
        int maxSparsePoints = 150,
        Vpsg3LiveExtractorScratch? scratch = null,
        bool includeAutomaticIdentityEvidence = false,
        IReadOnlyList<MapScreenRect>? excludedScreenRegions = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        IDVBuff.Core.Contracts.Online.RecognitionResources.Demand();
        if (source.Empty())
            throw new ArgumentException("Source frame cannot be empty.", nameof(source));

        var sw = Stopwatch.StartNew();

        var width = source.Width;
        var height = source.Height;
        var size = source.Size();
        var bounds = viewportBounds ?? new MapScreenRect(0, 0, width, height);

        using var rented = scratch is null ? RentScratch() : null;
        var s = scratch ?? rented!.Scratch;
        s.PrepareForSize(size);

        Mat? observedEdges = null, proposalEdges = null, validMask = null, semanticLabels = null;
        Mat? visibleInterior = null, automaticWallEvidence = null;
        var published = false;
        try
        {
            // 1. Channel normalization
            var bgr = NormalizeToBgr(source, s);

            // 2. Color space conversion to HSV for structure semantics
            Vpsg3LiveExtractorScratch.EnsureMatSized(s.Hsv, size, MatType.CV_8UC3);
            Cv2.CvtColor(bgr, s.Hsv, ColorConversionCodes.BGR2HSV);

            // 3. Fast dynamic exclusion (HUD and top glyph exclusion without connected components)
            ComputeFastDynamicExclusion(s.Hsv, s);
            if (excludedScreenRegions is { Count: > 0 })
                MapFrameUiExclusion.Fill(s.Exclusion, bounds, excludedScreenRegions, Scalar.White);
            NativeStructureTextureMask.Exclude(bgr, s.Exclusion);

            // 4. Classify room and corridor semantic areas with pure morphological filtering
            ClassifyStructurePureMorphology(s);

            // 5. Semantic candidate edge extraction via contour approximation
            ExtractSemanticCandidateEdges(s);
            RemoveWarmTextureBoundaries(s);
            // Clearing HUD pixels before contour extraction creates artificial boundaries.
            // Keep those boundaries out of both edge streams, including their drawn rim,
            // before photometric support can promote them back to valid observations.
            Cv2.Dilate(s.Exclusion, s.DilatedExclusion, s.K5);
            s.CandidateEdges.SetTo(Scalar.Black, s.DilatedExclusion);

            // 6. Strong edge support via Canny on grayscale + dilation
            ComputeStrongSourceEdgeSupport(bgr, s);

            // 7. Observed edges: semantic candidates supported by strong photometric edges
            observedEdges = new Mat(size, MatType.CV_8UC1);
            Cv2.BitwiseAnd(s.CandidateEdges, s.Support, observedEdges);
            proposalEdges = new Mat(size, MatType.CV_8UC1);
            Cv2.BitwiseAnd(s.CandidateEdges, s.StrongSupport, proposalEdges);

            // 8. Valid mask generation (fog frontier + exclusion masking)
            // Areas with semantic edges but missing strong photometric support represent fog frontiers
            Cv2.BitwiseNot(s.Support, s.NotSupport);
            Cv2.BitwiseAnd(s.CandidateEdges, s.NotSupport, s.UncertainFrontier);

            // In-place dilation on uncertain frontier
            Cv2.Dilate(s.UncertainFrontier, s.UncertainFrontier, s.K11);

            // Combined invalid mask
            Cv2.BitwiseOr(s.UncertainFrontier, s.DilatedExclusion, s.Invalid);

            // Final valid mask (inverted invalid, with observed edges forced valid)
            validMask = new Mat(size, MatType.CV_8UC1);
            Cv2.BitwiseNot(s.Invalid, validMask);
            validMask.SetTo(Scalar.White, observedEdges);

            // 9. Edge pixel metrics
            var edgePixelCount = Cv2.CountNonZero(observedEdges);
            var validPixelCount = Cv2.CountNonZero(validMask);

            semanticLabels = SemanticStructureContours.LabelLive(observedEdges, s.Room, s.Corridor,
                s.SmoothedGray, s.BrightDetail, s.CannyWeak, s.K5);
            if (includeAutomaticIdentityEvidence)
                (visibleInterior, automaticWallEvidence) = CreateAutomaticIdentityEvidence(s, observedEdges, proposalEdges);
            sw.Stop();

            var observation = new Vpsg3LiveObservation(
                observedEdges: observedEdges,
                validMask: validMask,
                width: width,
                height: height,
                edgePixelCount: edgePixelCount,
                validStructurePixelCount: validPixelCount,
                viewportBounds: bounds,
                maxSparsePoints: maxSparsePoints,
                sparseEdgePoints: null,
                extractionMilliseconds: sw.Elapsed.TotalMilliseconds,
                proposalEdges: proposalEdges,
                semanticLabels: semanticLabels,
                visibleInterior: visibleInterior, automaticWallEvidence: automaticWallEvidence);
            published = true;
            return observation;
        }
        finally
        {
            if (!published)
            {
                observedEdges?.Dispose(); proposalEdges?.Dispose();
                validMask?.Dispose(); semanticLabels?.Dispose();
                visibleInterior?.Dispose(); automaticWallEvidence?.Dispose();
            }
        }
    }

    private static Mat NormalizeToBgr(Mat source, Vpsg3LiveExtractorScratch scratch)
    {
        switch (source.Channels())
        {
            case 3:
                return source;
            case 4:
                Vpsg3LiveExtractorScratch.EnsureMatSized(scratch.Bgr, source.Size(), MatType.CV_8UC3);
                Cv2.CvtColor(source, scratch.Bgr, ColorConversionCodes.BGRA2BGR);
                return scratch.Bgr;
            case 1:
                Vpsg3LiveExtractorScratch.EnsureMatSized(scratch.Bgr, source.Size(), MatType.CV_8UC3);
                Cv2.CvtColor(source, scratch.Bgr, ColorConversionCodes.GRAY2BGR);
                return scratch.Bgr;
            default:
                throw new InvalidDataException($"Unsupported image channel count: {source.Channels()}");
        }
    }

    private static void ComputeFastDynamicExclusion(Mat hsv, Vpsg3LiveExtractorScratch scratch)
    {
        var h = hsv.Height;
        var w = hsv.Width;

        // 1. Bottom-left green avatar HUD detection
        var greenRoiY = (int)(0.68 * h);
        var greenRoiW = (int)(0.28 * w);
        var greenRoiH = h - greenRoiY;
        if (greenRoiW > 0 && greenRoiH > 0)
        {
            var greenRect = new Rect(0, greenRoiY, greenRoiW, greenRoiH);
            using var hsvGreen = new Mat(hsv, greenRect);
            Cv2.InRange(hsvGreen, new Scalar(35, 55, 45), new Scalar(95, 255, 255), scratch.GreenSeed);

            if (Cv2.CountNonZero(scratch.GreenSeed) > 40)
            {
                var fillRect = new Rect(0, (int)(0.72 * h), (int)(0.24 * w), (int)(0.28 * h));
                using var excluded = new Mat(scratch.Exclusion, fillRect);
                excluded.SetTo(Scalar.White);
            }
        }

        // 2. Top white glyph HUD detection
        var topRoiY = (int)(0.03 * h);
        var topRoiH = (int)(0.12 * h);
        var topRoiX = (int)(0.10 * w);
        var topRoiW = (int)(0.70 * w);
        if (topRoiW > 0 && topRoiH > 0)
        {
            var topRect = new Rect(topRoiX, topRoiY, topRoiW, topRoiH);
            using var hsvTop = new Mat(hsv, topRect);
            Cv2.InRange(hsvTop, new Scalar(0, 0, 120), new Scalar(180, 60, 255), scratch.WhiteSeed);

            if (Cv2.CountNonZero(scratch.WhiteSeed) > 100)
            {
                using var excluded = new Mat(scratch.Exclusion, topRect);
                excluded.SetTo(Scalar.White);
            }
        }
    }

    private static void ClassifyStructurePureMorphology(Vpsg3LiveExtractorScratch s)
    {
        var profile = NativeRecognitionProfile.Current;
        Cv2.InRange(s.Hsv, profile.RoomLow, profile.RoomHigh, s.Room1);
        Cv2.InRange(s.Hsv, profile.Room2Low, profile.Room2High, s.Room2);
        Cv2.BitwiseOr(s.Room1, s.Room2, s.Room);

        Cv2.InRange(s.Hsv, profile.CorridorLow, profile.CorridorHigh, s.Corridor);

        s.Room.SetTo(Scalar.Black, s.Exclusion);
        s.Corridor.SetTo(Scalar.Black, s.Exclusion);

        Cv2.MorphologyEx(s.Room, s.Room, MorphTypes.Open, s.K5);
        Cv2.MorphologyEx(s.Room, s.Room, MorphTypes.Close, s.K3);

        Cv2.MorphologyEx(s.Corridor, s.Corridor, MorphTypes.Open, s.K5);
        Cv2.MorphologyEx(s.Corridor, s.Corridor, MorphTypes.Close, s.K3);
    }

    private static void ExtractSemanticCandidateEdges(Vpsg3LiveExtractorScratch s)
    {
        AppendApproximatedContours(s.Room, s.ApproxContourBatch);
        AppendApproximatedContours(s.Corridor, s.ApproxContourBatch);

        if (s.ApproxContourBatch.Count > 0)
        {
            Cv2.DrawContours(
                s.CandidateEdges,
                s.ApproxContourBatch,
                contourIdx: -1,
                color: Scalar.White,
                thickness: 2,
                lineType: LineTypes.Link8);
        }
    }

    private static void RemoveWarmTextureBoundaries(Vpsg3LiveExtractorScratch s)
    {
        var profile = NativeRecognitionProfile.Current;
        using var originalEdges = s.CandidateEdges.Clone();
        Cv2.InRange(s.Hsv, profile.RoomLow with { Val2 = SemanticStructureContours.MinimumWarmFloorValue }, profile.RoomHigh, s.Room1);
        Cv2.InRange(s.Hsv, profile.Room2Low with { Val2 = SemanticStructureContours.MinimumWarmFloorValue }, profile.Room2High, s.Room2);
        Cv2.BitwiseOr(s.Room1, s.Room2, s.Room);
        s.Room.SetTo(Scalar.Black, s.Exclusion);
        Cv2.MorphologyEx(s.Room, s.Room, MorphTypes.Open, s.K5);
        Cv2.MorphologyEx(s.Room, s.Room, MorphTypes.Close, s.K3);
        s.ApproxContourBatch.Clear();
        s.CandidateEdges.SetTo(Scalar.Black);
        ExtractSemanticCandidateEdges(s);
        // The extended floor fill disproves interior texture boundaries only.
        // Keep original wall coordinates: moving the brightness contour changes
        // scale/translation estimates even when it follows the same physical rim.
        Cv2.Dilate(s.CandidateEdges, s.NotSupport, s.K5);
        Cv2.BitwiseAnd(originalEdges, s.NotSupport, s.CandidateEdges);
    }

    private static void AppendApproximatedContours(Mat mask, List<Point[]> batch)
    {
        Cv2.FindContours(
            mask,
            out var contours,
            out var hierarchy,
            RetrievalModes.CComp,
            ContourApproximationModes.ApproxSimple);

        if (contours.Length == 0 || hierarchy.Length == 0)
            return;

        for (var idx = 0; idx < contours.Length; idx++)
        {
            var contour = contours[idx];
            var perimeter = Cv2.ArcLength(contour, closed: true);
            if (perimeter < ContourMinPerimeter)
                continue;

            var parent = hierarchy[idx].Parent;
            if (parent != -1 && Math.Abs(Cv2.ContourArea(contour)) < HoleMinArea)
                continue;

            var approx = Cv2.ApproxPolyDP(contour, ApproxEpsilon, closed: true);
            batch.Add(approx);
        }
    }

    internal static void ComputeStrongSourceEdgeSupport(Mat bgr, Vpsg3LiveExtractorScratch s)
    {
        s.PrepareSupportForSize(bgr.Size());
        Cv2.CvtColor(bgr, s.Gray, ColorConversionCodes.BGR2GRAY);
        var profile = NativeRecognitionProfile.Current;
        Cv2.Canny(s.Gray, s.CannyStrong, profile.CannyLow, profile.CannyHigh, apertureSize: 3, L2gradient: true);
        Cv2.Dilate(s.CannyStrong, s.Support, s.K5);
        s.Support.CopyTo(s.StrongSupport);
        // Fog lowers wall contrast. Recover a sharp, bright rim only where the semantic
        // contour already exists; a smooth fog transition has no such high-frequency ridge.
        // This never promotes arbitrary grayscale edges to structural observations.
        Cv2.GaussianBlur(s.Gray, s.SmoothedGray, new Size(5, 5), 1.5);
        Cv2.Subtract(s.Gray, s.SmoothedGray, s.BrightDetail);
        Cv2.Threshold(s.BrightDetail, s.BrightDetail, 4d, 255d, ThresholdTypes.Binary);
        Cv2.Canny(s.Gray, s.CannyWeak, 25d, 65d, apertureSize: 3, L2gradient: true);
        Cv2.Dilate(s.BrightDetail, s.BrightDetail, s.K3);
        Cv2.BitwiseAnd(s.CannyWeak, s.BrightDetail, s.CannyWeak);
        Cv2.Dilate(s.CannyWeak, s.CannyWeak, s.K3);
        Cv2.BitwiseOr(s.Support, s.CannyWeak, s.Support);
    }

    internal static ScratchLease RentScratch()
    {
        lock (ScratchGate)
        {
            var scratch = s_sharedScratch ?? new Vpsg3LiveExtractorScratch();
            s_sharedScratch = null;
            return new ScratchLease(scratch, s_scratchGeneration);
        }
    }

    internal static void ReleaseSharedScratch()
    {
        Vpsg3LiveExtractorScratch? retired;
        lock (ScratchGate)
        {
            ++s_scratchGeneration;
            retired = s_sharedScratch;
            s_sharedScratch = null;
        }
        retired?.Dispose();
    }

    internal sealed class ScratchLease(Vpsg3LiveExtractorScratch scratch, int generation) : IDisposable
    {
        private Vpsg3LiveExtractorScratch? _scratch = scratch;
        public Vpsg3LiveExtractorScratch Scratch => _scratch!;
        public void Dispose()
        {
            var returned = Interlocked.Exchange(ref _scratch, null);
            if (returned is null) return;
            returned.ApproxContourBatch.Clear();
            lock (ScratchGate)
            {
                if (generation == s_scratchGeneration && s_sharedScratch is null)
                {
                    s_sharedScratch = returned;
                    return;
                }
            }
            returned.Dispose();
        }
    }
}
