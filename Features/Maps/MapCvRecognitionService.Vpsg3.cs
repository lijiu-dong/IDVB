using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService : IDisposable
{
    // Each build owns full-resolution native distance-transform scratch space.
    // Share the bounded floor-preparation worker count: a single builder made
    // match start wait for every floor in sequence.
    private static int Vpsg3RebuildWorkerCount => MapCvRecognitionHelpers.FloorPreparationWorkerCount;

    private readonly Vpsg3PreparedIndexRegistry _vpsg3Registry = new();
    private readonly CancellationTokenSource _vpsg3RebuildCts = new();
    private Task _vpsg3Preparation = Task.CompletedTask;
    internal Task WaitForVpsg3PreparationAsync(CancellationToken token) => _vpsg3Preparation.WaitAsync(token);
    private int _vpsg3ShadowRunning;

    /// <summary>
    /// Application-lifetime registry holding prepared reference floor indices for VPSG 3.0.
    /// </summary>
    public IVpsg3PreparedIndexRegistry Vpsg3Registry => _vpsg3Registry;


    private static bool TryGetVpsg3IndexKey(MapRecord map, string floorKey, out Vpsg3IndexCacheKey key) { key = default; var floor = map.Floors.FirstOrDefault(f => string.Equals(f.Key, floorKey, StringComparison.OrdinalIgnoreCase)); if (floor?.PrebuiltStructureLine is not { IsComplete: true } prebuilt || !string.Equals(prebuilt.SourceSha256, floor.RecognitionSha256, StringComparison.OrdinalIgnoreCase)) return false; key = new Vpsg3IndexCacheKey(map.Id, floor.Key, MapFeatureCacheRules.ComputeContentFingerprint(map), map.UpdatedAt, Vpsg3IndexCacheKey.CreatePrebuiltGenerationIdentity(prebuilt, schemaVersion: 1), SchemaVersion: 1); return true; }

    // ponytail: one in-flight shadow, no queue; add a bounded queue only if dropped
    // observations prevent certification. Cloned pixels and a lease outlive the caller.
    internal Task QueueVpsg3Shadow(CapturedGameFrame frame, MapRecord map, string floorKey,
        MapOverlayTransform? baseline, MapLogCollector log, int? diagnosticAttemptId = null)
    {
        if (_disposed || MapAlignmentChannelRegistry.Resolve(map, floorKey).Channel == MapAlignmentChannel.LowStructure)
            return Task.CompletedTask;
        if (Interlocked.CompareExchange(ref _vpsg3ShadowRunning, 1, 0) != 0)
        {
            log.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                "VPSG3 shadow skipped: busy", details: new() { ["mapId"] = map.Id, ["floorKey"] = floorKey });
            return Task.CompletedTask;
        }

        Vpsg3FloorIndexLease? lease = null;
        Mat? pixels = null;
        try
        {
            var floor = map.Floors.FirstOrDefault(f => string.Equals(f.Key, floorKey, StringComparison.OrdinalIgnoreCase));
            if (floor?.PrebuiltStructureLine is not { IsComplete: true } prebuilt
                || !string.Equals(prebuilt.SourceSha256, floor.RecognitionSha256, StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Exchange(ref _vpsg3ShadowRunning, 0);
                log.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                    "VPSG3 shadow skipped: prebuilt unavailable", details: new() { ["mapId"] = map.Id, ["floorKey"] = floorKey });
                return Task.CompletedTask;
            }
            var key = new Vpsg3IndexCacheKey(map.Id, floor.Key,
                MapFeatureCacheRules.ComputeContentFingerprint(map), map.UpdatedAt,
                Vpsg3IndexCacheKey.CreatePrebuiltGenerationIdentity(prebuilt));
            if (!_vpsg3Registry.TryGet(key, out lease))
            {
                Interlocked.Exchange(ref _vpsg3ShadowRunning, 0);
                _vpsg3Registry.TryGetDetailedStatus(key, out var shadowStatus, out var shadowAge, out var shadowFail, out var shadowActual);
                log.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                    "VPSG3 shadow skipped: index not ready", details: new()
                    {
                        ["mapId"] = map.Id,
                        ["floorKey"] = floorKey,
                        ["indexStatus"] = shadowStatus.ToString(),
                        ["statusAgeMs"] = shadowAge.TotalMilliseconds,
                        ["failureReason"] = shadowFail ?? string.Empty,
                        ["expectedKey"] = key.ToString(),
                        ["actualKey"] = shadowActual?.ToString() ?? string.Empty
                    });
                return Task.CompletedTask;
            }
            pixels = frame.Image.Clone();
            var ownedPixels = pixels;
            var ownedLease = lease;
            var bounds = frame.ViewportBounds;
            var capturedAt = DateTimeOffset.UtcNow;
            var referenceSha256 = prebuilt.Sha256;
            return Task.Run(() =>
            {
                using (ownedPixels)
                using (ownedLease)
                {
                    try
                    {
                        if (_disposed) return;
                        using var observation = Vpsg3FastLiveExtractor.Extract(ownedPixels, bounds);
                        var result = Vpsg3FastBootstrapSolver.TrySolve(observation, ownedLease.Floor);
                        if (MapDiagnosticModeCapture.IsActive)
                        {
                            try
                            {
                                var refPath = _repository.GetPrebuiltStructureLinePath(map, floorKey);
                                Vpsg3DiagnosticCapture.CaptureIfActive(
                                    diagnosticAttemptId,
                                    observation,
                                    refPath,
                                    result,
                                    tag: "vpsg3", preparedFloor: ownedLease.Floor);
                            }
                            catch (Exception diagEx)
                            {
                                log.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                                    "VPSG3 diagnostic capture failed", details: new() { ["error"] = diagEx.Message });
                            }
                        }
                        string? evidencePath = null;
                        string? evidenceError = null;
                        if (File.Exists(Path.Combine(log.LogDirectory, Vpsg3CertificationCapture.EnableFileName)))
                        {
                            try
                            {
                                evidencePath = Vpsg3CertificationCapture.Save(log.LogDirectory, ownedPixels,
                                    _repository.GetPrebuiltStructureLinePath(map, floorKey), referenceSha256,
                                    key, bounds, capturedAt, result, baseline);
                            }
                            catch (Exception ex)
                            {
                                evidenceError = ex.Message;
                                log.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                                    "VPSG3 certification capture failed", details: new() { ["cacheKey"] = key.ToString(), ["error"] = evidenceError });
                            }
                        }
                        log.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                            "VPSG3 shadow result", elapsedMs: result.Timing.TotalMs, details: new()
                            {
                                ["shadowOnly"] = true, ["capturedAt"] = capturedAt,
                                ["fastAcceptLocked"] = true, ["certificationEvidencePath"] = evidencePath,
                                ["certificationEvidenceError"] = evidenceError,
                                ["cacheKey"] = key.ToString(), ["mapId"] = key.MapId, ["floorKey"] = key.FloorKey,
                                ["viewportX"] = bounds.X, ["viewportY"] = bounds.Y,
                                ["viewportWidth"] = bounds.Width, ["viewportHeight"] = bounds.Height,
                                ["accepted"] = result.IsAccepted, ["reason"] = result.FallbackReason,
                                ["scale"] = result.Scale, ["tx"] = result.OffsetX, ["ty"] = result.OffsetY,
                                ["confidence"] = result.Confidence, ["margin"] = result.ApertureMargin,
                                ["hasDistinctRunnerUp"] = result.HasDistinctRunnerUp,
                                ["partitions"] = result.PassedPartitions,
                                ["baselineScale"] = baseline?.ScaleX, ["baselineTx"] = baseline?.OffsetX,
                                ["baselineTy"] = baseline?.OffsetY, ["baselineIsGroundTruth"] = false,
                                ["extractionMs"] = result.Timing.ExtractionMs, ["scaleMs"] = result.Timing.ScaleMs,
                                ["translationMs"] = result.Timing.TranslationMs, ["refineMs"] = result.Timing.RefineMs,
                                ["verificationMs"] = result.Timing.VerificationMs, ["gateMs"] = result.Timing.GateMs
                            });
                    }
                    catch (Exception ex)
                    {
                        log.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                            "VPSG3 shadow failed", details: new() { ["cacheKey"] = key.ToString(), ["error"] = ex.Message });
                    }
                    finally { Interlocked.Exchange(ref _vpsg3ShadowRunning, 0); }
                }
            });
        }
        catch (Exception ex)
        {
            pixels?.Dispose();
            lease?.Dispose();
            Interlocked.Exchange(ref _vpsg3ShadowRunning, 0);
            log.Append(MapLogCategory.StructureRegistration, MapLogLevel.Warning,
                "VPSG3 shadow scheduling failed", details: new() { ["mapId"] = map.Id, ["floorKey"] = floorKey, ["error"] = ex.Message });
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Synchronously invalidates changed maps in the VPSG3 registry, then triggers
    /// background asynchronous rebuilding for the invalidated floors using bounded parallelism (3 workers).
    /// Alignment never waits for rebuild to complete.
    /// Only floors with complete, valid PrebuiltStructureLine are eligible for VPSG3 index building.
    /// </summary>
    internal void InvalidateAndTriggerVpsg3Rebuild(
        IReadOnlyList<MapRecord> maps,
        IReadOnlySet<Guid> changedMapIds,
        Action<double, string>? reportProgress = null)
    {
        if (changedMapIds is null || changedMapIds.Count == 0)
            return;

        // 1. Synchronous invalidation in the registry
        lock (_vpsg3PreparationGate)
        {
            _vpsg3Registry.InvalidateMaps(changedMapIds);
            if (_selectedFloorMapId is { } selected && changedMapIds.Contains(selected))
            {
                _selectedFloorPreparationKey = null;
                _selectedFloorGeneration++;
            }
        }
        TriggerVpsg3Rebuild(maps, changedMapIds, reportProgress, _matchScopedResources);
    }

    private Task TriggerVpsg3Rebuild(
        IReadOnlyList<MapRecord> maps, IReadOnlySet<Guid> changedMapIds,
        Action<double, string>? reportProgress, bool scanFloorsOnly)
    {
        // 2. Asynchronous background rebuild without blocking caller or alignment
        if (_disposed || _vpsg3RebuildCts.IsCancellationRequested)
            return Task.CompletedTask;

        var token = _vpsg3RebuildCts.Token;
        long? selectionGeneration;
        lock (_vpsg3PreparationGate)
            selectionGeneration = _matchScopedResources && !scanFloorsOnly ? _selectedFloorGeneration : null;
        var targets = maps.Where(m => changedMapIds.Contains(m.Id)).ToArray();
        if (targets.Length == 0)
            return Task.CompletedTask;

        // Collect all eligible prebuilt tasks
        var buildTasks = new List<(MapRecord Map, FloorDefinition Floor, string LinePath, Vpsg3IndexCacheKey CacheKey)>();
        foreach (var map in targets)
        {
            var fingerprint = MapFeatureCacheRules.ComputeContentFingerprint(map);

            foreach (var floor in map.Floors)
            {
                if (scanFloorsOnly && !string.Equals(floor.Key, MapScanFloorRules.ResolveScanFloorKey(map),
                    StringComparison.OrdinalIgnoreCase)) continue;
                // Strict PrebuiltStructureLine contract: ineligible floors never get a VPSG3 index
                if (!TryGetEligiblePrebuiltPath(map, floor, out var linePath) || linePath is null)
                {
                    MapLogCollector.Instance.Append(
                        MapLogCategory.StructureRegistration,
                        MapLogLevel.Info,
                        "VPSG3 index build skipped: ineligible floor",
                        details: new()
                        {
                            ["mapId"] = map.Id,
                            ["floorKey"] = floor.Key,
                            ["hasPrebuilt"] = floor.PrebuiltStructureLine != null,
                            ["isComplete"] = floor.PrebuiltStructureLine?.IsComplete == true,
                            ["sourceShaMatch"] = string.Equals(floor.PrebuiltStructureLine?.SourceSha256, floor.RecognitionSha256, StringComparison.OrdinalIgnoreCase)
                        });
                    continue;
                }

                var structureGen = Vpsg3IndexCacheKey.CreatePrebuiltGenerationIdentity(
                    floor.PrebuiltStructureLine!,
                    schemaVersion: 1);

                var cacheKey = new Vpsg3IndexCacheKey(
                    map.Id,
                    floor.Key,
                    fingerprint,
                    map.UpdatedAt,
                    structureGen,
                    SchemaVersion: 1);

                buildTasks.Add((map, floor, linePath, cacheKey));
            }
        }

        MapLogCollector.Instance.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            "VPSG3 rebuild scheduled",
            details: new()
            {
                ["changedMapCount"] = changedMapIds.Count,
                ["buildTaskCount"] = buildTasks.Count
            });

        if (buildTasks.Count == 0)
            return Task.CompletedTask;

        // Dispatch background bounded parallel execution (3 workers)
        var completedFloors = 0;
        var rebuild = Task.Run(async () =>
        {
            // Yield startup CPU priority to initial UI presentation
            try { if (!_matchScopedResources) await Task.Delay(1500, token); }
            catch (OperationCanceledException) { return; }

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Vpsg3RebuildWorkerCount,
                CancellationToken = token
            };

            try
            {
                await Parallel.ForEachAsync(buildTasks, options, (taskItem, ct) =>
                {
                    if (ct.IsCancellationRequested)
                        return ValueTask.CompletedTask;

                    // Prevent duplicate parallel builds for the same key
                    if (!TryBeginSelectedFloorBuild(taskItem.CacheKey, selectionGeneration))
                    {
                        MapLogCollector.Instance.Append(
                            MapLogCategory.StructureRegistration,
                            MapLogLevel.Info,
                            "VPSG3 index build skipped (already building/ready)",
                            details: new()
                            {
                                ["mapId"] = taskItem.Map.Id,
                                ["floorKey"] = taskItem.Floor.Key,
                                ["cacheKey"] = taskItem.CacheKey.ToString()
                            });
                        reportProgress?.Invoke((double)Interlocked.Increment(ref completedFloors) / buildTasks.Count,
                            "正在准备楼层对齐索引");
                        return ValueTask.CompletedTask;
                    }

                    var swBuild = Stopwatch.StartNew();
                    MapLogCollector.Instance.Append(
                        MapLogCategory.StructureRegistration,
                        MapLogLevel.Info,
                        "VPSG3 index build started",
                        details: new()
                        {
                            ["mapId"] = taskItem.Map.Id,
                            ["floorKey"] = taskItem.Floor.Key,
                            ["cacheKey"] = taskItem.CacheKey.ToString(),
                            ["linePath"] = taskItem.LinePath
                        });

                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        using var scanReaders = _sideEntranceFeatureCache.Rent(key =>
                            key.Item1 == taskItem.Map.Id && key.Item2 == taskItem.Floor.Key);
                        scanReaders.Entries.TryGetValue((taskItem.Map.Id, taskItem.Floor.Key), out var scanIndex);
                        var preparedFloor = Vpsg3PreparedIndexBuilder.BuildFromPrebuiltFile(taskItem.LinePath, taskItem.CacheKey,
                            preparePrecision: false, reuseSemanticDistancesFrom: scanIndex?.SemanticIndex,
                            nativeSemanticStorage: _deterministicSemanticStorage);
                        if (preparedFloor is null)
                        {
                            const string reason = "Decoded prebuilt line image is empty.";
                            RecordSelectedFloorBuildFailure(taskItem.CacheKey, reason, selectionGeneration);
                            MapLogCollector.Instance.Append(
                                MapLogCategory.StructureRegistration,
                                MapLogLevel.Warning,
                                "VPSG3 index build failed: image empty",
                                details: new()
                                {
                                    ["mapId"] = taskItem.Map.Id,
                                    ["floorKey"] = taskItem.Floor.Key,
                                    ["cacheKey"] = taskItem.CacheKey.ToString(),
                                    ["linePath"] = taskItem.LinePath
                                });
                            return ValueTask.CompletedTask;
                        }

                        // Cancellation can arrive while the synchronous encoder runs.
                        // Retire its unpublished owner before leaving this worker.
                        if (ct.IsCancellationRequested)
                        {
                            preparedFloor.Dispose();
                            ct.ThrowIfCancellationRequested();
                        }
                        var published = TryPublishSelectedFloor(taskItem.CacheKey, preparedFloor, selectionGeneration);
                        swBuild.Stop();
                        if (published)
                        {
                            MapLogCollector.Instance.Append(
                                MapLogCategory.StructureRegistration,
                                MapLogLevel.Info,
                                "VPSG3 index build finished and published",
                                elapsedMs: swBuild.Elapsed.TotalMilliseconds,
                                details: new()
                                {
                                    ["mapId"] = taskItem.Map.Id,
                                    ["floorKey"] = taskItem.Floor.Key,
                                    ["cacheKey"] = taskItem.CacheKey.ToString(),
                                    ["memoryBytes"] = preparedFloor.MemoryBytes,
                                    ["width"] = preparedFloor.ReferenceWidth,
                                    ["height"] = preparedFloor.ReferenceHeight,
                                    ["elapsedMs"] = swBuild.Elapsed.TotalMilliseconds
                                });
                        }
                        else
                        {
                            MapLogCollector.Instance.Append(
                                MapLogCategory.StructureRegistration,
                                MapLogLevel.Warning,
                                "VPSG3 index build finished but rejected/displaced (stale slot)",
                                elapsedMs: swBuild.Elapsed.TotalMilliseconds,
                                details: new()
                                {
                                    ["mapId"] = taskItem.Map.Id,
                                    ["floorKey"] = taskItem.Floor.Key,
                                    ["cacheKey"] = taskItem.CacheKey.ToString()
                                });
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Background task cancelled
                    }
                    catch (Exception ex)
                    {
                        swBuild.Stop();
                        RecordSelectedFloorBuildFailure(taskItem.CacheKey, ex.Message, selectionGeneration);
                        MapLogCollector.Instance.Append(
                            MapLogCategory.StructureRegistration,
                            MapLogLevel.Warning,
                            "VPSG3 index build failed with exception",
                            elapsedMs: swBuild.Elapsed.TotalMilliseconds,
                            details: new()
                            {
                                ["mapId"] = taskItem.Map.Id,
                                ["floorKey"] = taskItem.Floor.Key,
                                ["cacheKey"] = taskItem.CacheKey.ToString(),
                                ["error"] = ex.Message
                            });
                    }
                    finally
                    {
                        reportProgress?.Invoke((double)Interlocked.Increment(ref completedFloors) / buildTasks.Count,
                            $"正在准备楼层对齐索引：{taskItem.Map.DisplayName} · {taskItem.Floor.Key}");
                    }

                    return ValueTask.CompletedTask;
                });
            }
            catch (OperationCanceledException)
            {
                // Service being disposed or rebuild cancelled
            }
        }, token);
        lock (_vpsg3PreparationGate)
            return _vpsg3Preparation = Task.WhenAll(_vpsg3Preparation, rebuild);
    }

    private bool TryGetEligiblePrebuiltPath(
        MapRecord map,
        FloorDefinition floor,
        out string? prebuiltPath)
    {
        prebuiltPath = null;
        if (floor.PrebuiltStructureLine?.IsComplete is not true)
            return false;

        if (!string.Equals(
                floor.PrebuiltStructureLine.SourceSha256,
                floor.RecognitionSha256,
                StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var path = _repository.GetPrebuiltStructureLinePath(map, floor.Key);
            if (File.Exists(path))
            {
                prebuiltPath = path;
                return true;
            }
        }
        catch
        {
            // Corrupted or missing prebuilt line file
        }

        return false;
    }

    private readonly object _precisionLruGate = new();
    private readonly List<Vpsg3PreparedFloor> _activePrecisionFloors = new();
    private const int MaxActivePrecisionFloors = 2;

    internal void TrackActivePrecisionFloor(Vpsg3PreparedFloor floor)
    {
        lock (_precisionLruGate)
        {
            _activePrecisionFloors.Remove(floor);
            _activePrecisionFloors.Add(floor);

            while (_activePrecisionFloors.Count > MaxActivePrecisionFloors)
            {
                var oldest = _activePrecisionFloors[0];
                _activePrecisionFloors.RemoveAt(0);
                oldest.EvictPrecisionDistance();
            }
        }
    }

    private void DisposeVpsg3()
    {
        try
        {
            _vpsg3RebuildCts.Cancel();
            _vpsg3RebuildCts.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        _vpsg3Registry.Dispose();
    }
}

