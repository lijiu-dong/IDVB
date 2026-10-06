using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Plugins.FaithNoRecovery;

/// <summary>
/// 只做一件事：盯着 ESC 什么时候被按（用低级键盘钩子），供状态机判断
/// 「这次蓄力是不是玩家自己按 ESC 取消的」。
///
/// 三条自我约束：
///   · **从不吞键**：钩子回调一律 <c>CallNextHookEx</c>，ESC 照常传给游戏；
///   · 只在 dwrg.exe 是前台窗口时记录（带 250ms 缓存，钩子回调很频繁）；
///   · 自己注入的合成输入带宿主标记，不计入玩家操作。
/// </summary>
internal sealed class FaithNoRecoveryEscMonitor : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const uint WmKeyDown = 0x0100;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmQuit = 0x0012;
    private const uint PmNoRemove = 0;
    private const uint EscapeVirtualKey = 0x1B;

    /// <summary>被监测的游戏进程名（与宿主抓帧所用的 dwrg 保持一致）。</summary>
    private const string GameProcessName = "dwrg";

    private static readonly TimeSpan ForegroundCheckCacheDuration = TimeSpan.FromMilliseconds(250);

    private readonly LowLevelKeyboardProc _callback;
    private readonly object _sync = new();
    private IntPtr _hook;
    private Thread? _thread;
    private uint _threadId;
    private long _lastEscapeDownTicks;
    private bool _disposed;

    // 前台判定缓存（只在钩子线程里访问）。
    private IntPtr _lastForegroundWindow;
    private bool _lastForegroundIsGame;
    private DateTime _lastForegroundCheckedAt = DateTime.MinValue;

    public FaithNoRecoveryEscMonitor() => _callback = HookCallback;

    /// <summary>最近一次「玩家在游戏里按下 ESC」的时刻（UTC ticks；0 = 还没按过）。</summary>
    public long LastEscapeDownTicks => Interlocked.Read(ref _lastEscapeDownTicks);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_thread is { IsAlive: true })
                return;
        }

        using var ready = new ManualResetEventSlim();
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                _threadId = GetCurrentThreadId();
                PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
                _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册 ESC 监听键盘钩子。");
                ready.Set();
                while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }
            catch (Exception exception)
            {
                error = exception;
                ready.Set();
            }
            finally
            {
                if (_hook != IntPtr.Zero)
                    UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
                _threadId = 0;
            }
        })
        {
            IsBackground = true,
            Name = "IDVB faith-no-recovery esc hook"
        };

        lock (_sync)
            _thread = thread;

        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("ESC 监听键盘钩子启动超时。");
        if (error is not null)
        {
            thread.Join(TimeSpan.FromSeconds(1));
            throw error;
        }
    }

    public void Stop()
    {
        Thread? thread;
        uint threadId;
        lock (_sync)
        {
            thread = _thread;
            threadId = _threadId;
            _thread = null;
        }

        if (threadId != 0)
            PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        if (thread is not null && thread != Thread.CurrentThread)
            thread.Join(TimeSpan.FromSeconds(2));

        Interlocked.Exchange(ref _lastEscapeDownTicks, 0);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0)
            {
                var message = (uint)wParam.ToInt64();
                if (message is WmKeyDown or WmSysKeyDown)
                {
                    var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                    if (data.VirtualKey == EscapeVirtualKey
                        && data.ExtraInfo.ToInt64() != InputInjectionMarkers.HostGeneratedInput
                        && IsGameForeground())
                    {
                        Interlocked.Exchange(ref _lastEscapeDownTicks, DateTime.UtcNow.Ticks);
                    }
                }
            }
        }
        catch
        {
            // 钩子回调里绝不抛异常；也绝不吞键。
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private bool IsGameForeground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return false;

        var now = DateTime.UtcNow;
        if (window == _lastForegroundWindow
            && now - _lastForegroundCheckedAt < ForegroundCheckCacheDuration)
        {
            return _lastForegroundIsGame;
        }

        var isGame = IsGameProcess(window);
        _lastForegroundWindow = window;
        _lastForegroundIsGame = isGame;
        _lastForegroundCheckedAt = now;
        return isGame;
    }

    private static bool IsGameProcess(IntPtr window)
    {
        if (window == IntPtr.Zero
            || GetWindowThreadProcessId(window, out var processId) == 0
            || processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, GameProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // 进程已退出或没有查询权限时按「不是游戏」处理。
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    private static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint id, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
