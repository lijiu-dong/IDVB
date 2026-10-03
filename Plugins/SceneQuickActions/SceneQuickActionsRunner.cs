using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.SceneQuickActions;

/// <summary>
/// 背包辅助的工作线程：按检测间隔抓一帧 → 模板匹配 → 命中则执行点击流程。
///
/// 线程只在插件启用期间存在；未在游戏中（dwrg.exe 不是前台窗口）时抓帧会立刻失败，
/// 开销接近 0。
/// </summary>
internal sealed class SceneQuickActionsRunner : IDisposable
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

    private readonly object _sync = new();
    private readonly GameFrameGrabber _grabber;
    private readonly SceneQuickActionsMatcher _matcher;
    private readonly IPluginLogger _logger;

    private SceneQuickActionsOptions _options = new();
    private CancellationTokenSource? _cancellation;
    private Thread? _worker;
    private volatile bool _mapOpen;
    private bool _inviteArmed = true;
    private bool _pickupArmed = true;
    private DateTime _inviteReadyAt = DateTime.MinValue;
    private DateTime _pickupReadyAt = DateTime.MinValue;
    private string _lastWarning = string.Empty;
    private DateTime _lastWarningAt = DateTime.MinValue;
    private bool _disposed;

    public SceneQuickActionsRunner(
        GameFrameGrabber grabber,
        SceneQuickActionsMatcher matcher,
        IPluginLogger logger)
    {
        _grabber = grabber ?? throw new ArgumentNullException(nameof(grabber));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void SetOptions(SceneQuickActionsOptions options) =>
        Volatile.Write(ref _options, options ?? new SceneQuickActionsOptions());

    /// <summary>宿主地图界面打开时暂停检测：那时不可能出现这两个场景，且避免与识别流程抢抓帧。</summary>
    public void SetMapOpen(bool mapOpen) => _mapOpen = mapOpen;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
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
        }

        _logger.Info($"背包辅助已启动（抓帧路径：{(_grabber.HasFastPath ? "宿主快速抓帧" : "PNG 截图")}）。");
    }

    public void Stop()
    {
        Thread? worker;
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            worker = _worker;
            cancellation = _cancellation;
            _worker = null;
            _cancellation = null;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (worker is not null && worker != Thread.CurrentThread)
            worker.Join(TimeSpan.FromSeconds(3));
        cancellation?.Dispose();

        _inviteArmed = true;
        _pickupArmed = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var options = Volatile.Read(ref _options);
            try
            {
                if (!_mapOpen && (options.InviteEnabled || options.PickupEnabled))
                    ProcessOnce(options, token);
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

    private void ProcessOnce(SceneQuickActionsOptions options, CancellationToken token)
    {
        if (!_grabber.TryGrab(out var frame, out var failure, token) || frame is null)
        {
            if (!IsIdleFailure(failure))
                LogThrottled($"抓帧失败：{failure}");
            return;
        }

        using (frame)
        {
            var now = DateTime.UtcNow;
            if (options.InviteEnabled && ProcessInviteScene(frame, options, now, token))
                return;
            if (options.PickupEnabled)
                ProcessPickupScene(frame, options, now, token);
        }
    }

    private bool ProcessInviteScene(
        GrabbedFrame frame,
        SceneQuickActionsOptions options,
        DateTime now,
        CancellationToken token)
    {
        var matched = _matcher.TryMatch(
            frame.Gray,
            SceneQuickActionsTemplates.AcceptButton,
            options.MatchThreshold,
            out var acceptHit);
        if (!matched)
        {
            if (!_inviteArmed)
                _inviteArmed = true;
            return false;
        }

        if (!_inviteArmed || now < _inviteReadyAt)
            return false;

        _inviteArmed = false;
        _inviteReadyAt = now.AddMilliseconds(options.CooldownMilliseconds);
        PerformInvite(frame, acceptHit, options, token);
        return true;
    }

    private void PerformInvite(
        GrabbedFrame frame,
        MatchHit hit,
        SceneQuickActionsOptions options,
        CancellationToken token)
    {
        var (x, y) = SceneQuickActionsMatcher.ToScreen(hit.CenterX, hit.CenterY, frame.Bounds, frame.Size);
        _logger.Info($"识别到「接受」按钮（相似度 {hit.Score:P0}），执行「按 Tab 开背包切出鼠标 → 点击接受 → 按 Tab 关背包」。");

        WakeMouse(options, frame.WindowHandle, token);
        try
        {
            EnsureForeground(frame.WindowHandle);
            NativeInput.MoveSmoothly(x, y, MoveDurationMilliseconds, token);
            NativeInput.Sleep(PreClickDelayMilliseconds, token);
            EnsureForeground(frame.WindowHandle);
            NativeInput.ClickLeft(ClickHoldMilliseconds);
            _logger.Info($"已点击「接受」（屏幕坐标 {x},{y}）。");
        }
        finally
        {
            // 无论点击成功与否都要把背包关掉：留在背包界面里会挡住游戏后续操作。
            CloseBackpack(frame.WindowHandle);
        }
    }

    private void ProcessPickupScene(
        GrabbedFrame frame,
        SceneQuickActionsOptions options,
        DateTime now,
        CancellationToken token)
    {
        // 「拾取全部」文案在任何键位下都成立（模板已去掉按键框）；「快捷替换」按钮也算面板存在的证据。
        var hasAnchor = _matcher.TryMatch(
            frame.Gray,
            SceneQuickActionsTemplates.PickupAll,
            options.MatchThreshold,
            out var anchorHit);
        var hasQuickReplace = _matcher.TryMatch(
            frame.Gray,
            SceneQuickActionsTemplates.QuickReplace,
            options.MatchThreshold,
            out var quickReplaceHit);

        if (!hasAnchor && !hasQuickReplace)
        {
            if (!_pickupArmed)
                _pickupArmed = true;
            return;
        }

        if (!_pickupArmed || now < _pickupReadyAt)
            return;

        // 用鼠标点击「拾取全部」时必须先认到它的位置，认不到就再等下一帧，而不是乱点。
        if (!options.PickupAllBinding.IsConfigured && !hasAnchor)
        {
            LogThrottled("可拾取面板出现了，但没认到「拾取全部」的位置，本次不点击。");
            return;
        }

        _pickupArmed = false;
        _pickupReadyAt = now.AddMilliseconds(options.CooldownMilliseconds);
        PerformPickup(
            frame,
            hasAnchor ? anchorHit : null,
            hasQuickReplace ? quickReplaceHit : null,
            options,
            token);
    }

    private void PerformPickup(
        GrabbedFrame frame,
        MatchHit? pickupHit,
        MatchHit? quickReplaceHit,
        SceneQuickActionsOptions options,
        CancellationToken token)
    {
        var wokeMouse = false;
        if (options.PickupWakeMouse)
        {
            WakeMouse(options, frame.WindowHandle, token);
            wokeMouse = true;
        }

        try
        {
            // 第一步：拾取全部（按下配置的按键，未配置则点击提示位置）
            if (options.PickupAllBinding.IsConfigured)
            {
                EnsureForeground(frame.WindowHandle);
                NativeInput.InjectBinding(options.PickupAllBinding, KeyHoldMilliseconds);
                _logger.Info($"已按下「拾取全部」按键（{options.PickupAllBinding.DisplayName}）。");
            }
            else if (pickupHit is { } anchor)
            {
                var (x, y) = SceneQuickActionsMatcher.ToScreen(
                    anchor.CenterX,
                    anchor.CenterY,
                    frame.Bounds,
                    frame.Size);
                EnsureForeground(frame.WindowHandle);
                NativeInput.MoveSmoothly(x, y, MoveDurationMilliseconds, token);
                NativeInput.Sleep(PreClickDelayMilliseconds, token);
                EnsureForeground(frame.WindowHandle);
                NativeInput.ClickLeft(ClickHoldMilliseconds);
                _logger.Info($"已点击「拾取全部」（相似度 {anchor.Score:P0}，屏幕坐标 {x},{y}）。");
            }
            else
            {
                return;
            }

            NativeInput.Sleep(options.BetweenClicksMilliseconds, token);

            // 第二步：快捷替换
            if (options.QuickReplaceBinding.IsConfigured)
            {
                PressQuickReplaceKey(options, token);
                return;
            }

            ClickQuickReplace(frame, quickReplaceHit, options, token);
        }
        finally
        {
            // 切出过背包就必须关掉，否则会把拾取面板顶掉、还挡住后续操作。
            if (wokeMouse)
                CloseBackpack(frame.WindowHandle);
        }
    }

    /// <summary>「快捷替换」用按键：只需要确认面板还在，位置不重要。</summary>
    private void PressQuickReplaceKey(SceneQuickActionsOptions options, CancellationToken token)
    {
        for (var attempt = 0; attempt < QuickReplaceAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (attempt > 0)
                NativeInput.Sleep(QuickReplaceRetryMilliseconds, token);

            if (!_grabber.TryGrab(out var fresh, out var failure, token) || fresh is null)
            {
                LogThrottled($"按下「快捷替换」按键前抓帧失败：{failure}");
                break;
            }

            using (fresh)
            {
                var panelOpen =
                    _matcher.TryMatch(
                        fresh.Gray,
                        SceneQuickActionsTemplates.PickupAll,
                        options.MatchThreshold,
                        out _)
                    || _matcher.TryMatch(
                        fresh.Gray,
                        SceneQuickActionsTemplates.QuickReplace,
                        options.MatchThreshold,
                        out _);
                if (!panelOpen)
                    continue;

                EnsureForeground(fresh.WindowHandle);
                NativeInput.InjectBinding(options.QuickReplaceBinding, KeyHoldMilliseconds);
                _logger.Info($"已按下「快捷替换」按键（{options.QuickReplaceBinding.DisplayName}）。");
                return;
            }
        }

        LogThrottled("没能确认可拾取面板仍在，已跳过「快捷替换」按键，避免误触其它操作。");
    }

    /// <summary>「快捷替换」用鼠标：必须重新认到按钮位置，绝不盲点。</summary>
    private void ClickQuickReplace(
        GrabbedFrame frame,
        MatchHit? quickReplaceHit,
        SceneQuickActionsOptions options,
        CancellationToken token)
    {
        for (var attempt = 0; attempt < QuickReplaceAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (attempt > 0)
                NativeInput.Sleep(QuickReplaceRetryMilliseconds, token);

            if (!_grabber.TryGrab(out var fresh, out var failure, token) || fresh is null)
            {
                LogThrottled($"「快捷替换」定位时抓帧失败：{failure}");
                break;
            }

            using (fresh)
            {
                if (!_matcher.TryMatch(
                        fresh.Gray,
                        SceneQuickActionsTemplates.QuickReplace,
                        options.MatchThreshold,
                        out var freshHit))
                {
                    continue;
                }

                ClickHit(fresh, freshHit, "快捷替换", token);
                return;
            }
        }

        // 拿不到新画面时，退回「拾取全部」那一帧里已经确认过的位置。
        if (quickReplaceHit is { } remembered)
        {
            ClickHit(frame, remembered, "快捷替换（上一帧位置）", token);
            return;
        }

        LogThrottled("面板里没有识别到「快捷替换」，已跳过第二步。");
    }

    private void ClickHit(GrabbedFrame frame, MatchHit hit, string label, CancellationToken token)
    {
        var (x, y) = SceneQuickActionsMatcher.ToScreen(hit.CenterX, hit.CenterY, frame.Bounds, frame.Size);
        EnsureForeground(frame.WindowHandle);
        NativeInput.MoveSmoothly(x, y, MoveDurationMilliseconds, token);
        NativeInput.Sleep(PreClickDelayMilliseconds, token);
        EnsureForeground(frame.WindowHandle);
        NativeInput.ClickLeft(ClickHoldMilliseconds);
        _logger.Info($"已点击「{label}」（相似度 {hit.Score:P0}，屏幕坐标 {x},{y}）。");
    }

    /// <summary>
    /// 切出鼠标：按一次 Tab 打开背包，把系统指针切出来。
    /// 之所以不用鼠标侧键：实测合成的侧键事件游戏不认（物理侧键正常），
    /// 而游戏自身键位（Tab）走键盘输入路径是通的。调用方负责成对调用
    /// <see cref="CloseBackpack"/> 把背包关掉。
    /// </summary>
    private void WakeMouse(SceneQuickActionsOptions options, IntPtr windowHandle, CancellationToken token)
    {
        EnsureForeground(windowHandle);
        NativeInput.InjectKey(SceneQuickActionsOptions.WakeVirtualKey, WakeHoldMilliseconds);
        _logger.Info("已按 Tab 打开背包切出鼠标。");

        // 下限 120ms：按完 Tab 背包还要一帧才真的开出来，太快移动会白动。
        var wait = Math.Max(options.WakeDelayMilliseconds, MinimumWakeDelayMilliseconds);
        NativeInput.Sleep(wait, token);
    }

    /// <summary>再按一次 Tab 把背包关掉，恢复游戏手势。游戏已不在前台时只记日志、不注入。</summary>
    private void CloseBackpack(IntPtr windowHandle)
    {
        if (!NativeInput.IsForegroundWindow(windowHandle))
        {
            _logger.Warning("游戏已不在前台，跳过「按 Tab 关闭背包」。");
            return;
        }

        try
        {
            NativeInput.InjectKey(SceneQuickActionsOptions.WakeVirtualKey, WakeHoldMilliseconds);
            _logger.Info("已再按一次 Tab 关闭背包。");
        }
        catch (Exception exception)
        {
            _logger.Warning($"关闭背包失败：{exception.Message}");
        }
    }

    private static void EnsureForeground(IntPtr windowHandle)
    {
        if (!NativeInput.IsForegroundWindow(windowHandle))
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
