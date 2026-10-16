using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace IDVBuff.Core.Diagnostics;

/// <summary>
/// Streaming, process-local ownership/operation ledger shared by DLLs. Ownership
/// release is NOT a heap-free event; scopes are NOT allocation attribution.
/// Native heap/VirtualAlloc stacks must be collected with ETW separately.
/// </summary>
public sealed class MemoryJournal : IDisposable
{
    private const long SegmentBytes = 16L * 1024 * 1024;
    private static MemoryJournal? _current;
    private readonly AsyncLocal<long> _operation = new();
    private readonly ConditionalWeakTable<object, IDisposable> _resources = new();
    private readonly ConcurrentDictionary<long, ObservedArray> _arrays = new();
    private readonly ConcurrentDictionary<long, ObservedObject> _objects = new();
    private int _objectTrackingGap;
    private int _arrayTrackingGap;
    private readonly string _directory;
    private FileStream _stream;
    // Bounded metadata only, never pixel buffers or traced objects. File I/O and
    // JSON serialization belong to this process's writer, not the WinUI thread.
    private readonly PendingQueue _pending = new();
    private readonly AutoResetEvent _wakeWriter = new(false);
    private int _producers;
    private readonly Thread _writer;
    private int _segment;
    private long _identity;
    private int _disposed;
    private Exception? _writerFailure;
    public static MemoryJournal? Current => Volatile.Read(ref _current);
    public event Action<Exception>? WriteFailed;
    // A consumer relinquished ownership, not an observed heap-free event.
    // Subscribers receive identifiers only, never the resource or its pixels.
    public readonly record struct NativeRetirement(long Id, string Module, string Resource, string NativeModule);
    public event Action<NativeRetirement>? NativeOwnershipReleased;
    // A lifecycle owner finished releasing a large graph, such as at match end.
    // Periodic capture leases never leave the retirement stream quiet, so the
    // owner asks explicitly instead of waiting for a quiet window.
    public event Action<string>? NativeBackingReturnRequested;
    public string DirectoryPath => _directory;

    public MemoryJournal(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        _stream = OpenSegment();
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "IDVB memory journal" };
        _writer.Start();
    }

    public void Activate()
    {
        if (Interlocked.CompareExchange(ref _current, this, null) is not null)
            throw new InvalidOperationException("A memory journal is already active.");
    }

    private FileStream OpenSegment() => new(Path.Combine(_directory, $"memory-{_segment:D3}.jsonl"),
        FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16 * 1024, FileOptions.SequentialScan);

    public void Record(string kind, object data, bool durable = false)
    {
        var completion = durable ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        var accepted = false;
        var envelope = new Envelope(DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(),
            Environment.ProcessId, Environment.CurrentManagedThreadId,
            OperatingSystem.IsWindows() ? GetCurrentThreadId() : 0,
            _operation.Value, kind, data);
        accepted = TryPublish(new(envelope, completion));
        if (!accepted && Volatile.Read(ref _disposed) != 0) return;
        if (!accepted)
        {
            // Never silently claim full trace coverage after losing a record,
            // and never block the UI waiting for a slow disk.
            var error = new IOException("内存诊断写入积压超过 1024 条，记录不完整。");
            WriteFailed?.Invoke(error);
            if (durable) throw error;
            return;
        }
        completion?.Task.GetAwaiter().GetResult();
    }

    public void Flush()
    {
        if (Thread.CurrentThread == _writer) { _stream.Flush(true); return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new Pending(null, completion);
        // Only explicit flush/exit callers wait for capacity or disk durability.
        while (!TryPublish(pending))
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            Thread.Sleep(1);
        }
        completion.Task.GetAwaiter().GetResult();
    }

    private bool TryPublish(Pending pending)
    {
        // BlockingCollection.TryAdd still enters SemaphoreSlim's monitor. On
        // WinUI's STA, a contended monitor can pump COM messages inside layout.
        // Ordinary producers must never wait on the writer or a managed lock.
        var failure = Volatile.Read(ref _writerFailure);
        if (failure is not null) throw new IOException("内存诊断写入已失败。", failure);
        if (Volatile.Read(ref _disposed) != 0) return false;
        Interlocked.Increment(ref _producers);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                failure = Volatile.Read(ref _writerFailure);
                if (failure is not null) throw new IOException("内存诊断写入已失败。", failure);
                return false;
            }
            if (!_pending.TryAdd(pending)) return false;
            // Signal before retiring the producer: Dispose cannot close the
            // event while an accepted producer still needs it.
            _wakeWriter.Set();
            return true;
        }
        finally { Interlocked.Decrement(ref _producers); }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // ETW uses OS thread IDs; managed thread IDs cannot join allocator events to
    // these scopes. Keep both, with the same monotonic QPC clock and timestamp.
    private sealed record Envelope(DateTimeOffset utc, long ticks, int pid,
        int thread, uint nativeThread, long operationId, string kind, object data)
    {
        public long sequence { get; set; }
    }
    private sealed record Pending(Envelope? Envelope, TaskCompletionSource? Completion);

    // Fixed MPSC ring: no growth, semaphore or monitor in the publication path.
    // A slot's generation prevents reuse before its previous item is consumed.
    // Only the writer consumes; producers reserve slots with compare/exchange.
    private sealed class PendingQueue
    {
        private const int Capacity = 1024;
        private sealed class Slot(long sequence)
        {
            public long Sequence = sequence;
            public Pending? Value;
        }
        private readonly Slot[] _slots = Enumerable.Range(0, Capacity).Select(i => new Slot(i)).ToArray();
        private long _tail, _head;
        public bool IsEmpty => Volatile.Read(ref _tail) == _head;

        public bool TryAdd(Pending pending)
        {
            while (true)
            {
                var position = Volatile.Read(ref _tail);
                var slot = _slots[(int)(position & (Capacity - 1))];
                var difference = Volatile.Read(ref slot.Sequence) - position;
                if (difference < 0) return false;
                if (difference != 0 || Interlocked.CompareExchange(ref _tail, position + 1, position) != position)
                    continue;
                slot.Value = pending;
                Volatile.Write(ref slot.Sequence, position + 1);
                return true;
            }
        }

        public bool TryTake(out Pending? pending)
        {
            var slot = _slots[(int)(_head & (Capacity - 1))];
            if (Volatile.Read(ref slot.Sequence) != _head + 1) { pending = null; return false; }
            pending = slot.Value!;
            slot.Value = null;
            Volatile.Write(ref slot.Sequence, _head + Capacity);
            ++_head;
            return true;
        }
    }

    private void WriteLoop()
    {
        Exception? failure = null;
        long sequence = 0;
        try
        {
            while (true)
            {
                if (!_pending.TryTake(out var pending))
                {
                    if (Volatile.Read(ref _disposed) != 0 && Volatile.Read(ref _producers) == 0 && _pending.IsEmpty) break;
                    if (failure is null) _stream.Flush();
                    _wakeWriter.WaitOne(250);
                    continue;
                }
                try
                {
                    if (failure is not null) throw new IOException("内存诊断写入已失败。", failure);
                    if (pending!.Envelope is not null)
                    {
                        pending.Envelope.sequence = ++sequence;
                        if (_stream.Position >= SegmentBytes)
                        {
                            _stream.Flush(true); _stream.Dispose();
                            ++_segment; _stream = OpenSegment();
                        }
                        JsonSerializer.Serialize(_stream, pending.Envelope);
                        _stream.WriteByte((byte)'\n');
                    }
                    if (pending.Completion is not null) _stream.Flush(true);
                    pending.Completion?.TrySetResult();
                }
                catch (Exception ex)
                {
                    pending!.Completion?.TrySetException(ex);
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            Volatile.Write(ref _writerFailure, ex);
            Interlocked.Exchange(ref _disposed, 1);
            // An accepted producer can be preempted before publishing its slot.
            // Drain it too, so every durable waiter observes the writer failure.
            while (Volatile.Read(ref _producers) != 0 || !_pending.IsEmpty)
                if (_pending.TryTake(out var pending)) pending!.Completion?.TrySetException(ex);
                else _wakeWriter.WaitOne(1);
            WriteFailed?.Invoke(ex);
        }
        finally
        {
            try { if (failure is null) _stream.Flush(true); }
            catch (Exception ex) { WriteFailed?.Invoke(ex); }
            finally
            {
                try { _stream.Dispose(); }
                catch (Exception ex) { WriteFailed?.Invoke(ex); }
                _wakeWriter.Dispose();
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long Request(string resource, long? payloadBytes = null, string? nativeModule = null)
    {
        var journal = Current;
        if (journal is null) return 0;
        MemoryBudget.Process.ThrowIfExceeded();
        var id = Interlocked.Increment(ref journal._identity);
        journal.Record("resource-request", new { id, resource, payloadBytes, nativeApiModule = nativeModule,
            module = Assembly.GetCallingAssembly().GetName().Name });
        return id;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IDisposable? BeginOperation(string name, string? context = null)
    {
        var journal = Current;
        return journal is null ? null : new Operation(journal,
            Assembly.GetCallingAssembly().GetName().Name ?? "unknown", name, context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IDisposable? Own(string resource, long? payloadBytes = null, string? nativeModule = null, long requestId = 0)
    {
        var journal = Current;
        if (journal is null) return null;
        var id = requestId != 0 ? requestId : Interlocked.Increment(ref journal._identity);
        var module = Assembly.GetCallingAssembly().GetName().Name ?? "unknown";
        journal.Record("ownership-acquired", new { id, module, resource, payloadBytes, nativeApiModule = nativeModule });
        return new Ownership(journal, id, module, resource, payloadBytes, nativeModule);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void TrackResource(object identity, string resource, long? payloadBytes,
        string? nativeModule = null, long requestId = 0)
    {
        var journal = Current;
        if (journal is null) return;
        var id = requestId != 0 ? requestId : Interlocked.Increment(ref journal._identity);
        var module = Assembly.GetCallingAssembly().GetName().Name ?? "unknown";
        journal.Record("ownership-acquired", new { id, module, resource, payloadBytes, nativeApiModule = nativeModule });
        journal._resources.Add(identity, new Ownership(journal, id, module, resource, payloadBytes, nativeModule));
    }

    public static void ReleaseResource(object identity)
    {
        var journal = Current;
        if (journal is not null && journal._resources.TryGetValue(identity, out var ownership))
        {
            journal._resources.Remove(identity);
            ownership.Dispose();
        }
    }

    // Weak references observe GC reclamation without keeping the payload alive or
    // adding finalizers. Cache-ownership release and GC reclamation remain distinct.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ObserveManagedArray(Array array, string resource, long requestId = 0)
    {
        var journal = Current;
        if (journal is null) return;
        long bytes;
        try { bytes = Buffer.ByteLength(array); }
        catch (ArgumentException)
        {
            journal.Record("managed-array-observation-gap", new { resource,
                elementType = array.GetType().GetElementType()?.FullName,
                reason = "untyped non-primitive array; use the typed observation overload" });
            return;
        }
        journal.ObserveArray(array, resource, bytes,
            Assembly.GetCallingAssembly().GetName().Name ?? "unknown", requestId);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ObserveManagedArray<T>(T[] array, string resource, long requestId = 0)
    {
        var journal = Current;
        if (journal is null) return;
        // The GC array stores value-type cells or object references. Count the
        // actual cell payload, excluding headers and separately owned objects.
        journal.ObserveArray(array, resource, checked(array.LongLength * Unsafe.SizeOf<T>()),
            Assembly.GetCallingAssembly().GetName().Name ?? "unknown", requestId);
    }

    private void ObserveArray(Array array, string resource, long bytes, string module, long requestId)
    {
        if (_arrays.Count >= 8192)
        {
            if (Interlocked.Exchange(ref _arrayTrackingGap, 1) == 0)
                Record("managed-array-tracking-gap", new { reason = "8192 weak observations reached; coverage incomplete" });
            return;
        }
        var id = requestId != 0 ? requestId : Interlocked.Increment(ref _identity);
        var observed = new ObservedArray(new(array), module, resource, bytes);
        Record("managed-array-observed", new { id, module, resource, payloadBytes = bytes,
            meaning = "actual instrumented array payload; may overlap cache ownership accounting" });
        // Publish the weak observation only after its event is queued. Otherwise
        // the sampler can emit collection before the matching observed record.
        _arrays[id] = observed;
    }

    public void PollCollectedManagedArrays()
    {
        foreach (var (id, observed) in _arrays)
            if (!observed.Array.TryGetTarget(out _) && _arrays.TryRemove(id, out _))
                Record("managed-array-collected", new { id, module = observed.Module,
                    resource = observed.Resource, payloadBytes = observed.Bytes,
                    meaning = "GC weak reference cleared; not evidence of OS working-set/commit return" });
        foreach (var (id, observed) in _objects)
            if (!observed.Object.TryGetTarget(out _) && _objects.TryRemove(id, out _))
                Record("managed-object-collected", new { id, module = observed.Module, resource = observed.Resource,
                    meaning = "managed wrapper weak reference cleared; not native heap/OS release evidence" });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ObserveManagedObject(object identity, string resource)
    {
        var journal = Current;
        if (journal is null) return;
        if (journal._objects.Count >= 4096)
        {
            if (Interlocked.Exchange(ref journal._objectTrackingGap, 1) == 0)
                journal.Record("managed-object-tracking-gap", new { reason = "4096 weak observations reached; coverage incomplete" });
            return;
        }
        var id = Interlocked.Increment(ref journal._identity);
        var module = Assembly.GetCallingAssembly().GetName().Name ?? "unknown";
        var observed = new ObservedObject(new(identity), module, resource);
        journal.Record("managed-object-observed", new { id, module, resource,
            meaning = "weak lifetime observation; no strong payload ownership or measured size" });
        journal._objects[id] = observed;
    }

    private sealed record ObservedObject(WeakReference<object> Object, string Module, string Resource);

    private sealed record ObservedArray(WeakReference<Array> Array, string Module, string Resource, long Bytes);

    private sealed class Ownership(MemoryJournal journal, long id, string module,
        string resource, long? payloadBytes, string? nativeModule) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            journal.Record("ownership-released", new { id, module, resource, payloadBytes, nativeApiModule = nativeModule });
            if (nativeModule is not null)
                journal.NotifyNativeOwnershipReleased(new(id, module, resource, nativeModule));
        }
    }

    private void NotifyNativeOwnershipReleased(NativeRetirement retirement)
    {
        if (Volatile.Read(ref _disposed) == 0) NativeOwnershipReleased?.Invoke(retirement);
    }

    public static void RequestNativeBackingReturn(string reason)
    {
        var journal = Current;
        if (journal is not null && Volatile.Read(ref journal._disposed) == 0)
            journal.NativeBackingReturnRequested?.Invoke(reason);
    }

    private sealed class Operation : IDisposable
    {
        private readonly MemoryJournal _journal;
        private readonly long _id, _parent, _started, _allocated;
        private readonly int _thread;
        private readonly string _module, _name;
        private int _disposed;
        public Operation(MemoryJournal journal, string module, string name, string? context)
        {
            _journal = journal; _module = module; _name = name;
            _parent = journal._operation.Value;
            _id = Interlocked.Increment(ref journal._identity);
            _thread = Environment.CurrentManagedThreadId;
            _allocated = GC.GetAllocatedBytesForCurrentThread();
            _started = Stopwatch.GetTimestamp();
            journal._operation.Value = _id;
            journal.Record("operation-begin", new { id = _id, parent = _parent, module, name, context });
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // A thread-local counter cannot be subtracted across await/thread switches.
            long? threadAllocatedBytes = _thread == Environment.CurrentManagedThreadId
                ? GC.GetAllocatedBytesForCurrentThread() - _allocated : null;
            _journal.Record("operation-end", new { id = _id, parent = _parent,
                module = _module, name = _name, elapsedMs = Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
                threadAllocatedBytes, managedLiveBytes = GC.GetTotalMemory(false),
                gcCommittedBytes = GC.GetGCMemoryInfo().TotalCommittedBytes,
                allocatedTotalBytes = GC.GetTotalAllocatedBytes(false),
                attribution = "inclusive-thread-counter and process-wide GC readings; includes diagnostics and overlapping work; not heap ownership" });
            _journal._operation.Value = _parent;
        }
    }

    public void Dispose()
    {
        Interlocked.CompareExchange(ref _current, null, this);
        Interlocked.Exchange(ref _disposed, 1);
        if (Thread.CurrentThread != _writer) _writer.Join();
    }
}
