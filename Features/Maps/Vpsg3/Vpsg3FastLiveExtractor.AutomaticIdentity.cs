using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public static partial class Vpsg3FastLiveExtractor
{
    private const int VisibleCorridorMaximumSaturation = 72;

    private static (Mat Interior, Mat Walls) CreateAutomaticIdentityEvidence(
        Vpsg3LiveExtractorScratch scratch, Mat observedEdges, Mat proposalEdges)
    {
        var interior = CreateSupportedVisibleFill(scratch, observedEdges);
        Mat? walls = null;
        try
        {
            Cv2.Erode(interior, interior, scratch.K11,
                borderType: BorderTypes.Constant, borderValue: Scalar.Black);
            var radius = (int)Math.Ceiling(Vpsg3LiveObservation.AutomaticPoseTolerancePixels);
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2 * radius + 1, 2 * radius + 1));
            using var uncertain = new Mat();
            Cv2.Dilate(scratch.Support, uncertain, kernel);
            interior.SetTo(Scalar.Black, uncertain);
            interior.SetTo(Scalar.Black, scratch.Invalid);
            walls = proposalEdges.Clone();
            walls.SetTo(Scalar.Black, scratch.Invalid);
            return (interior, walls);
        }
        catch { interior.Dispose(); walls?.Dispose(); throw; }
    }

    private static Mat CreateSupportedVisibleFill(Vpsg3LiveExtractorScratch s, Mat observedEdges)
    {
        // Wall proposals deliberately include dim/saturated blue pixels. That
        // permissive mask also includes fog, so it cannot certify empty floor.
        // Classify positive corridor fill separately without changing any of
        // the existing observed/proposal/valid-mask inputs or outputs.
        using var corridor = new Mat();
        Cv2.InRange(s.Hsv, new Scalar(95, 14, 82),
            new Scalar(130, VisibleCorridorMaximumSaturation, 200), corridor);
        corridor.SetTo(Scalar.Black, s.Exclusion);
        Cv2.MorphologyEx(corridor, corridor, MorphTypes.Open, s.K5);
        Cv2.MorphologyEx(corridor, corridor, MorphTypes.Close, s.K3);
        var fill = new Mat();
        Cv2.BitwiseOr(s.Room, corridor, fill);

        // A flat field of palette-colored fog/background supplies no structural
        // witness. Only fill components adjacent to an observed wall contour
        // of the extractor's existing minimum perimeter may become positive.
        // We make no assumption that a crop border is empty or unexplored.
        Cv2.FindContours(observedEdges, out var contours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var supportedContours = contours.Where(contour =>
            Cv2.ArcLength(contour, closed: true) >= ContourMinPerimeter).ToArray();
        if (supportedContours.Length == 0)
        {
            fill.SetTo(Scalar.Black);
            return fill;
        }

        using var witnesses = new Mat(fill.Size(), MatType.CV_8UC1, Scalar.Black);
        Cv2.DrawContours(witnesses, supportedContours, -1, Scalar.White,
            thickness: -1, lineType: LineTypes.Link8);
        Cv2.BitwiseAnd(witnesses, observedEdges, witnesses);
        Cv2.Dilate(witnesses, witnesses, s.K5);
        Cv2.BitwiseAnd(witnesses, fill, witnesses);

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        Cv2.ConnectedComponentsWithStats(fill, labels, stats, centroids,
            PixelConnectivity.Connectivity8);
        using var witnessPoints = new Mat();
        Cv2.FindNonZero(witnesses, witnessPoints);
        var supportedLabels = new HashSet<int>();
        if (!witnessPoints.Empty())
        {
            witnessPoints.GetArray(out Point[] points);
            foreach (var point in points)
                supportedLabels.Add(labels.At<int>(point.Y, point.X));
        }
        fill.SetTo(Scalar.Black);
        using var component = new Mat();
        foreach (var label in supportedLabels)
        {
            if (label == 0) continue;
            Cv2.InRange(labels, new Scalar(label), new Scalar(label), component);
            fill.SetTo(Scalar.White, component);
        }
        return fill;
    }
}
