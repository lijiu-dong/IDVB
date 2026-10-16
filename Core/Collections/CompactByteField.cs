using System.Runtime.CompilerServices;
using System.Buffers.Binary;
using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Core.Collections;

/// <summary>Lossless immutable byte raster. Independent 8x8 tiles support allocation-free
/// random reads; affine tiles store only their residual bits. Incompressible rasters
/// retain their original buffer instead. No decompression cache or external storage.</summary>
public sealed partial class CompactByteField
{
    private readonly byte[] _data;
    private readonly uint[]? _tiles;
    private readonly byte[]? _compactTiles;
    private readonly int _width, _height, _tileColumns;
    private readonly bool _constant;
    public long MemoryBytes => _data.LongLength + (_tiles?.LongLength ?? 0) * sizeof(uint) + (_compactTiles?.LongLength ?? 0);

    /// <summary>Retires idle construction metadata at a resource lifecycle boundary.
    /// Encoded fields remain immutable and readable; an older borrower cannot
    /// put its metadata back into the cache after this boundary.</summary>
    public static void ReleaseSharedScratch() => TileDedupWorkspace.ReleaseSharedScratch();

    public CompactByteField(byte[] source, int width, int height, string resource)
        : this(source.AsSpan(), width, height, resource, source, false) { }

    public CompactByteField(ReadOnlySpan<byte> source, int width, int height, string resource)
        : this(source, width, height, resource, null, false) { }

    /// <summary>Allows a bounded 24-bit directory with inline constant/affine
    /// tiles. Consumers selecting this format use AtCompact to keep the ordinary
    /// distance reader's hot path independent of the directory representation.</summary>
    public CompactByteField(ReadOnlySpan<byte> source, int width, int height, string resource, bool preferCompactDirectory)
        : this(source, width, height, resource, null, preferCompactDirectory) { }

    // Trusted in-process pixel kernels borrow immutable encoded arrays. No
    // public mutable storage or source raster escapes this snapshot.
    internal EncodedStorage GetEncodedStorage() => new(_data, _tiles, _compactTiles, _width, _height, _tileColumns, _constant);
    internal readonly record struct EncodedStorage(byte[] Data, uint[]? Tiles, byte[]? CompactTiles,
        int Width, int Height, int TileColumns, bool Constant);

    internal static CompactByteField CreateWindow(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight,
        int left, int top, int width, int height, string resource)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || source.Length != checked(sourceWidth * sourceHeight)
            || left < 0 || top < 0 || width <= 0 || height <= 0 || left > sourceWidth - width || top > sourceHeight - height)
            throw new ArgumentException("Invalid raster window.");
        var start = checked(top * sourceWidth + left);
        var length = checked((height - 1) * sourceWidth + width);
        return new(source.Slice(start, length), width, height, resource, null, true, sourceWidth);
    }

    private CompactByteField(ReadOnlySpan<byte> source, int width, int height, string resource, byte[]? original, bool preferCompactDirectory,
        int sourceStride = 0)
    {
        if (sourceStride == 0) sourceStride = width;
        if (width <= 0 || height <= 0 || sourceStride < width || source.Length != checked((height - 1) * sourceStride + width))
            throw new ArgumentException("Invalid raster dimensions.");
        var packedLength = checked(width * height);
        _width = width; _height = height; _tileColumns = (width + 7) / 8;
        var constant = true;
        if (sourceStride == width) constant = source.IndexOfAnyExcept(source[0]) < 0;
        else for (var y = 0; y < height && constant; y++)
            constant = source.Slice(y * sourceStride, width).IndexOfAnyExcept(source[0]) < 0;
        if (constant)
        {
            var constantRequest = MemoryJournal.Request(resource + ".Constant", 1);
            _data = [source[0]];
            _constant = true;
            MemoryJournal.ObserveManagedArray(_data, resource + ".Constant", constantRequest);
            return;
        }
        var count = checked(_tileColumns * ((height + 7) / 8));
        Span<byte> tile = stackalloc byte[64];
        Span<byte> previousTile = stackalloc byte[64];
        // Distance fields repeat the same immutable tiles around straight walls
        // and empty regions. Share their encoded bytes, retaining exact random
        // access. Verify all 64 bytes on a hash hit; collisions cannot change a
        // pixel. Bound temporary metadata even for incompressible images.
        const int maximumUniqueTiles = 32768;
        using var metadata = TileDedupWorkspace.Rent(resource);
        var uniqueTiles = metadata.Workspace.Dictionary;
        long length = 0, compactLength = 0;
        for (var i = 0; i < count; i++)
        {
            ReadTile(source, width, height, i, tile, sourceStride);
            if (FindTile(uniqueTiles, source, width, height, tile, previousTile, out var signature, sourceStride) >= 0) continue;
            if (uniqueTiles.Count < maximumUniqueTiles) metadata.Workspace.Add(signature, i);
            var plan = Plan(tile);
            length += plan.Length;
            if (preferCompactDirectory && !CanInline(plan)) compactLength += plan.Length;
        }
        if (length + 4L * count >= packedLength || length >= 1 << 28)
        {
            var rawRequest = original is null ? MemoryJournal.Request(resource + ".Raw", packedLength) : 0;
            if (original is not null) _data = original;
            else if (sourceStride == width) _data = source.ToArray();
            else
            {
                _data = new byte[packedLength];
                for (var y = 0; y < height; y++) source.Slice(y * sourceStride, width).CopyTo(_data.AsSpan(y * width, width));
            }
            if (original is null) MemoryJournal.ObserveManagedArray(_data, resource + ".Raw", rawRequest);
            return;
        }
        // Packing is useful for the small, largely affine semantic payloads.
        // Large/incompressible fields retain their direct uint directory. The
        // fourth padding byte makes every checked unaligned read fully bounded.
        var compact = preferCompactDirectory && BitConverter.IsLittleEndian
            && compactLength < (1 << 20) && length <= uniqueTiles.Count * 32L;
        if (compact) length = compactLength;
        var request = MemoryJournal.Request(resource + ".Tiles", compact ? 3L * count + 1 : 4L * count);
        if (compact)
        {
            _compactTiles = new byte[checked(count * 3 + 1)];
            MemoryJournal.ObserveManagedArray(_compactTiles, resource + ".Tiles", request);
        }
        else
        {
            _tiles = new uint[count];
            MemoryJournal.ObserveManagedArray(_tiles, resource + ".Tiles", request);
        }
        request = MemoryJournal.Request(resource + ".Payload", length);
        _data = new byte[(int)length];
        MemoryJournal.ObserveManagedArray(_data, resource + ".Payload", request);
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            ReadTile(source, width, height, i, tile, sourceStride);
            var shared = FindTile(uniqueTiles, source, width, height, tile, previousTile, out _, sourceStride);
            if (shared >= 0 && shared < i)
            {
                if (_tiles is not null) _tiles[i] = _tiles[shared];
                else WriteCompactDescriptor(i, ReadCompactDescriptor(shared));
                continue;
            }
            var plan = Plan(tile);
            if (compact && plan.Mode == 9)
            {
                WriteCompactDescriptor(i, tile[0] | (9u << 20));
                continue;
            }
            if (compact && CanInline(plan))
            {
                WriteCompactDescriptor(i, tile[0] | ((uint)(plan.Dx & 63) << 8)
                    | ((uint)(plan.Dy & 63) << 14) | (10u << 20));
                continue;
            }
            if (_tiles is not null) _tiles[i] = (uint)offset | ((uint)plan.Mode << 28);
            else WriteCompactDescriptor(i, (uint)offset | ((uint)plan.Mode << 20));
            if (plan.Mode == 9) _data[offset] = tile[0];
            else if (plan.Mode == 8) tile.CopyTo(_data.AsSpan(offset, 64));
            else
            {
                _data[offset] = tile[0];
                _data[offset + 1] = unchecked((byte)plan.Dx);
                _data[offset + 2] = unchecked((byte)plan.Dy);
                _data[offset + 3] = unchecked((byte)plan.Minimum);
                _data[offset + 4] = unchecked((byte)(plan.Minimum >> 8));
                for (var p = 0; p < 64 && plan.Mode > 0; p++)
                {
                    var residual = tile[p] - tile[0] - plan.Dx * (p & 7) - plan.Dy * (p >> 3) - plan.Minimum;
                    var bit = p * plan.Mode;
                    var packed = residual << (bit & 7);
                    _data[offset + 5 + (bit >> 3)] |= (byte)packed;
                    if ((bit & 7) + plan.Mode > 8) _data[offset + 6 + (bit >> 3)] |= (byte)(packed >> 8);
                }
            }
            offset += plan.Length;
        }
        MemoryJournal.Current?.Record("managed-raster-layout", new { resource, width, height,
            tileSize = 8, descriptorBits = compact ? 24 : 32, inlineTiles = compact,
            payloadBytes = _data.LongLength, directoryBytes = MemoryBytes - _data.LongLength,
            meaning = "lossless immutable process-local storage; natural GC release is observed separately" });
    }

    private CompactByteField(int width, int height, byte value, string resource)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("Invalid raster dimensions.");
        _width = width;
        _height = height;
        _tileColumns = (width + 7) / 8;
        _constant = true;
        var request = MemoryJournal.Request(resource + ".Constant", 1);
        _data = [value];
        MemoryJournal.ObserveManagedArray(_data, resource + ".Constant", request);
    }

    public static CompactByteField Filled(int width, int height, byte value, string resource) => new(width, height, value, resource);

    /// <summary>Exact storage comparison for immutable consumers. A caller must
    /// also match its raster dimensions before sharing a field instance.</summary>
    public bool HasSameEncodedStorageAs(CompactByteField other) =>
        _width == other._width && _height == other._height && _tileColumns == other._tileColumns && _constant == other._constant
        && _data.AsSpan().SequenceEqual(other._data)
        && (_tiles is null ? other._tiles is null : other._tiles is not null && _tiles.AsSpan().SequenceEqual(other._tiles))
        && (_compactTiles is null ? other._compactTiles is null : other._compactTiles is not null && _compactTiles.AsSpan().SequenceEqual(other._compactTiles));

    /// <summary>Compare a new raster before requesting duplicate encoded storage.
    /// Reads the immutable field directly without a decompression buffer.</summary>
    public bool HasSamePixelsAs(ReadOnlySpan<byte> pixels, int width, int height)
    {
        if (width != _width || height != _height || pixels.Length != checked(width * height)) return false;
        if (_constant) return pixels.IndexOfAnyExcept(_data[0]) < 0;
        if (_tiles is null && _compactTiles is null) return pixels.SequenceEqual(_data);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            if (pixels[y * width + x] != AtCompact(x, y)) return false;
        return true;
    }

    public bool HasSameConstantPixelsAs(byte value, int width, int height) =>
        _width == width && _height == height && _constant && _data[0] == value;

    private void ReadTile(ReadOnlySpan<byte> source, int width, int height, int index, Span<byte> tile, int sourceStride)
    {
        var left = index % _tileColumns * 8; var top = index / _tileColumns * 8;
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
            tile[y * 8 + x] = source[Math.Min(top + y, height - 1) * sourceStride + Math.Min(left + x, width - 1)];
    }

    private readonly record struct TilePlan(int Mode, int Dx, int Dy, int Minimum)
    { public int Length => Mode == 9 ? 1 : Mode == 8 ? 64 : 5 + 8 * Mode; }

    private static bool CanInline(TilePlan plan) => plan.Mode == 9 ||
        (plan.Mode == 0 && plan.Minimum == 0 && plan.Dx is >= -32 and <= 31 && plan.Dy is >= -32 and <= 31);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadCompactDescriptor(int index)
    {
        var tiles = _compactTiles!;
        var offset = checked(index * 3);
        if ((uint)offset >= (uint)(tiles.Length - 3)) throw new IndexOutOfRangeException();
        return Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(tiles), offset)) & 0xffffff;
    }

    private void WriteCompactDescriptor(int index, uint descriptor)
    {
        var tiles = _compactTiles!;
        var offset = checked(index * 3);
        if ((uint)offset >= (uint)(tiles.Length - 3)) throw new IndexOutOfRangeException();
        // Construction visits descriptors in ascending order. The extra byte
        // belongs to the next unwritten descriptor, or to the final padding.
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(tiles), offset), descriptor & 0xffffff);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte AtCompact(int x, int y)
    {
        if (_constant) return _data[0];
        if (_compactTiles is null) return At(x, y);
        var descriptor = ReadCompactDescriptor((y >> 3) * _tileColumns + (x >> 3));
        var offset = (int)(descriptor & 0xfffff);
        var mode = (int)(descriptor >> 20);
        if (mode == 9) return (byte)offset;
        if (mode == 10) return (byte)((byte)offset + ((offset << 18) >> 26) * (x & 7) + ((offset << 12) >> 26) * (y & 7));
        var pixel = (y & 7) * 8 + (x & 7);
        if (mode == 8) return _data[offset + pixel];
        var minimum = (short)(_data[offset + 3] | _data[offset + 4] << 8);
        var residual = 0;
        if (mode > 0)
        {
            var bit = pixel * mode; var at = offset + 5 + (bit >> 3);
            var packed = (int)_data[at];
            if ((bit & 7) + mode > 8) packed |= _data[at + 1] << 8;
            residual = (packed >> (bit & 7)) & ((1 << mode) - 1);
        }
        return (byte)(_data[offset] + (sbyte)_data[offset + 1] * (x & 7)
            + (sbyte)_data[offset + 2] * (y & 7) + minimum + residual);
    }

    // Only numeric hash/index metadata is retained; no raster, source span or
    // map object can escape construction. One cached workspace bounds retained
    // capacity. Concurrent constructors rent independent dictionaries.
    private sealed class TileDedupWorkspace
    {
        private const string Resource = "CompactByteField.TileDedupMetadata";
        private static readonly object CacheGate = new();
        private static TileDedupWorkspace? _cached;
        private static long _generation;
        internal Dictionary<ulong, int> Dictionary { get; } = new();
        private int _capacity;
        private IDisposable? _ownership;

        private TileDedupWorkspace() => MemoryJournal.ObserveManagedObject(Dictionary, Resource);

        internal static Lease Rent(string consumer)
        {
            // Allocate the lease before taking a cached workspace so allocation
            // failure cannot orphan an existing workspace ownership record.
            var lease = new Lease(consumer);
            TileDedupWorkspace? workspace;
            long generation;
            lock (CacheGate)
            {
                workspace = _cached;
                _cached = null;
                generation = _generation;
            }
            lease.Attach(workspace ?? new TileDedupWorkspace(), generation);
            return lease;
        }

        internal static void ReleaseSharedScratch()
        {
            TileDedupWorkspace? retired;
            lock (CacheGate)
            {
                ++_generation;
                retired = _cached;
                _cached = null;
            }
            retired?.RetireOwnership();
            MemoryJournal.Current?.Record("workspace-retired", new { resource = Resource,
                capacity = retired?._capacity ?? 0,
                meaning = "idle managed construction metadata detached; GC/allocator release is observed separately" });
        }

        private void RetireOwnership()
        {
            _ownership?.Dispose();
            _ownership = null;
        }

        internal void Add(ulong signature, int index)
        {
            if (Dictionary.Count == _capacity)
            {
                var requested = _capacity == 0 ? 3 : checked(_capacity * 2);
                var request = MemoryJournal.Request(Resource, checked(requested * 28L));
                var capacity = Dictionary.EnsureCapacity(requested);
                var ownership = MemoryJournal.Own(Resource, checked(capacity * 28L), requestId: request);
                _ownership?.Dispose(); // Ownership retirement; managed GC free is recorded separately.
                _ownership = ownership;
                _capacity = capacity;
            }
            Dictionary.Add(signature, index);
        }

        private void Return(string consumer, long generation)
        {
            Dictionary.Clear();
            var capacity = _capacity;
            bool cached;
            lock (CacheGate)
            {
                cached = generation == _generation && _cached is null;
                if (cached) _cached = this;
            }
            if (!cached) RetireOwnership();
            MemoryJournal.Current?.Record("workspace-returned", new { resource = Resource, consumer,
                capacity, cached, meaning = "numeric scratch metadata; retired ownership is not a GC free" });
        }

        internal sealed class Lease(string consumer) : IDisposable
        {
            private TileDedupWorkspace? _workspace;
            private long _generation;
            internal TileDedupWorkspace Workspace => _workspace!;
            internal void Attach(TileDedupWorkspace workspace, long generation)
            {
                _workspace = workspace;
                _generation = generation;
                MemoryJournal.Current?.Record("workspace-borrowed", new { resource = Resource,
                    consumer, capacity = workspace._capacity });
            }
            public void Dispose() => Interlocked.Exchange(ref _workspace, null)?.Return(consumer, _generation);
        }
    }

    private int FindTile(Dictionary<ulong, int> unique, ReadOnlySpan<byte> source,
        int width, int height, ReadOnlySpan<byte> tile, Span<byte> previous, out ulong signature, int sourceStride)
    {
        signature = 14695981039346656037UL;
        for (var word = 0; word < 8; word++)
            signature = unchecked((signature ^ BinaryPrimitives.ReadUInt64LittleEndian(tile.Slice(word * 8, 8))) * 1099511628211UL);
        while (unique.TryGetValue(signature, out var candidate))
        {
            ReadTile(source, width, height, candidate, previous, sourceStride);
            if (tile.SequenceEqual(previous)) return candidate;
            // Deterministic probing handles genuine hash collisions. No probabilistic
            // equality and no unbounded list of copied raster bytes.
            signature = unchecked(signature + 0x9e3779b97f4a7c15UL);
        }
        return -1;
    }


    private static TilePlan Plan(ReadOnlySpan<byte> tile)
    {
        var best = new TilePlan(8, 0, 0, 0);
        var constant = true;
        for (var p = 1; p < 64; p++) constant &= tile[p] == tile[0];
        if (constant) return new(9, 0, 0, 0);
        for (var predictor = 0; predictor < 2; predictor++)
        {
            var dx = predictor == 0 ? 0 : (int)Math.Round((tile[7] - tile[0]) / 7d);
            var dy = predictor == 0 ? 0 : (int)Math.Round((tile[56] - tile[0]) / 7d);
            var min = int.MaxValue; var max = int.MinValue;
            for (var p = 0; p < 64; p++)
            {
                var r = tile[p] - tile[0] - dx * (p & 7) - dy * (p >> 3);
                min = Math.Min(min, r); max = Math.Max(max, r);
            }
            var bits = 0;
            while ((max - min) >> bits != 0) bits++;
            var candidate = new TilePlan(bits, dx, dy, min);
            if (bits < 8 && candidate.Length < best.Length) best = candidate;
        }
        return best;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte At(int x, int y)
    {
        if (_constant) return _data[0];
        if (_tiles is null) return _compactTiles is not null ? AtCompact(x, y) : _data[y * _width + x];
        var descriptor = _tiles[(y >> 3) * _tileColumns + (x >> 3)];
        var offset = (int)(descriptor & 0x0fffffff); var mode = (int)(descriptor >> 28);
        var p = (y & 7) * 8 + (x & 7);
        if (mode == 9) return _data[offset];
        if (mode == 8) return _data[offset + p];
        var minimum = (short)(_data[offset + 3] | _data[offset + 4] << 8);
        var residual = 0;
        if (mode > 0)
        {
            var bit = p * mode; var at = offset + 5 + (bit >> 3);
            var packed = (int)_data[at];
            if ((bit & 7) + mode > 8) packed |= _data[at + 1] << 8;
            residual = (packed >> (bit & 7)) & ((1 << mode) - 1);
        }
        return (byte)(_data[offset] + (sbyte)_data[offset + 1] * (x & 7)
            + (sbyte)_data[offset + 2] * (y & 7) + minimum + residual);
    }
}
