using System.Runtime.InteropServices;
using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;

public sealed partial class MapGlobalInputService
{
    private readonly Dictionary<(string PluginId, string BindingKey), MapKeyboardChordLatch> _pluginChordLatches = new();

    private void CollectPluginKeyboardEdges(uint key, bool isDown,
        ref List<(string PluginId, string BindingKey, MapInputBinding Binding, bool IsDown)>? matches)
    {
        foreach (var (pluginId, bindings) in _pluginBindings)
        foreach (var (bindingKey, binding) in bindings)
        {
            if (binding.Kind == MapInputBindingKind.Mouse)
            {
                if (TryMouseVirtualKey(binding.MouseButton, out var mouseKey) && mouseKey == key)
                    (matches ??= []).Add((pluginId, bindingKey, binding, isDown));
                continue;
            }
            if (binding.Kind != MapInputBindingKind.Keyboard) continue;
            if (!binding.NormalizedCompanionVirtualKeys().Any())
            {
                if (binding.VirtualKey == key && (!isDown || IsKeyboardBindingActive(binding)))
                    (matches ??= []).Add((pluginId, bindingKey, binding, isDown));
                continue;
            }
            if (key != binding.VirtualKey && !binding.NormalizedCompanionVirtualKeys().Contains(key)
                && key is not (0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C)) continue;
            var id = (pluginId, bindingKey);
            if (!_pluginChordLatches.TryGetValue(id, out var latch))
                _pluginChordLatches[id] = latch = new MapKeyboardChordLatch();
            var edge = latch.Observe(binding, candidate => candidate == key ? isDown : IsKeyDown(candidate));
            if (edge is { } pressed) (matches ??= []).Add((pluginId, bindingKey, binding, pressed));
        }
    }
    public void ApplyPluginBinding(
        string pluginId,
        string bindingKey,
        MapInputBinding binding)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingKey);
        ArgumentNullException.ThrowIfNull(binding);

        lock (_keyboardStateLock)
        {
            if (!_pluginBindings.TryGetValue(pluginId, out var bindings))
            {
                bindings = new Dictionary<string, MapInputBinding>(
                    StringComparer.OrdinalIgnoreCase);
                _pluginBindings[pluginId] = bindings;
            }
            bindings[bindingKey] = binding.Clone();
            _keyboardPollKeysDirty = true;
        }
        RestartMonitoring();
    }

    public void ClearPluginBindings(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
            return;
        lock (_keyboardStateLock)
        {
            _pluginBindings.Remove(pluginId);
            _keyboardPollKeysDirty = true;
        }
        if (HasAnyBinding())
            RestartMonitoring();
        else
            UnregisterBindings();
    }

    public bool IsPluginBindingPressed(string pluginId, string bindingKey)
    {
        MapInputBinding? binding;
        lock (_keyboardStateLock)
        {
            binding = _pluginBindings.TryGetValue(pluginId, out var bindings)
                && bindings.TryGetValue(bindingKey, out var configured)
                    ? configured
                    : null;
        }
        if (binding is null || !binding.IsConfigured)
            return false;

        return binding.Kind switch
        {
            MapInputBindingKind.Keyboard =>
                IsKeyDown(binding.VirtualKey)
                && IsKeyboardBindingActive(binding),
            MapInputBindingKind.Mouse => IsMouseButtonDown(binding.MouseButton),
            _ => false
        };
    }

    private IReadOnlySet<PluginInputBindingState> SnapshotPressedPluginBindings()
    {
        lock (_keyboardStateLock)
        {
            var pressed = new HashSet<PluginInputBindingState>();
            foreach (var (pluginId, bindings) in _pluginBindings)
            {
                foreach (var (bindingKey, binding) in bindings)
                {
                    var isPressed = binding.Kind switch
                    {
                        MapInputBindingKind.Keyboard =>
                            IsKeyDown(binding.VirtualKey)
                            && IsKeyboardBindingActive(binding),
                        MapInputBindingKind.Mouse => IsMouseButtonDown(binding.MouseButton),
                        _ => false
                    };
                    if (isPressed)
                        pressed.Add(new PluginInputBindingState(pluginId, bindingKey));
                }
            }
            return pressed;
        }
    }

    private void RestartMonitoringIfNeeded()
    {
        if (HasAnyBinding())
            RestartMonitoring();
    }

    private void RestartMonitoring()
    {
        UnregisterBindings();
        StartHookThread(installKeyboardHook: true, installMouseHook: true);
        StartKeyboardPolling();
    }

    private bool HasAnyBinding()
    {
        lock (_keyboardStateLock)
        {
            return new[]
            {
                _quickScan,
                _overlayToggle,
                _manualRecognition,
                _gameMapToggle,
                _controlPanelToggle,
                _switchFloor,
                _saveMapCache,
                _restMapDisplay,
                _matchStateToggle
            }.Any(binding => binding.IsConfigured)
            || _pluginBindings.Values.Any(bindings =>
                bindings.Values.Any(binding => binding.IsConfigured));
        }
    }

    private static bool TryGetMouseButton(
        uint message,
        IntPtr lParam,
        out MapMouseButton button,
        out bool isDown)
    {
        button = MapMouseButton.Left;
        isDown = true;
        switch (message)
        {
            case 0x0201: button = MapMouseButton.Left; return true;
            case 0x0202: button = MapMouseButton.Left; isDown = false; return true;
            case 0x0204: button = MapMouseButton.Right; return true;
            case 0x0205: button = MapMouseButton.Right; isDown = false; return true;
            case 0x0207: button = MapMouseButton.Middle; return true;
            case 0x0208: button = MapMouseButton.Middle; isDown = false; return true;
            case 0x020B:
                button = ReadXButton(lParam);
                return true;
            case 0x020C:
                button = ReadXButton(lParam);
                isDown = false;
                return true;
            default:
                return false;
        }
    }

    private static MapMouseButton ReadXButton(IntPtr lParam)
    {
        var data = Marshal.PtrToStructure<MsLlHookStruct>(lParam).MouseData;
        return ((data >> 16) & 0xFFFF) == 2
            ? MapMouseButton.XButton2
            : MapMouseButton.XButton1;
    }

    internal static bool TryMouseVirtualKey(MapMouseButton button, out uint virtualKey)
    {
        virtualKey = button switch
        {
            MapMouseButton.Left => 1,
            MapMouseButton.Right => 2,
            MapMouseButton.Middle => 4,
            MapMouseButton.XButton1 => 5,
            MapMouseButton.XButton2 => 6,
            _ => 0
        };
        return virtualKey != 0;
    }

    private static bool IsMouseButtonDown(MapMouseButton button) =>
        TryMouseVirtualKey(button, out var virtualKey) && IsKeyDown(virtualKey);
}
