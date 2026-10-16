using OpenCvSharp;

namespace IDVBuff.Core.Imaging;

/// <summary>Exact forward residual over a caller-owned native point list and
/// an independently leased precision-distance raster. No point copy or sampling.</summary>
public static class NativeDistanceMetrics
{
    // Borrow the caller's pinned/leased precision raster for this native call.
    // Exact distance zero is precisely the original binary geometry seed set.
    public static unsafe Mat ZeroMask(ReadOnlySpan<float> distances, int width, int height)
    {
        if (width <= 0 || height <= 0 || distances.Length != checked(width * height))
            throw new ArgumentException("Precision raster dimensions do not match its pixels.", nameof(distances));
        var mask = new Mat();
        try
        {
            fixed (float* pixels = distances)
            {
                using var view = Mat.FromPixelData(height, width, MatType.CV_32FC1, (IntPtr)pixels);
                Cv2.InRange(view, Scalar.All(0), Scalar.All(0), mask);
            }
            return mask;
        }
        catch { mask.Dispose(); throw; }
    }

    public static double? ForwardMean(ReadOnlySpan<Point> points, ReadOnlySpan<float> distances,
        int width, int height, double viewportX, double viewportY,
        double scale, double offsetX, double offsetY)
    {
        if (points.IsEmpty) return null;
        if (width <= 0 || height <= 0 || distances.Length != checked(width * height))
            throw new ArgumentException("Precision raster dimensions do not match its pixels.", nameof(distances));
        var sum = 0d;
        foreach (var point in points)
        {
            var x = (point.X + viewportX - offsetX) / scale;
            var y = (point.Y + viewportY - offsetY) / scale;
            // Preserve the original border penalty and summation order exactly.
            if (x < 0 || y < 0 || x >= width - 1 || y >= height - 1)
            {
                sum += 50d;
                continue;
            }
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                sum += double.PositiveInfinity * scale;
                continue;
            }
            var ix = (int)x; var iy = (int)y;
            var fx = x - ix; var fy = y - iy;
            var row = iy * width + ix;
            var distance = (distances[row] * (1 - fx) + distances[row + 1] * fx) * (1 - fy)
                + (distances[row + width] * (1 - fx) + distances[row + width + 1] * fx) * fy;
            sum += distance * scale;
        }
        return sum / points.Length;
    }
}
