using OpenCvSharp;
using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class MapStructureFeatures
{
    private ClippedFloatPlane? _compactClippedDistance;

    // Only the cache calls this with no outstanding feature rental. A reader's
    // returned Mat remains intact until its complete feature lease ends.
    internal void CompactReferenceDistanceRaster()
    {
        lock (_visibleAwareCacheSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ClippedReferenceDistanceMap is not { } raster || raster.Empty()
                || ClippedDistancePixels is not { } clip || !double.IsFinite(clip) || clip <= 0) return;
            using var operation = MemoryJournal.BeginOperation("Structure.Reference.ClippedDistance.Compact");
            _compactClippedDistance ??= new ClippedFloatPlane(raster, (float)clip);
            raster.Dispose();
            MemoryJournal.ReleaseResource(raster);
            ClippedReferenceDistanceMap = null;
            MemoryJournal.Current?.Record("reference-distance-residency", new
            {
                clip, bytes = _compactClippedDistance.MemoryBytes,
                meaning = "exact float pixels retained; full native matrix physically disposed after the last feature reader"
            });
        }
    }
}
