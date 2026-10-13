using System.Diagnostics;
using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.SceneQuickActions;

/// <summary>
/// 背包辅助的工作线程：按检测间隔抓一帧 → 模板匹配 → 命中则执行点击流程。
///
/// 线程只在插件启用期间存在；未在游戏中（dwrg.exe 不是前台窗口）时抓帧会立刻失败，
/// 开销接近 0。
///
/// 另外负责三个手动热键：
///   总开关（默认未设置）—— 按一下停用整个插件（不抓帧、不自动动作、拖拽热键也不响应），再按一下恢复；
///   F5 —— 把背包上排 3 格 + 中排 3 格的物品依次拖到屏幕正中心松手；
///   F6 —— 每按一次，把底部道具栏的下一个格子拖到屏幕正中心松手（1 → 2 → 3 → 4 循环）。
/// 触发后一段时间内暂停「拾取相关」，免得把刚丢出来的东西又捡回去。
///
/// 背包状态（<see cref="SceneQuickActionsBackpackState"/>）：识别到背包开着时
/// 「拾取相关」不动作（你要自己丢东西的时候别来抢），并且两个拖拽热键 / 接受邀请
/// 不再多按一次 Tab 把背包关掉。
/// </summary>
internal sealed partial class SceneQuickActionsRunner : IDisposable
{
    private const int MoveDurationMilliseconds = 140;
    private const int ClickHoldMilliseconds = 35;
    private const int KeyHoldMilliseconds = 30;
    private const int WakeHoldMilliseconds = 25;
    // 「切出鼠标后等待」的内部下限：按完 Tab 背包还没开出来就开始点会白点一次。
    private const int MinimumWakeDelayMilliseconds = 120;
    private const int PreClickDelayMilliseconds = 40;
    private const int QuickReplaceAttempts = 3;
    private const int QuickReplaceRetryMilliseconds = 150;
    private const int WarningThrottleSeconds = 20;

    /// <summary>本插件自己注入 Tab 之后，屏蔽宿主钩子送来的那次边沿的时间窗（毫秒）。</summary>
    private const int SuppressBackpackKeyMilliseconds = 800;

    // —— 拖拽热键相关 ——
    private const int HotkeyDebounceMilliseconds = 400;

    private const int BagSlotCount = 6;
    private const int HotbarSlotCount = 4;

    /// <summary>落点：屏幕正中心。</summary>
    private const double DropTargetX = 0.50;
    private const double DropTargetY = 0.50;

    private readonly object _lifecycleSync = new();
    private readonly object _sync = new();
    // 跨启停保留同一把操作锁，直到最后一次 MouseUp / Tab 清理完成才交接。
    private readonly SemaphoreSlim _inputOperationGate = new(1, 1);
    private readonly ISceneQuickActionsFrameGrabber _grabber;
    private readonly ISceneQuickActionsMatcher _matcher;
    private readonly ISceneQuickActionsInput _input;
    private readonly IPluginLogger _logger;
    private readonly SceneQuickActionsBackpackState _backpack = new();

    private SceneQuickActionsOptions _options = new();
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _automaticOperationCancellation;
    private Thread? _worker;
    private Task? _hotkeyTask;
    private volatile bool _mapOpen;
    private bool _inviteArmed = true;
    private bool _pickupArmed = true;
    private DateTime _inviteReadyAt = DateTime.MinValue;
    private DateTime _pickupReadyAt = DateTime.MinValue;
    private string _lastWarning = string.Empty;
    private DateTime _lastWarningAt = DateTime.MinValue;
    private bool _disposed;

    // 总开关：1 = 运行中（默认），0 = 已停用（不抓帧、不自动动作、拖拽热键也不响应）
    private int _automaticEnabled = 1;

    // 热键状态（走宿主的 IPluginInputService，键盘键与鼠标键都能绑）
    private IPluginInputService? _hotkeyService;
    private int _hotkeyBusy;
    private int _hotbarCursor;
    private DateTime _lastHotkeyAt = DateTime.MinValue;
    private DateTime _lastMasterHotkeyAt = DateTime.MinValue;
    private long _dropSilenceUntilTicks;
    /// <summary>本插件自己注入 Tab 的时间窗截止（Stopwatch 时间戳），窗内的 Tab 边沿不计入状态机。</summary>
    private long _suppressBackpackKeyUntilTimestamp;

    public SceneQuickActionsRunner(
        ISceneQuickActionsFrameGrabber grabber,
        ISceneQuickActionsMatcher matcher,
        IPluginLogger logger,
        ISceneQuickActionsInput? input = null)
    {
        _grabber = grabber ?? throw new ArgumentNullException(nameof(grabber));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _input = input ?? SceneQuickActionsNativeInput.Instance;
    }

    /// <summary>总开关热键切换后触发（true = 已启用），交给插件去发游戏内浮层提示。</summary>
    public event Action<bool>? AutomaticStateChanged;

    public void SetOptions(SceneQuickActionsOptions options) =>
        Volatile.Write(ref _options, options ?? new SceneQuickActionsOptions());

    /// <summary>宿主地图界面打开时暂停检测：那时不可能出现这两个场景，且避免与识别流程抢抓帧。</summary>
    public void SetMapOpen(bool mapOpen) => _mapOpen = mapOpen;

    /// <summary>总开关是否处于「运行中」。</summary>
    public bool AutomaticEnabled => Volatile.Read(ref _automaticEnabled) == 1;

    /// <summary>当前是否判定为「背包开着」（供探针与日志使用）。</summary>
    public bool IsBackpackOpen => _backpack.IsOpen;

    /// <summary>
    /// 总开关热键：启用 ⇄ 停用整个插件。停用后不抓帧、不自动动作，两个拖拽热键也不响应
    /// （只留这个总开关本身还能按回来），返回切换后的状态。
    /// </summary>
    public bool ToggleAutomatic()
    {
        var enabled = Volatile.Read(ref _automaticEnabled) == 0;
        Volatile.Write(ref _automaticEnabled, enabled ? 1 : 0);
        lock (_sync)
        {
            // 恢复时让下一帧命中就能动作，不必等上一轮的冷却。
            _inviteReadyAt = DateTime.MinValue;
            _pickupReadyAt = DateTime.MinValue;
        }

        _logger.Info(enabled
            ? "总开关：已启用（识别与自动动作恢复）。"
            : "总开关：已停用（不抓帧、不自动动作，拖拽热键也暂不响应；再按一次恢复）。");
        return enabled;
    }

    /// <summary>插件刚启用 / 重新启用时回到「运行中」，并把背包状态清零重算。</summary>
    public void ResetAutomatic()
    {
        Volatile.Write(ref _automaticEnabled, 1);
        lock (_sync)
        {
            _inviteArmed = true;
            _pickupArmed = true;
            _inviteReadyAt = DateTime.MinValue;
            _pickupReadyAt = DateTime.MinValue;
        }

        _backpack.Reset(DateTime.UtcNow);
    }

    public void Start()
    {
        lock (_lifecycleSync)
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is { IsAlive: true })
                return;
            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _worker = new Thread(() => Run(token))
            {
                IsBackground = true,
                Name = "IDVB scene-quick-actions"
            };
            _worker.Start();
            _lastHotkeyAt = DateTime.MinValue;
            _lastMasterHotkeyAt = DateTime.MinValue;
            StartHotkeys();
        }

        _logger.Info($"背包辅助已启动（抓帧路径：{(_grabber.HasFastPath ? "宿主快速抓帧" : "PNG 截图")}）。");
    }

    public void Stop()
    {
        lock (_lifecycleSync)
            StopCore();
    }

    private void StopCore()
    {
        Thread? worker;
        Task? hotkeyTask;
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            worker = _worker;
            hotkeyTask = _hotkeyTask;
            cancellation = _cancellation;
            _cancellation = null;
            StopHotkeys();
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (worker is not null && worker != Thread.CurrentThread)
            worker.Join();
        hotkeyTask?.GetAwaiter().GetResult();
        // 不能在仍使用 WaitHandle 或仍会执行 finally 时销毁 token / 开始新一轮。
        cancellation?.Dispose();
        lock (_sync)
        {
            _worker = null;
            _hotkeyTask = null;
            _inviteArmed = true;
            _pickupArmed = true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        Stop();
        lock (_sync)
            _hotkeyService = null;
    }

    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var options = Volatile.Read(ref _options);
            try
            {
                DrainBackpackTransitions();

                var now = DateTime.UtcNow;
                // 每轮都重新接入一次设置：判断方式改了就清零重算，最长保持也在这里兜底。
                _backpack.Configure(options.BackpackMode, options.BackpackMaxHoldSeconds, now);

                if (AutomaticEnabled && !_mapOpen)
                {
                    ObserveBackpackCursor(options, now);
                    // 背包开着的时候不需要为「拾取相关」抓帧；只有「自动接受邀请」还盯着画面。
                    if (options.InviteEnabled
                        || (options.PickupEnabled && !_backpack.IsOpen))
                    {
                        ProcessOnce(options, token);
                    }
                }

                DrainBackpackTransitions();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                LogThrottled($"检测异常：{exception.Message}");
            }

            var interval = Math.Clamp(options.PollIntervalMilliseconds, 100, 5000);
            if (token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(interval)))
                break;
        }
    }

    /// <summary>把背包状态的翻转写成日志（统一由工作线程输出，避免多线程抢同一个节流字段）。</summary>
    private void DrainBackpackTransitions()
    {
        while (_backpack.TryTakeTransition(out var message))
            _logger.Info(message);
    }

    /// <summary>
    /// 采样一次「指针可见性」。只在游戏处于前台时采样：切到别的窗口时桌面指针当然可见，
    /// 拿它当证据会把「背包已打开」误判出来。
    /// </summary>
    private void ObserveBackpackCursor(SceneQuickActionsOptions options, DateTime now)
    {
        if (options.BackpackMode == SceneQuickActionsBackpackMode.Off)
            return;
        if (!_input.TryGetForegroundGameWindow(out _))
            return;

        _backpack.ObserveCursor(SceneQuickActionsCursorProbe.IsCursorVisible(), now);
    }

    internal void ProcessOnce(SceneQuickActionsOptions options, CancellationToken token)
    {
        // 自动动作从取得新帧前开始独占；手动请求到达时优先取消自动动作并接管。
        // 不排队旧检测帧，拖拽结束后下一轮重新抓取。
        if (Volatile.Read(ref _hotkeyBusy) != 0 || !_inputOperationGate.Wait(0, token))
            return;

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            lock (_sync)
            {
                if (Volatile.Read(ref _hotkeyBusy) != 0)
                    return;
                _automaticOperationCancellation = operationCancellation;
            }
            ProcessAutomaticScene(options, operationCancellation.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // 手动热键取消当前自动动作后，检测线程仍需继续下一轮。
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_automaticOperationCancellation, operationCancellation))
                    _automaticOperationCancellation = null;
            }
            _inputOperationGate.Release();
        }
    }

    private void ProcessAutomaticScene(SceneQuickActionsOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_mapOpen)
            return;
        if (!_grabber.TryGrab(out var frame, out var failure, token) || frame is null)
        {
            if (!IsIdleFailure(failure))
                LogThrottled($"抓帧失败：{failure}");
            return;
        }

        using (frame)
        {
            token.ThrowIfCancellationRequested();
            var now = DateTime.UtcNow;
            if (options.InviteEnabled && ProcessInviteScene(frame, options, now, token))
                return;
            // 背包开着的时候不拾取 —— 这时候你要自己整理 / 丢东西，别来抢。
            if (options.PickupEnabled
                && !_backpack.IsOpen
                && now.Ticks >= Interlocked.Read(ref _dropSilenceUntilTicks))
            {
                ProcessPickupScene(frame, options, now, token);
            }
        }
    }

    /// <summary>
    /// 切出鼠标：**背包已经开着就什么都不按**（再按一次 Tab 会把它关掉，拖拽 / 点击就全错位了），
    /// 否则按一次 Tab 打开背包、把系统指针切出来。之所以不用鼠标侧键：实测合成的侧键事件游戏不认
    /// （物理侧键正常），而游戏自身键位（Tab）走键盘输入路径是通的。调用方负责成对调用
    /// <see cref="CloseBackpack"/> 把背包关掉（只有真的按过 Tab 才需要关）。
    /// </summary>
    private void EnsureMouseOut(SceneQuickActionsOptions options, IntPtr windowHandle, CancellationToken token,
        ref bool wokeMouse)
    {
        EnsureCanInject(windowHandle, token);

        var wait = Math.Max(options.WakeDelayMilliseconds, MinimumWakeDelayMilliseconds);
        if (_backpack.IsOpen)
        {
            _logger.Info("检测到背包已经开着（指针已切出），本次不按 Tab，直接用现在的指针操作。");
            SleepAfterStep(options, wait, token);
            return;
        }

        SuppressBackpackKeyEdges();
        _input.InjectKey(SceneQuickActionsOptions.WakeVirtualKey, WakeHoldMilliseconds);
        _backpack.MarkOpenedByPlugin(DateTime.UtcNow, "本插件按了 Tab 开背包（切出鼠标）");
        wokeMouse = true;
        _logger.Info("已按 Tab 打开背包切出鼠标。");

        // 下限 120ms：按完 Tab 背包还要一帧才真的开出来，太快移动会白动。
        SleepAfterStep(options, wait, token);
    }

    /// <summary>再按一次 Tab 把背包关掉，恢复游戏手势。游戏已不在前台时只记日志、不注入。</summary>
    private void CloseBackpack(IntPtr windowHandle, SceneQuickActionsOptions options, CancellationToken token)
    {
        if (!_input.IsForegroundWindow(windowHandle))
        {
            _logger.Warning("游戏已不在前台，跳过「按 Tab 关闭背包」。");
            return;
        }

        try
        {
            SuppressBackpackKeyEdges();
            _input.InjectKey(SceneQuickActionsOptions.WakeVirtualKey, WakeHoldMilliseconds);
            _backpack.MarkClosedByPlugin(DateTime.UtcNow, "本插件又按了一次 Tab 关背包");
            // 清理必须完成，但取消后的所有权交接无需再等待随机延迟。
            if (!token.IsCancellationRequested)
                SleepAfterStep(options, 0, token);
            _logger.Info("已再按一次 Tab 关闭背包。");
        }
        catch (Exception exception)
        {
            _logger.Warning($"关闭背包失败：{exception.Message}");
        }
    }

    /// <summary>
    /// 屏蔽「本插件自己注入的 Tab」在宿主键盘钩子里产生的那一次边沿：否则状态机会把
    /// 自己的开 / 关背包当成你按的，奇偶就乱了。窗口按事件时间戳判定，与投递时机无关。
    /// （internal 是为了让离线探针能验证这个时间窗。）
    /// </summary>
    internal void SuppressBackpackKeyEdges() =>
        Interlocked.Exchange(
            ref _suppressBackpackKeyUntilTimestamp,
            Stopwatch.GetTimestamp() + (SuppressBackpackKeyMilliseconds * Stopwatch.Frequency / 1000));

    private void EnsureCanInject(IntPtr windowHandle, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_input.IsForegroundWindow(windowHandle))
            throw new InvalidOperationException("游戏已不在前台，已放弃本次自动点击。");
    }

    private static bool IsIdleFailure(string failure) =>
        string.IsNullOrWhiteSpace(failure)
        || failure.Contains("前台", StringComparison.Ordinal)
        || failure.Contains("dwrg", StringComparison.OrdinalIgnoreCase);

    private void LogThrottled(string message)
    {
        var now = DateTime.UtcNow;
        if (string.Equals(message, _lastWarning, StringComparison.Ordinal)
            && (now - _lastWarningAt).TotalSeconds < WarningThrottleSeconds)
        {
            return;
        }

        _lastWarning = message;
        _lastWarningAt = now;
        _logger.Warning(message);
    }
}
