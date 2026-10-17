using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static class NativeStructureTextureMask
{
    // Stair overlays are compact rectangles of repeated thin bars. Their rims
    // and bars are artwork inside a floor, not additional map walls.
    internal static void Exclude(Mat bgr, Mat exclusion)
    {
        using var gray = new Mat();
        using var detail = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.MorphologyEx(gray, detail, MorphTypes.TopHat, kernel);
        Cv2.Threshold(detail, detail, 8, 255, ThresholdTypes.Binary);
        foreach (var vertical in new[] { true, false })
        {
            using var barKernel = Cv2.GetStructuringElement(MorphShapes.Rect, vertical ? new Size(1, 9) : new Size(9, 1));
            using var joinKernel = Cv2.GetStructuringElement(MorphShapes.Rect, vertical ? new Size(9, 1) : new Size(1, 9));
            using var bars = new Mat();
            using var joined = new Mat();
            Cv2.MorphologyEx(detail, bars, MorphTypes.Open, barKernel);
            Cv2.MorphologyEx(bars, joined, MorphTypes.Close, joinKernel);
            Cv2.FindContours(joined, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            foreach (var contour in contours)
            {
                var box = Cv2.BoundingRect(contour);
                if (box.Width < 12 || box.Height < 12 || box.Width > 128 || box.Height > 128) continue;
                var length = vertical ? box.Width : box.Height;
                var extent = vertical ? box.Height : box.Width;
                var runs = 0;
                var run = 0;
                var widest = 0;
                for (var axis = 0; axis < length; axis++)
                {
                    var count = 0;
                    for (var along = 0; along < extent; along++)
                        if (bars.At<byte>(box.Y + (vertical ? along : axis), box.X + (vertical ? axis : along)) != 0) count++;
                    if (count >= extent * .65)
                    {
                        if (run == 0) runs++;
                        widest = Math.Max(widest, ++run);
                    }
                    else run = 0;
                }
                if (runs < 5 || widest > 3) continue;
                var rect = new Rect(box.X - 2, box.Y - 2, box.Width + 4, box.Height + 4)
                    .Intersect(new(0, 0, bgr.Width, bgr.Height));
                Cv2.Rectangle(exclusion, rect, Scalar.White, -1);
            }
        }
    }
}
