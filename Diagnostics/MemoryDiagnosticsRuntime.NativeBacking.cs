using System.Diagnostics;
using System.Runtime.InteropServices;
using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Diagnostics;

internal sealed partial class MemoryDiagnosticsRuntime
{
    internal readonly record struct NativeBackingResult(bool Succeeded, int Error, double ElapsedMs);
    private sealed record BackingPolicy(Func<bool> CanReturn, Func<NativeBackingResult> ReturnUnused);
    private readonly object _backingGate = new();
    private BackingPolicy? _backingPolicy;
    private MemoryJournal.NativeRetirement _retiredNativeResource;
    private long _nativeRetirementRevision, _handledNativeRetirementRevision, _nativeRetirementTicks, _lastBackingAttemptTicks;
    private int _backingReturnsSuspended;

    internal void ConfigureNativeBackingReturns(Func<bool> canReturn, Func<NativeBackingResult>? returnUnused = null)
    {
        ArgumentNullException.ThrowIfNull(canReturn);
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _backingReturnsSuspended) != 0)
            throw new ObjectDisposedException(nameof(MemoryDiagnosticsRuntime));
        if (Interlocked.CompareExchange(ref _backingPolicy, new(canReturn, returnUnused ?? ReturnUnusedHeapBacking), null) is not null)
            throw new InvalidOperationException("Native backing return policy is already configured.");
    }

    internal void SuspendNativeBackingReturns() => Interlocked.Exchange(ref _backingReturnsSuspended, 1);

    private void QueueNativeBackingReturn(MemoryJournal.NativeRetirement retirement)
    {
        if (Volatile.Read(ref _backingPolicy) is null || Volatile.Read(ref _disposed) != 0
            || Volatile.Read(ref _backingReturnsSuspended) != 0) return;
        // One bounded pending slot: no resource, page, bitmap or work queue.
        lock (_backingGate)
        {
            _retiredNativeResource = retirement;
            _nativeRetirementTicks = Stopwatch.GetTimestamp();
            ++_nativeRetirementRevision;
        }
    }

    private void ReturnRetiredNativeBacking()
    {
        if (Volatile.Read(ref _backingPolicy) is not { } policy || Volatile.Read(ref _backingReturnsSuspended) != 0
            || Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _faulted) != 0 || _stop.IsSet) return;
        MemoryJournal.NativeRetirement retirement;
        long revision, retiredAt;
        lock (_backingGate)
        {
            revision = _nativeRetirementRevision;
            retiredAt = _nativeRetirementTicks;
            retirement = _retiredNativeResource;
        }
        var now = Stopwatch.GetTimestamp();
        if (revision == _handledNativeRetirementRevision || Stopwatch.GetElapsedTime(retiredAt, now).TotalMilliseconds < 500
            || Stopwatch.GetElapsedTime(_lastBackingAttemptTicks, now).TotalSeconds < 5) return;
        _lastBackingAttemptTicks = now;
        if (!policy.CanReturn() || Volatile.Read(ref _backingReturnsSuspended) != 0) return;

        // Never return backing before checking the actual metric: an observed
        // limit violation remains fatal even if a later reading is lower.
        var before = _read();
        if (ObserveBudget(before)) return;
        _handledNativeRetirementRevision = revision;
        using var operation = MemoryJournal.BeginOperation("Memory.ReturnRetiredNativeBacking", retirement.Resource);
        var gcBefore = GC.GetGCMemoryInfo().TotalCommittedBytes;
        _journal.Record("native-backing-return-start", new { revision, retirement, retiredAtTicks = retiredAt,
            before, gcCommittedBytes = gcBefore, api = "kernel32!HeapSetInformation/HeapOptimizeResources",
            meaning = "A native consumer relinquished ownership. Request eligible unused LFH backing only; this record does not prove business object or heap payload free." });
        // Run on the existing budget monitor, never the UI thread. No forced
        // GC, working-set eviction, memory-limit retry or external service.
        var result = policy.ReturnUnused();
        var after = _read();
        _journal.Record("native-backing-return-result", new { revision, retirement, result.Succeeded, result.Error,
            result.ElapsedMs, before, after, gcCommittedBeforeBytes = gcBefore,
            gcCommittedAfterBytes = GC.GetGCMemoryInfo().TotalCommittedBytes,
            privateCommitDeltaBytes = after.PrivateCommitBytes - before.PrivateCommitBytes,
            privateWorkingSetDeltaBytes = after.PrivateWorkingSetBytes - before.PrivateWorkingSetBytes,
            meaning = "Concurrent readings, not released payload accounting. Attribute actual decommit with same-PID/native-thread/QPC VirtualFree events; ownership release, heap free, and returned backing remain distinct." });
        ObserveBudget(after);
    }

    private static NativeBackingResult ReturnUnusedHeapBacking()
    {
        var information = new HeapOptimizationInformation { Version = 1, Flags = 0 };
        var started = Stopwatch.GetTimestamp();
        var succeeded = HeapSetInformation(IntPtr.Zero, 3, ref information, (nuint)Marshal.SizeOf<HeapOptimizationInformation>());
        var error = succeeded ? 0 : Marshal.GetLastWin32Error();
        return new(succeeded, error, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HeapOptimizationInformation { public uint Version, Flags; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HeapSetInformation(IntPtr heap, int informationClass,
        ref HeapOptimizationInformation information, nuint length);
}
