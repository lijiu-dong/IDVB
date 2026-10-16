using IDVBuff.Core.Collections;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal sealed partial class ScanStructureIndex
{
    private readonly NativeEncodedByteField.ReadLease? _distanceReader;
    private readonly long _distanceBytes;
    private readonly bool _ownsDistanceReader;
    private readonly bool _ownsSemanticReader;
    private int _readerReleased;

    // Rent a canonical owner under the publication gate before it can retire.
    // Distance and semantic fields each keep independent reader ownership.
    internal ScanStructureIndex BorrowReader()
    {
        if (_distanceReader is null && SemanticIndex?.UsesNativeStorage != true) return this;
        var reader = _distanceReader?.Clone();
        try { return new ScanStructureIndex(this, reader); }
        catch { reader?.Dispose(); throw; }
    }

    private ScanStructureIndex(ScanStructureIndex source, NativeEncodedByteField.ReadLease? reader)
    {
        Width = source.Width; Height = source.Height;
        _distances = source._distances;
        _distanceReader = reader; _ownsDistanceReader = reader is not null;
        _distanceBytes = source._distanceBytes;
        SemanticIndex = source.SemanticIndex?.BorrowReader();
        _ownsSemanticReader = SemanticIndex?.UsesNativeStorage == true;
        _unknownBounds = source._unknownBounds;
        try { LocalGeometry = source.LocalGeometry?.BorrowWithDistances(this); }
        catch { if (_ownsSemanticReader) SemanticIndex?.Dispose(); throw; }
    }

    public void Dispose()
    {
        if ((!_ownsDistanceReader && !_ownsSemanticReader) || Interlocked.Exchange(ref _readerReleased, 1) != 0)return;
        try { if (_ownsDistanceReader) _distanceReader?.Dispose(); }
        finally { if (_ownsSemanticReader) SemanticIndex?.Dispose(); }
    }


}

internal sealed partial class ScanExecutionContext
{
    private readonly object _indexReaderGate = new();
    private Dictionary<ScanStructureIndex, ScanStructureIndex>? _indexReaders;

    // The same immutable reader serves returned hypotheses, formal validation,
    // optional manual selection, and the final source-frame commit check.
    internal ScanStructureIndex RetainIndexReader(ScanStructureIndex source)
    {
        lock (_indexReaderGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _indexReaders ??= new();
            if (_indexReaders.TryGetValue(source, out var existing)) return existing;
            var reader = source.BorrowReader();
            try { _indexReaders.Add(source, reader); return reader; }
            catch { if (!ReferenceEquals(source, reader)) reader.Dispose(); throw; }
        }
    }

    private void ReleaseIndexReaders()
    {
        lock (_indexReaderGate)
        {
            if (_indexReaders is not { } readers) return;
            foreach (var (owner, reader) in readers)
                if (!ReferenceEquals(owner, reader)) reader.Dispose();
            _indexReaders = null;
        }
    }
}

/// <summary>Independent result scopes sharing the same already-rented indices.
/// The final explicit scope releases their readers. There is deliberately no
/// group finalizer: a candidate may still hold an individual index after an
/// abandoned wrapper becomes unreachable; each native reader already has its
/// own safe fallback finalizer. Normal application paths must dispose scopes.</summary>
internal sealed class ScanIndexReaderLifetime : IDisposable
{
    private Shared? _shared;
    // Takes ownership of the supplied independent readers, never cache roots.
    internal ScanIndexReaderLifetime(ScanStructureIndex[] readers) => _shared = new(readers);
    private ScanIndexReaderLifetime() { }
    internal ScanIndexReaderLifetime Retain()
    {
        var shared = Volatile.Read(ref _shared) ?? throw new ObjectDisposedException(nameof(ScanIndexReaderLifetime));
        var reader = new ScanIndexReaderLifetime(); // Allocate before incrementing.
        if (!shared.TryRetain()) throw new ObjectDisposedException(nameof(ScanIndexReaderLifetime));
        reader._shared = shared;
        return reader;
    }
    public void Dispose() => Interlocked.Exchange(ref _shared, null)?.Release();
    private sealed class Shared(ScanStructureIndex[] readers)
    {
        private int _references = 1;
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
            if (Interlocked.Decrement(ref _references) != 0) return;
            foreach (var reader in readers) reader.Dispose();
        }
    }
}

/// <summary>Publishes immutable dictionaries while renting readers under the
/// same gate that retires canonical owners. Dictionaries are transferred here;
/// callers must never mutate them after Publish.</summary>
internal sealed class ScanIndexCache : IDisposable
{
    private readonly object _gate = new();
    private Dictionary<(Guid, string), ScanStructureIndex> _entries = [];
    private bool _disposed;
    internal int Count { get { lock (_gate) return _entries.Count; } }
    // Metadata-only snapshots may outlive retirement: dimensions, retained byte
    // counts are immutable. Neither distance nor semantic pixels may be read
    // through these roots after retirement; use Rent instead.
    internal IReadOnlyDictionary<(Guid, string), ScanStructureIndex> MetadataSnapshot
    { get { lock (_gate) return _entries; } }

    internal ScanIndexReadSnapshot Rent(Func<(Guid, string), bool> include)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var selected = new Dictionary<(Guid, string), ScanStructureIndex>();
            var owned = new List<ScanStructureIndex>();
            try
            {
                foreach (var (key, source) in _entries)
                {
                    if (!include(key)) continue;
                    var reader = source.BorrowReader();
                    if (!ReferenceEquals(source, reader))
                    {
                        try { owned.Add(reader); }
                        catch { reader.Dispose(); throw; }
                    }
                    selected.Add(key, reader);
                }
                return new(selected, owned.Count == 0 ? null : new ScanIndexReaderLifetime(owned.ToArray()));
            }
            catch
            {
                foreach (var reader in owned) reader.Dispose();
                throw;
            }
        }
    }

    // Takes ownership even on failure. Partial refresh transfers every unchanged
    // root from the previous dictionary before replacing the changed map owners.
    internal void Publish(Dictionary<(Guid, string), ScanStructureIndex> next, IReadOnlySet<Guid>? changedMaps = null)
    {
        lock (_gate)
        {
            var previous = _entries;
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (ReferenceEquals(next, previous)) throw new ArgumentException("Published dictionaries cannot be mutated or republished.");
                if (changedMaps is not null)
                    foreach (var (key, value) in previous)
                        if (!changedMaps.Contains(key.Item1)) next.Add(key, value);
            }
            catch
            {
                RetireExcept(next.Values, previous.Values);
                throw;
            }
            _entries = next;
            RetireExcept(previous.Values, next.Values);
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            var previous = _entries; _entries = [];
            RetireExcept(previous.Values, []);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            var previous = _entries; _entries = [];
            RetireExcept(previous.Values, []);
        }
    }

    internal static void RetireUnpublished(IEnumerable<ScanStructureIndex> entries) => RetireExcept(entries, []);

    private static void RetireExcept(IEnumerable<ScanStructureIndex> retired, IEnumerable<ScanStructureIndex> survivors)
    {
        // Dictionaries stay immutable here. Use reference identity even if a
        // future index type adds value equality; never revoke a transferred root.
        Exception? failure = null;
        foreach (var index in retired)
        {
            if (survivors.Any(survivor => ReferenceEquals(index, survivor))) continue;
            try { IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(index); }
            catch (Exception error) { failure ??= error; }
            finally { index.Dispose(); }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

internal sealed class ScanIndexReadSnapshot : IDisposable
{
    private readonly IReadOnlyDictionary<(Guid, string), ScanStructureIndex> _entries;
    private ScanIndexReaderLifetime? _lifetime;
    private int _closed;
    internal ScanIndexReadSnapshot(IReadOnlyDictionary<(Guid, string), ScanStructureIndex> entries, ScanIndexReaderLifetime? lifetime)
    { _entries = entries; _lifetime = lifetime; }
    internal IReadOnlyDictionary<(Guid, string), ScanStructureIndex> Entries
    { get { ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this); return _entries; } }
    // Transfer once, after candidate computation succeeds. The receiving result
    // must dispose this lifetime or retain an independent scope before handoff.
    internal ScanIndexReaderLifetime? TakeLifetime()
    {
        ObjectDisposedException.ThrowIf(Interlocked.Exchange(ref _closed, 1) != 0, this);
        return Interlocked.Exchange(ref _lifetime, null);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
            Interlocked.Exchange(ref _lifetime, null)?.Dispose();
    }
}

internal sealed class ScanIndexInputBatch(ScanIndexReadSnapshot snapshot, int capacity)
    : List<(MapRecord map, string floorKey, ScanStructureIndex template)>(capacity), IDisposable
{
    internal ScanIndexReaderLifetime? TakeLifetime() => snapshot.TakeLifetime();
    public void Dispose() => snapshot.Dispose();
}

// A field owner and a consumer acquire/retire under the same gate. No reader
// can observe the field just before its last owning scope is disposed.
internal sealed class ScanResultSlot : IDisposable
{
    private readonly object _gate = new();
    private SideEntranceScanResult? _result;
    internal SideEntranceScanResult? Rent()
    {
        lock (_gate) return _result?.Retain();
    }
    // Takes ownership of this independent result scope.
    internal void Replace(SideEntranceScanResult? result)
    {
        SideEntranceScanResult? previous;
        lock (_gate) { previous = _result; _result = result; }
        if (!ReferenceEquals(previous, result)) previous?.Dispose();
    }
    public void Dispose() => Replace(null);
}
