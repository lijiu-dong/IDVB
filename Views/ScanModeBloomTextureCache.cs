using IDVBuff.Core.Diagnostics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace IDVBuff.Views;

// All access is on the CanvasControl/main window UI thread.
// Owned by the main window, not by a page or a process-wide image cache.
// Keep one neutral raster for the current device/size. Re-entering Home must
// not regenerate the same blur graphs and their driver-side scratch surfaces.
internal sealed class ScanModeBloomTextureCache : IDisposable
{
    private Entry? _current;
    internal bool IsDisposed { get; private set; }

    internal Lease Acquire(CanvasControl canvas, float width, float height, Func<CanvasRenderTarget> create)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var device = canvas.Device;
        var entry = _current;
        if (entry is null || !ReferenceEquals(entry.Device, device)
            || entry.Width != width || entry.Height != height || entry.Dpi != canvas.Dpi)
        {
            _current = null;
            entry?.Retire();
            entry = new Entry(create(), device, width, height, canvas.Dpi);
            _current = entry;
        }
        return entry.Borrow();
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Invalidate();
    }

    internal void Invalidate()
    {
        var entry = _current;
        _current = null;
        entry?.Retire();
    }

#if IDVB_DEVELOPMENT_BUILD
    internal readonly record struct DevelopmentState(bool textureAlive, int borrowers, bool disposed);
    internal DevelopmentState ReadDevelopmentState() => new(_current is not null, _current?.Borrowers ?? 0, IsDisposed);
#endif

    internal sealed class Lease : IDisposable
    {
        private Entry? _entry;
        private readonly IDisposable? _ownership;
        internal CanvasRenderTarget Texture => _entry?.Texture
            ?? throw new ObjectDisposedException(nameof(Lease));
        internal Lease(Entry entry)
        {
            _entry = entry;
            _ownership = MemoryJournal.Own("UI.Home.BloomTextureLease", nativeModule: "Microsoft.Graphics.Canvas.dll");
        }
        public void Dispose()
        {
            var entry = _entry;
            _entry = null;
            if (entry is null) return;
            entry.Return();
            _ownership?.Dispose();
        }
    }

    internal sealed class Entry(CanvasRenderTarget texture, CanvasDevice device, float width, float height, float dpi)
    {
        internal CanvasRenderTarget Texture { get; } = texture;
        internal CanvasDevice Device { get; } = device;
        internal float Width { get; } = width;
        internal float Height { get; } = height;
        internal float Dpi { get; } = dpi;
        private int _borrowers;
        internal int Borrowers => _borrowers;
        private bool _retired, _disposed;
        internal Lease Borrow()
        {
            if (_retired) throw new ObjectDisposedException(nameof(Entry));
            var lease = new Lease(this);
            _borrowers++;
            return lease;
        }
        internal void Return()
        {
            if (_borrowers <= 0) throw new InvalidOperationException("光效纹理租约重复归还。");
            _borrowers--;
            ReleaseIfRetired();
        }
        internal void Retire() { _retired = true; ReleaseIfRetired(); }
        private void ReleaseIfRetired()
        {
            if (!_retired || _borrowers != 0 || _disposed) return;
            Texture.Dispose();
            _disposed = true;
            MemoryJournal.ReleaseResource(Texture);
        }
    }
}
