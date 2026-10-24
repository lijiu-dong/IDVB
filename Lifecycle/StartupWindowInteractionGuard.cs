using System.ComponentModel;
using System.Runtime.InteropServices;

namespace IDVBuff.Lifecycle;

/// <summary>
/// Prevents an incomplete WinUI HWND from accepting input or activation while
/// still allowing it to be shown for Loaded/rendering and placement restoration.
/// </summary>
internal sealed class StartupWindowInteractionGuard : IDisposable
{
    private const int ExStyleIndex = -20;
    private const int NoActivate = 0x08000000;
    private readonly IntPtr _window;
    private readonly bool _wasEnabled;
    private readonly bool _hadNoActivate;
    private readonly HookProcedure _procedure;
    private GCHandle _procedureRoot;
    private IntPtr _activationHook;
    private bool _disposed;

    internal StartupWindowInteractionGuard(IntPtr window)
    {
        if (!IsWindow(window))
            throw new ArgumentException("A live startup window is required.", nameof(window));
        _window = window;
        if (GetWindowThreadProcessId(window, out _) != GetCurrentThreadId())
            throw new ArgumentException("The startup guard must run on the window's owning thread.", nameof(window));
        _wasEnabled = IsWindowEnabled(window);
        var style = GetWindowLong(window, ExStyleIndex);
        _hadNoActivate = (style & NoActivate) != 0;
        _procedure = PreventStartupActivation;
        try
        {
            // WS_EX_NOACTIVATE and EnableWindow(false) do not reject explicit
            // native activation. Veto it before Windows changes active/focus
            // state, including activation inside SetWindowPlacement.
            _procedureRoot = GCHandle.Alloc(_procedure);
            _activationHook = SetWindowsHookEx(5, _procedure, IntPtr.Zero, GetCurrentThreadId()); // WH_CBT
            if (_activationHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            SetExtendedStyle(style | NoActivate);
            EnableWindow(window, false);
            if (IsWindowEnabled(window))
                throw new Win32Exception("The startup window could not be disabled.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (IsWindow(_window))
            {
                try
                {
                    // Preserve unrelated style changes made by WinUI/theme registration.
                    var style = GetWindowLong(_window, ExStyleIndex);
                    SetExtendedStyle(_hadNoActivate ? style | NoActivate : style & ~NoActivate);
                }
                finally { EnableWindow(_window, _wasEnabled); }
            }
        }
        finally
        {
            if (_activationHook != IntPtr.Zero && !UnhookWindowsHookEx(_activationHook))
            {
                var error = Marshal.GetLastWin32Error();
                // An already removed hook cannot call the delegate again.
                if (error != 1404) throw new Win32Exception(error);
            }
            _activationHook = IntPtr.Zero;
            if (_procedureRoot.IsAllocated) _procedureRoot.Free();
        }
    }

    private IntPtr PreventStartupActivation(int code, IntPtr target, IntPtr data)
    {
        if (code is 5 or 9 && (target == _window || IsChild(_window, target))) // HCBT_ACTIVATE / HCBT_SETFOCUS
            return new IntPtr(1);
        return CallNextHookEx(_activationHook, code, target, data);
    }

    private delegate IntPtr HookProcedure(int code, IntPtr target, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int kind, HookProcedure procedure, IntPtr module, uint thread);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr target, IntPtr data);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private void SetExtendedStyle(int style)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLong(_window, ExStyleIndex, style);
        var error = Marshal.GetLastPInvokeError();
        if (previous == 0 && error != 0)
            throw new Win32Exception(error);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool enabled);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr window, int index, int value);
}
