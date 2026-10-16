#if IDVB_DEVELOPMENT_BUILD
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Lifecycle;

// Development transport only. Recognition, alignment and owned frame pixels
// remain in the normal application. Never authorize the interactive desktop.
internal static class BackgroundTestRuntime
{
    internal const string DesktopPrefix = "IDVB.BackgroundTests.";
    internal const uint InputMessage = 0x8000 + 0x71;
    private static nint _window;
    private static uint _processId;
    private static uint _threadId;
    internal static bool Enabled { get; private set; }
    internal static string DesktopName { get; private set; } = "";

    internal static void Initialize(bool requested)
    {
        if (!requested) return;
        var name = Name(GetThreadDesktop(GetCurrentThreadId()));
        var input = OpenInputDesktop(0, false, 1);
        try
        {
            if (!Program.IsDevelopmentInstance || !name.StartsWith(DesktopPrefix, StringComparison.Ordinal)
                || input == 0 || name == Name(input))
                throw new InvalidOperationException("后台测试必须在独立的 IDVB 测试桌面启动，禁止使用交互桌面。");
        }
        finally { if (input != 0) CloseDesktop(input); }
        DesktopName = name;
        Enabled = true;
    }

    internal static void Bind(nint window, int pid, bool background, int version)
    {
        if (!Enabled || !background || version < 4 || window == 0 || pid <= 0)
            throw new InvalidOperationException("OG 未提供独立桌面的后台捕获协议。");
        var thread = GetWindowThreadProcessId(window, out var owner);
        if (owner != pid || Name(GetThreadDesktop(thread)) != DesktopName)
            throw new InvalidOperationException("后台捕获目标与测试桌面或进程不一致。");
        using var process = Process.GetProcessById(pid);
        if (!string.Equals(process.ProcessName, "dwrg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("后台捕获只允许绑定 OG。");
        _processId = owner;
        _threadId = thread;
        if (Volatile.Read(ref _window) == window) return;
        Volatile.Write(ref _window, window);
        MemoryJournal.Current?.Record("background-capture-bound", new { window = window.ToInt64(), pid, DesktopName,
            backend = "OG WM_PRINTCLIENT; same DrawScene as WM_PAINT; IDVB-owned DIB", input = "private window input; no SendInput" });
    }

    internal static bool TryGetWindow(out nint window)
    {
        window = Volatile.Read(ref _window);
        if (!Enabled || window == 0) return false;
        var thread = GetWindowThreadProcessId(window, out var pid);
        if (pid != _processId || thread == 0 || thread != _threadId) return false;
        // The owner cannot change its desktop while this window exists. Avoid
        // allocating a desktop-name buffer in the 2ms tracking/input loop.
        return true;
    }

    internal static bool TryInput(out int x, out int y, out bool down, out bool mapOpen)
    {
        x = y = 0; down = mapOpen = false;
        if (!TryGetWindow(out var window)
            || SendMessageTimeoutW(window, InputMessage, 0, 0, 3, 50, out var result) == 0) return false;
        var bits = result.ToUInt64();
        if ((bits & (1UL << 34)) == 0) return false;
        x = (short)(bits & 0xffff); y = (short)((bits >> 16) & 0xffff);
        down = (bits & (1UL << 32)) != 0;
        mapOpen = (bits & (1UL << 33)) != 0;
        var point = new Point { X = x, Y = y };
        if (!ClientToScreen(window, ref point)) return false;
        x = point.X; y = point.Y;
        return true;
    }

    private static string Name(nint desktop)
    {
        if (desktop == 0) return "";
        var buffer = new StringBuilder(256);
        return GetUserObjectInformationW(desktop, 2, buffer, 512, out _) ? buffer.ToString() : "";
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder buffer, uint length, out uint needed);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeoutW(nint window, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
}
#endif
