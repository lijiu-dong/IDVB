using OpenCvSharp;
using System.Runtime.InteropServices;

namespace IDVBuff.Features.Maps;

public static partial class MapViewportPresenceDetector
{
    public static MapViewportColorSignature CreateSignature(Mat viewport) => CreateSignature(viewport, true);

    public static MapViewportColorSignature CreateSignature(Mat viewport, bool includeStructure)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        if (viewport.Empty())
            return new MapViewportColorSignature([], 0d, 0d, null);

        // Channel removal/replication commutes with area resize. Convert only 160x100 pixels.
        using var reduced = new Mat();
        Cv2.Resize(viewport, reduced, new Size(SignatureWidth, SignatureHeight),
            interpolation: InterpolationFlags.Area);
        using var resized = new Mat();
        switch (reduced.Channels())
        {
            case 4:
                Cv2.CvtColor(reduced, resized, ColorConversionCodes.BGRA2BGR);
                break;
            case 3:
                reduced.CopyTo(resized);
                break;
            default:
                Cv2.CvtColor(reduced, resized, ColorConversionCodes.GRAY2BGR);
                break;
        }
        using var hsv = new Mat();
        Cv2.CvtColor(resized, hsv, ColorConversionCodes.BGR2HSV);
        MapViewportStructureSignature? structure = null;
        if (includeStructure)
        {
            using var gray = new Mat();
            Cv2.CvtColor(resized, gray, ColorConversionCodes.BGR2GRAY);
            using var edges = new Mat();
            Cv2.Canny(gray, edges, 45d, 135d);
            structure = CreateStructureSignature(edges);
        }

        var histogram = new double[HueBins * SaturationBins];
        var blueGrayPixels = 0;
        var valueSum = 0d;
        var valuePixels = new byte[hsv.Rows * hsv.Cols];
        var rows = hsv.Rows;
        var columns = hsv.Cols;
        var pixelCount = rows * columns;
        for (var y = 0; y < rows; y++)
        {
            var row = MemoryMarshal.CreateReadOnlySpan(ref hsv.At<Vec3b>(y, 0), columns);
            for (var x = 0; x < columns; x++)
            {
                var pixel = row[x];
                var hueBin = Math.Min(HueBins - 1, pixel.Item0 * HueBins / 180);
                var saturationBin = Math.Min(
                    SaturationBins - 1,
                    pixel.Item1 * SaturationBins / 256);
                var index = (hueBin * SaturationBins) + saturationBin;
                histogram[index]++;

                if (pixel.Item0 is >= 90 and <= 130 && pixel.Item1 < 140)
                    blueGrayPixels++;
                valueSum += pixel.Item2;
                valuePixels[y * columns + x] = pixel.Item2;
            }
        }

        if (pixelCount > 0)
        {
            for (var i = 0; i < histogram.Length; i++)
                histogram[i] /= pixelCount;
        }

        return new MapViewportColorSignature(
            histogram,
            pixelCount > 0 ? blueGrayPixels / (double)pixelCount : 0d,
            pixelCount > 0 ? valueSum / pixelCount : 0d,
            structure) { ValuePixels = valuePixels };
    }

}
