using System.ComponentModel;
using System.Runtime.InteropServices;
using IDVBuff.Core.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// One top-down BGRA DIB. A frame borrows its pixels exclusively; retirement
/// defers native deletion until that frame ends. Never overwrite a leased DIB.
/// </summary>
internal sealed class GdiCaptureSurface : IDisposable
{
    private readonly object _gate = new();
    private IntPtr _dc, _bitmap, _previousBitmap, _bits;
    private IDisposable? _ownership;
    private bool _leased, _retired;
    internal int Width { get; }
    internal int Height { get; }
    internal bool CanCapture { get { lock (_gate) return !_retired && !_leased; } }
    internal bool IsReleased { get { lock (_gate) return _bitmap == IntPtr.Zero; } }

    internal GdiCaptureSurface(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        var bytes = checked((long)width * height * 4);
        // A successful allocation is recorded once, not again for its Mat view.
        var request = MemoryJournal.Request("GDI.CaptureSurface.Pixels", bytes, "gdi32.dll");
#if IDVB_DEVELOPMENT_BUILD
        // Capture only on a new surface, never per frame. Correlate an
        // outstanding allocation with its real caller across reset/drain.
        MemoryJournal.Current?.Record("capture-surface-request-context",
            new { request, width, height, stack = Environment.StackTrace });
#endif
        var screen = IntPtr.Zero;
        try
        {
            // Supplying a screen DC avoids tying the memory DC to the lifetime
            // of whichever thread-pool thread first performs this capture.
            screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) throw NativeFailure("GetDC");
            _dc = CreateCompatibleDC(screen);
            if (_dc == IntPtr.Zero) throw NativeFailure("CreateCompatibleDC");
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width,
                    Height = -height, Planes = 1, BitCount = 32
                }
            };
            // NULL hSection: ordinary process-owned DIB storage, no file mapping.
            _bitmap = CreateDIBSection(_dc, in info, 0, out _bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero) throw NativeFailure("CreateDIBSection");
            _previousBitmap = SelectObject(_dc, _bitmap);
            if (_previousBitmap == IntPtr.Zero || _previousBitmap == new IntPtr(-1))
                throw NativeFailure("SelectObject");
            _ownership = MemoryJournal.Own("GDI.CaptureSurface.Pixels", bytes, "gdi32.dll", request);
        }
        catch { Dispose(); throw; }
        finally { if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen); }
    }

    internal void Capture(int screenX, int screenY)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_retired, this);
            if (_leased) throw new InvalidOperationException("Cannot overwrite an in-flight captured frame.");
            var screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) throw NativeFailure("GetDC");
            try
            {
                if (!BitBlt(_dc, 0, 0, Width, Height, screen, screenX, screenY, 0x00CC0020))
                    throw NativeFailure("BitBlt");
                // Flush this thread's GDI batch before OpenCV can read the bits.
                if (!GdiFlush()) throw NativeFailure("GdiFlush");
            }
            finally { ReleaseDC(IntPtr.Zero, screen); }
        }
    }

#if IDVB_DEVELOPMENT_BUILD
    internal void CaptureWindow(nint window, int clientX, int clientY)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_retired, this);
            if (_leased) throw new InvalidOperationException("Cannot overwrite an in-flight captured frame.");
            var saved = SaveDC(_dc);
            if (saved == 0) throw NativeFailure("SaveDC");
            try
            {
                if (!SetViewportOrgEx(_dc, -clientX, -clientY, 0)) throw NativeFailure("SetViewportOrgEx");
                // Synchronous completion is required: an abandoned timeout
                // must never let OG write into a retired/deleted frame's DC.
                if (!PrintWindow(window, _dc, 1))
                    throw new InvalidOperationException("OG 未完成专用窗口渲染。");
                if (!GdiFlush()) throw NativeFailure("GdiFlush");
            }
            finally { if (!RestoreDC(_dc, saved)) throw NativeFailure("RestoreDC"); }
        }
    }
    [DllImport("gdi32.dll", SetLastError = true)] private static extern int SaveDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool RestoreDC(nint dc, int saved);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool SetViewportOrgEx(nint dc, int x, int y, nint previous);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PrintWindow(nint window, nint dc, uint flags);
#endif

    internal PixelLease RentPixels()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_retired, this);
            if (_leased) throw new InvalidOperationException("Captured pixels already have a frame owner.");
            var image = Mat.FromPixelData(Height, Width, MatType.CV_8UC4, _bits, checked(Width * 4L));
            _leased = true;
            return new PixelLease(this, image);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _retired = true;
            if (!_leased) ReleaseNative();
        }
    }

    private void ReleaseLease()
    {
        lock (_gate)
        {
            _leased = false;
            if (_retired) ReleaseNative();
        }
    }

    private void ReleaseNative()
    {
        if (_previousBitmap != IntPtr.Zero && _previousBitmap != new IntPtr(-1))
        {
            if (SelectObject(_dc, _previousBitmap) == IntPtr.Zero) throw NativeFailure("SelectObject restore");
            _previousBitmap = IntPtr.Zero;
        }
        if (_bitmap != IntPtr.Zero)
        {
            if (!DeleteObject(_bitmap)) throw NativeFailure("DeleteObject DIB");
            _bitmap = _bits = IntPtr.Zero;
            _ownership?.Dispose();
            _ownership = null;
        }
        if (_dc != IntPtr.Zero)
        {
            if (!DeleteDC(_dc)) throw NativeFailure("DeleteDC");
            _dc = IntPtr.Zero;
        }
    }

    internal sealed class PixelLease(GdiCaptureSurface owner, Mat image) : IDisposable
    {
        private GdiCaptureSurface? _owner = owner;
        internal Mat Image { get; } = image;
        public void Dispose()
        {
            var previous = Interlocked.Exchange(ref _owner, null);
            if (previous is null) return;
            try { Image.Dispose(); }
            finally { previous.ReleaseLease(); }
        }
    }

    private static Win32Exception NativeFailure(string action) => new(Marshal.GetLastPInvokeError(), action);
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, in BitmapInfo info,
        uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(IntPtr destination, int x, int y,
        int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool GdiFlush();
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}

/// <summary>Bounded private-pixel cache. Its capture service serializes Get,
/// TrimIdle and Reset with capture/rent; outstanding frame leases own retired surfaces.</summary>
internal sealed class GdiCaptureSurfaceCache : IDisposable
{
    // Map viewport and full-client captures (plugins) alternate. One reusable
    // surface per size avoids reallocating a 13-16 MB DIB on every switch; an
    // overlapping in-flight frame still gets independent storage.
    private const int MaximumSurfaces = 3;
    private readonly List<(GdiCaptureSurface Surface, long LastUsed)> _surfaces = [];

    internal int Count => _surfaces.Count;

    internal GdiCaptureSurface Get(int width, int height) => Get(width, height, Environment.TickCount64);

    internal GdiCaptureSurface Get(int width, int height, long now)
    {
        for (var index = _surfaces.Count - 1; index >= 0; index--)
        {
            var cached = _surfaces[index].Surface;
            if (cached.Width != width || cached.Height != height || !cached.CanCapture) continue;
            // Most recently used last; eviction takes the front.
            _surfaces.RemoveAt(index);
            _surfaces.Add((cached, now));
            return cached;
        }
        while (_surfaces.Count >= MaximumSurfaces)
        {
            var oldest = _surfaces[0].Surface;
            _surfaces.RemoveAt(0);
            oldest.Dispose();
        }
        var surface = new GdiCaptureSurface(width, height);
        _surfaces.Add((surface, now));
        return surface;
    }

    /// <summary>Retires surfaces no capture has used for <paramref name="idleMilliseconds"/>.
    /// A surface still leased by a frame is freed when that frame ends.</summary>
    internal void TrimIdle(long now, long idleMilliseconds)
    {
        for (var index = _surfaces.Count - 1; index >= 0; index--)
        {
            var (surface, lastUsed) = _surfaces[index];
            if (now - lastUsed < idleMilliseconds) continue;
            _surfaces.RemoveAt(index);
            surface.Dispose();
        }
    }

    internal void Reset()
    {
        foreach (var (surface, _) in _surfaces) surface.Dispose();
        _surfaces.Clear();
    }
    public void Dispose() => Reset();
}
