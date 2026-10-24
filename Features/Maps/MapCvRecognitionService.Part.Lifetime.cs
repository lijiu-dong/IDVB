namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _sideEntranceFeatureCache.Dispose();
        ResetScaleCoverage();
        lock (_floorPrewarmGate)
        {
            _matchCts.Cancel();
            _matchCts.Dispose();
            _floorPrewarmTasks.Clear();
        }
        _gateDetector.Dispose();
        Vpsg3FastLiveExtractor.ReleaseSharedScratch();
        _deepScanGateDetector?.Dispose();
        _structureCache.Dispose();
        MapStructurePreprocessor.ClearReferenceCache();
        DisposeVpsg3();
        _auxiliaryTemplateCache.Dispose();
        _cacheGate.Dispose();
        _referenceGateSizes.Clear();
    }
}
