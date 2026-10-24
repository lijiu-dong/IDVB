using OpenCvSharp;
using System.Text.Json;
using IDVBuff.Diagnostics;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;
/// <summary>
/// Non-authoritative derived cache. It never writes into MapRepository or
/// changes maps.json.
/// </summary>
public sealed partial class MapStructureReferenceCache : IDisposable
{
    internal int ResidentCount
    {
        get
        {
            lock (_memoryGate)
                return _memoryCache.Count;
        }
    }

    /// <summary>
    /// Discards all cached reference features and releases unmanaged memory.
    /// Retires cache ownership immediately. Active readers keep their native
    /// pixels until the last lease returns, including across repeated resets.
    /// </summary>
    public void Clear()
    {
        using var perfScope = RealtimePerformanceTracker.TrackScope("StructureCache.Clear", forceLog: true);
        lock (_memoryGate)
        {
            _generation++;
            foreach (var (_, (features, _)) in _memoryCache)
            {
                if (_leaseCounts.GetValueOrDefault(features) > 0)
                    _evictedWhileLeased.Add(features);
                else
                    features.Dispose();
            }
            _memoryCache.Clear();
            // Already-retired borrowers belong to their feature generation,
            // not the reusable cache key. Clear must not erase their counts.
            _lruList.Clear();
        }
    }

    public void InvalidateMaps(IReadOnlySet<Guid> mapIds)
    {
        if (mapIds.Count == 0)
            return;
        var dispose = new List<MapStructureFeatures>();
        lock (_memoryGate)
        {
            foreach (var key in _memoryCache.Keys
                .Where(key => mapIds.Contains(key.MapId))
                .ToArray())
            {
                var cached = _memoryCache[key];
                _memoryCache.Remove(key);
                _lruList.Remove(cached.Node);
                if (_leaseCounts.GetValueOrDefault(cached.Features) > 0)
                    _evictedWhileLeased.Add(cached.Features);
                else
                    dispose.Add(cached.Features);
            }
        }
        foreach (var features in dispose)
            features.Dispose();
    }

    // Prebuilt references are immutable pixels, independent of identity,
    // alignment and scale state. Their separate one-entry budget and complete
    // cache key remain in force; a rescan should not rebuild the same raster.
    // Legacy descriptor references are not retained by this rescan policy.
    internal void PrepareForRescan()
    {
        var dispose = new List<MapStructureFeatures>();
        lock (_memoryGate)
        {
            // Reject producers started before this reset without retiring a
            // healthy resident prebuilt owner or invalidating its readers.
            _generation++;
            foreach (var key in _memoryCache.Keys.Where(key =>
                key.Profile != MapStructurePreprocessingProfile.PrebuiltStructureLine).ToArray())
            {
                var cached = _memoryCache[key];
                _memoryCache.Remove(key);
                _lruList.Remove(cached.Node);
                if (_leaseCounts.GetValueOrDefault(cached.Features) > 0) _evictedWhileLeased.Add(cached.Features);
                else dispose.Add(cached.Features);
            }
            foreach (var cached in _memoryCache.Values)
            {
                if (_leaseCounts.GetValueOrDefault(cached.Features) == 0)
                    cached.Features.CompactReferenceDistanceRaster();
            }
        }
        foreach (var features in dispose) features.Dispose();
    }

    private sealed class KeyPointDocument
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Size { get; set; }
        public float Angle { get; set; }
        public float Response { get; set; }
        public int Octave { get; set; }
        public int ClassId { get; set; }

        public static KeyPointDocument From(KeyPoint point) => new()
        {
            X = point.Pt.X,
            Y = point.Pt.Y,
            Size = point.Size,
            Angle = point.Angle,
            Response = point.Response,
            Octave = point.Octave,
            ClassId = point.ClassId
        };

        public KeyPoint ToKeyPoint() => new(
            X,
            Y,
            Size,
            Angle,
            Response,
            Octave,
            ClassId);
    }

    private static MapStructureGenerationTuning NormalizeGeneration(
        MapStructureGenerationTuning? generationTuning)
    {
        var normalized = generationTuning?.Clone() ?? new();
        normalized.Normalize();
        return normalized;
    }

    private static KeyPoint[] ReadKeyPoints(string path)
    {
        try
        {
            var documents = JsonSerializer.Deserialize<KeyPointDocument[]>(
                File.ReadAllText(path));
            return documents?.Select(document => document.ToKeyPoint()).ToArray()
                ?? [];
        }
        catch
        {
            return [];
        }
    }
}
