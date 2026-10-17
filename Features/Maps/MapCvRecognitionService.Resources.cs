using IDVBuff.Diagnostics;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    /// <summary>
    /// 开局时等待对齐索引的最长时间。超过就让它在后台继续，先把对局状态开起来
    /// （一个模式几十张图时，逐张从 prebuilt 线图现算索引实测要 7 秒）。
    /// </summary>
    private const int Vpsg3StartupWaitMilliseconds = 1200;

    internal IReadOnlyList<MapVariantGroup> CatalogVariantGroups { get; private set; } = [];
    private IReadOnlyList<MapRecord> ResidentMaps(IReadOnlyList<MapRecord> maps) =>
        !_matchScopedResources ? maps : string.IsNullOrWhiteSpace(_residentMapClass)
            ? []
            : maps.Where(map => string.Equals(map.Class, _residentMapClass,
                StringComparison.OrdinalIgnoreCase)).ToArray();

    // Called under the orchestrator's lifecycle gate, before publishing a started
    // match. Preparation must not consume the first scan's deadline.
    internal async Task PrepareMatchResourcesAsync(string mapClass)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapClass);
        using var memoryOperation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Match.PrepareResources", mapClass);
        if (!_matchScopedResources) return;
        MapOperationProgress.Report(0, "等待地图资源");
        await _cacheGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Volatile.Read(ref _vpsg3Preparation);
            Vpsg3FastLiveExtractor.ReleaseSharedScratch();
            MapOperationProgress.Report(.05, "释放上一局资源");
            await Task.Run(ClearResidentCatalogResources);
            MapOperationProgress.Report(.1, "准备扫描和对齐资源");
            _residentMapClass = mapClass;
            try
            {
                await LoadResidentRecognitionCatalogAsync(mapClass);
                var maps = ResidentMaps(_maps);
                var report = MapOperationProgress.Capture();
                var progressGate = new object();
                double scanFraction = 0, alignmentFraction = 0;
                void ReportBranch(bool scan, MapOperationUpdate update)
                {
                    lock (progressGate)
                    {
                        if (scan) scanFraction = Math.Max(scanFraction, update.Fraction);
                        else alignmentFraction = Math.Max(alignmentFraction, update.Fraction);
                        report(.2 + .8 * (scanFraction + alignmentFraction) / 2, update.Operation);
                    }
                }
                var scanPreparation = Task.Run(() =>
                {
                    using var memory = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Match.PrepareScan", mapClass);
                    using var progress = MapOperationProgress.Begin(new MatchResourceProgress(update => ReportBranch(true, update)));
                    using (MapOperationProgress.Range(0, .8))
                        _sideEntranceFeatureCache.Publish(MapCvRecognitionHelpers.BuildSideEntranceFeatureCache(_repository, maps, _deterministicScanStorage, _deterministicSemanticStorage));
                    if (_sideEntranceFeatureCache.Count > 0)
                    {
                        using var prewarmProgress = MapOperationProgress.Range(.8, 1);
                        PrewarmDeepScan(_sideEntranceFeatureCache.MetadataSnapshot.Values, MapOperationProgress.Capture());
                    }
                    MapOperationProgress.Report(1, "扫描资源已就绪");
                });
                var alignmentPreparation = Task.Run(async () =>
                {
                    // Both branches build full-resolution native scratch rasters.
                    // Finish/release scan scratch before starting alignment scratch.
                    await scanPreparation;
                    using var memory = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Match.PrepareAlignment", mapClass);
                    using var progress = MapOperationProgress.Begin(new MatchResourceProgress(update => ReportBranch(false, update)));
                    InvalidateAndTriggerVpsg3Rebuild(maps, maps.Select(map => map.Id).ToHashSet(), MapOperationProgress.Capture());
                    // 对齐索引是按 prebuilt 线图现算的（无磁盘缓存），一个模式下有几十张图时
                    // 实测要 7 秒。这里最多等 1.2 秒：超时就让它在后台继续跑，先把对局开起来，
                    // 否则「按一下进入对局」会被这 7 秒卡住。真正要做对齐时，
                    // PrepareSelectedMapFloorsAsync 会为该图补建，不会拿到空索引。
                    var preparation = Volatile.Read(ref _vpsg3Preparation);
                    if (!preparation.IsCompleted)
                    {
                        await Task.WhenAny(preparation, Task.Delay(Vpsg3StartupWaitMilliseconds));
                        // 超时后不再等；观察它的异常，避免后台任务的异常没人接。
                        _ = preparation.ContinueWith(
                            faulted => _ = faulted.Exception,
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted,
                            TaskScheduler.Default);
                    }
                    MapOperationProgress.Report(1, "对齐资源已就绪");
                });
                // These owners prepare independent caches. Drain both before
                // publishing readiness or cleaning up after either failure.
                await Task.WhenAll(scanPreparation, alignmentPreparation);
                AuditResidentResources("MatchResources.Prepared");
                MapOperationProgress.Report(1, "地图资源已就绪");
            }
            catch
            {
                await Task.Run(ClearResidentCatalogResources);
                throw;
            }
        }
        finally { _cacheGate.Release(); }
    }

    private sealed class MatchResourceProgress(Action<MapOperationUpdate> report) : IProgress<MapOperationUpdate>
    {
        public void Report(MapOperationUpdate value) => report(value);
    }

    // The orchestrator has already stopped and drained scans/alignment. Drain
    // preparation as well: a timeout is not proof that its native work stopped.
    internal async Task ReleaseMatchResourcesAsync()
    {
        Task[] pending;
        lock (_floorPrewarmGate)
        {
            _matchCts.Cancel();
            pending = _allFloorPrewarmTasks.ToArray();
        }
        try { await Task.WhenAll(pending); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OutputLog.Write("WARN", "PERF/RESOURCES", $"Floor preparation failed while draining: {ex}");
        }

        await _cacheGate.WaitAsync();
        try
        {
            await Volatile.Read(ref _vpsg3Preparation);
            AuditResidentResources("MatchResources.BeforeRelease");
            // The caller has drained capture/recognition/tracking. The shared
            // live extractor's native planes are match scratch, not catalog
            // metadata; retire their generation at end as well as next start.
            Vpsg3FastLiveExtractor.ReleaseSharedScratch();
            await Task.Run(() =>
            {
                if (_matchScopedResources)
                    ClearResidentCatalogResources();
                else
                    ClearPrecisionResources();
                _vpsgScaleGraphCache.Clear();
            });
            AuditResidentResources("MatchResources.Released");
        }
        finally { _cacheGate.Release(); }
    }

    private void ClearPrecisionResources()
    {
        lock (_precisionLruGate)
        {
            foreach (var floor in _activePrecisionFloors)
                floor.EvictPrecisionDistance();
            _activePrecisionFloors.Clear();
        }
    }

    private void ClearResidentCatalogResources()
    {
        using var memory = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Match.ClearResidentCatalog");
        _residentMapClass = null;
        if (_matchScopedResources)
        {
            _maps = _maps.Select(map => map.IsListProjection ? map : map.CloneRecognitionMetadata()).ToArray();
            _fingerprints = [];
        }
        ClearPrecisionResources();
        _vpsg3Registry.Clear();
        lock (_vpsg3PreparationGate)
        { _selectedFloorMapId = null; _selectedFloorPreparationKey = null; _selectedFloorPreparation = Task.CompletedTask; _selectedFloorGeneration++; }
        _sideEntranceFeatureCache.Clear();
        _deepScanGateDetector?.Dispose();
        _deepScanGateDetector = null;
    }

    private void AuditResidentResources(string phase)
    {
        long distanceBytes = 0, semanticBytes = 0;
        foreach (var mat in _sideEntranceFeatureCache.MetadataSnapshot.Values)
        {
            distanceBytes += mat.RetainedDistanceBytes;
            semanticBytes += mat.SemanticIndex?.MemoryBytes ?? 0;
        }
        RealtimePerformanceTracker.AuditLifecycle(phase,
            $"class={_residentMapClass ?? "<idle>"}; scanFloors={_sideEntranceFeatureCache.Count}; "
            + $"contourBytes=0; scanDistanceBytes={distanceBytes}; scanSemanticBytes={semanticBytes}; "
            + $"vpsg3Floors={_vpsg3Registry.ReadyCount}; vpsg3Bytes={_vpsg3Registry.TotalMemoryBytes}");
    }

    private CacheBuildResult BuildFingerprintCache(MapRecord[] snapshot, Guid? changedMapId)
    {
        using var memory = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Recognition.BuildFingerprints", $"maps={snapshot.Length}");
        IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedObject(snapshot, "Recognition.CacheMaps");
        var fingerprintMaps = ResidentMaps(snapshot);
        CacheBuildResult buildResult;
        if (!_cacheInitialized)
        {
            buildResult = new CacheBuildResult(
                snapshot,
                fingerprintMaps.Select((map, index) =>
                {
                    MapOperationProgress.Report(.75 + .15 * index / Math.Max(1, snapshot.Length),
                        $"正在建立地图指纹：{map.DisplayName}……");
                    return TryCreateFingerprint(map);
                })
                    .Where(fingerprint => fingerprint is not null)
                    .Cast<MapGeometryFingerprint>()
                    .ToArray(),
                snapshot.Select(map => map.Id).ToHashSet());
        }
        else
        {
            var previousMaps = _maps.ToDictionary(map => map.Id);
            var previousFingerprints = _fingerprints.ToDictionary(
                fingerprint => fingerprint.Map.Id);
            var changedIds = snapshot
                .Where(map => changedMapId == map.Id
                    || !previousMaps.TryGetValue(map.Id, out var previous)
                    || !MapCvRecognitionHelpers.HaveSameFingerprintInputs(previous, map))
                .Select(map => map.Id)
                .ToHashSet();

            var fingerprints = new List<MapGeometryFingerprint>();
            var processedMaps = 0;
            foreach (var map in fingerprintMaps)
            {
                MapOperationProgress.Report(.75 + .15 * processedMaps++ / Math.Max(1, snapshot.Length),
                    $"正在更新地图指纹：{map.DisplayName}……");
                if (!changedIds.Contains(map.Id)
                    && previousFingerprints.TryGetValue(map.Id, out var existing))
                {
                    fingerprints.Add(RebindFingerprint(existing, map));
                    continue;
                }

                if (TryCreateFingerprint(map) is { } rebuilt)
                    fingerprints.Add(rebuilt);
            }

            buildResult = new CacheBuildResult(snapshot, fingerprints, changedIds);
        }

        return buildResult;
    }

    private async Task<bool> TryRefreshScanFloorAsync(MapCatalogRevision revision)
    {
        if (!_repository.TryGetScanFloorAdvance(_catalogRevision, revision, out var className, out var floorKey))
            return false;
        using var memory = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Recognition.RefreshScanFloor", className);
        var maps = await Task.Run(() =>
        {
            // Other classes are immutable cache records. Only the affected class
            // needs detached records, so readers of the old cache remain isolated.
            return _maps.Select(map =>
            {
                if (!string.Equals(map.Class, className, StringComparison.OrdinalIgnoreCase)) return map;
                var updated = map.Clone();
                updated.ClassProperties.ScanFloorKey = floorKey;
                return updated;
            }).ToArray();
        });
        var affected = maps.Where(map => string.Equals(map.Class, className, StringComparison.OrdinalIgnoreCase)).ToArray();
        var properties = affected.FirstOrDefault()?.ClassProperties.Clone() ?? new MapClassProperties { ScanFloorKey = floorKey };
        var assetMaps = affected.Where(map => !map.IsListProjection).ToArray();
        // Inactive classes own metadata only. Their complete image assets are
        // verified during match preparation, before any capture deadline.
        if (assetMaps.Length > 0)
            await MapOperationProgress.RunAsync(0, .65, () => _repository.EnsureDerivedAssetsFromSnapshotAsync(assetMaps,
            new Dictionary<string, MapClassProperties>(StringComparer.OrdinalIgnoreCase) { [className] = properties }));
        // Actual repairs invalidate the single-property lineage and use the full
        // synchronization path. This optimization must not conceal asset changes.
        if (_repository.GetCatalogRevision() != revision) return false;
        var (cache, features) = await Task.Run(() =>
        {
            var built = BuildFingerprintCache(maps, null);
            Dictionary<(Guid, string), ScanStructureIndex> scanFeatures;
            using (MapOperationProgress.Range(.9, .97))
                scanFeatures = MapCvRecognitionHelpers.BuildSideEntranceFeatureCache(_repository,
                    ResidentMaps(built.Maps).Where(map => built.ChangedMapIds.Contains(map.Id)).ToArray(), _deterministicScanStorage, _deterministicSemanticStorage);
            try
            {
                if (scanFeatures.Count > 0)
                {
                    using var range = MapOperationProgress.Range(.97, .99);
                    PrewarmDeepScan(scanFeatures.Values, MapOperationProgress.Capture());
                }
                return (built, scanFeatures);
            }
            catch { ScanIndexCache.RetireUnpublished(scanFeatures.Values); throw; }
        });
        if (_repository.GetCatalogRevision() != revision)
        {
            ScanIndexCache.RetireUnpublished(features.Values);
            return false;
        }
        _maps = cache.Maps;
        _fingerprints = cache.Fingerprints;
        _catalogRevision = revision;
        // Scan selection changes geometry fingerprints, not any floor's image,
        // semantic contour, alignment key or scale. Keep those floor-specific
        // owners; replacing them here used to queue redundant full-class builds.
        _sideEntranceFeatureCache.Publish(features, cache.ChangedMapIds);
        await ReconcileScanFloorResidencyAsync();
        AuditResidentResources("Catalog.ScanFloorUpdated");
        MapOperationProgress.Report(1, "扫描楼层缓存已同步");
        return true;
    }
    public async Task RefreshCacheAsync(Guid? changedMapId = null)
    {
        using var memory = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Recognition.RefreshCatalog", changedMapId?.ToString());
        ObjectDisposedException.ThrowIf(_disposed, this);
        MapOperationProgress.Report(0, "正在读取地图缓存……");
        var revision = _repository.GetCatalogRevision();
        if (_cacheInitialized && revision == _catalogRevision)
        {
            MapOperationProgress.Report(1, "地图缓存已同步");
            return;
        }

        Dictionary<(Guid, string), ScanStructureIndex>? unpublishedScanCache = null;
        await _cacheGate.WaitAsync();
        try
        {
            revision = _repository.GetCatalogRevision();
            if (_cacheInitialized && revision == _catalogRevision)
            {
                MapOperationProgress.Report(1, "地图缓存已同步");
                return;
            }
            if (_cacheInitialized && changedMapId is null
                && await TryRefreshScanFloorAsync(revision))
                return;
            // Migrate before loading Mats/fingerprints. Background diagnostics alone
            // could repair files after the first scan had already cached stale lines.
            var catalog = await LoadRefreshCatalogAsync();
            catalog = await HealRefreshCatalogAsync(catalog);
            var maps = catalog.Maps;
            await MapOperationProgress.RunAsync(.35, .75,
                () => _repository.EnsureDerivedAssetsFromSnapshotAsync(maps, catalog.ClassProperties));

            var cacheDispatch = MapOperationTraceAmbient.StartChild(
                "map_catalog_fingerprint_dispatch_wait",
                MapOperationWaitKind.Queue);
            CacheBuildResult cache;
            Dictionary<(Guid, string), ScanStructureIndex> sideEntranceCache;
            try
            {
                (cache, sideEntranceCache) = await Task.Run(() =>
                {
                    cacheDispatch.Complete();
                    using var cacheWorker = MapOperationTraceAmbient.StartChild(
                        "map_catalog_fingerprint_build",
                        MapOperationWaitKind.Compute);
                    // The repository returned a fresh detached graph, exclusively
                    // owned by this refresh. Transfer it to the cache rather than
                    // holding both it and a deep clone during fingerprint work.
                    var snapshot = maps.ToArray();
                    ProjectInactiveRecognitionMaps(snapshot);
                    var buildResult = BuildFingerprintCache(snapshot, changedMapId);

                    Dictionary<(Guid, string), ScanStructureIndex> builtSideEntrance;
                    using (MapOperationProgress.Range(.9, .98))
                        builtSideEntrance = MapCvRecognitionHelpers.BuildSideEntranceFeatureCache(_repository, ResidentMaps(buildResult.Maps), _deterministicScanStorage, _deterministicSemanticStorage);
                    try
                    {
                        MapOperationProgress.Report(.98, "正在预热识别缓存……");
                        if (builtSideEntrance.Count > 0)
                            PrewarmDeepScan(builtSideEntrance.Values);
                        return (buildResult, builtSideEntrance);
                    }
                    catch { ScanIndexCache.RetireUnpublished(builtSideEntrance.Values); throw; }
                });
                unpublishedScanCache = sideEntranceCache;
            }
            finally
            {
                cacheDispatch.Complete();
            }

            MapOperationProgress.Report(.99, "正在应用地图缓存……");
            TotalMapCount = cache.Maps.Count;
            _maps = cache.Maps;
            ScanVariantGroups = catalog.VariantGroups.Select(g => g.MapIds.ToArray()).ToArray();
            CatalogVariantGroups = catalog.VariantGroups.Select(g => g.Clone()).ToArray();
            _fingerprints = cache.Fingerprints;
            // A template install can refresh in the background while the user
            // edits again. Never stamp this earlier snapshot with a newer
            // catalog revision that arrived during fingerprint construction.
            // Repairs may also change the revision; a later sync then reloads it.
            _catalogRevision = revision;
            _cacheInitialized = true;
            _structureCache.InvalidateMaps(cache.ChangedMapIds);
            InvalidateAndTriggerVpsg3Rebuild(ResidentMaps(cache.Maps), cache.ChangedMapIds);
            // MapRepository may overwrite an image without changing its path.
            _invalidateOverlayImageCache?.Invoke();

            // 刷新侧门特征缓存
            unpublishedScanCache = null; // Publish owns this dictionary on success or failure.
            _sideEntranceFeatureCache.Publish(sideEntranceCache);
            AuditResidentResources("Catalog.ResourcesPrepared");
            MapOperationProgress.Report(1, "地图缓存刷新完成");
        }
        finally
        {
            try { if (unpublishedScanCache is not null) ScanIndexCache.RetireUnpublished(unpublishedScanCache.Values); }
            finally { _cacheGate.Release(); }
        }
    }


    // ── 公开识别入口 ──────────────────────────────────────────────────────────


}


