using System.Diagnostics.CodeAnalysis;
using IDVBuff.Core.Diagnostics;
using OpenCvSharp;
namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    /// <summary>以目标楼层的预制轮廓完成 VPSG 3.0 尺度、平移和空间验证；失败或未就绪时返回 false，并通过 status 携带原因供调用方回退。</summary>
    public bool TryAlignWithVpsg3(
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey,
        double identityPriorConfidence,
        [NotNullWhen(true)] out MapRecognitionAttempt? attempt,
        out IdvbStatus status,
        double? knownScaleSeed = null,
        bool hasValidatedFloorScale = false,
        bool requireObservableScale = false)
    {
        attempt = null;
        var alignmentStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_disposed)
        {
            status = IdvbStatus.ClientError(
                IdvbHttpCode.ServiceUnavailable,
                IdvbSubCode.StructureCvException,
                "Vpsg3CoreServiceFailed",
                "识别服务已释放",
                stage: "Vpsg3.Precheck");
            return false;
        }

        var channel = MapAlignmentChannelRegistry.Resolve(map, floorKey).Channel;
        var isLowStructure = channel == MapAlignmentChannel.LowStructure;
        if (!CanUseVpsg3(channel, knownScaleSeed, hasValidatedFloorScale))
        {
            status = IdvbStatus.ClientError(
                IdvbHttpCode.FeatureDisabled,
                IdvbSubCode.Vpsg3FallbackDegraded,
                "Vpsg3LowStructureUnsupported",
                "低结构楼层尚无独立验证的本楼层尺度",
                stage: "Vpsg3.ChannelCheck");
            return false;
        }

        if (!TryGetVpsg3IndexKey(map, floorKey, out var key))
        {
            status = IdvbStatus.ClientError(
                IdvbHttpCode.MapNotFound,
                IdvbSubCode.FloorKeyNotMatched,
                "Vpsg3PrebuiltMissing",
                $"目标楼层缺少可用的预制线图 · map={map.SequenceNumber}#{floorKey}",
                stage: "Vpsg3.KeyResolution");
            return false;
        }

        if (!_vpsg3Registry.TryGet(key, out var lease))
        {
            _vpsg3Registry.TryGetDetailedStatus(
                key,
                out var indexStatus,
                out var statusAge,
                out var failureReason,
                out var actualKey);

            var keyMismatch = actualKey.HasValue && actualKey.Value != key;
            var detailReason = indexStatus switch
            {
                Vpsg3IndexStatus.Missing => "索引未构建(未进入预构建队列或prebuilt文件不合规)",
                Vpsg3IndexStatus.Building => $"索引正在后台构建中 (已耗时 {statusAge.TotalMilliseconds:F0}ms)",
                Vpsg3IndexStatus.Failed => $"索引后台构建失败: {failureReason ?? "未知错误"}",
                Vpsg3IndexStatus.Stale => "索引已失效(版本/时间戳变更)",
                _ when keyMismatch => "缓存键不匹配",
                _ => "索引未就绪"
            };

            var reasonPhrase = indexStatus switch
            {
                Vpsg3IndexStatus.Missing => "Vpsg3PrebuiltMissing",
                Vpsg3IndexStatus.Failed => "Vpsg3CoreServiceFailed",
                _ => "Vpsg3IndexNotReady"
            };

            status = IdvbStatus.Fallback(
                IdvbHttpCode.Vpsg3FallbackToLegacy,
                IdvbSubCode.Vpsg3FallbackIndexNotReady,
                reasonPhrase,
                $"VPSG 3.0 快速对齐跳过 · 索引未就绪({indexStatus}: {detailReason}) · map={map.SequenceNumber}#{floorKey}",
                stage: "Vpsg3.RegistryCheck");
            MapLogCollector.Instance.AppendStatus(
                status,
                MapLogCategory.StructureRegistration,
                details: new()
                {
                    ["mapId"] = map.Id,
                    ["floorKey"] = floorKey,
                    ["indexStatus"] = indexStatus.ToString(),
                    ["statusAgeMs"] = statusAge.TotalMilliseconds,
                    ["failureReason"] = failureReason ?? string.Empty,
                    ["detailReason"] = detailReason,
                    ["expectedKey"] = key.ToString(),
                    ["actualKey"] = actualKey?.ToString() ?? string.Empty,
                    ["keyMatch"] = !keyMismatch
                });
            return false;
        }

        using (lease)
        {
            if (_matchScopedResources)
                _vpsg3Registry.ReleaseIdleDerivedGeometryForOtherMaps(map.Id);
            double? refreshPrior = null;
            (knownScaleSeed, refreshPrior) = ResolveVpsgScaleLock(
                    frame, map, floorKey, knownScaleSeed);
            var scanFrame = ScanExecutionContext.Current is { IsAutomatic: true } scan ? scan.Frame : null;
            var sharedObservation = ReferenceEquals(scanFrame?.Source, frame.Image) ? scanFrame.Observation : null;
            var observationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            // Borrow the frame-owned observation prepared during readiness. The frame
            // outlives alignment and recovery; only its owner may dispose this data.
            var observation = sharedObservation ?? frame.GetOrCreateVpsg3Observation();
            var observationPreparationMs = System.Diagnostics.Stopwatch.GetElapsedTime(observationStarted).TotalMilliseconds;
            var solverStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var preSolverMs = System.Diagnostics.Stopwatch.GetElapsedTime(alignmentStarted, solverStarted).TotalMilliseconds;
            var result = Vpsg3FastBootstrapSolver.TrySolve(observation, lease.Floor, knownScaleSeed: knownScaleSeed);
            if (requireObservableScale && knownScaleSeed is null && !result.IsAccepted
                && TrySolveScaleHarmonic(observation, lease.Floor, result.Scale, out var harmonic, out var harmonicCount))
            {
                MapLogCollector.Instance.Append(
                    MapLogCategory.StructureRegistration,
                    MapLogLevel.Info,
                    "VPSG3 scale harmonic corrected",
                    details: new()
                    {
                        ["mapId"] = map.Id,
                        ["floor"] = floorKey,
                        ["estimatedScale"] = result.Scale,
                        ["estimateRejection"] = result.FallbackReason,
                        ["correctedScale"] = harmonic.Scale,
                        ["confidence"] = harmonic.Confidence,
                        ["testedHarmonics"] = harmonicCount
                    });
                // A harmonic of a coarse pitch estimate is a basin, not a
                // calibrated scale: leave precision refinement free to move it.
                result = harmonic;
            }
            var solverWallMs = System.Diagnostics.Stopwatch.GetElapsedTime(solverStarted).TotalMilliseconds;
            var refreshStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var refreshComparisonMs = 0d;
            var refreshScaleCount = result.TestedScaleHypotheses;
            if (refreshPrior is { } prior
                && double.IsFinite(prior)
                && prior >= Vpsg3TuningConfig.Default.MinSupportedScale
                && prior <= Vpsg3TuningConfig.Default.MaxSupportedScale)
            {
                var baseline = result;
                var comparison = CompareVpsg3ScaleRefresh(
                    observation, lease.Floor, baseline, prior);
                result = comparison.Selected;
                knownScaleSeed = comparison.SelectedPriorHypothesis
                    ? result.Scale : null;
                refreshComparisonMs = comparison.AdditionalMilliseconds;
                refreshScaleCount = comparison.TestedScaleCount;
                MapLogCollector.Instance.Append(
                    MapLogCategory.StructureRegistration,
                    MapLogLevel.Info,
                    "VPSG3 floor scale checked against observed color residuals",
                    details: new()
                    {
                        ["mapId"] = map.Id,
                        ["floor"] = floorKey,
                        ["priorScale"] = prior,
                        ["priorAccepted"] = comparison.Baseline?.IsAccepted,
                        ["priorConfidence"] = comparison.Baseline?.Confidence,
                        ["reestimated"] = refreshScaleCount > baseline.TestedScaleHypotheses,
                        ["bestPriorScale"] = comparison.BestPrior?.Scale,
                        ["bestPriorConfidence"] = comparison.BestPrior?.Confidence,
                        ["selectedScale"] = result.Scale,
                        ["selectedPriorHypothesis"] = comparison.SelectedPriorHypothesis,
                        ["testedScaleCount"] = refreshScaleCount
                    });
            }
            var scaleRefreshWallMs = System.Diagnostics.Stopwatch.GetElapsedTime(refreshStarted).TotalMilliseconds;
            var voteDiagnosticStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var score = Vpsg3LocalRefiner.CountHits(observation.SparseEdgePoints, lease.Floor, result.Scale, result.OffsetX, result.OffsetY, frame.ViewportBounds);
            var sampling = observation.GetSparseSamplingDiagnostics();
            MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, result.IsAccepted ? MapLogLevel.Info : MapLogLevel.Warning, "VPSG3VoteDiagnostics", details: new() { ["mapId"] = map.Id, ["floor"] = floorKey, ["totalEdgePoints"] = sampling.TotalEdgePoints, ["requestedSparsePoints"] = sampling.RequestedSparsePoints, ["actualSparsePoints"] = sampling.ActualSparsePoints, ["samplingStep"] = sampling.SamplingStep, ["sparsePointHash"] = sampling.SparsePointHash, ["quadrantPointCounts"] = new[] { sampling.TopLeftCount, sampling.TopRightCount, sampling.BottomLeftCount, sampling.BottomRightCount }, ["sparsePointCount"] = score.PointCount, ["hitsK5"] = score.HitsK5, ["hitsK3"] = score.HitsK3, ["weightedNumerator"] = score.HitsK5 + 2 * score.HitsK3, ["weightedDenominator"] = 3 * score.PointCount, ["weightedScore"] = result.Confidence, ["minimumVerificationScore"] = Vpsg3TuningConfig.Default.MinVerificationScore, ["scoreUnitsBelowThreshold"] = Math.Max(0, (int)Math.Ceiling(Vpsg3TuningConfig.Default.MinVerificationScore * 3 * score.PointCount) - (score.HitsK5 + 2 * score.HitsK3)) });

            var diagnosticTiming = default(Vpsg3DiagnosticTiming);
            if (MapDiagnosticModeCapture.IsActive)
            {
                try
                {
                    var refPath = _repository.GetPrebuiltStructureLinePath(map, floorKey);
                    diagnosticTiming = Vpsg3DiagnosticCapture.CaptureIfActive(
                        MapDiagnosticModeCapture.CurrentMapOpenId,
                        observation,
                        refPath,
                        result,
                        tag: "vpsg3-live", preparedFloor: lease.Floor);
                }
                catch (Exception diagEx)
                {
                    MapLogCollector.Instance.Append(
                        MapLogCategory.StructureRegistration,
                        MapLogLevel.Warning,
                        "VPSG3 diagnostic capture failed",
                        details: new() { ["error"] = diagEx.Message });
                }
            }

            var voteDiagnosticWallMs = System.Diagnostics.Stopwatch.GetElapsedTime(voteDiagnosticStarted).TotalMilliseconds;
            if (!result.IsAccepted)
            {
                var subCode = result.FallbackReason?.Contains("ApertureMargin", StringComparison.OrdinalIgnoreCase) == true
                    ? IdvbSubCode.Vpsg3FallbackApertureMarginLow
                    : IdvbSubCode.Vpsg3FallbackNotConverged;
                status = IdvbStatus.Fallback(
                    IdvbHttpCode.Vpsg3FallbackToLegacy,
                    subCode,
                    "Vpsg3SolverRejected",
                    $"VPSG 3.0 快速对齐未接受 · {result.FallbackReason}",
                    technicalDetail: $"map={map.SequenceNumber}#{floorKey}, scale={result.Scale:F5}, margin={result.ApertureMargin:F4}, conf={result.Confidence:F3}",
                    stage: "Vpsg3.FastBootstrapSolver");

                MapLogCollector.Instance.AppendStatus(
                    status,
                    MapLogCategory.StructureRegistration,
                    elapsedMs: result.Timing.TotalMs,
                    details: new()
                    {
                        ["mapId"] = map.Id,
                        ["floor"] = floorKey,
                        ["reason"] = result.FallbackReason,
                        ["scale"] = result.Scale,
                        ["confidence"] = result.Confidence,
                        ["margin"] = result.ApertureMargin,
                        ["totalMs"] = result.Timing.TotalMs
                    });
                return false;
            }

            if (requireObservableScale && !IsVpsg3ScaleObservable(observation, lease.Floor, result))
            {
                status = IdvbStatus.Fallback(
                    IdvbHttpCode.Vpsg3FallbackToLegacy,
                    IdvbSubCode.Vpsg3FallbackNotConverged,
                    Vpsg3ScaleUnobservableReason,
                    "VPSG 3.0 快速对齐未接受 · 可见结构不足以确定本楼层尺度",
                    technicalDetail: $"map={map.SequenceNumber}#{floorKey}, scale={result.Scale:F5}, conf={result.Confidence:F3}",
                    stage: "Vpsg3.ScaleObservability");
                MapLogCollector.Instance.AppendStatus(
                    status,
                    MapLogCategory.StructureRegistration,
                    elapsedMs: System.Diagnostics.Stopwatch.GetElapsedTime(alignmentStarted).TotalMilliseconds,
                    details: new()
                    {
                        ["mapId"] = map.Id,
                        ["floor"] = floorKey,
                        ["scale"] = result.Scale,
                        ["confidence"] = result.Confidence,
                        ["probeStep"] = ScaleObservabilityStep
                    });
                return false;
            }

            var finalScale = result.Scale;
            var finalX = result.OffsetX;
            var finalY = result.OffsetY;
            var refineTime = result.Timing.RefineMs;

            var precisionStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!lease.Floor.HasPrecisionDistance)
            {
                var floorDef = map.Floors.FirstOrDefault(f => string.Equals(f.Key, floorKey, StringComparison.OrdinalIgnoreCase));
                if (floorDef is not null && TryGetEligiblePrebuiltPath(map, floorDef, out var prebuiltPath) && prebuiltPath is not null)
                {
                    lease.Floor.EnsurePrecisionDistance(prebuiltPath);
                }
            }

            Vpsg3PrecisionResult? precision = null;
            if (lease.Floor.HasPrecisionDistance)
            {
                TrackActivePrecisionFloor(lease.Floor);
                var precisionBudget = Vpsg3PrecisionBudget.Start();
                var lockScale = knownScaleSeed is { } s && s > 0;
                precision = Vpsg3PrecisionRefiner.Refine(
                    observation, lease.Floor, result.Scale, result.OffsetX, result.OffsetY, precisionBudget, lockScale: lockScale);
                refineTime += precision.Milliseconds;
                if (precision.Calibrated)
                {
                    finalScale = precision.Scale;
                    finalX = precision.OffsetX;
                    finalY = precision.OffsetY;
                }
            }

            var precisionWallMs = System.Diagnostics.Stopwatch.GetElapsedTime(precisionStarted).TotalMilliseconds;
            var semanticStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            if (lease.Floor.SemanticIndex is { } semanticIndex)
            {
                var semantic = semanticIndex.VerifyObservation(observation,
                    finalScale, finalX, finalY, observation.ViewportBounds);
                if (!semantic.Accepted)
                {
                    status = IdvbStatus.Fallback(IdvbHttpCode.Vpsg3FallbackToLegacy,
                        IdvbSubCode.Vpsg3FallbackNotConverged, "SemanticContourRejected", semantic.Reason,
                        stage: "Vpsg3.SemanticContours");
                    return false;
                }
            }

            var semanticWallMs = System.Diagnostics.Stopwatch.GetElapsedTime(semanticStarted).TotalMilliseconds;
            var resultBuildStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var transform = MapCanonicalTransformMath.BuildOverlayTransform(
                finalScale,
                finalScale,
                finalX,
                finalY,
                lease.Floor.ReferenceWidth,
                lease.Floor.ReferenceHeight,
                residualPixels: 0d,
                orientationDegrees: MapFloorRules.GetFloorProfile(map, floorKey)?.OrientationDegrees ?? 0,
                alignmentMode: MapOverlayAlignmentMode.Uniform);

            var forwardStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var forwardMean = Vpsg3PrecisionRefiner.MeasureForwardMean(observation, lease.Floor, finalScale, finalX, finalY);
            var forwardMeanMs = System.Diagnostics.Stopwatch.GetElapsedTime(forwardStarted).TotalMilliseconds;
            var chamferEstimate = forwardMean ?? double.PositiveInfinity;

            var coverage = (double)result.BestCandidate.Spatial.HitPoints / Math.Max(1, result.BestCandidate.Spatial.TotalValidPoints);
            var breakdown = new MapStructureConfidenceBreakdown
            {
                ChamferPixels = chamferEstimate,
                ChamferQuality = Math.Clamp(1.0d - (chamferEstimate / 3.0d), 0d, 1d),
                EdgeCoverage = coverage,
                OccupancyCoverage = result.BestCandidate.K5Score,
                MeasuredForwardMeanPixels = forwardMean,
                VpsgVoteScore = result.BestCandidate.WeightedScore,
                PrecisionHuberLoss = precision?.After?.Loss ?? precision?.Before?.Loss,
                ConsistentPartitions = result.PassedPartitions,
                PartitionQuality = Math.Clamp(result.PassedPartitions / 4.0d, 0d, 1d),
                StructureQuality = result.Confidence,
                CandidateSeparation = result.ApertureMargin,
                GeometricFitQuality = result.Confidence,
                EvidenceConfidence = result.Confidence,
                GeometricLockConfidence = result.Confidence,
                LockConfidence = result.Confidence,
                EffectiveWeight = 1.0d,
                FinalScore = result.BestCandidate.WeightedScore
            };

            var mainCandidate = new MapStructureCandidate
            {
                Scale = finalScale,
                OffsetX = finalX,
                OffsetY = finalY,
                ChamferPixels = chamferEstimate,
                EdgeCoverage = coverage,
                OccupancyCoverage = result.BestCandidate.K5Score,
                ConsistentPartitions = result.PassedPartitions,
                CompositeCost = result.BestCandidate.WeightedScore,
                AppearanceCorrelation = 0d
            };

            var structureResult = new MapStructureRegistrationResult
            {
                Accepted = true,
                Transform = transform,
                Confidence = result.Confidence,
                ConfidenceBreakdown = breakdown,
                Candidates = [mainCandidate],
                BestScore = result.BestCandidate.WeightedScore,
                SecondScore = result.RunnerUpCandidate?.WeightedScore ?? double.PositiveInfinity,
                CandidateMargin = result.ApertureMargin,
                RejectionReason = MapStructureRejectionReason.None,
                PreprocessMilliseconds = result.Timing.ExtractionMs,
                SearchMilliseconds = result.Timing.TranslationMs,
                RefineMilliseconds = refineTime,
                UsedFastStrategy = true,
                LockedScale = finalScale,
                ReferenceWidth = lease.Floor.ReferenceWidth,
                ReferenceHeight = lease.Floor.ReferenceHeight
            };

            var recognition = MapCvRecognitionBuilders.BuildFloorStructureRecognition(
                map,
                floorKey,
                _repository.GetFloorOverlayPath(map, floorKey),
                transform,
                structureResult,
                identityPriorConfidence);

            var resultBuildWallMs = System.Diagnostics.Stopwatch.GetElapsedTime(resultBuildStarted).TotalMilliseconds - forwardMeanMs;
            var coverageTimer = System.Diagnostics.Stopwatch.StartNew();
            MeasureAlignmentCoverage(frame, recognition, observation);
            coverageTimer.Stop();

            // Wall time includes semantic checks, lazy index work and diagnostics.
            // Extraction may precede this call and must not be charged again.
            var totalTime = System.Diagnostics.Stopwatch.GetElapsedTime(alignmentStarted).TotalMilliseconds;
            var diagnostics = MapCvRecognitionDiagnostics.CreateDiagnostics(ReadyMapCount, TotalMapCount);
            diagnostics.ScaleBootstrapAttempted = true;
            diagnostics.ScaleBootstrapSucceeded = true;
            diagnostics.ScaleBootstrapValidated = !isLowStructure;
            diagnostics.ScaleBootstrapScale = finalScale;
            diagnostics.ScaleBootstrapConfidence = result.Confidence;
            diagnostics.ScaleBootstrapMode = "Vpsg3";
            diagnostics.ScaleBootstrapMethod = "vpsg3";
            diagnostics.ScaleBootstrapCost = result.BestCandidate.WeightedScore;
            diagnostics.ScaleBootstrapMargin = result.ApertureMargin;
            diagnostics.ScaleBootstrapCandidateCount = result.HasDistinctRunnerUp ? 2 : 1;
            diagnostics.ScaleBootstrapSelectedCandidateIndex = 0;
            diagnostics.ScaleBootstrapTestedScaleCount = refreshScaleCount;
            diagnostics.ScaleBootstrapStructureMilliseconds = result.Timing.ScaleMs;
            diagnostics.LiveStructurePreprocessMilliseconds = result.Timing.ExtractionMs;
            diagnostics.StructurePreprocessMilliseconds = result.Timing.ExtractionMs;
            diagnostics.StructureSearchMilliseconds = result.Timing.TranslationMs + refineTime;
            diagnostics.StructureRefineMilliseconds = refineTime;
            diagnostics.StructureBestScore = result.BestCandidate.WeightedScore;
            diagnostics.StructureSecondScore = result.RunnerUpCandidate?.WeightedScore ?? double.PositiveInfinity;
            diagnostics.StructureCandidateMargin = result.ApertureMargin;
            diagnostics.StructureCandidateCount = result.HasDistinctRunnerUp ? 2 : 1;
            diagnostics.AlignmentEvidence = MapAlignmentEvidenceKind.Structure;
            diagnostics.StructureAccepted = true;
            diagnostics.StructureAttempted = true;
            diagnostics.StructureRejectionReason = MapStructureRejectionReason.None;
            diagnostics.TotalMilliseconds = totalTime;
            diagnostics.TrackingMode = MapAlignmentTrackingMode.StructureMatched;
            if (isLowStructure)
            {
                diagnostics.LowStructureRoute = nameof(LowStructureAlignmentRoute.CachedFixed);
                diagnostics.LowStructureValidatedScaleSeed = true;
                diagnostics.LowStructureVpsgEnabled = true;
                diagnostics.AlignmentChannel = MapAlignmentChannelRegistry.LowStructure.DiagnosticLabel;
            }

            status = IdvbStatus.Ok(
                $"VPSG 3.0 快速对齐通过 · floor={floorKey} · scale={finalScale:F5}",
                subCode: IdvbSubCode.StructureVpsg3Ok,
                stage: "Vpsg3.Aligned");

            attempt = new MapRecognitionAttempt
            {
                Diagnostics = diagnostics,
                StructureResult = structureResult,
                Recognition = recognition,
                StructureAttempted = true,
                StructureAccepted = true,
                Status = status,
                SearchStage = AlignmentSearchStage.StructureFallback
            };

            MapLogCollector.Instance.AppendStatus(
                status,
                MapLogCategory.StructureRegistration,
                elapsedMs: totalTime,
                details: new()
                {
                    ["mapId"] = map.Id,
                    ["floor"] = floorKey,
                    ["scale"] = finalScale,
                    ["tx"] = finalX,
                    ["ty"] = finalY,
                    ["confidence"] = result.Confidence,
                    ["margin"] = result.ApertureMargin,
                    ["partitions"] = result.PassedPartitions,
                    ["extractionMs"] = result.Timing.ExtractionMs,
                    ["observationSource"] = sharedObservation is null ? "frame-owned" : "scan-owned",
                    ["observationPreparationMs"] = observationPreparationMs,
                    ["scaleRefreshAdditionalMs"] = refreshComparisonMs,
                    ["scaleMs"] = result.Timing.ScaleMs,
                    ["scaleLockUsed"] = knownScaleSeed.HasValue,
                    ["coverageMs"] = coverageTimer.Elapsed.TotalMilliseconds,
                    ["forwardMeanMs"] = forwardMeanMs,
                    ["preSolverMs"] = preSolverMs,
                    ["solverWallMs"] = solverWallMs,
                    ["scaleRefreshWallMs"] = scaleRefreshWallMs,
                    ["voteDiagnosticWallMs"] = voteDiagnosticWallMs,
                    ["diagnosticInputWriteMs"] = diagnosticTiming.InputWriteMs,
                    ["diagnosticSemanticWriteMs"] = diagnosticTiming.SemanticWriteMs,
                    ["diagnosticOverlayMs"] = diagnosticTiming.OverlayMs,
                    ["diagnosticFitnessWriteMs"] = diagnosticTiming.FitnessWriteMs,
                    ["diagnosticEnqueueMs"] = diagnosticTiming.EnqueueMs, ["diagnosticDeferred"] = diagnosticTiming.Deferred,
                    ["precisionWallMs"] = precisionWallMs,
                    ["semanticWallMs"] = semanticWallMs,
                    ["resultBuildWallMs"] = resultBuildWallMs,
                    ["translationMs"] = result.Timing.TranslationMs,
                    ["refineMs"] = refineTime,
                    ["verificationMs"] = result.Timing.VerificationMs,
                    ["totalMs"] = totalTime
                });

            CompleteVpsgScaleRefresh(frame, finalScale);
            return true;
        }
    }


}
