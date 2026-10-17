using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Production translation candidate generator for VPSG 3.0 (Method T-3).
/// Operates directly on resident PreparedFloor 64-bit row-major bitsets.
/// Prohibits runtime Mat resizing, DistanceTransform, or MatchTemplate.
/// Generates Top-1 candidate and distinct Top-2 runner-up with spatial NMS suppression.
/// </summary>
public static class Vpsg3TranslationSolver
{
    /// <summary>
    /// Generates translation candidates and detects presence of a distinct spatial runner-up.
    /// </summary>
    public static (Vpsg3TranslationCandidate Top1, Vpsg3TranslationCandidate? DistinctRunnerUp, Vpsg3TranslationCandidate? DistinctRunnerUp2, bool HasDistinctRunnerUp) GenerateCandidates(
        Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor preparedFloor,
        double estimatedScale,
        Vpsg3TuningConfig? config = null,
        Vpsg3SolverScratch? scratch = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(preparedFloor);

        var cfg = config ?? Vpsg3TuningConfig.Default;
        using var scratchLease = scratch is null ? Vpsg3SolverScratch.Rent() : null;
        var sc = scratch ?? scratchLease!.Scratch;
        sc.CandidateCount = 0;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cfg.CoarseTranslationStride);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cfg.MaxSparsePoints);

        var sparsePoints = observation.SparseEdgePoints;
        var classes = (sparsePoints as Vpsg3SemanticPoints)?.Classes;
        var pointCount = sparsePoints.Count;
        if (pointCount == 0 || !double.IsFinite(estimatedScale) || estimatedScale <= 0.01d)
        {
            return (default, null, null, false);
        }

        // 1. Scale sparse query points to reference scale into scratch buffer (zero allocation)
        var limitPts = Math.Min(pointCount, Math.Min(cfg.MaxSparsePoints, sc.ScaledQueryPointsBuffer.Length));
        var scaledPts = sc.ScaledQueryPointsBuffer.AsSpan(0, limitPts);
        var invScale = 1.0d / estimatedScale;

        for (var i = 0; i < limitPts; i++)
        {
            var p = sparsePoints[i];
            scaledPts[i] = new Point(
                (int)Math.Round(p.X * invScale),
                (int)Math.Round(p.Y * invScale));
        }

        var refW = preparedFloor.ReferenceWidth;
        var refH = preparedFloor.ReferenceHeight;
        var stride = cfg.CoarseTranslationStride;

        // A passing weighted score needs at least this many in-reference points.
        // Quantile bounds include every such transform, including padded viewports.
        Span<int> xs = stackalloc int[limitPts];
        Span<int> ys = stackalloc int[limitPts];
        for (var i = 0; i < limitPts; i++) { xs[i] = scaledPts[i].X; ys[i] = scaledPts[i].Y; }
        xs.Sort(); ys.Sort();
        var misses = Math.Clamp(limitPts - (int)Math.Ceiling(cfg.MinVerificationScore * limitPts), 0, limitPts - 1);
        var minDx = -xs[misses] - 2;
        var maxDx = refW + 1 - xs[limitPts - 1 - misses];
        var minDy = -ys[misses] - 2;
        var maxDy = refH + 1 - ys[limitPts - 1 - misses];
        if (maxDx < minDx || maxDy < minDy) return (default, null, null, false);
        var columns = (maxDx - minDx) / stride + 1;
        var scoreCount = checked(columns * ((maxDy - minDy) / stride + 1));
        sc.EnsureTranslationCapacity(scoreCount);
        var scores = sc.TranslationScores.AsSpan(0, scoreCount);
        ScoreClassifiedGridBands(sc.ScaledQueryPointsBuffer, limitPts, preparedFloor, maxDx, maxDy, stride,
            sc.TranslationScores, minDx, minDy, classes);

        // 2. Coarse 2D translation grid search across reference space maintaining Top-32 candidates
        Span<CoarseCand> topPool = stackalloc CoarseCand[32];
        var poolCount = 0;

        for (var dy = minDy; dy <= maxDy; dy += stride)
        {
            for (var dx = minDx; dx <= maxDx; dx += stride)
            {
                var hits = scores[(dy - minDy) / stride * columns + (dx - minDx) / stride];

                if (hits >= 5)
                {
                    InsertCoarseCandidate(topPool, ref poolCount, dx, dy, hits);
                }
            }
        }

        if (poolCount == 0 || topPool[0].Hits < 5)
        {
            return (default, null, null, false);
        }

        // 3. Sub-window +/-2px polish for all pool candidates to eliminate coarse grid quantization error
        Span<CoarseCand> polished = stackalloc CoarseCand[poolCount];
        for (var i = 0; i < poolCount; i++)
        {
            var (pDx, pDy, pHits) = PolishTranslationSubWindow(
                scaledPts, preparedFloor, topPool[i].Dx, topPool[i].Dy, maxDx, maxDy, minDx, minDy, classes);
            polished[i] = new CoarseCand(pDx, pDy, pHits);
        }
        SortCoarsePool(polished);

        // 4. Post-polish deduplication into distinct physical peaks
        Span<CoarseCand> distinctPeaks = stackalloc CoarseCand[poolCount];
        var distinctCount = 0;
        for (var i = 0; i < poolCount; i++)
        {
            var cand = polished[i];
            var isDuplicate = false;
            for (var j = 0; j < distinctCount; j++)
            {
                var existing = distinctPeaks[j];
                // In reference space, candidates within 2px are samples of the same physical peak
                if (Math.Abs(cand.Dx - existing.Dx) <= 2 && Math.Abs(cand.Dy - existing.Dy) <= 2)
                {
                    isDuplicate = true;
                    break;
                }
            }

            if (!isDuplicate)
            {
                distinctPeaks[distinctCount++] = cand;
            }
        }

        var best = distinctPeaks[0];
        // Retain the already-scored pool so merged refined peaks can be replaced
        // without correlating the reference grid again.
        for (var i = 0; i < distinctCount; i++)
        {
            var c = distinctPeaks[i];
            sc.CandidateBuffer[i] = new Vpsg3TranslationCandidate(
                observation.ViewportBounds.X - c.Dx * estimatedScale,
                observation.ViewportBounds.Y - c.Dy * estimatedScale, c.Hits, i + 1);
        }
        sc.CandidateCount = distinctCount;
        var top1Offset = new Vpsg3TranslationCandidate(
            OffsetX: observation.ViewportBounds.X - best.Dx * estimatedScale,
            OffsetY: observation.ViewportBounds.Y - best.Dy * estimatedScale,
            RawScore: best.Hits,
            Rank: 1);

        // 5. Distinct runner-up selection outside NMS suppression basin of best
        var minDistinctDistRef = cfg.MinDistinctDistance * invScale;
        var minDistinctDistSqRef = minDistinctDistRef * minDistinctDistRef;

        Vpsg3TranslationCandidate? runnerUpCandidate1 = null;
        Vpsg3TranslationCandidate? runnerUpCandidate2 = null;

        for (var i = 1; i < distinctCount; i++)
        {
            var c = distinctPeaks[i];
            var distSq = (c.Dx - best.Dx) * (c.Dx - best.Dx) + (c.Dy - best.Dy) * (c.Dy - best.Dy);
            if (distSq >= minDistinctDistSqRef && c.Hits >= 3)
            {
                if (runnerUpCandidate1 is null)
                {
                    runnerUpCandidate1 = new Vpsg3TranslationCandidate(
                        OffsetX: observation.ViewportBounds.X - c.Dx * estimatedScale,
                        OffsetY: observation.ViewportBounds.Y - c.Dy * estimatedScale,
                        RawScore: c.Hits,
                        Rank: 2);
                }
                else
                {
                    runnerUpCandidate2 = new Vpsg3TranslationCandidate(
                        OffsetX: observation.ViewportBounds.X - c.Dx * estimatedScale,
                        OffsetY: observation.ViewportBounds.Y - c.Dy * estimatedScale,
                        RawScore: c.Hits,
                        Rank: 3);
                    break;
                }
            }
        }

        // If not found in polished pool, search coarse grid outside NMS basin
        if (runnerUpCandidate1 is null)
        {
            var runnerUpDx = 0;
            var runnerUpDy = 0;
            var runnerUpHits = 0;
            var foundRunnerUp = false;

            for (var dy = minDy; dy <= maxDy; dy += stride)
            {
                for (var dx = minDx; dx <= maxDx; dx += stride)
                {
                    var distSq = (dx - best.Dx) * (dx - best.Dx) + (dy - best.Dy) * (dy - best.Dy);
                    if (distSq < minDistinctDistSqRef)
                        continue;

                    var hits = scores[(dy - minDy) / stride * columns + (dx - minDx) / stride];

                    if (hits > runnerUpHits)
                    {
                        runnerUpHits = hits;
                        runnerUpDx = dx;
                        runnerUpDy = dy;
                        foundRunnerUp = true;
                    }
                }
            }

            if (foundRunnerUp && runnerUpHits >= 3)
            {
                var (pDx2, pDy2, pHits2) = PolishTranslationSubWindow(
                    scaledPts, preparedFloor, runnerUpDx, runnerUpDy, maxDx, maxDy, minDx, minDy, classes);

                var pDistSq = (pDx2 - best.Dx) * (pDx2 - best.Dx) + (pDy2 - best.Dy) * (pDy2 - best.Dy);
                if (pDistSq >= minDistinctDistSqRef)
                {
                    runnerUpCandidate1 = new Vpsg3TranslationCandidate(
                        OffsetX: observation.ViewportBounds.X - pDx2 * estimatedScale,
                        OffsetY: observation.ViewportBounds.Y - pDy2 * estimatedScale,
                        RawScore: pHits2,
                        Rank: 2);
                }
            }
        }

        return (top1Offset, runnerUpCandidate1, runnerUpCandidate2, runnerUpCandidate1.HasValue);
    }

    private readonly record struct CoarseCand(int Dx, int Dy, int Hits);

    // Every translation row is scored independently, so bands of rows run
    // concurrently and write disjoint score ranges. Results are bit-identical.
    private static readonly int TranslationWorkers = Math.Clamp(Environment.ProcessorCount - 2, 1, 4);

    private static void ScoreClassifiedGridBands(Point[] points, int pointCount, Vpsg3PreparedFloor floor,
        int maxDx, int maxDy, int stride, int[] scores, int minDx, int minDy, byte[]? classes)
    {
        var rows = (maxDy - minDy) / stride + 1;
        var columns = (maxDx - minDx) / stride + 1;
        var bands = Math.Min(rows, TranslationWorkers * 2);
        if (TranslationWorkers <= 1 || bands <= 1)
        {
            ScoreClassifiedGrid(points.AsSpan(0, pointCount), floor, maxDx, maxDy, stride,
                scores.AsSpan(0, rows * columns), minDx, minDy, classes);
            return;
        }
        // Derive the lazily built geometry once, under its own lock, before sharing it.
        _ = floor.DilatedBitsetK3Span.Length;
        try
        {
            Parallel.For(0, bands, new ParallelOptions { MaxDegreeOfParallelism = TranslationWorkers }, band =>
            {
                var first = rows * band / bands;
                var last = rows * (band + 1) / bands;
                if (first == last) return;
                ScoreClassifiedGrid(points.AsSpan(0, pointCount), floor, maxDx, minDy + (last - 1) * stride, stride,
                    scores.AsSpan(first * columns, (last - first) * columns), minDx, minDy + first * stride, classes);
            });
        }
        catch (AggregateException exception) when (exception.InnerExceptions.Count > 0)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerExceptions[0]).Throw();
            throw;
        }
    }

    // Each bit lane counts one translation. Binary addition scores 64 offsets together,
    // preserving integer scores, grid order and ties; the runner-up reuses the same grid.
    internal static void ScoreCoarseGrid(ReadOnlySpan<Point> points, Vpsg3PreparedFloor floor,
        int maxDx, int maxDy, int stride, Span<int> scores, int minDx = 0, int minDy = 0)
        => ScoreClassifiedGrid(points, floor, maxDx, maxDy, stride, scores, minDx, minDy, null);

    private static void ScoreClassifiedGrid(ReadOnlySpan<Point> points, Vpsg3PreparedFloor floor,
        int maxDx, int maxDy, int stride, Span<int> scores, int minDx, int minDy, byte[]? classes)
    {
        // A floor lease keeps these immutable words alive throughout the grid.
        // Resolve lazy geometry and class fields once, rather than for every
        // point at every translation. No scores, search bounds or pixels change.
        var words = floor.DilatedBitsetK3Span;
        var semantic = floor.SemanticIndex;
        var warm = semantic?.BitsK3(SemanticStructureContours.Warm);
        var cool = semantic?.BitsK3(SemanticStructureContours.Cool);
        if (warm?.NativeWords is { } nativeWarm && cool?.NativeWords is { } nativeCool)
        {
            IDVBuff.Core.Imaging.NativeSemanticCorrelationGrid.Score(points, words, nativeWarm, nativeCool,
                floor.ReferenceWidth, floor.ReferenceHeight, floor.WordsPerRow,
                maxDx, maxDy, stride, scores, minDx, minDy, classes);
            return;
        }
        // Keep the managed path's original direct CompactBitWords access.
        var warmWords = warm?.ManagedWords;
        var coolWords = cool?.ManagedWords;
        var referenceWidth = floor.ReferenceWidth;
        var referenceHeight = floor.ReferenceHeight;
        var wordsPerRow = floor.WordsPerRow;
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
                    var semanticWords = kind == SemanticStructureContours.Warm ? warmWords
                        : kind == SemanticStructureContours.Cool ? coolWords : null;
                    var x = point.X + blockX;
                    var y = point.Y + dy;
                    if ((uint)y >= (uint)referenceHeight || x >= referenceWidth || x <= -64)
                        continue;
                    ulong carry;
                    if (x < 0)
                    {
                        var index = y * wordsPerRow;
                        carry = (semanticWords is null ? words[index] : semanticWords[index]) << -x;
                    }
                    else
                    {
                        var wordX = x >> 6;
                        var shift = x & 63;
                        var index = y * wordsPerRow + wordX;
                        carry = (semanticWords is null ? words[index] : semanticWords[index]) >> shift;
                        if (shift != 0 && wordX + 1 < wordsPerRow)
                            carry |= (semanticWords is null ? words[index + 1] : semanticWords[index + 1]) << (64 - shift);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InsertCoarseCandidate(
        Span<CoarseCand> pool,
        ref int count,
        int dx,
        int dy,
        int hits)
    {
        // Grid coordinates are unique. Reject noncompetitive scores before sorting.
        if (count == pool.Length && hits <= pool[count - 1].Hits) return;

        if (count < pool.Length)
        {
            pool[count++] = new CoarseCand(dx, dy, hits);
            SortCoarsePool(pool.Slice(0, count));
        }
        else if (hits > pool[pool.Length - 1].Hits)
        {
            pool[pool.Length - 1] = new CoarseCand(dx, dy, hits);
            SortCoarsePool(pool);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SortCoarsePool(Span<CoarseCand> pool)
    {
        for (var i = 1; i < pool.Length; i++)
        {
            var key = pool[i];
            var j = i - 1;
            while (j >= 0 && pool[j].Hits < key.Hits)
            {
                pool[j + 1] = pool[j];
                j--;
            }
            pool[j + 1] = key;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int PolishedDx, int PolishedDy, int BestHits) PolishTranslationSubWindow(
        ReadOnlySpan<Point> scaledPts,
        Vpsg3PreparedFloor preparedFloor,
        int centerDx,
        int centerDy,
        int maxDx,
        int maxDy,
        int minDx,
        int minDy,
        byte[]? classes = null)
    {
        var bestDx = centerDx;
        var bestDy = centerDy;
        var bestHits = 0;
        for (var i = 0; i < scaledPts.Length; i++)
        {
            var rx = scaledPts[i].X + centerDx;
            var ry = scaledPts[i].Y + centerDy;
            if (Hit(i, rx, ry))
            {
                bestHits++;
            }
        }

        var startY = Math.Max(minDy, centerDy - 2);
        var endY = Math.Min(maxDy, centerDy + 2);
        var startX = Math.Max(minDx, centerDx - 2);
        var endX = Math.Min(maxDx, centerDx + 2);

        for (var ldy = startY; ldy <= endY; ldy++)
        {
            for (var ldx = startX; ldx <= endX; ldx++)
            {
                if (ldx == centerDx && ldy == centerDy)
                    continue;

                var hits = 0;
                for (var i = 0; i < scaledPts.Length; i++)
                {
                    var rx = scaledPts[i].X + ldx;
                    var ry = scaledPts[i].Y + ldy;
                    if (Hit(i, rx, ry))
                    {
                        hits++;
                    }
                }

                if (hits > bestHits)
                {
                    bestHits = hits;
                    bestDx = ldx;
                    bestDy = ldy;
                }
            }
        }

        return (bestDx, bestDy, bestHits);

        bool Hit(int i, int x, int y) => preparedFloor.SemanticIndex is { } semantic
            && classes is not null && classes[i] is SemanticStructureContours.Warm or SemanticStructureContours.Cool
            ? semantic.IsHit(x, y, classes[i], 1) : preparedFloor.IsHitK3(x, y);
    }
}
