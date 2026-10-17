using System.Diagnostics;
using System.Reflection;
using System.Diagnostics.Tracing;
using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Diagnostics;

/// <summary>Always-on GUI resource observations, independent of optional scan/output logging.</summary>
internal sealed partial class MemoryDiagnosticsRuntime : IDisposable
{
    private readonly MemoryJournal _journal;
    private readonly LargePoolBufferListener _poolListener;
    private readonly Thread _monitor;
    private readonly ManualResetEventSlim _stop = new();
    private readonly Action<string> _fatal;
    private readonly Func<ProcessMemoryReader.Reading> _read;
    private readonly MemoryBudget _budget;
    private readonly bool _enforceLimit;
    private bool _aboveReference;
    private long _peakPrivateWorkingSet, _peakWorkingSet, _peakPrivateCommit;
    private ProcessMemoryReader.Reading _lastReading;
    private readonly HashSet<string> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _assemblies = new(StringComparer.Ordinal);
    private int _faulted;
    private int _disposed;
    internal static MemoryBudget Budget => MemoryBudget.Process;
    internal string LogDirectory => _journal.DirectoryPath;

    internal MemoryDiagnosticsRuntime(string root, string build, Action<string> fatal,
        Func<ProcessMemoryReader.Reading>? read = null, MemoryBudget? budget = null, bool enforceLimit = false)
    {
        _fatal = fatal;
        _read = read ?? ProcessMemoryReader.Read;
        _budget = budget ?? Budget;
        _enforceLimit = enforceLimit;
        _journal = new MemoryJournal(Path.Combine(root, "Memory", $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}"));
        _journal.WriteFailed += DiagnosticFailure;
        _journal.Activate();
        _journal.NativeOwnershipReleased += QueueNativeBackingReturn;
        _journal.NativeBackingReturnRequested += RequestNativeBackingReturn;
        _journal.Record("session-start", new { build, dataRoot = Path.GetFullPath(root),
            limitBytes = MemoryBudget.LimitBytes, enforceLimit,
            acceptance = "Comparable idle recovery and no unreclaimable growth across repeated cycles; peak is observed, not capped",
            metric = "Task Manager Processes: private working set", sampleIntervalMs = 100,
            coverage = "process metrics + instrumented operations/resource ownership; not every heap allocation/free",
            nativeAttribution = "ETW Heap/VirtualAllocation stacks required",
            managedAttribution = "CLR allocation ticks are sampled; ownership release is not GC object free",
            businessMemoryRemainsInIdvb = true,
            gcConfiguration = GC.GetConfigurationVariables(),
            gcServer = System.Runtime.GCSettings.IsServerGC,
            gcLatencyMode = System.Runtime.GCSettings.LatencyMode.ToString() }, durable: true);
        _poolListener = new LargePoolBufferListener();
        _journal.Record("managed-pool-coverage", new { minimumElements = 1048576,
            events = "ArrayPool allocation and dropped/trimmed pool references",
            meaning = "informational events only; leases are not traced; element count is not byte count for every array type; pool-reference removal is not GC reclamation" });
        _monitor = new Thread(Monitor) { IsBackground = true, Name = "IDVB memory observations" };
        _monitor.Start();
    }

    private void RecordAssembly(Assembly assembly)
    {
        if (_assemblies.Count < 2048 && _assemblies.Add(assembly.FullName ?? "<unknown>"))
            _journal.Record("managed-module", new { assembly.FullName,
                path = assembly.IsDynamic ? "<dynamic>" : assembly.Location });
    }

    private void Monitor()
    {
        var tick = 0;
        while (!_stop.IsSet && Volatile.Read(ref _faulted) == 0)
        {
            try
            {
                var reading = _read();
                _lastReading = reading;
                _peakPrivateWorkingSet = Math.Max(_peakPrivateWorkingSet, reading.PrivateWorkingSetBytes);
                _peakWorkingSet = Math.Max(_peakWorkingSet, reading.WorkingSetBytes);
                _peakPrivateCommit = Math.Max(_peakPrivateCommit, reading.PrivateCommitBytes);
                if (ObserveBudget(reading)) return;
                if (tick % 10 == 0)
                {
                    _journal.PollCollectedManagedArrays();
                    var gc = GC.GetGCMemoryInfo();
                    _journal.Record("sample", new { reading, resources = ProcessMemoryReader.ReadResourceCounts(),
                        observedPeakPrivateWorkingSetBytes = _peakPrivateWorkingSet,
                        observedPeakWorkingSetBytes = _peakWorkingSet, observedPeakPrivateCommitBytes = _peakPrivateCommit,
                        managedLiveBytes = GC.GetTotalMemory(false),
                        gcCommittedBytes = gc.TotalCommittedBytes, gc.HeapSizeBytes, gc.FragmentedBytes,
                        allocatedTotalBytes = GC.GetTotalAllocatedBytes(false),
                        gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2) });
                }
                if (tick++ % 100 == 0) RecordNativeModules();
                ReturnRetiredNativeBacking();
            }
            catch (Exception ex) { DiagnosticFailure(ex); return; }
            _stop.Wait(100);
        }
    }

    private void RecordNativeModules()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) RecordAssembly(assembly);
        using var process = Process.GetCurrentProcess();
        foreach (ProcessModule module in process.Modules)
        {
            using (module)
                if (_modules.Count < 2048 && _modules.Add(module.FileName))
                    _journal.Record("native-module", new { module.ModuleName, module.FileName,
                        baseAddress = module.BaseAddress.ToInt64(), module.ModuleMemorySize,
                        meaning = "loaded image size, not allocated heap bytes" });
        }
    }

    private sealed class LargePoolBufferListener : EventListener
    {
        [ThreadStatic] private static bool _recording;
        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "System.Buffers.ArrayPoolEventSource")
                EnableEvents(source, EventLevel.Informational);
        }
        protected override void OnEventWritten(EventWrittenEventArgs args)
        {
            if (_recording || args.EventId is not (2 or 4 or 6)
                || args.Payload is not { Count: >= 3 } payload || payload[1] is not int elements
                || elements < 1048576 || MemoryJournal.Current is not { } journal) return;
            _recording = true;
            try
            {
                // Allocation stacks are captured only for new large buffers.
                // Verbose rent/return events would box metadata on every lease.
                var frames = args.EventId == 2 ? new StackTrace(1, false).GetFrames()
                    .Select(frame => frame.GetMethod())
                    .Where(method => method is not null)
                    .Select(method => new { module = method!.Module.Assembly.GetName().Name,
                        type = method.DeclaringType?.FullName, method = method.Name }).ToArray() : null;
                journal.Record(args.EventId == 2 ? "managed-pool-buffer-allocated"
                    : args.EventId == 4 ? "managed-pool-reference-trimmed" : "managed-pool-reference-dropped",
                    new { bufferId = payload[0], elementCount = elements, poolId = payload[2],
                        runtimeEvent = args.EventName, frames,
                        meaning = args.EventId == 2 ? "runtime ArrayPool allocation event; element count, not universal byte count"
                            : "pool reference removed; does not prove GC reclaimed the array" });
            }
            finally { _recording = false; }
        }
    }

    private void DiagnosticFailure(Exception ex) => Fail($"内存诊断失败，IDVB 将退出：{ex.Message}");
    private bool ObserveBudget(ProcessMemoryReader.Reading reading)
    {
        if (!_enforceLimit)
        {
            var above = reading.PrivateWorkingSetBytes > MemoryBudget.LimitBytes;
            if (above != _aboveReference)
            {
                _aboveReference = above;
                _journal.Record("memory-reference-crossing", new { reading, above,
                    referenceBytes = MemoryBudget.LimitBytes, enforced = false,
                    meaning = "Historical reference only; lifecycle recovery, not peak, is the acceptance criterion" });
            }
            return false;
        }
        if (!_budget.Observe(reading.PrivateWorkingSetBytes) && !_budget.Exceeded) return false;
        if (Volatile.Read(ref _faulted) != 0) return true;
        var message = $"IDVB 内存超过 300MB 上限：专用工作集 {reading.PrivateWorkingSetBytes / 1048576d:F2}MB。正在退出整个程序。";
        var gc = GC.GetGCMemoryInfo();
        _journal.Record("budget-exceeded", new { reading, limitBytes = MemoryBudget.LimitBytes,
            gcCommittedBytes = gc.TotalCommittedBytes, gc.HeapSizeBytes, gc.FragmentedBytes,
            allocatedTotalBytes = GC.GetTotalAllocatedBytes(false), message }, durable: true);
        Fail(message);
        return true;
    }
    private void Fail(string message)
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0) return;
        _fatal($"{message}\n诊断目录：{LogDirectory}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        SuspendNativeBackingReturns();
        _journal.NativeOwnershipReleased -= QueueNativeBackingReturn;
        _journal.NativeBackingReturnRequested -= RequestNativeBackingReturn;
        _stop.Set();
        // Never close the file while a sampler still writes to it.
        if (Thread.CurrentThread != _monitor) _monitor.Join();
        _poolListener.Dispose();
        _journal.PollCollectedManagedArrays();
        _journal.Record("session-end", new { budgetExceeded = _budget.Exceeded, enforceLimit = _enforceLimit,
            lastReading = _lastReading, observedPeakPrivateWorkingSetBytes = _peakPrivateWorkingSet,
            observedPeakWorkingSetBytes = _peakWorkingSet, observedPeakPrivateCommitBytes = _peakPrivateCommit }, durable: true);
        _journal.Dispose();
        _stop.Dispose();
    }
}
