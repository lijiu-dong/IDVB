using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static partial class MapStructureScaleEstimator
{
    private sealed record ScaleScore(double Scale, double Cost, double X = double.NaN, double Y = double.NaN);

    // Scale hypotheses are independent, so a cold floor scores them
    // concurrently. Leave cores to the game and the capture threads.
    private static readonly int ParallelScoreWorkers = Math.Clamp(Environment.ProcessorCount - 2, 1, 4);

    private static void RunParallel(int count, Action<int> body)
    {
        if (count <= 1 || ParallelScoreWorkers <= 1)
        {
            for (var index = 0; index < count; index++) body(index);
            return;
        }
        try
        {
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = ParallelScoreWorkers }, body);
        }
        catch (AggregateException exception) when (exception.InnerExceptions.Count > 0)
        {
            // Callers handle OpenCV failures by type; keep the original exception.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerExceptions[0]).Throw();
            throw;
        }
    }

    internal static IReadOnlyList<double> Rank(
        MapStructureFeatures live,
        MapStructureFeatures reference,
        MapStructureRegistrationTuning tuning)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var selection = Analyze(live, reference, tuning);
        timer.Stop();
        selection = selection with { ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds };
        LowStructureScaleSelectionContext.Current = selection;
        return selection.Scales;
    }

    internal static LowStructureScaleSelection Analyze(
        MapStructureFeatures live,
        MapStructureFeatures reference,
        MapStructureRegistrationTuning tuning,
        bool includeAppearanceScale = true,
        double? preferredScale = null,
        bool useScaleHint = false,
        bool refineNativeResolution = false)
    {
        var minimumScale = tuning.LowStructureMinimumScale;
        var maximumScale = tuning.LowStructureMaximumScale;
        MapStructureScaleHint? hint = null;
        if (useScaleHint
            && MapStructureScaleHintEstimator.TryEstimate(
                reference,
                live,
                minimumScale,
                maximumScale,
                out var estimatedHint)
            && estimatedHint.Confidence >= 0.70d)
        {
            hint = estimatedHint;
            var relativeRadius = estimatedHint.Confidence >= 0.82d
                ? 0.05d
                : 0.10d;
            minimumScale = Math.Max(
                minimumScale,
                estimatedHint.Scale * (1d - relativeRadius));
            maximumScale = Math.Min(
                maximumScale,
                estimatedHint.Scale * (1d + relativeRadius));
            if (maximumScale <= minimumScale)
            {
                minimumScale = tuning.LowStructureMinimumScale;
                maximumScale = tuning.LowStructureMaximumScale;
            }
        }
        var coarseGrid = MapStructureScaleSearch.BuildLowStructureScaleHypotheses(
                minimumScale,
                maximumScale,
                tuning.LowStructureScaleHypothesisCount,
                tuning.MinimumUsableScale)
            .OrderBy(scale => scale)
            .ToArray();
        if (Cv2.CountNonZero(live.Edges) < tuning.MinimumEdgePixels)
            return new([], 0d, coarseGrid.Length, true,
                HintScale: hint?.Scale,
                HintConfidence: hint?.Confidence ?? 0d,
                SearchMinimumScale: minimumScale,
                SearchMaximumScale: maximumScale);
        var bounds = MapStructureScaleSearch.FindTemplateBounds(live.Edges);
        if (bounds.Width < tuning.MinimumSpanPixels || bounds.Height < tuning.MinimumSpanPixels)
            return new([], 0d, coarseGrid.Length, true,
                HintScale: hint?.Scale,
                HintConfidence: hint?.Confidence ?? 0d,
                SearchMinimumScale: minimumScale,
                SearchMaximumScale: maximumScale);

        using var livePatch = new Mat(live.Edges, bounds);
        using var semantic = refineNativeResolution
            ? SemanticScaleSearch.CreateCached(reference, live.SemanticLabels,
                live.Edges, bounds, tuning.DistanceClipPixels) : null;
        var asymmetricObserved = live.RawVisibleMask is not null
            && live.DiagnosticTiming?.Profile ==
                MapStructurePreprocessingProfile.NativeObservedStructureLine;
        // Semantic scoring uses its own color distance channels. The geometry
        // distance was previously allocated solely to supply their dimensions.
        var referenceDistance = semantic is null
            ? reference.GetOrCreateClippedReferenceDistanceMap(tuning.DistanceClipPixels)
            : null;
        // A narrow observed strip can collapse to a few rows at factor eight,
        // eliminating the correct basin before any fine search gets to see it.
        var coarseFactor = refineNativeResolution
            ? 2 : Math.Max(4, tuning.FastCoarseDownsampleFactor * 2);
        using var coarseDistance = referenceDistance is null ? null : Resize(referenceDistance, coarseFactor);
        using var coarseEdges = semantic is null ? Resize(reference.Edges, coarseFactor) : null;
        var coarse = ScoreScales(
                coarseGrid,
                coarseFactor,
                ReducedSize(reference.Edges.Size(), coarseFactor),
                coarseDistance,
                coarseEdges,
                livePatch,
                tuning.DistanceClipPixels,
                asymmetricObserved, semantic)
            .OrderBy(item => item.Cost)
            .ThenBy(item => PreferredScaleDistance(item.Scale, preferredScale))
            .ToArray();
        var topBasinScales = coarse
            .Take(2)
            .Select(item => item.Scale)
            .ToArray();
        if (coarse.Length == 0)
            return new([], 0d, coarseGrid.Length, true,
                HintScale: hint?.Scale,
                HintConfidence: hint?.Confidence ?? 0d,
                SearchMinimumScale: minimumScale,
                SearchMaximumScale: maximumScale,
                TopBasinScales: topBasinScales);

        if (refineNativeResolution)
            return RefineNativeBasins(coarse, coarseGrid, referenceDistance,
                reference.Edges, livePatch, tuning.DistanceClipPixels,
                asymmetricObserved, hint, minimumScale, maximumScale, semantic);

        var ambiguous = IsAmbiguous(coarse);
        var fineFactor = Math.Max(2, tuning.FastCoarseDownsampleFactor);
        using var fineDistance = Resize(referenceDistance!, fineFactor);
        using var fineEdges = Resize(reference.Edges, fineFactor);
        var winnerIndex = Array.FindIndex(
            coarseGrid,
            scale => Math.Abs(scale - coarse[0].Scale) < 1e-9d);
        var firstRound = BuildRefinementRound(coarseGrid, winnerIndex, 5);
        var firstScores = ScoreScales(
                firstRound,
                fineFactor,
                fineDistance.Size(),
                fineDistance,
                fineEdges,
                livePatch,
                tuning.DistanceClipPixels,
                asymmetricObserved)
            .OrderBy(item => item.Cost)
            .ToArray();
        if (firstScores.Length == 0)
            return new(
                [coarse[0].Scale],
                AdjacentResolution(coarseGrid, coarse[0].Scale),
                coarseGrid.Length,
                false,
                HintScale: hint?.Scale,
                HintConfidence: hint?.Confidence ?? 0d,
                SearchMinimumScale: minimumScale,
                SearchMaximumScale: maximumScale,
                TopBasinScales: topBasinScales);

        var firstOrdered = firstRound.OrderBy(scale => scale).ToArray();
        var firstWinnerIndex = Array.FindIndex(
            firstOrdered,
            scale => Math.Abs(scale - firstScores[0].Scale) < 1e-9d);
        var secondRound = BuildRefinementRound(firstOrdered, firstWinnerIndex, 5);
        var finalScores = ScoreScales(
                secondRound,
                fineFactor,
                fineDistance.Size(),
                fineDistance,
                fineEdges,
                livePatch,
                tuning.DistanceClipPixels,
                asymmetricObserved)
            .OrderBy(item => item.Cost)
            .ThenBy(item => PreferredScaleDistance(item.Scale, preferredScale))
            .ToArray();
        var finalGrid = secondRound.OrderBy(scale => scale).ToArray();
        if (finalScores.Length == 0)
        {
            finalScores = firstScores;
            finalGrid = firstOrdered;
        }

        var coarseWinner = finalScores[0].Scale;
        var fittedWinner = FitLogScaleMinimum(
            finalScores,
            finalGrid,
            coarseWinner);
        var winner = fittedWinner.Scale;
        var appearanceWinner = includeAppearanceScale
            ? FindAppearanceScale(live, reference, bounds, coarseGrid)
            : null;
        var selected = SelectExactCandidates(
            winner,
            finalGrid,
            coarse.Select(item => item.Scale).ToArray(),
            ambiguous)
            .Prepend(appearanceWinner ?? winner)
            .Take(2)
            .Append(coarseGrid[0])
            .DistinctBy(scale => Math.Round(scale, 9))
            .Take(3)
            .ToArray();
        return new(
            selected,
            AdjacentResolution(finalGrid, coarseWinner),
            coarseGrid.Length,
            ambiguous,
            BestCost: fittedWinner.Cost,
            SecondCost: finalScores.ElementAtOrDefault(1)?.Cost
                ?? double.PositiveInfinity,
            HintScale: hint?.Scale,
            HintConfidence: hint?.Confidence ?? 0d,
            SearchMinimumScale: minimumScale,
            SearchMaximumScale: maximumScale,
            TopBasinScales: topBasinScales);
    }

    internal static IReadOnlyList<double> BuildCoarseGrid(
        double minimumScale,
        double maximumScale,
        int count,
        double minimumUsableScale = 0.05d,
        double? preferredScale = null) =>
        MapStructureScaleSearch.BuildLowStructureScaleHypotheses(
            minimumScale,
            maximumScale,
            count,
            minimumUsableScale,
            preferredScale);

    private static double? FindAppearanceScale(
        MapStructureFeatures live,
        MapStructureFeatures reference,
        Rect bounds,
        IReadOnlyList<double> coarseGrid)
    {
        if (live.NormalizedGray.Empty() || reference.NormalizedGray.Empty())
            return null;
        const int factor = 4;
        using var livePatch = new Mat(live.NormalizedGray, bounds);
        using var coarseReference = Resize(reference.NormalizedGray, factor);
        var coarse = ScoreAppearanceScales(
                coarseGrid,
                livePatch,
                coarseReference,
                factor)
            .OrderBy(score => score.Cost)
            .ToArray();
        if (coarse.Length == 0)
            return null;
        var winnerIndex = Array.FindIndex(
            coarseGrid.ToArray(),
            scale => Math.Abs(scale - coarse[0].Scale) < 1e-9d);
        var fineGrid = BuildRefinementRound(coarseGrid, winnerIndex, 5);
        var fineReference = reference.NormalizedGray;
        var fineScores = ScoreAppearanceScales(
            fineGrid,
            livePatch,
            fineReference,
            1)
            .OrderBy(score => score.Cost)
            .ToArray();
        if (fineScores.Length == 0)
            return coarse[0].Scale;
        var orderedFineGrid = fineGrid.OrderBy(scale => scale).ToArray();
        var fineWinnerIndex = Array.FindIndex(
            orderedFineGrid,
            scale => Math.Abs(scale - fineScores[0].Scale) < 1e-9d);
        var finalGrid = BuildRefinementRound(
            orderedFineGrid,
            fineWinnerIndex,
            5);
        var finalScores = ScoreAppearanceScales(
                finalGrid,
                livePatch,
                fineReference,
                1)
            .OrderBy(score => score.Cost)
            .ToArray();
        return finalScores.FirstOrDefault()?.Scale ?? fineScores[0].Scale;
    }

    private static IReadOnlyList<ScaleScore> ScoreAppearanceScales(
        IEnumerable<double> candidates,
        Mat livePatch,
        Mat referenceGray,
        int factor)
    {
        var scores = new List<ScaleScore>();
        foreach (var scale in candidates)
        {
            var size = new Size(
                Math.Max(1, (int)Math.Round(livePatch.Width / scale / factor)),
                Math.Max(1, (int)Math.Round(livePatch.Height / scale / factor)));
            if (size.Width < 8 || size.Height < 8
                || size.Width >= referenceGray.Width
                || size.Height >= referenceGray.Height)
            {
                continue;
            }
            using var template = new Mat();
            Cv2.Resize(
                livePatch,
                template,
                size,
                interpolation: InterpolationFlags.Area);
            using var scoreMap = new Mat();
            Cv2.MatchTemplate(
                referenceGray,
                template,
                scoreMap,
                TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(scoreMap, out _, out var maximum, out _, out _);
            if (double.IsFinite(maximum))
                scores.Add(new(scale, -maximum));
        }
        return scores;
    }

    internal static IReadOnlyList<double> SelectExactCandidates(
        double refinedWinner,
        IReadOnlyList<double> refinedGrid,
        IReadOnlyList<double> orderedCoarseBasins,
        bool ambiguous)
    {
        var selected = new List<double> { refinedWinner };
        if (ambiguous)
        {
            selected.AddRange(orderedCoarseBasins
                .Skip(1)
                .Where(scale => LowStructureScaleEvidenceRules.RelativeDifference(
                    scale,
                    orderedCoarseBasins[0]) > 0.03d));
        }
        else
        {
            var winnerIndex = refinedGrid
                .Select((scale, index) => (scale, index))
                .OrderBy(item => Math.Abs(item.scale - refinedWinner))
                .FirstOrDefault().index;
            if (winnerIndex > 0)
                selected.Add(refinedGrid[winnerIndex - 1]);
            if (winnerIndex + 1 < refinedGrid.Count)
                selected.Add(refinedGrid[winnerIndex + 1]);
        }
        return selected
            .DistinctBy(scale => Math.Round(scale, 9))
            .Take(3)
            .ToArray();
    }

    private static bool IsAmbiguous(IReadOnlyList<ScaleScore> ordered)
    {
        if (ordered.Count < 2)
            return false;
        return (ordered[1].Cost - ordered[0].Cost)
            / Math.Max(Math.Abs(ordered[0].Cost), 0.05d) < 0.005d;
    }

    private static IReadOnlyList<ScaleScore> ScoreScales(
        IEnumerable<double> candidates,
        int factor,
        Size referenceSize,
        Mat? referenceDistance,
        Mat? referenceEdges,
        Mat livePatch,
        double distanceClipPixels,
        bool asymmetricObserved,
        SemanticScaleSearch? semantic = null, ScaleScore? anchor = null, int radius = 0)
    {
        var scores = new List<ScaleScore>();
        if (semantic is not null && factor == 1 && anchor is not null)
        {
            candidates = candidates as IReadOnlyList<double> ?? candidates.ToArray();
            if (ScoreLocalSemanticGrid(candidates, referenceSize, livePatch, semantic, anchor, radius) is { } local)
                return local;
        }
        if (semantic is not null && factor != 1)
            return ScoreSemanticBatch(candidates, factor, referenceSize, livePatch, semantic, anchor, radius);
        if (semantic is not null && anchor is null)
            candidates = ReserveSemanticGrid(candidates, factor, referenceSize, livePatch, semantic);
        foreach (var scale in candidates)
        {
            var size = new Size(
                Math.Max(1, (int)Math.Round(livePatch.Width / scale / factor)),
                Math.Max(1, (int)Math.Round(livePatch.Height / scale / factor)));
            if (size.Width < 3 || size.Height < 3
                || size.Width >= referenceSize.Width || size.Height >= referenceSize.Height)
                continue;
            if (semantic is not null)
            {
                var region = SemanticSearchRegion(anchor, livePatch.Size(), size,
                    referenceSize, factor, radius);
                var cost = semantic.Score(size, factor, region, out var location);
                if (double.IsFinite(cost)) scores.Add(new(scale, cost, location.X * factor, location.Y * factor));
                continue;
            }
            using var template = new Mat();
            Cv2.Resize(livePatch, template, size, interpolation: InterpolationFlags.Area);
            using var templateFloat = new Mat();
            template.ConvertTo(templateFloat, MatType.CV_32FC1, 1d / 255d);
            var mass = Cv2.Sum(templateFloat).Val0;
            if (mass < 1d)
                continue;
            using var scoreMap = new Mat();
            Cv2.MatchTemplate(referenceDistance!, templateFloat, scoreMap, TemplateMatchModes.CCorr);
            Cv2.MinMaxLoc(scoreMap, out var minimum, out _, out var minimumLocation, out _);
            if (!double.IsFinite(minimum))
                continue;

            using var binaryTemplate = new Mat();
            Cv2.Threshold(template, binaryTemplate, 32d, 255d, ThresholdTypes.Binary);
            using var invertedTemplate = new Mat();
            Cv2.BitwiseNot(binaryTemplate, invertedTemplate);
            using var templateDistance = new Mat();
            Cv2.DistanceTransform(
                invertedTemplate,
                templateDistance,
                DistanceTypes.L2,
                DistanceTransformMasks.Mask3);
            using var referenceWindow = new Mat(
                referenceEdges!,
                new Rect(minimumLocation.X, minimumLocation.Y, size.Width, size.Height));
            using var referenceBinary = new Mat();
            Cv2.Threshold(referenceWindow, referenceBinary, 32d, 1d, ThresholdTypes.Binary);
            using var referenceFloat = new Mat();
            referenceBinary.ConvertTo(referenceFloat, MatType.CV_32FC1);
            var referenceMass = Cv2.Sum(referenceFloat).Val0;
            var reverse = referenceMass < 1d
                ? distanceClipPixels
                : Cv2.Sum(templateDistance.Mul(referenceFloat)).Val0 / referenceMass;
            scores.Add(new(scale, asymmetricObserved
                ? minimum / mass
                : ((minimum / mass) * 0.95d) + (reverse * 0.05d)));
        }
        return scores;
    }

    // Same requests, order and results as the sequential semantic loop below.
    private static IReadOnlyList<ScaleScore> ScoreSemanticBatch(IEnumerable<double> candidates, int factor,
        Size referenceSize, Mat livePatch, SemanticScaleSearch semantic, ScaleScore? anchor, int radius)
    {
        var requests = new List<(double Scale, Size Size, Rect? Region)>();
        foreach (var scale in candidates)
        {
            var size = new Size(
                Math.Max(1, (int)Math.Round(livePatch.Width / scale / factor)),
                Math.Max(1, (int)Math.Round(livePatch.Height / scale / factor)));
            if (size.Width < 3 || size.Height < 3
                || size.Width >= referenceSize.Width || size.Height >= referenceSize.Height)
                continue;
            requests.Add((scale, size, SemanticSearchRegion(anchor, livePatch.Size(), size,
                referenceSize, factor, radius)));
        }
        var costs = semantic.ScoreBatch(requests.Select(request => (request.Size, request.Region)).ToArray(),
            factor, out var locations);
        var scores = new List<ScaleScore>(requests.Count);
        for (var i = 0; i < requests.Count; i++)
            if (double.IsFinite(costs[i]))
                scores.Add(new(requests[i].Scale, costs[i], locations[i].X * factor, locations[i].Y * factor));
        return scores;
    }

    private static double AdjacentResolution(IReadOnlyList<double> grid, double scale)
    {
        var ordered = grid.OrderBy(item => item).ToArray();
        var index = Array.FindIndex(ordered, item => Math.Abs(item - scale) < 1e-9d);
        if (index < 0 || ordered.Length < 2)
            return 0d;
        var differences = new List<double>();
        if (index > 0)
            differences.Add(LowStructureScaleEvidenceRules.RelativeDifference(ordered[index], ordered[index - 1]));
        if (index + 1 < ordered.Length)
            differences.Add(LowStructureScaleEvidenceRules.RelativeDifference(ordered[index], ordered[index + 1]));
        return differences.Count == 0 ? 0d : differences.Min();
    }

    private static IReadOnlyList<double> BuildRefinementRound(
        IReadOnlyList<double> grid,
        int winnerIndex,
        int pointCount)
    {
        if (grid.Count == 0 || winnerIndex < 0)
            return [];
        winnerIndex = Math.Min(winnerIndex, grid.Count - 1);
        var winner = grid[winnerIndex];
        var lower = winnerIndex > 0
            ? Math.Sqrt(grid[winnerIndex - 1] * winner)
            : winner / Math.Sqrt(grid[Math.Min(1, grid.Count - 1)] / winner);
        var upper = winnerIndex + 1 < grid.Count
            ? Math.Sqrt(winner * grid[winnerIndex + 1])
            : winner * Math.Sqrt(winner / grid[Math.Max(0, winnerIndex - 1)]);
        if (!double.IsFinite(lower) || !double.IsFinite(upper) || lower <= 0d || upper < lower)
            return [winner];
        var count = Math.Clamp(pointCount, 3, 5);
        return Enumerable.Range(0, count)
            .Select(index => lower * Math.Exp(Math.Log(upper / lower) * index / (count - 1d)))
            .ToArray();
    }

    internal static IReadOnlyList<double> BuildFineScaleGrid(
        IReadOnlyList<double> coarseGrid,
        int winnerIndex,
        double maximumRelativeStep)
    {
        if (coarseGrid.Count == 0 || winnerIndex < 0)
            return [];
        winnerIndex = Math.Min(winnerIndex, coarseGrid.Count - 1);
        var lower = coarseGrid[Math.Max(0, winnerIndex - 1)];
        var upper = coarseGrid[Math.Min(coarseGrid.Count - 1, winnerIndex + 1)];
        if (!double.IsFinite(lower) || !double.IsFinite(upper) || lower <= 0d || upper < lower)
            return [];
        if (Math.Abs(upper - lower) < 1e-9d)
            return [lower];
        var segments = Math.Clamp(
            (int)Math.Ceiling(Math.Log(upper / lower)
                / Math.Clamp(maximumRelativeStep, 0.0025d, 0.02d)),
            2,
            48);
        return Enumerable.Range(0, segments + 1)
            .Select(index => lower * Math.Exp(Math.Log(upper / lower) * index / segments))
            .ToArray();
    }
}

