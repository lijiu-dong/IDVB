using System.Diagnostics;
using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;

public sealed partial class MapGlobalInputService
{
    // Release compilation can hoist captured dispatch state to a method's entry.
    // Keep it out of idle polling: only an observed edge reaches this method.
    private void DispatchKeyboardEdge(
        bool invokeQuickScan, bool invokeOverlayToggle, bool invokeManualRecognition,
        bool invokeGameMapToggle, bool invokeControlPanelToggle, bool invokeSwitchFloor,
        bool invokeSaveMapCache, bool invokeRestMapDisplay, bool invokeMatchStateToggle,
        bool invokeHideAlignmentResult, bool invokeAlt,
        List<(string PluginId, string BindingKey, MapInputBinding Binding, bool IsDown)>? pluginMatches)
    {
        var invoked = new MapInputInvokedEventArgs(Stopwatch.GetTimestamp());
        static string Device(MapInputBinding binding) =>
            binding.Kind == MapInputBindingKind.Mouse ? "mouse" : "keyboard";
        if (invokeQuickScan)
            DispatchInput(invoked, Device(_quickScan), _quickScan.DisplayName,
                "quick-scan", () => QuickScanInvoked?.Invoke(this, invoked));
        if (invokeOverlayToggle)
            DispatchInput(invoked, Device(_overlayToggle), _overlayToggle.DisplayName,
                "overlay-toggle", () => OverlayToggleInvoked?.Invoke(this, invoked));
        if (invokeManualRecognition)
            DispatchInput(invoked, Device(_manualRecognition), _manualRecognition.DisplayName,
                "manual-recognition", () => ManualRecognitionInvoked?.Invoke(this, invoked));
        if (invokeGameMapToggle)
        {
            Interlocked.Increment(ref _gameMapDispatch);
            DispatchInput(invoked, Device(_gameMapToggle), _gameMapToggle.DisplayName,
                "game-map-toggle", () => GameMapToggleInvoked?.Invoke(this, invoked));
        }
        if (invokeControlPanelToggle)
        {
            DispatchInput(invoked, Device(_controlPanelToggle), _controlPanelToggle.DisplayName,
                "control-panel-toggle",
                () => ControlPanelToggleInvoked?.Invoke(this, invoked));
        }
        if (invokeSwitchFloor)
            DispatchInput(invoked, Device(_switchFloor), _switchFloor.DisplayName,
                "switch-floor", () => SwitchFloorInvoked?.Invoke(this, invoked));
        if (invokeSaveMapCache)
            DispatchInput(invoked, Device(_saveMapCache), _saveMapCache.DisplayName,
                "save-map-cache", () => SaveMapCacheInvoked?.Invoke(this, invoked));
        if (invokeRestMapDisplay)
            DispatchInput(invoked, Device(_restMapDisplay), _restMapDisplay.DisplayName,
                "rest-map-display", () => RestMapDisplayInvoked?.Invoke(this, invoked));
        if (invokeMatchStateToggle)
            DispatchInput(invoked, Device(_matchStateToggle), _matchStateToggle.DisplayName,
                "match-state-toggle", () => MatchStateToggleInvoked?.Invoke(this, invoked));
        if (invokeHideAlignmentResult)
            DispatchInput(invoked, Device(_hideAlignmentResult), _hideAlignmentResult.DisplayName,
                "hide-alignment-result",
                () => HideAlignmentResultInvoked?.Invoke(this, invoked));
        if (invokeAlt)
            DispatchInput(invoked, "keyboard", "Alt", "alt",
                () => AltInvoked?.Invoke(this, invoked));
        if (pluginMatches is not null)
        {
            foreach (var match in pluginMatches)
            {
                var pluginEvent = new PluginInputInvokedEventArgs(
                    match.PluginId,
                    match.BindingKey,
                    invoked.Timestamp,
                    match.IsDown);
                DispatchPluginInput(pluginEvent);
            }
        }
    }
}
