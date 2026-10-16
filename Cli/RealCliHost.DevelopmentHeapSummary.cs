#if IDVB_DEVELOPMENT_BUILD
using System.Diagnostics;
using System.Runtime.InteropServices;
using IDVBuff.Core.Diagnostics;
using IDVBuff.Diagnostics;

namespace IDVBuff.Cli;

internal sealed partial class RealCliHost
{
    private static async Task<CliCommandResult> ExecuteDevelopmentHeapSummaryAsync(string input,
        IReadOnlyList<string> tokens, Stopwatch stopwatch, CancellationToken cancellation)
    {
        if (ParseOptionInt(tokens, "--pid") != Environment.ProcessId || !Program.IsDevelopmentInstance
            || App.IsSafeMode || Microsoft.UI.Xaml.Application.Current is not App application
            || application.MainWindow.Content is not Microsoft.UI.Xaml.Controls.Frame { Content: IDVBuff.Views.MainPage })
            throw new InvalidOperationException("只读堆账本诊断要求完整正常开发客户端和当前准确 PID。");
        await App.ServicesReadyTask.WaitAsync(cancellation);
        if (App.Session.MatchSnapshot.IsStarted)
            throw new InvalidOperationException("只读堆账本诊断要求先结束对局。");

        var reading = await Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            var beforeUtc = DateTime.UtcNow;
            var before = ProcessMemoryReader.Read();
            var gcBefore = GC.GetGCMemoryInfo();
            var collectionsBefore = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
            var allocatedBefore = GC.GetTotalAllocatedBytes();
            var handles = NativeHeapSummaryRead.GetHandles();
            var heaps = new List<NativeHeapSummaryRead.Row>(handles.Length);
            foreach (var handle in handles)
            {
                cancellation.ThrowIfCancellationRequested();
                heaps.Add(NativeHeapSummaryRead.Read(handle));
            }
            var allocatedAfter = GC.GetTotalAllocatedBytes();
            var after = ProcessMemoryReader.Read();
            var gcAfter = GC.GetGCMemoryInfo();
            var result = new
            {
                pid = Environment.ProcessId, beforeUtc, afterUtc = DateTime.UtcNow,
                api = "kernel32!GetProcessHeaps + HeapSummary(flags=0)", before, after,
                gcCommittedBeforeBytes = gcBefore.TotalCommittedBytes,
                gcCommittedAfterBytes = gcAfter.TotalCommittedBytes,
                collectionsBefore,
                collectionsAfter = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
                diagnosticManagedAllocatedBytes = allocatedAfter - allocatedBefore,
                complete = heaps.All(heap => heap.Succeeded), heaps,
                meaning = "Read-only sequential process-heap summaries. Allocated, committed, reserved and private residency are distinct. Committed-minus-allocated includes metadata and fragmentation; it is not pure free memory. Per-heap reads are not an atomic cross-heap snapshot. GetProcessHeaps does not cover arbitrary VirtualAlloc/GC/mapped/GPU storage. No release, optimization, GC, trim or target COM call is requested."
            };
            MemoryJournal.Current?.Record("native-heap-summary-read", result, durable: true);
            return result;
        }, cancellation);
        return CliCommandResult.Ok(input, "只读原生堆账本已读取。", stopwatch.Elapsed.TotalMilliseconds, reading);
    }

    private static class NativeHeapSummaryRead
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Summary
        {
            public uint Size;
            public nuint Allocated, Committed, Reserved, MaxReserve;
        }

        internal readonly record struct Row(ulong Handle, bool Succeeded, uint StructureBytes,
            ulong AllocatedBytes, ulong CommittedBytes, ulong ReservedBytes, ulong MaxReserveBytes,
            double ElapsedMs);

        [DllImport("kernel32.dll")]
        private static extern uint GetProcessHeaps(uint count, [Out] nint[] heaps);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool HeapSummary(nint heap, uint flags, ref Summary summary);

        internal static nint[] GetHandles()
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var count = GetProcessHeaps(0, Array.Empty<nint>());
                if (count is 0 or > 4096) throw new InvalidOperationException("进程堆数量不在有界诊断范围内。");
                var heaps = new nint[count];
                var actual = GetProcessHeaps(count, heaps);
                if (actual == 0) throw new InvalidOperationException("进程堆枚举失败。");
                if (actual > count) continue;
                if (actual < count) Array.Resize(ref heaps, checked((int)actual));
                if (heaps.Distinct().Count() != heaps.Length || heaps.Any(heap => heap == 0))
                    throw new InvalidOperationException("进程堆句柄枚举包含重复项或空项。");
                return heaps;
            }
            throw new InvalidOperationException("堆列表在有界重试期间持续变化，不能建立完整账本。");
        }

        internal static Row Read(nint heap)
        {
            var summary = new Summary { Size = checked((uint)Marshal.SizeOf<Summary>()) };
            if (IntPtr.Size != 8 || summary.Size != 40)
                throw new PlatformNotSupportedException("该已验证诊断布局仅适用于 x64。");
            var started = Stopwatch.GetTimestamp();
            var succeeded = HeapSummary(heap, 0, ref summary);
            return new(unchecked((ulong)heap), succeeded, summary.Size,
                (ulong)summary.Allocated, (ulong)summary.Committed, (ulong)summary.Reserved,
                (ulong)summary.MaxReserve, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
#endif
