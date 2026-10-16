using System.ComponentModel;
using System.Runtime.InteropServices;

namespace IDVBuff.Diagnostics;

/// <summary>Report residency, private commit and handles separately for lifecycle comparisons.</summary>
internal static class ProcessMemoryReader
{
    internal readonly record struct Reading(long PrivateWorkingSetBytes, long WorkingSetBytes, long PrivateCommitBytes);
    internal readonly record struct ResourceCounts(uint Handles, uint? GdiHandles, uint? UserHandles);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessHandleCount(nint process, out uint count);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(nint process, uint flags);

    internal static ResourceCounts ReadResourceCounts()
    {
        if (!GetProcessHandleCount(-1, out var handles))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取当前进程句柄数量。");
        return new(handles, ReadGuiCount(0), ReadGuiCount(1));
    }

    private static uint? ReadGuiCount(uint flags)
    {
        Marshal.SetLastPInvokeError(0);
        var count = GetGuiResources(-1, flags);
        return count == 0 && Marshal.GetLastPInvokeError() != 0 ? null : count;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Counters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool,
            PeakNonPagedPool, NonPagedPool, Pagefile, PeakPagefile, PrivateUsage, PrivateWorkingSet;
        public ulong SharedCommit;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(nint process, out Counters counters, uint size);
    internal static Reading Read()
    {
        if (!K32GetProcessMemoryInfo(-1, out var counters, (uint)Marshal.SizeOf<Counters>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取任务管理器专用工作集，内存观测无法继续。");
        return new(checked((long)counters.PrivateWorkingSet), checked((long)counters.WorkingSet),
            checked((long)counters.PrivateUsage));
    }
}
