using OpenCvSharp;
using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Features.Maps;

public static partial class MapBackgroundProcessor
{
    // Only an exclusive 8-bit BGRA owner may use this view. Each source band
    // is copied before packing BGR into the same rows, so overlapping channel
    // writes cannot corrupt subsequent reads. The caller must close the view
    // before disposing its BGRA owner; the view owns no pixel allocation.
    internal static Mat CompositeExclusiveBgraToBgr(Mat source)
    {
        if (source.Empty() || source.Type() != MatType.CV_8UC4)
            throw new InvalidOperationException("独占缩略图输入必须为 8 位 BGRA。");
        var width = source.Width;
        var height = source.Height;
        var rows = Math.Max(1, Math.Min(height, Math.Min(64, 65536 / width)));
        Mat? inputCopy = null, alphaInput = null, alphaFloat = null, colorInput = null, colorFloat = null, colorOutput = null;
        var result = Mat.FromPixelData(height, width, MatType.CV_8UC3, source.Data, source.Step());
        try
        {
            inputCopy = CreateCompositeScratch(rows, width, MatType.CV_8UC4, 4, "ExclusiveInputBand");
            alphaInput = CreateCompositeScratch(rows, width, MatType.CV_8UC1, 1, "AlphaInput");
            alphaFloat = CreateCompositeScratch(rows, width, MatType.CV_32FC1, 4, "AlphaFloat");
            colorInput = CreateCompositeScratch(rows, width, MatType.CV_8UC1, 1, "ColorInput");
            colorFloat = CreateCompositeScratch(rows, width, MatType.CV_32FC1, 4, "ColorFloat");
            colorOutput = CreateCompositeScratch(rows, width, MatType.CV_8UC1, 1, "ColorOutput");
            for (var first = 0; first < height; first += rows)
            {
                var count = Math.Min(rows, height - first);
                using var sourceBand = source.RowRange(first, first + count);
                using var input = inputCopy.RowRange(0, count);
                sourceBand.CopyTo(input);
                using var output = result.RowRange(first, first + count);
                using var alpha = alphaInput.RowRange(0, count);
                using var alpha32 = alphaFloat.RowRange(0, count);
                using var color = colorInput.RowRange(0, count);
                using var color32 = colorFloat.RowRange(0, count);
                using var bytes = colorOutput.RowRange(0, count);
                Cv2.ExtractChannel(input, alpha, 3);
                alpha.ConvertTo(alpha32, MatType.CV_32FC1, 1.0 / 255.0);
                for (var channel = 0; channel < 3; channel++)
                {
                    Cv2.ExtractChannel(input, color, channel);
                    color.ConvertTo(color32, MatType.CV_32FC1);
                    Cv2.Multiply(color32, alpha32, color32);
                    color32.ConvertTo(bytes, MatType.CV_8UC1);
                    Cv2.InsertChannel(bytes, output, channel);
                }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
        finally
        {
            foreach (var scratch in new[] { colorOutput, colorFloat, colorInput, alphaFloat, alphaInput, inputCopy })
                if (scratch is not null) { scratch.Dispose(); MemoryJournal.ReleaseResource(scratch); }
        }
    }

    private static Mat CompositeAlphaToBgr(Mat source)
    {
        // Preserve the original OpenCV float multiply/rounding, but work in
        // bounded bands. Full-image split and float rasters served no consumer.
        var width = source.Width;
        var height = source.Height;
        var rows = Math.Max(1, Math.Min(height, Math.Min(64, 65536 / width)));
        var inputType = MatType.MakeType(source.Depth(), 1);
        var inputBytes = checked((int)source.ElemSize1());
        Mat? alphaInput = null, alphaFloat = null, colorInput = null, colorFloat = null, colorOutput = null;
        var result = new Mat(height, width, MatType.CV_8UC3);
        try
        {
            alphaInput = CreateCompositeScratch(rows, width, inputType, inputBytes, "AlphaInput");
            alphaFloat = CreateCompositeScratch(rows, width, MatType.CV_32FC1, 4, "AlphaFloat");
            colorInput = CreateCompositeScratch(rows, width, inputType, inputBytes, "ColorInput");
            colorFloat = CreateCompositeScratch(rows, width, MatType.CV_32FC1, 4, "ColorFloat");
            colorOutput = CreateCompositeScratch(rows, width, MatType.CV_8UC1, 1, "ColorOutput");
            for (var first = 0; first < height; first += rows)
            {
                var count = Math.Min(rows, height - first);
                using var input = source.RowRange(first, first + count);
                using var output = result.RowRange(first, first + count);
                using var alpha = alphaInput.RowRange(0, count);
                using var alpha32 = alphaFloat.RowRange(0, count);
                using var color = colorInput.RowRange(0, count);
                using var color32 = colorFloat.RowRange(0, count);
                using var bytes = colorOutput.RowRange(0, count);
                Cv2.ExtractChannel(input, alpha, 3);
                alpha.ConvertTo(alpha32, MatType.CV_32FC1, 1.0 / 255.0);
                for (var channel = 0; channel < 3; channel++)
                {
                    Cv2.ExtractChannel(input, color, channel);
                    color.ConvertTo(color32, MatType.CV_32FC1);
                    Cv2.Multiply(color32, alpha32, color32);
                    color32.ConvertTo(bytes, MatType.CV_8UC1);
                    Cv2.InsertChannel(bytes, output, channel);
                }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
        finally
        {
            foreach (var scratch in new[] { colorOutput, colorFloat, colorInput, alphaFloat, alphaInput })
                if (scratch is not null) { scratch.Dispose(); MemoryJournal.ReleaseResource(scratch); }
        }
    }

    private static Mat CreateCompositeScratch(int rows, int columns, MatType type, int elementBytes, string name)
    {
        var resource = "Background.CompositeToBgr." + name;
        var bytes = checked((long)rows * columns * elementBytes);
        var request = MemoryJournal.Request(resource, bytes, "OpenCvSharpExtern.dll");
        var scratch = new Mat(rows, columns, type);
        try
        {
            MemoryJournal.TrackResource(scratch, resource, bytes, "OpenCvSharpExtern.dll", request);
            return scratch;
        }
        catch { scratch.Dispose(); throw; }
    }
}
