using System.Runtime.InteropServices;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Records a binding from the input another foreground application actually
/// receives. Mouse software such as G HUB remaps buttons per application, so a
/// side button pressed inside IDVB can differ from the key the game sees.
/// </summary>
internal sealed class ForegroundBindingRecorder
{
    private const uint VkLeftButton = 0x01;
    private const uint VkRightButton = 0x02;
    private const uint VkCancel = 0x03;
    private const uint VkTab = 0x09;

    private readonly Func<uint, bool> _isDown;
    private readonly bool[] _wasDown = new bool[256];

    public ForegroundBindingRecorder(Func<uint, bool> isDown)
    {
        _isDown = isDown;
        // Keys already held when recording starts are not a new press.
        for (uint key = 1; key < 256; key++)
            _wasDown[key] = isDown(key);
    }

    /// <summary>
    /// Samples every virtual key once. Returns the binding for the first new
    /// press made while another application is foreground, otherwise null.
    /// </summary>
    public MapInputBinding? Observe(bool otherApplicationForeground)
    {
        MapInputBinding? recorded = null;
        for (uint key = 1; key < 256; key++)
        {
            var down = _isDown(key);
            var pressed = down && !_wasDown[key];
            _wasDown[key] = down;
            if (recorded is null && pressed && otherApplicationForeground && IsRecordableTrigger(key))
                recorded = CreateBinding(key);
        }
        return recorded;
    }

    private bool IsRecordableTrigger(uint key) =>
        // Left/right click focuses the game window; they stay bindable inside IDVB.
        key is not (VkLeftButton or VkRightButton or VkCancel)
        && !IsModifier(key)
        // Alt+Tab is how the player returns to IDVB, not the binding.
        && !(key == VkTab && IsAnyDown(0x12, 0xA4, 0xA5));

    private MapInputBinding CreateBinding(uint key)
    {
        if (TryGetMouseButton(key, out var button))
            return new MapInputBinding { Kind = MapInputBindingKind.Mouse, MouseButton = button };

        var modifiers = MapInputModifiers.None;
        if (IsAnyDown(0x11, 0xA2, 0xA3)) modifiers |= MapInputModifiers.Control;
        if (IsAnyDown(0x12, 0xA4, 0xA5)) modifiers |= MapInputModifiers.Alt;
        if (IsAnyDown(0x10, 0xA0, 0xA1)) modifiers |= MapInputModifiers.Shift;
        if (IsAnyDown(0x5B, 0x5C)) modifiers |= MapInputModifiers.Windows;
        return new MapInputBinding
        {
            Kind = MapInputBindingKind.Keyboard,
            VirtualKey = key,
            Modifiers = modifiers
        };
    }

    private static bool TryGetMouseButton(uint key, out MapMouseButton button)
    {
        button = key switch
        {
            0x04 => MapMouseButton.Middle,
            0x05 => MapMouseButton.XButton1,
            0x06 => MapMouseButton.XButton2,
            _ => MapMouseButton.Left
        };
        return key is 0x04 or 0x05 or 0x06;
    }

    private static bool IsModifier(uint key) =>
        key is 0x10 or 0x11 or 0x12 or (>= 0xA0 and <= 0xA5) or 0x5B or 0x5C;

    private bool IsAnyDown(params uint[] keys) => keys.Any(_isDown);
}

/// <summary>Live system input state for <see cref="ForegroundBindingRecorder"/>.</summary>
internal static class ForegroundInputState
{
    public static bool IsKeyDown(uint key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    public static bool IsOtherApplicationForeground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(window, out var processId);
        return processId != 0 && processId != (uint)Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
