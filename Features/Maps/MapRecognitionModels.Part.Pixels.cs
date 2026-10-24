using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class CapturedGameFrame
{
    internal CapturedGameFrame CreateSubview(Rect rect, MapScreenRect viewportBounds, string? floorKey = null)
    {
        lock (_derivedFeaturesGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var lifetime = _pixels.Rent();
            Mat? view = null;
            try
            {
                view = new Mat(Image, rect);
                return new CapturedGameFrame(view, ClientBounds, viewportBounds, WindowHandle, lifetime)
                {
                    DetectedFloorKey = floorKey ?? DetectedFloorKey,
                    CaptureBackend = CaptureBackend,
                    CaptureSystemRelativeTicks = CaptureSystemRelativeTicks,
                    CaptureReadbackMilliseconds = CaptureReadbackMilliseconds,
                    CaptureDroppedFrames = CaptureDroppedFrames,
                    AutomaticIdentityUiExclusions = AutomaticIdentityUiExclusions
                };
            }
            catch { view?.Dispose(); lifetime.Dispose(); throw; }
        }
    }

    // Mat headers retain OpenCV-owned pixels, but cannot retain an external GDI
    // DIB. Keep the actual pixel owner until the last nested view is released.
    private sealed class PixelStorage
    {
        private readonly Mat _image;
        private readonly IDisposable? _lifetime, _ownership;
        internal string NativeModule { get; }
        private int _references = 1;
        internal PixelStorage(Mat image, IDisposable? lifetime)
        {
            _image = image;
            _lifetime = lifetime;
            NativeModule = lifetime is PixelLease retained ? retained.NativeModule
                : lifetime is null ? "OpenCvSharpExtern.dll" : "gdi32.dll";
            var resource = lifetime is PixelLease ? "CapturedGameFrame.PixelLease"
                : lifetime is null ? "CapturedGameFrame.Image" : "CapturedGameFrame.GdiPixelLease";
            _ownership = IDVBuff.Core.Diagnostics.MemoryJournal.Own(resource,
                lifetime is null ? image.Total() * image.ElemSize() : 0, NativeModule);
        }
        internal PixelLease Rent()
        {
            var lease = new PixelLease(this);
            Interlocked.Increment(ref _references);
            return lease;
        }
        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0) return;
            try { _image.Dispose(); }
            finally
            {
                try { _lifetime?.Dispose(); }
                finally { _ownership?.Dispose(); }
            }
        }
    }

    private sealed class PixelLease(PixelStorage storage) : IDisposable
    {
        private PixelStorage? _storage = storage;
        internal string NativeModule => _storage!.NativeModule;
        public void Dispose() => Interlocked.Exchange(ref _storage, null)?.Release();
    }

}
