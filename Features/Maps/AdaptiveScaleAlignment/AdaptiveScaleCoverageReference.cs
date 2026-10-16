using System.Runtime.InteropServices;
using IDVBuff.Core.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps.AdaptiveScaleAlignment;

// FindNonZero already owns the complete ordered point buffer. Keep that buffer
// instead of copying it to a large managed array on every rescan. Reset retires
// the cache owner; an in-flight measurement keeps it alive only through its lease.
internal sealed class AdaptiveScaleCoverageReference : IDisposable
{
    private readonly object _gate = new();
    private Mat? _storage;
    private int _readers;
    private bool _retired;

    private AdaptiveScaleCoverageReference(Mat storage) => _storage = storage;

    internal static AdaptiveScaleCoverageReference Create(Mat contours)
    {
        var request = MemoryJournal.Request("Structure.Coverage.ReferencePoints", nativeModule: "OpenCvSharpExtern.dll");
        var storage = new Mat();
        try
        {
            Cv2.FindNonZero(contours, storage);
            if (!storage.Empty() && (!storage.IsContinuous() || storage.Type() != MatType.CV_32SC2 || storage.Cols != 1))
                throw new InvalidDataException("覆盖率轮廓点存储格式不正确。");
            MemoryJournal.TrackResource(storage, "Structure.Coverage.ReferencePoints",
                storage.Total() * storage.ElemSize(), "OpenCvSharpExtern.dll", request);
            return new(storage);
        }
        catch
        {
            storage.Dispose();
            MemoryJournal.ReleaseResource(storage);
            throw;
        }
    }

    internal Lease Borrow()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_retired, this);
            var lease = new Lease(this, _storage!);
            _readers++;
            return lease;
        }
    }

    public void Dispose()
    {
        Mat? release;
        lock (_gate)
        {
            if (_retired) return;
            _retired = true;
            release = _readers == 0 ? TakeStorage() : null;
        }
        Free(release);
    }

    private Mat? TakeStorage()
    {
        var storage = _storage;
        _storage = null;
        return storage;
    }

    private void Return()
    {
        Mat? release;
        lock (_gate)
        {
            _readers--;
            release = _retired && _readers == 0 ? TakeStorage() : null;
        }
        Free(release);
    }

    private static void Free(Mat? storage)
    {
        if (storage is null) return;
        try { storage.Dispose(); }
        finally { MemoryJournal.ReleaseResource(storage); }
    }

    internal sealed class Lease(AdaptiveScaleCoverageReference owner, Mat storage) : IDisposable
    {
        private AdaptiveScaleCoverageReference? _owner = owner;

        // The caller must keep this lease until the synchronous span consumer
        // returns. Neither the span nor the Mat is exposed to an async cache.
        internal ReadOnlySpan<Point> Points
        {
            get
            {
                ObjectDisposedException.ThrowIf(_owner is null, this);
                return storage.Empty() ? [] : MemoryMarshal.CreateReadOnlySpan(ref storage.At<Point>(0, 0), storage.Rows);
            }
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Return();
    }
}
