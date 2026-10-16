namespace IDVBuff.Core.Diagnostics;

/// <summary>A latched limit on the same private working set shown by Task Manager.</summary>
public sealed class MemoryBudget
{
    public const long LimitBytes = 300L * 1024 * 1024;
    public static MemoryBudget Process { get; } = new();
    private int _exceeded;
    public bool Exceeded => Volatile.Read(ref _exceeded) != 0;

    public bool Observe(long privateWorkingSetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(privateWorkingSetBytes);
        return privateWorkingSetBytes > LimitBytes
            && Interlocked.CompareExchange(ref _exceeded, 1, 0) == 0;
    }

    public void ThrowIfExceeded()
    {
        if (Exceeded)
            throw new InvalidOperationException("IDVB 已超过 300MB 内存上限，正在退出。");
    }
}
