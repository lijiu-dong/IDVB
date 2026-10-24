using OpenCvSharp;

namespace IDVBuff.Core.Imaging;

/// <summary>Exact semantic bit-plane correlation over independently owned readers.
/// Coordinates, padding, lane sampling, score width and integer arithmetic match
/// the managed grid. Callers hold a floor lease until this synchronous batch ends.</summary>
public static class NativeSemanticCorrelationGrid
{
    public static void Score(ReadOnlySpan<Point> points, ReadOnlySpan<ulong> words,
        NativeCompactBitWords warm, NativeCompactBitWords cool,
        int referenceWidth, int referenceHeight, int wordsPerRow,
        int maxDx, int maxDy, int stride, Span<int> scores, int minDx, int minDy, byte[]? classes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stride);
        if (referenceWidth <= 0 || referenceHeight <= 0 || wordsPerRow != checked((referenceWidth + 63) / 64))
            throw new ArgumentException("Invalid grid dimensions.");
        var length = checked(wordsPerRow * referenceHeight);
        if (words.Length != length || warm.Length != length || cool.Length != length)
            throw new ArgumentException("Bit readers do not match the reference grid.");
        if (points.Length > 511 || classes is not null && classes.Length < points.Length)
            throw new ArgumentException("Invalid point/class count for nine score planes.");
        if (maxDx < minDx || maxDy < minDy) return;
        if (scores.Length < checked(((maxDx - minDx) / stride + 1) * ((maxDy - minDy) / stride + 1)))
            throw new ArgumentException("Score output is too small.");
        var warmWords = warm.BorrowWordKernel();
        var coolWords = cool.BorrowWordKernel();
        try
        {
            var columns = (maxDx - minDx) / stride + 1;
            Span<ulong> planes = stackalloc ulong[9]; // Up to 256 sampled points.
            for (var dy = minDy; dy <= maxDy; dy += stride)
            {
                for (var blockX = minDx; blockX <= maxDx; blockX += 64)
                {
                    planes.Clear();
                    for (var pointIndex = 0; pointIndex < points.Length; pointIndex++)
                    {
                        var point = points[pointIndex];
                        var kind = classes?[pointIndex] ?? 0;
                        var useSemantic = kind is 127 or 255;
                        var semanticWords = kind == 127 ? warmWords : coolWords;
                        var x = point.X + blockX;
                        var y = point.Y + dy;
                        if ((uint)y >= (uint)referenceHeight || x >= referenceWidth || x <= -64)
                            continue;
                        ulong carry;
                        if (x < 0)
                        {
                            var index = y * wordsPerRow;
                            carry = (useSemantic ? semanticWords.At(index) : words[index]) << -x;
                        }
                        else
                        {
                            var wordX = x >> 6;
                            var shift = x & 63;
                            var index = y * wordsPerRow + wordX;
                            carry = (useSemantic ? semanticWords.At(index) : words[index]) >> shift;
                            if (shift != 0 && wordX + 1 < wordsPerRow)
                                carry |= (useSemantic ? semanticWords.At(index + 1) : words[index + 1]) << (64 - shift);
                        }
                        var remaining = referenceWidth - x;
                        if (remaining < 64)
                            carry &= (1UL << remaining) - 1;
                        for (var bit = 0; carry != 0; bit++)
                        {
                            var nextCarry = planes[bit] & carry;
                            planes[bit] ^= carry;
                            carry = nextCarry;
                        }
                    }
    
                    var first = (stride - (blockX - minDx) % stride) % stride;
                    var last = Math.Min(63, maxDx - blockX);
                    for (var lane = first; lane <= last; lane += stride)
                    {
                        // Constant plane indices let the JIT avoid loop/index overhead.
                        var hits = (int)((planes[0] >> lane) & 1)
                            | (int)((planes[1] >> lane) & 1) << 1
                            | (int)((planes[2] >> lane) & 1) << 2
                            | (int)((planes[3] >> lane) & 1) << 3
                            | (int)((planes[4] >> lane) & 1) << 4
                            | (int)((planes[5] >> lane) & 1) << 5
                            | (int)((planes[6] >> lane) & 1) << 6
                            | (int)((planes[7] >> lane) & 1) << 7
                            | (int)((planes[8] >> lane) & 1) << 8;
                        scores[(dy - minDy) / stride * columns + (blockX + lane - minDx) / stride] = hits;
                    }
                }
            }
        }
        finally
        {
            // Pointer kernels never outlive these independently owned readers.
            GC.KeepAlive(warm); GC.KeepAlive(cool);
        }
    }
}
