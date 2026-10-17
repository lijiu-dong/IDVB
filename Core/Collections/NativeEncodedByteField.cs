using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IDVBuff.Core.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace IDVBuff.Core.Collections;

/// <summary>Exact process-local encoded bytes with explicit owner/reader lifetimes.
/// Retiring the owner prevents new root readers. Existing readers may clone;
/// the last owner or reader decommits the allocation. No managed raster cache.</summary>
public sealed partial class NativeEncodedByteField : IDisposable
{
    private Storage? _owner;
    public NativeEncodedByteField(CompactByteField source, string resource)
        : this(source, resource, null) { }

    internal NativeEncodedByteField(CompactByteField source, string resource, Action? afterAllocation)
    {
        ArgumentNullException.ThrowIfNull(source);
        _owner = new Storage(source.GetEncodedStorage(), resource, afterAllocation);
    }

    public ReadLease? TryRent()
    {
        var owner = Volatile.Read(ref _owner);
        if (owner is null) return null;
        var lease = new ReadLease(); // Allocate before acquiring a reference.
        if (!owner.TryRetain()) return null;
        lease.Attach(owner);
        if (ReferenceEquals(Volatile.Read(ref _owner), owner)) return lease;
        lease.Dispose();
        return null;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _owner, null)?.Release();
        GC.SuppressFinalize(this);
    }
    ~NativeEncodedByteField() => Dispose();

    /// <summary>One independently disposable reader. Finish using every borrowed
    /// view before disposing this reader; other readers and the cache owner may
    /// retire concurrently. Cloning does not resurrect a released allocation.</summary>
    public sealed class ReadLease : IDisposable
    {
        private Storage? _storage;
        internal ReadLease() { }
        internal void Attach(Storage storage) => _storage = storage;
        public long PayloadBytes => GetStorage().PayloadBytes;
        public long CommittedBytes => GetStorage().CommittedBytes;
        public long ReservedBytes => GetStorage().ReservedBytes;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadView Borrow() => new(this, GetStorage().Pixels);
        /// <summary>Read one byte while keeping this independently owned reader alive.</summary>
        public byte ReadByte(int x, int y)
        {
            var value = GetStorage().Pixels.At(x, y);
            GC.KeepAlive(this);
            return value;
        }
        /// <summary>Four exact bytes at (x,y), (x+1,y), (x,y+1), (x+1,y+1),
        /// packed from least to most significant byte. This reader must not be
        /// disposed concurrently; independent readers and owners may retire.</summary>
        public uint ReadQuad(int x, int y)
        {
            var value = GetStorage().Pixels.Quad(x, y);
            GC.KeepAlive(this); // Keep this lease's finalizer behind the pointer read.
            return value;
        }
        // Trusted raster kernels borrow validated pointers once, then keep this
        // lease alive and undisposed for their entire read scope.
        internal PixelKernel BorrowPixelKernel() => GetStorage().Pixels;
        public ReadLease Clone()
        {
            var storage = GetStorage();
            var lease = new ReadLease();
            if (!storage.TryRetain()) throw new ObjectDisposedException(nameof(ReadLease));
            lease.Attach(storage);
            return lease;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Storage GetStorage() => Volatile.Read(ref _storage)
            ?? throw new ObjectDisposedException(nameof(ReadLease));
        public void Dispose()
        {
            Interlocked.Exchange(ref _storage, null)?.Release();
            GC.SuppressFinalize(this);
        }
        ~ReadLease() => Dispose();
    }

    internal sealed unsafe class Storage
    {
        private readonly Allocation _allocation;
        internal readonly PixelKernel Pixels;
        private readonly int _width, _height, _columns, _dataLength, _tilesLength, _compactLength, _tilesOffset, _compactOffset;
        private readonly bool _constant, _hasTiles, _hasCompact;
        private int _references = 1;
        internal long PayloadBytes => _allocation.PayloadBytes;
        internal long CommittedBytes => _allocation.CommittedBytes;
        internal long ReservedBytes => _allocation.ReservedBytes;
        internal Storage(CompactByteField.EncodedStorage encoded, string resource, Action? afterAllocation)
            : this(ValidatedShape(encoded), resource, afterAllocation)
        {
            try
            {
                var target = BorrowEncodingTarget();
                encoded.Data.AsSpan().CopyTo(target.Data);
                if (encoded.Tiles is { } tiles) tiles.AsSpan().CopyTo(target.Tiles);
                if (encoded.CompactTiles is { } compact) compact.AsSpan().CopyTo(target.Compact);
            }
            catch { _allocation.Dispose(); throw; }
        }
        internal Storage(EncodedShape shape, string resource, Action? afterAllocation)
        {
            _width = shape.Width; _height = shape.Height; _columns = shape.Columns; _constant = shape.Constant;
            _dataLength = shape.DataLength; _tilesLength = shape.TilesLength; _compactLength = shape.CompactLength;
            if (_width <= 0 || _height <= 0 || _columns != checked((_width + 7) / 8)
                || _dataLength < 0 || _tilesLength < 0 || _compactLength < 0)
                throw new ArgumentException("Invalid native encoding layout.");
            var count = checked(_columns * ((_height + 7) / 8));
            _hasTiles = _tilesLength > 0; _hasCompact = _compactLength > 0;
            if ((_hasTiles && _hasCompact) || (_hasTiles && _tilesLength != count)
                || (_hasCompact && _compactLength != checked(count * 3 + 1))
                || (_constant && (_dataLength != 1 || _hasTiles || _hasCompact))
                || (!_constant && !_hasTiles && !_hasCompact && _dataLength != checked(_width * _height)))
                throw new ArgumentException("Invalid native encoding directory.");
            _tilesOffset = checked((_dataLength + 3) & ~3); _compactOffset = checked(_tilesOffset + _tilesLength * sizeof(uint));
            _allocation = new Allocation(checked(_compactOffset + _compactLength),
                _dataLength + (long)_tilesLength * sizeof(uint) + _compactLength, resource);
            try
            {
                afterAllocation?.Invoke();
                var pointer = (byte*)_allocation.DangerousGetHandle();
                Pixels = new(pointer, (uint*)(pointer + _tilesOffset), pointer + _compactOffset,
                    _width, _height, _columns, _constant, _hasTiles, _hasCompact);
            }
            catch { _allocation.Dispose(); throw; }
        }
        internal WritableEncoding BorrowEncodingTarget()
        {
            ObjectDisposedException.ThrowIf(_allocation.IsClosed, this);
            var pointer = (byte*)_allocation.DangerousGetHandle();
            return new(new Span<byte>(pointer, _dataLength), new Span<uint>(pointer + _tilesOffset, _tilesLength),
                new Span<byte>(pointer + _compactOffset, _compactLength));
        }
        internal void ValidateWrittenEncoding()
        {
            var target = BorrowEncodingTarget();
            ValidateEncoding(_width, _height, _columns, _constant, target.Data, target.Tiles, target.Compact, _hasTiles, _hasCompact);
        }
        internal bool TryRetain()
        {
            while (true)
            {
                var count = Volatile.Read(ref _references);
                if (count <= 0) return false;
                if (Interlocked.CompareExchange(ref _references, checked(count + 1), count) == count) return true;
            }
        }
        internal void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0) _allocation.Dispose();
        }

    }

    internal static long OutstandingCommittedBytes => Volatile.Read(ref Allocation.OutstandingCommitted);
    private sealed class Allocation : SafeHandleZeroOrMinusOneIsInvalid
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFree(nint address, nuint size, uint freeType);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern nuint VirtualQuery(nint address, out MemoryRegion region, nuint length);
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryRegion
        {
            internal nint BaseAddress, AllocationBase;
            internal uint AllocationProtect;
            internal ushort PartitionId;
            internal nuint RegionSize;
            internal uint State, Protect, Type;
        }
        internal static long OutstandingCommitted;
        internal long PayloadBytes { get; }
        internal long CommittedBytes { get; }
        internal long ReservedBytes { get; }
        private readonly string _resource;
        private readonly long _request;
        private readonly MemoryJournal? _journal;
        private IDisposable? _ownership;
        internal Allocation(int length, long payloadBytes, string resource) : base(true)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native encoded storage requires Windows.");
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
            _resource = resource; _journal = MemoryJournal.Current;
            PayloadBytes = payloadBytes;
            _request = MemoryJournal.Request(resource, payloadBytes, "kernel32.dll/VirtualAlloc");
            var pointer = VirtualAlloc(0, (nuint)length, 0x3000, 0x04);
            if (pointer == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if (VirtualQuery(pointer, out var region, (nuint)Marshal.SizeOf<MemoryRegion>()) == 0
                || region.BaseAddress != pointer || region.State != 0x1000 || region.RegionSize < (nuint)length)
            {
                var queryError = Marshal.GetLastWin32Error();
                VirtualFree(pointer, 0, 0x8000);
                throw new InvalidOperationException($"Cannot account native encoded allocation: {queryError}.");
            }
            // The entire owned region is committed. Report the OS extent, not
            // address-alignment granularity as if it were reserved memory.
            CommittedBytes = ReservedBytes = checked((long)region.RegionSize);
            SetHandle(pointer);
            Interlocked.Add(ref OutstandingCommitted, CommittedBytes);
            try
            {
                _ownership = MemoryJournal.Own(resource, payloadBytes, "kernel32.dll/VirtualAlloc", _request);
                _journal?.Record("native-encoded-allocate", new { request = _request, resource, address = pointer.ToInt64(), payloadBytes, committedBytes = CommittedBytes, reservedBytes = ReservedBytes });
            }
            catch { Dispose(); throw; }
        }
        protected override bool ReleaseHandle()
        {
            var released = VirtualFree(handle, 0, 0x8000);
            var error = released ? 0 : Marshal.GetLastWin32Error();
            if (released) Interlocked.Add(ref OutstandingCommitted, -CommittedBytes);
            try
            {
                _journal?.Record("native-encoded-release", new { request = _request, resource = _resource, address = handle.ToInt64(), released, error, payloadBytes = PayloadBytes, committedBytes = CommittedBytes, reservedBytes = ReservedBytes });
                if (released) Interlocked.Exchange(ref _ownership, null)?.Dispose();
            }
            catch { /* SafeHandle finalization must not throw. The allocation counter still reflects OS release. */ }
            return released;
        }
    }
}
