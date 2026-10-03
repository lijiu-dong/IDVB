using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Plugins.AutoGatling;
/// <summary>
/// 自动加特林的 Win32 实现：T/Y 由低级键盘钩子拦截，实际操作由
/// SendInput 注入。开火和装弹始终串行，停止时保证释放鼠标左键。
/// </summary>
public sealed partial class AutoGatlingService : IDisposable
{

    private async Task PressKeyAsync(
        uint virtualKey,
        CancellationToken cancellationToken)
    {
        SendKey(virtualKey, keyUp: false);
        try
        {
            await JitteredDelayAsync(
                _options.KeyPressDelayMilliseconds,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SendKey(virtualKey, keyUp: true);
        }
    }

    private Task StandardDelayAsync(CancellationToken cancellationToken) =>
        JitteredDelayAsync(
            _options.StandardDelayMilliseconds,
            cancellationToken);

    private async Task PressBindingAsync(
        PluginInputBinding binding,
        CancellationToken cancellationToken)
    {
        if (binding.Kind == PluginInputBindingKind.Mouse)
        {
            SendMouseButton(binding.MouseButton, true);
            try
            {
                await JitteredDelayAsync(_options.KeyPressDelayMilliseconds, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally { SendMouseButton(binding.MouseButton, false); }
            return;
        }
        var modifierKeys = GetModifierVirtualKeys(binding.Modifiers).ToArray();
        foreach (var modifierKey in modifierKeys)
            SendKey(modifierKey, keyUp: false);

        try
        {
            await PressKeyAsync(binding.VirtualKey, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            for (var index = modifierKeys.Length - 1; index >= 0; index--)
                SendKey(modifierKeys[index], keyUp: true);
        }
    }

    /// <summary>
    /// 在鼠标左键保持按下时，按多个中间点移动到目标位置。
    /// 拖动延迟是整段轨迹的总时长，而不是移动到终点后的空等。
    /// </summary>
    private async Task MoveSmoothlyAsync(
        GameLayout layout,
        PluginInventoryCoordinate coordinate,
        int baseDurationMilliseconds,
        CancellationToken cancellationToken)
    {
        if (!GetCursorPos(out var start))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "无法读取当前鼠标位置。");
        }

        var target = ToScreenPoint(layout, coordinate);
        var distance = Math.Sqrt(
            Math.Pow(target.X - start.X, 2)
            + Math.Pow(target.Y - start.Y, 2));
        var stepCount = Math.Clamp((int)Math.Ceiling(distance / 24d), 4, 48);
        var (minimumRandomDelay, maximumRandomDelay) =
            _options.GetOrderedRandomDelayRange();
        var durationMilliseconds = _options.CoerceDelay(baseDurationMilliseconds)
            + Random.Shared.Next(minimumRandomDelay, maximumRandomDelay + 1);
        var stopwatch = Stopwatch.StartNew();

        for (var step = 1; step <= stepCount; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progress = step / (double)stepCount;
            // Smooth-step easing avoids an abrupt start/stop while preserving
            // a continuous path between the inventory slot and hotbar slot.
            var eased = progress * progress * (3d - (2d * progress));
            var x = (int)Math.Round(start.X + ((target.X - start.X) * eased));
            var y = (int)Math.Round(start.Y + ((target.Y - start.Y) * eased));
            if (!SetCursorPos(x, y))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法移动鼠标到拖动路径上的目标坐标。");
            }

            var targetElapsed = durationMilliseconds * progress;
            var remainingMilliseconds = targetElapsed - stopwatch.Elapsed.TotalMilliseconds;
            if (remainingMilliseconds > 0)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(remainingMilliseconds),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private Task JitteredDelayAsync(
        int milliseconds,
        CancellationToken cancellationToken)
    {
        var (minimum, maximum) = _options.GetOrderedRandomDelayRange();
        return Task.Delay(
            _options.CoerceDelay(milliseconds)
            + Random.Shared.Next(minimum, maximum + 1),
            cancellationToken);
    }

    private static void MoveTo(
        GameLayout layout,
        PluginInventoryCoordinate coordinate)
    {
        var target = ToScreenPoint(layout, coordinate);
        if (!SetCursorPos(target.X, target.Y))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法移动鼠标到目标坐标。");
    }

    private static NativePoint ToScreenPoint(
        GameLayout layout,
        PluginInventoryCoordinate coordinate)
    {
        var x = layout.OriginX + (int)Math.Round(coordinate.X * layout.Width);
        var y = layout.OriginY + (int)Math.Round(coordinate.Y * layout.Height);
        return new NativePoint { X = x, Y = y };
    }

    private static void SendKey(uint virtualKey, bool keyUp)
    {
        var input = new NativeInput
        {
            Type = InputKeyboard,
            Data = new NativeInputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = (ushort)virtualKey,
                    Flags = keyUp ? KeyeventfKeyup : 0,
                    ExtraInfo = InputInjectionMarker
                }
            }
        };
        lock (typeof(AutoGatlingService))
        {
            if (SendInput(1, [input], Marshal.SizeOf<NativeInput>()) != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注入键盘输入。");
        }
    }

    private static void SendMouseButton(PluginMouseButton button, bool down)
    {
        var encoded = PluginMouseInput.Encode(button, down);
        var input = new NativeInput
        {
            Type = InputMouse,
            Data = new NativeInputUnion
            {
                Mouse = new MouseInput
                {
                    Flags = encoded.Flags,
                    MouseData = encoded.Data,
                    ExtraInfo = InputInjectionMarker
                }
            }
        };
        lock (typeof(AutoGatlingService))
            if (SendInput(1, [input], Marshal.SizeOf<NativeInput>()) != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注入鼠标输入。");
    }

    private static void SendLeftButton(bool down)
    {
        var input = new NativeInput
        {
            Type = InputMouse,
            Data = new NativeInputUnion
            {
                Mouse = new MouseInput
                {
                    Flags = down ? MouseeventfLeftdown : MouseeventfLeftup,
                    ExtraInfo = InputInjectionMarker
                }
            }
        };
        lock (typeof(AutoGatlingService))
            _ = SendInput(1, [input], Marshal.SizeOf<NativeInput>());
    }

    /// <summary>
    /// 前台窗口是否属于游戏进程（带短时缓存）。键盘/鼠标钩子据此决定要不要接管触发键。
    /// </summary>
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

    /// <summary>指定窗口是否属于被监测的游戏进程。</summary>
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
            return IsGameProcessName(process.ProcessName);
        }
        catch (Exception)
        {
            // 进程已退出或没有查询权限时按“不是游戏”处理：宁可放行按键，也不误吞。
            return false;
        }
    }

    private static bool IsGameProcessName(string? processName) =>
        string.Equals(processName, GameProcessName, StringComparison.OrdinalIgnoreCase);

    private bool TryGetLayout(out GameLayout layout)
    {
        layout = default;
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !GetClientRect(window, out var rect))
            return false;

        // 本修改版新增的护栏：前台窗口必须属于游戏进程，否则一律不操作。
        if (!IsGameProcess(window))
        {
            _log("自动加特林未执行：前台窗口不是游戏进程（dwrg.exe）。");
            return false;
        }

        var origin = new NativePoint();
        if (!ClientToScreen(window, ref origin))
            return false;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (!AutoGatlingPlan.TryGetCoordinates(width, height, out var coordinates)
            || coordinates is null)
        {
            return false;
        }

        layout = new GameLayout(origin.X, origin.Y, width, height, coordinates);
        return true;
    }

    private bool AreBindingsReady() =>
        _inventoryBinding.IsConfigured
        && _activateBinding.IsConfigured
        && _reloadBinding.IsConfigured;

    private bool HasDuplicateBindings() =>
        _inventoryBinding.Equals(_activateBinding)
        || _inventoryBinding.Equals(_reloadBinding)
        || _activateBinding.Equals(_reloadBinding);

    private static IEnumerable<uint> GetModifierVirtualKeys(
        PluginInputModifiers modifiers)
    {
        if (modifiers.HasFlag(PluginInputModifiers.Control))
            yield return 0x11;
        if (modifiers.HasFlag(PluginInputModifiers.Alt))
            yield return 0x12;
        if (modifiers.HasFlag(PluginInputModifiers.Shift))
            yield return 0x10;
        if (modifiers.HasFlag(PluginInputModifiers.Windows))
            yield return 0x5B;
    }

    private static void ValidateBinding(PluginInputBinding binding, string parameterName)
    {
        if (binding.IsConfigured
            && !PluginInputBinding.TryParse(binding.StorageValue, out _))
        {
            throw new ArgumentException("自动加特林按键绑定无效。", parameterName);
        }
    }

    private static bool AreRequiredModifiersDown(PluginInputModifiers modifiers)
    {
        if (modifiers.HasFlag(PluginInputModifiers.Control)
            && !IsAnyKeyDown(0x11, 0xA2, 0xA3))
            return false;
        if (modifiers.HasFlag(PluginInputModifiers.Alt)
            && !IsAnyKeyDown(0x12, 0xA4, 0xA5))
            return false;
        if (modifiers.HasFlag(PluginInputModifiers.Shift)
            && !IsAnyKeyDown(0x10, 0xA0, 0xA1))
            return false;
        if (modifiers.HasFlag(PluginInputModifiers.Windows)
            && !IsAnyKeyDown(0x5B, 0x5C))
            return false;
        return true;
    }

    private static bool IsAnyKeyDown(params int[] keys) =>
        keys.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    private readonly record struct GameLayout(
        int OriginX,
        int OriginY,
        int Width,
        int Height,
        IReadOnlyList<PluginInventoryCoordinate> Coordinates);

}
