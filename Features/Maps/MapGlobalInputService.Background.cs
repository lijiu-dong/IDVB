#if IDVB_DEVELOPMENT_BUILD
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class MapGlobalInputService
{
    private bool? _backgroundMapOpen;
    private void StartBackgroundMapPolling()
    {
        lock (_keyboardStateLock)
        {
            _backgroundMapOpen = null;
            _keyboardBindingsActive = true;
            var generation = ++_keyboardPollGeneration;
            _keyboardPoller = new Timer(PollBackgroundMap, generation, 15, 15);
        }
    }
    private void PollBackgroundMap(object? state)
    {
        lock (_keyboardStateLock)
        {
            if (_disposed || !_keyboardBindingsActive || state is not int generation || generation != _keyboardPollGeneration) return;
            // Sample inside the lock so overlapping timers cannot publish an
            // older state after a newer toggle, matching normal input edges.
            if (!IDVBuff.Lifecycle.BackgroundTestRuntime.TryInput(out _, out _, out _, out var open)) return;
            var changed = _backgroundMapOpen.HasValue && _backgroundMapOpen.Value != open;
            _backgroundMapOpen = open;
            if (!changed) return;
            var invoked = new MapInputInvokedEventArgs(Stopwatch.GetTimestamp());
            DispatchInput(invoked, "private-og", "后台地图开关", "game-map-toggle", () => GameMapToggleInvoked?.Invoke(this, invoked));
        }
    }
}
#endif
