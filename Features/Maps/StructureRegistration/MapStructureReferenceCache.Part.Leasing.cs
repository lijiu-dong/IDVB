using OpenCvSharp;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class MapStructureReferenceCache
{
    /// <summary>Rent the actual immutable reference on cold and warm paths.
    /// A cold lookup does not create a second set of native raster buffers.</summary>
    public MapStructureFeaturesLease RentOrCreate(
        Guid mapId, DateTimeOffset updatedAt, Mat referenceImage,
        IReadOnlyList<NormalizedRectangle>? ignoreRegions = null, string floor = "1f",
        MapStructureGenerationTuning? generationTuning = null,
        MapStructurePreprocessingProfile profile = MapStructurePreprocessingProfile.EdgesAndFeatures,
        SemanticStructureIndex? residentSemanticIndex = null)
    {
        using var operation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Structure.Reference.RentOrCreate", $"map={mapId:D};floor={floor};profile={profile}");
        using var protection = AppDataPaths.ProtectCachePath(Path.Combine(_rootDirectory, mapId.ToString("N")));
        MapStructureFeaturesLease? lease = null;
        GetOrCreateCore(mapId, updatedAt, referenceImage, ignoreRegions, floor, generationTuning, profile, value => lease = value,
            residentSemanticIndex);
        return lease ?? throw new InvalidOperationException("Reference cache did not return its ownership lease.");
    }

    // Caller holds _memoryGate. Count by the feature owner, not its reusable key:
    // an invalidated old generation may still be borrowed when the key is rebuilt.
    private MapStructureFeaturesLease CreateLease(MapStructureFeatures features)
    {
        _leaseCounts[features] = _leaseCounts.GetValueOrDefault(features) + 1;
        return new(this, features);
    }

    private MapStructureFeatures AccessResident(MapStructureFeatures features, Action<MapStructureFeaturesLease>? rental)
    {
        if (rental is null)
        {
            EnsureOwnedCopyDistances(features);
            return features.Clone();
        }
        rental(CreateLease(features));
        return features;
    }

    private static void EnsureOwnedCopyDistances(MapStructureFeatures features)
    {
        using var operation = MapOperationTraceAmbient.StartChild("reference_distance_map", MapOperationWaitKind.Compute);
        features.GetOrCreateReferenceDistanceMap();
        features.GetOrCreateClippedReferenceDistanceMap(12d);
    }

    public sealed class MapStructureFeaturesLease : IDisposable
    {
        private readonly MapStructureReferenceCache? _owner;
        private int _returned;
        internal MapStructureFeaturesLease(MapStructureReferenceCache? owner, MapStructureFeatures features)
        {
            _owner = owner;
            Features = features;
        }
        public MapStructureFeatures Features { get; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) != 0) return;
            if (_owner is null) Features.Dispose();
            else _owner.ReturnLease(Features);
        }
    }
}
