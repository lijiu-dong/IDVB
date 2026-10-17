namespace IDVBuff.Features.Maps;

public sealed partial class MapGlobalInputService
{
    private uint[] _keyboardPollKeys = [];
    private bool _keyboardPollKeysDirty = true;

    // Caller holds _keyboardStateLock. The array is immutable after publication:
    // in-flight polls keep their snapshot, while generation checks reject them
    // after restart. Only binding changes rebuild the original ordered key set.
    private uint[] GetKeyboardPollKeys()
    {
        if (!_keyboardPollKeysDirty) return _keyboardPollKeys;
        var configured = new[]
        {
            _quickScan, _overlayToggle, _manualRecognition, _gameMapToggle,
            _controlPanelToggle, _switchFloor, _saveMapCache, _restMapDisplay,
            _matchStateToggle, _hideAlignmentResult
        }.Concat(_pluginBindings.Values.SelectMany(bindings => bindings.Values));
        var keyboard = configured
            .Where(binding => binding.Kind == MapInputBindingKind.Keyboard)
            .SelectMany(binding => new[] { binding.VirtualKey }.Concat(binding.NormalizedCompanionVirtualKeys())
                .Concat(binding.NormalizedCompanionVirtualKeys().Any()
                    ? new uint[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C } : []));
        // Games can deliver side buttons through raw input without a low-level
        // XBUTTON message. Sample the same virtual keys GetAsyncKeyState already
        // exposes, and let the shared edge tracker suppress hook/poll doubles.
        var mouse = configured
            .Where(binding => binding.Kind == MapInputBindingKind.Mouse)
            .Select(binding => TryMouseVirtualKey(binding.MouseButton, out var key) ? key : 0u);
        _keyboardPollKeys = keyboard.Concat(mouse).Where(key => key != 0).Distinct().ToArray();
        _keyboardPollKeysDirty = false;
        return _keyboardPollKeys;
    }
}
