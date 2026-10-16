using System.Drawing;
using System.Drawing.Imaging;
using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Features.Maps;

internal sealed partial class MapOverlayNativeWindow
{
    // One private process-local pixel allocation backs both the cache's GDI+
    // bitmap and its native presentation. No mapped section or external owner.
    internal sealed class SharedDib : IDisposable
    {
        private readonly object _gate = new();
        private readonly DibHandle? _handleOwner;
        private int _references = 1, _selectedReferences;
        private IntPtr _selectedDc;
        private bool _cacheDisposed;
        internal Bitmap Bitmap { get; }
        internal IntPtr Handle { get; private set; }
        internal IntPtr Pixels { get; }

        internal SharedDib(int width, int height, string resource)
        {
            var bytes = checked((long)width * height * 4);
            var request = MemoryJournal.Request(resource + ".Pixels", bytes, "gdi32.dll");
            var info = new BitmapInfoHeader
            {
                biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32
            };
            var dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) throw NativeFailure("Unable to acquire the map layer screen DC.");
            IntPtr pixels;
            try { Handle = CreateDIBSection(dc, ref info, 0, out pixels, IntPtr.Zero, 0); }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            if (Handle == IntPtr.Zero || pixels == IntPtr.Zero)
            {
                if (Handle != IntPtr.Zero) DeleteObject(Handle);
                Handle = IntPtr.Zero;
                throw NativeFailure("Unable to create the map layer pixel buffer.");
            }
            Pixels = pixels;
            try
            {
                _handleOwner = new DibHandle(Handle);
                _handleOwner.Ownership = MemoryJournal.Own(resource + ".Pixels", bytes, "gdi32.dll", request);
                Bitmap = new Bitmap(width, height, checked(width * 4), PixelFormat.Format32bppPArgb, pixels);
                using var graphics = Graphics.FromImage(Bitmap);
                graphics.Clear(Color.Transparent);
            }
            catch
            {
                Bitmap?.Dispose();
                if (_handleOwner is not null) _handleOwner.Dispose(); else DeleteObject(Handle);
                Handle = IntPtr.Zero;
                throw;
            }
        }

        // An HBITMAP can be selected into only one DC. Concurrent presenters
        // keep the existing private-copy path rather than borrowing illegally.
        internal IDisposable? TryBorrow(IntPtr dc)
        {
            lock (_gate)
            {
                if (_cacheDisposed || (_selectedDc != IntPtr.Zero && _selectedDc != dc)) return null;
                _references++; _selectedReferences++; _selectedDc = dc;
                return new Borrow(this);
            }
        }
        private sealed class Borrow(SharedDib owner) : IDisposable
        {
            private SharedDib? _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseBorrow();
        }
        private void ReleaseBorrow()
        {
            lock (_gate)
            {
                if (--_selectedReferences == 0) _selectedDc = IntPtr.Zero;
                ReleaseCore();
            }
        }
        private void ReleaseCore()
        {
            if (--_references != 0) return;
            _handleOwner!.Dispose();
            if (!_handleOwner.Deleted) throw NativeFailure("Unable to release the map layer pixel buffer.");
            Handle = IntPtr.Zero;
        }
        // Exception rollback is explicit; SafeHandle also protects an orphaned
        // unpublished layer without keeping its pixel allocation forever.
        private sealed class DibHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
        {
            internal IDisposable? Ownership;
            internal bool Deleted;
            internal DibHandle(IntPtr handle) : base(true) => SetHandle(handle);
            protected override bool ReleaseHandle()
            {
                Deleted = DeleteObject(handle);
                try
                {
                    if (Deleted) Ownership?.Dispose();
                    else MemoryJournal.Current?.Record("resource-release-failed", new { resource = "Overlay.BakedMapLayer.Pixels", handle = handle.ToInt64() });
                }
                catch { /* A failed journal already reports its writer failure. */ }
                return Deleted;
            }
        }
        public void Dispose()
        {
            lock (_gate)
            {
                if (_cacheDisposed) return;
                _cacheDisposed = true;
                Bitmap.Dispose();
                ReleaseCore();
            }
        }
    }

    private bool TryBindSharedMapLayer(Bitmap bitmap, int width, int height)
    {
        if (!MapOverlayBitmapRenderer.TryGetSharedMapLayer(bitmap, out var layer) || layer is null) return false;
        if (_borrowedDib == layer && HasRetainedLayer(width, height)) return true;
        CleanupCachedBuffer();
        _cachedScreenDc = GetDC(IntPtr.Zero);
        if (_cachedScreenDc == IntPtr.Zero) throw NativeFailure("Unable to acquire the map presentation screen DC.");
        try
        {
            _cachedMemoryDc = CreateCompatibleDC(_cachedScreenDc);
            if (_cachedMemoryDc == IntPtr.Zero) throw NativeFailure("Unable to create the map presentation memory DC.");
            var lease = layer.TryBorrow(_cachedMemoryDc);
            if (lease is null) { CleanupCachedBuffer(); return false; }
            _borrowedDib = layer; _borrowedDibLease = lease;
            _cachedDIBSection = layer.Handle; _cachedBitsPtr = layer.Pixels;
            _cachedOldBitmap = SelectObject(_cachedMemoryDc, layer.Handle);
            if (_cachedOldBitmap == IntPtr.Zero || _cachedOldBitmap == new IntPtr(-1))
                throw NativeFailure("Unable to select the map presentation pixel buffer.");
            _cachedDIBWidth = width; _cachedDIBHeight = height;
            MemoryJournal.Current?.Record("native-buffer-borrowed", new { resource = "Overlay.MapLayer",
                bytes = checked((long)width * height * 4), meaning = "same private pixel buffer retained by cache and native window; no copy or second allocation" });
            return true;
        }
        catch { CleanupCachedBuffer(); throw; }
    }
}
