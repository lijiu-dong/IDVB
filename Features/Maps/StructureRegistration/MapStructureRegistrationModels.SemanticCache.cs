using OpenCvSharp;
using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class MapStructureFeatures
{
    // At most three channels for one exact clipping value, inside the reference
    // owner's existing one-entry budget. Retired readers hold their own leases.
    private readonly Dictionary<byte, SemanticDistanceOwner> _semanticDistances = [];
    private long _semanticClipBits;

    internal SemanticDistanceOwner.Lease RentSemanticDistance(byte kind, double clip)
    {
        lock (_visibleAwareCacheSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (SemanticLabels is null) throw new InvalidOperationException("Reference semantic labels are required.");
            if (kind != 0 && kind != SemanticStructureContours.Warm && kind != SemanticStructureContours.Cool)
                throw new ArgumentOutOfRangeException(nameof(kind));
            if (!double.IsFinite(clip) || clip <= 0) throw new ArgumentOutOfRangeException(nameof(clip));
            var bits = BitConverter.DoubleToInt64Bits(clip);
            if (bits != _semanticClipBits)
            {
                foreach (var previous in _semanticDistances.Values) previous.Dispose();
                _semanticDistances.Clear();
                _semanticClipBits = bits;
            }
            if (!_semanticDistances.TryGetValue(kind, out var cached))
            {
                cached = SemanticDistanceOwner.Create(SemanticLabels, kind, clip);
                try { _semanticDistances.Add(kind, cached); }
                catch { cached.Dispose(); throw; }
            }
            return cached.Rent();
        }
    }
}

internal sealed class SemanticDistanceOwner : IDisposable
{
    internal ClippedFloatPlane Distance { get; }
    private int _references = 1;
    private int _retired;
    private SemanticDistanceOwner(ClippedFloatPlane distance) => Distance = distance;

    internal static SemanticDistanceOwner Create(Mat reference, byte kind, double clip)
    {
        using var operation = MemoryJournal.BeginOperation("Structure.SemanticReference.Build", $"kind={kind};clip={clip:R}");
        using var mask = new Mat();
        if (kind == 0) Cv2.Threshold(reference, mask, 0, 255, ThresholdTypes.Binary);
        else Cv2.InRange(reference, new Scalar(kind), new Scalar(kind), mask);
        var name = $"Structure.SemanticScale.Distance/{kind}";
        var request = MemoryJournal.Request(name, reference.Total() * sizeof(float), "OpenCvSharpExtern.dll");
        var distance = new NativeDistanceMat(reference.Size(), MatType.CV_32FC1, new Scalar(clip));
        ClippedFloatPlane? packed = null;
        try
        {
            MemoryJournal.TrackResource(distance, name, distance.Total() * sizeof(float), "OpenCvSharpExtern.dll", request);
            if (Cv2.CountNonZero(mask) > 0)
            {
                using var inverse = new Mat();
                Cv2.BitwiseNot(mask, inverse);
                Cv2.DistanceTransform(inverse, distance, DistanceTypes.L2, DistanceTransformMasks.Mask3);
                Cv2.Threshold(distance, distance, clip, clip, ThresholdTypes.Trunc);
            }
            packed = new ClippedFloatPlane(distance, (float)clip);
            return new(packed);
        }
        catch
        {
            packed?.Dispose();
            throw;
        }
        finally
        {
            distance.Dispose(); MemoryJournal.ReleaseResource(distance);
        }
    }

    internal Lease Rent()
    {
        if (Volatile.Read(ref _retired) != 0) throw new ObjectDisposedException(nameof(SemanticDistanceOwner));
        Interlocked.Increment(ref _references);
        return new(this);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _retired, 1) == 0) Release();
    }
    private void Release()
    {
        if (Interlocked.Decrement(ref _references) != 0) return;
        Distance.Dispose();
    }
    internal sealed class Lease(SemanticDistanceOwner owner) : IDisposable
    {
        private SemanticDistanceOwner? _owner = owner;
        internal ClippedFloatPlane Distance => _owner?.Distance ?? throw new ObjectDisposedException(nameof(Lease));
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
