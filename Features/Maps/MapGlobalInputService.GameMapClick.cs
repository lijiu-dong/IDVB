using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class MapGlobalInputService
{
    // Read on the hook thread for every mouse button edge. Volatile publication
    // is enough: the predicate is a cheap filter and the runtime rechecks its
    // own state before acting on a dispatched click.
    private volatile Func<bool>? _gameMapClickWatch;

    /// <summary>
    /// A physical left click was released while the watch predicate reported
    /// the game's map as open. It may have hit the game's own close button.
    /// </summary>
    public event EventHandler<MapInputInvokedEventArgs>? GameMapClickObserved;

    /// <summary>
    /// Sets the predicate that arms <see cref="GameMapClickObserved"/>. It runs
    /// on the low-level hook thread and must return without blocking.
    /// </summary>
    public void SetGameMapClickWatch(Func<bool>? isArmed) => _gameMapClickWatch = isArmed;

    private void ObserveGameMapClick(MsLlHookStruct mouse, MapMouseButton button, bool isDown)
    {
        if (isDown || button != MapMouseButton.Left || IsMarkedInjectedMouse(mouse))
            return;
        // A left-button game-map binding already reports its own toggle.
        if (_gameMapToggle.Kind == MapInputBindingKind.Mouse
            && _gameMapToggle.MouseButton == MapMouseButton.Left)
        {
            return;
        }
        if (_gameMapClickWatch is not { } isArmed || !isArmed())
            return;

        var invoked = new MapInputInvokedEventArgs(Stopwatch.GetTimestamp());
        DispatchInput(invoked, "mouse", "Left", "game-map-click",
            () => GameMapClickObserved?.Invoke(this, invoked));
    }
}
