#if IDVB_DEVELOPMENT_BUILD
using System.Diagnostics;
using System.Runtime.InteropServices;
using IDVBuff.Core.Diagnostics;
using IDVBuff.Diagnostics;

namespace IDVBuff.Cli;

internal sealed partial class RealCliHost
{
    private static async Task<CliCommandResult> ExecuteDevelopmentHeapProbeAsync(string input,
        IReadOnlyList<string> tokens, Stopwatch stopwatch, CancellationToken cancellation)
    {
        if (ParseOptionInt(tokens, "--pid") != Environment.ProcessId || !Program.IsDevelopmentInstance
            || App.IsSafeMode || Microsoft.UI.Xaml.Application.Current is not App application
            || application.MainWindow.Content is not Microsoft.UI.Xaml.Controls.Frame { Content: IDVBuff.Views.MainPage })
            throw new InvalidOperationException("原生堆诊断要求完整正常开发客户端和当前准确 PID。");
        await App.ServicesReadyTask.WaitAsync(cancellation);
        if (App.Session.MatchSnapshot.IsStarted)
            throw new InvalidOperationException("原生堆页诊断实验要求先结束对局。");
        var result = await Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            MemoryBudget.Process.ThrowIfExceeded();
            using var operation = MemoryJournal.BeginOperation("Memory.NativeHeapBackingProbe", "one-shot development diagnostic");
            var before = ProcessMemoryReader.Read();
            var gcBefore = GC.GetGCMemoryInfo().TotalCommittedBytes;
            var heapsBefore = GetProcessHeaps(0, IntPtr.Zero);
            MemoryJournal.Current?.Record("native-heap-backing-probe-start", new
            {
                before, gcCommittedBytes = gcBefore, heapsBefore,
                api = "kernel32!HeapSetInformation/HeapOptimizeResources",
                meaning = "Single diagnostic request to decommit eligible unused LFH backing. No object ownership is released by this record."
            }, durable: true);
            // Official HeapOptimizeResources contract: version 1, flags 0,
            // NULL heap applies to the current process's LFH heaps. This is an
            // explicit experiment, never a timer, navigation hook or guard bypass.
            var information = new HeapOptimizationInformation { Version = 1, Flags = 0 };
            var started = Stopwatch.GetTimestamp();
            var succeeded = HeapSetInformation(IntPtr.Zero, 3, ref information, (nuint)Marshal.SizeOf<HeapOptimizationInformation>());
            var error = succeeded ? 0 : Marshal.GetLastWin32Error();
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var after = ProcessMemoryReader.Read();
            var reading = new
            {
                pid = Environment.ProcessId, succeeded, error, elapsedMs, before, after,
                gcCommittedBeforeBytes = gcBefore, gcCommittedAfterBytes = GC.GetGCMemoryInfo().TotalCommittedBytes,
                heapsBefore, heapsAfter = GetProcessHeaps(0, IntPtr.Zero),
                privateCommitDeltaBytes = after.PrivateCommitBytes - before.PrivateCommitBytes,
                privateWorkingSetDeltaBytes = after.PrivateWorkingSetBytes - before.PrivateWorkingSetBytes,
                meaning = "Concurrent process readings; require actual ETW decommit/free ranges to attribute returned backing. This is not proof that business objects were freed or that page growth is fixed."
            };
            MemoryJournal.Current?.Record("native-heap-backing-probe-result", reading, durable: true);
            return reading;
        }, cancellation);
        return CliCommandResult.Ok(input, result.succeeded ? "原生堆页返还诊断实验已执行。" : "原生堆诊断 API 返回失败，详情已记录。",
            stopwatch.Elapsed.TotalMilliseconds, result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HeapOptimizationInformation { public uint Version, Flags; }

    [DllImport("kernel32.dll")]
    private static extern uint GetProcessHeaps(uint count, IntPtr heaps);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HeapSetInformation(IntPtr heap, int informationClass,
        ref HeapOptimizationInformation information, nuint length);
}
#endif
