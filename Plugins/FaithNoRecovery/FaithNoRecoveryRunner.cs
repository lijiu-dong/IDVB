using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Plugins.FaithNoRecovery;

/// <summary>
/// 「信仰无后摇」的工作线程：
///   ① 按检测间隔抓一帧前台游戏画面（游戏不在前台时抓帧立刻失败，开销接近 0）；
///   ② 在准心下方的蓄力环上做几何检测（几个扇区比背景亮）、在右下角做
///      「点击ESC，取消操作」文案的模板匹配；
///   ③ 两个标志都在 ⇒ 记为「正在蓄力」；两个标志都消失、且这次蓄力期间玩家没按过 ESC
///      ⇒ 判定为「蓄力已经打完」，延迟若干毫秒后用道具轮切（先切到别的道具栏、再切回原栏）顶掉后摇；
///   ④ 玩家自己按 ESC 取消的那种，一次都不动作。
///
/// 安全约束：只有 dwrg.exe 是前台窗口时才抓帧；注入前再确认一次前台没变；宿主地图界面打开时暂停。
/// 开关热键默认「未设置」——没录热键时检测就是暂停的，不会偷偷动你的道具栏。
/// </summary>
internal sealed class FaithNoRecoveryRunner : IDisposable
{
    private const int WarningThrottleSeconds = 20;
    private const int HotkeyDebounceMilliseconds = 400;

    private readonly object _sync = new();
    private readonly FaithNoRecoveryFrameGrabber _grabber;
    private readonly FaithNoRecoveryMatcher _matcher;
    private readonly FaithChargeRingDetector _ringDetector;
    private readonly FaithNoRecoveryEscMonitor _escMonitor;
    private readonly FaithNoRecoveryChargeTracker _tracker = new();
    private readonly IPluginLogger _logger;

    private FaithNoRecoveryOptions _options = new();
    private CancellationTokenSource? _cancellation;
    private Thread? _worker;
    private volatile bool _mapOpen;
    private int _detectionEnabled;
    private int _chargedThisRun;
    private int _aspectWarned;
    private int _frameInfoLogged;
    private DateTime _lastHeartbeatAt = DateTime.MinValue;
    private DateTime _lastPausedReminderAt = DateTime.MinValue;
    private string _lastWarning = string.Empty;
    private DateTime _lastWarningAt = DateTime.MinValue;
    private bool _lastRingPresent;
    private bool _lastHintPresent;
    private bool _disposed;

    // 热键状态（走宿主的 IPluginInputService，键盘键与鼠标键都能绑）
    private IPluginInputService? _hotkeyService;
    private string _pluginId = FaithNoRecoveryOptions.PluginId;
    private long _hotkeyBindingsUpdatedAt;
    private DateTime _lastHotkeyAt = DateTime.MinValue;
    private int _hotkeyBusy;

    public FaithNoRecoveryRunner(
        FaithNoRecoveryFrameGrabber grabber,
        FaithNoRecoveryMatcher matcher,
        FaithChargeRingDetector ringDetector,
        FaithNoRecoveryEscMonitor escMonitor,
        IPluginLogger logger)
    {
        _grabber = grabber ?? throw new ArgumentNullException(nameof(grabber));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _ringDetector = ringDetector ?? throw new ArgumentNullException(nameof(ringDetector));
        _escMonitor = escMonitor ?? throw new ArgumentNullException(nameof(escMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>检测开关被热键切换后触发，交给插件去发游戏内浮层提示。</summary>
    public event Action<bool>? DetectionStateChanged;

    /// <summary>完成一次道具轮切后触发（只有设置里打开提示时才订阅到东西）。</summary>
    public event Action<string>? ActionPerformed;

    public bool DetectionEnabled => Volatile.Read(ref _detectionEnabled) == 1;

    public void SetOptions(FaithNoRecoveryOptions options) =>
        Volatile.Write(ref _options, options ?? new FaithNoRecoveryOptions());

    /// <summary>宿主地图界面打开时暂停检测：那时不可能在蓄力，也避免与识别流程抢抓帧。</summary>
    public void SetMapOpen(bool mapOpen) => _mapOpen = mapOpen;

    /// <summary>
    /// 插件刚启用 / 重新启用时回到初始状态。默认要求先录「开关检测」热键（按用户要求
    /// 「不录制就完全不动作」）；如果打开了「未录热键也自动开始检测」，就直接进入检测态。
    /// </summary>
    public void ResetDetection()
    {
        var options = Volatile.Read(ref _options);
        var hasHotkey = options.ToggleHotkey.IsConfigured;
        var armed = hasHotkey || options.AutoArmWithoutHotkey;
        Volatile.Write(ref _detectionEnabled, armed ? 1 : 0);
        _tracker.ResetAll();
        Interlocked.Exchange(ref _chargedThisRun, 0);
        Interlocked.Exchange(ref _frameInfoLogged, 0);
        _lastHeartbeatAt = DateTime.MinValue;
        _lastPausedReminderAt = DateTime.MinValue;
        _logger.Info(armed
            ? hasHotkey
                ? $"检测已开启（开关热键：{options.ToggleHotkey.DisplayName}）。"
                : "检测已开启（未录开关热键，但设置里打开了「未录热键也自动开始检测」）。"
            : "开关热键未设置：检测保持暂停，先到插件设置里录一个键、再按一下它才开始工作"
              + "（或者打开「未录热键也自动开始检测」，让插件页一开就工作）。");
    }

    /// <summary>
    /// 设置页改了「未录热键也自动开始检测」之后重新评估：只有"因为没录热键而暂停"时才自动武装，
    /// 免得把玩家用热键手动暂停的状态又打开。
    /// </summary>
    public void ReevaluateArming()
    {
        var options = Volatile.Read(ref _options);
        if (!options.AutoArmWithoutHotkey || options.ToggleHotkey.IsConfigured || DetectionEnabled)
            return;

        Volatile.Write(ref _detectionEnabled, 1);
        _tracker.ResetAll();
        _logger.Info("设置里打开了「未录热键也自动开始检测」：检测已开启。");
        DetectionStateChanged?.Invoke(true);
    }

    public void Start()
    {
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
                Name = "IDVB faith-no-recovery"
            };
            _worker.Start();
            _lastHotkeyAt = DateTime.MinValue;
            StartHotkeys();
        }

        // 先把抓帧路径探明，免得暂停态下这条日志误报成「PNG 截图」。
        _grabber.EnsureFastPath();
        _logger.Info($"信仰无后摇已启动（抓帧路径：{(_grabber.HasFastPath ? "宿主快速抓帧" : "PNG 截图")}）。");
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
            worker.Join(TimeSpan.FromSeconds(3));
        cancellation?.Dispose();

        _tracker.Reset();
        _lastRingPresent = false;
        _lastHintPresent = false;
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
                if (DetectionEnabled && !_mapOpen)
                    ProcessOnce(options, token);
                else
                    RemindPausedOnce();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                LogThrottled($"检测异常：{exception.Message}");
            }

            var interval = Math.Clamp(
                options.PollIntervalMilliseconds,
                FaithNoRecoveryOptions.PollIntervalMinimum,
                FaithNoRecoveryOptions.PollIntervalMaximum);
            if (token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(interval)))
                break;
        }
    }

    private void ProcessOnce(FaithNoRecoveryOptions options, CancellationToken token)
    {
        if (!_grabber.TryGrab(out var frame, out var failure, token) || frame is null)
        {
            if (!IsIdleFailure(failure))
                LogThrottled($"抓帧失败：{failure}");
            return;
        }

        using (frame)
        {
            WarnAboutAspectOnce(frame);
            LogFrameInfoOnce(frame);

            var ringPresent = false;
            var brightSectors = 0;
            var bestContrast = 0d;
            if (_ringDetector.TryDetect(frame.Gray, options.RingContrastThreshold, out var ring))
            {
                brightSectors = ring.BrightSectors;
                bestContrast = ring.BestContrast;
                ringPresent = ring.IsPresent(options.RingMinimumSectors);
            }

            // 不看阈值地取最佳相似度：命中不了时也要知道"最像的那一次"是多少分。
            var hintScore = _matcher.MatchBest(frame.Gray, out var hint);
            var hintPresent = hintScore >= options.MatchThreshold;
            var now = DateTime.UtcNow;
            var decision = _tracker.Observe(
                options, ringPresent, hintPresent, _escMonitor.LastEscapeDownTicks, now);

            LogDetectionChange(options, ringPresent, brightSectors, bestContrast, hintPresent, hint, decision);
            LogHeartbeat(options, ringPresent, brightSectors, bestContrast, hintPresent, hintScore, decision);

            if (!decision.ShouldSwitch)
                return;

            if (decision.DelayMilliseconds > 0)
                FaithNoRecoveryNativeInput.Sleep(decision.DelayMilliseconds, token);

            PerformSwitch(frame, options, decision, token);
        }
    }

    /// <summary>
    /// 第一次抓到帧时把客户区尺寸写进日志：识别位置全按 1920×1080 标定，
    /// 出问题时这一行就是判断"是不是分辨率/窗口比例不对"的第一手证据。
    /// </summary>
    private void LogFrameInfoOnce(GrabbedFrame frame)
    {
        if (Interlocked.Exchange(ref _frameInfoLogged, 1) != 0)
            return;

        var width = frame.Bounds.Width;
        var height = frame.Bounds.Height;
        var ratio = height > 0 ? width / (double)height : 0d;
        _logger.Info(
            $"首帧：客户区 {width}×{height}（比例 {ratio:F3}）；"
            + "蓄力环按高度缩放（圆心横向居中、纵向 50.3%、半径 3.7%–4.8% 高度），"
            + "取消提示搜索区为右下角 x 70%–100% / y 52%–84%。");
    }

    /// <summary>
    /// 诊断心跳：检测开着的时候每隔几秒写一行当前识别结果，
    /// 这样"到底没认到环、还是没认到文案、还是压根没在蓄力"一眼就能分清。
    /// </summary>
    private void LogHeartbeat(
        FaithNoRecoveryOptions options,
        bool ringPresent,
        int brightSectors,
        double bestContrast,
        bool hintPresent,
        double hintScore,
        FaithChargeDecision decision)
    {
        var seconds = Math.Clamp(options.HeartbeatSeconds, 1, 30);
        var now = DateTime.UtcNow;
        if (now - _lastHeartbeatAt < TimeSpan.FromSeconds(seconds))
            return;

        _lastHeartbeatAt = now;
        _logger.Info(
            $"心跳：蓄力环 {(ringPresent ? "在" : "无")}"
            + $"（亮扇区 {brightSectors}/{FaithNoRecoveryOptions.RingSectorsMaximum}，"
            + $"最高对比度 {bestContrast:F0}，判定线 {options.RingMinimumSectors} 扇区 / {options.RingContrastThreshold}）；"
            + $"取消提示 {(hintPresent ? "在" : "无")}"
            + $"（最佳相似度 {hintScore:P0}，阈值 {options.MatchThreshold:P0}）"
            + (decision.Phase == FaithChargePhase.WaitingOtherCue ? "；只剩一个标志，正在等宽限" : string.Empty));
    }

    /// <summary>检测处于暂停时，每 30 秒在日志里提醒一次"为什么不动作"，免得让人以为插件坏了。</summary>
    private void RemindPausedOnce()
    {
        var now = DateTime.UtcNow;
        if (now - _lastPausedReminderAt < TimeSpan.FromSeconds(30))
            return;

        _lastPausedReminderAt = now;
        var reason = _mapOpen
            ? "宿主的 IDVB 地图界面正开着"
            : "检测处于暂停（没录开关热键，或被热键暂停了）";
        _logger.Info($"{reason}：不抓帧、不动作。");
    }

    /// <summary>「道具轮切」：先按「切到别的道具栏」、停一下、再按「切回原来那栏」。</summary>
    private void PerformSwitch(
        GrabbedFrame frame,
        FaithNoRecoveryOptions options,
        FaithChargeDecision decision,
        CancellationToken token)
    {
        if (!options.SwitchToBinding.IsConfigured || !options.SwitchBackBinding.IsConfigured)
        {
            LogThrottled("两个标志已经消失，但设置页里没有配好两个道具栏按键，本次不动作。");
            return;
        }

        token.ThrowIfCancellationRequested();

        // 注入前再确认一次：游戏已经切走就放弃这一次，绝不往别的窗口按键。
        if (!FaithNoRecoveryNativeInput.IsForegroundWindow(frame.WindowHandle))
        {
            LogThrottled("两个标志已经消失，但游戏不在前台，已放弃本次道具轮切。");
            return;
        }

        var gap = Math.Clamp(
            options.SwitchGapMilliseconds,
            FaithNoRecoveryOptions.SwitchGapMinimum,
            FaithNoRecoveryOptions.SwitchGapMaximum);
        var hold = Math.Clamp(
            options.KeyHoldMilliseconds,
            FaithNoRecoveryOptions.KeyHoldMinimum,
            FaithNoRecoveryOptions.KeyHoldMaximum);

        FaithNoRecoveryNativeInput.InjectBinding(options.SwitchToBinding, hold);
        FaithNoRecoveryNativeInput.Sleep(gap, token);
        FaithNoRecoveryNativeInput.InjectBinding(options.SwitchBackBinding, hold);

        Interlocked.Increment(ref _chargedThisRun);
        _logger.Info(
            $"{decision.Note} 已轮切：{options.SwitchToBinding.DisplayName} → "
            + $"{options.SwitchBackBinding.DisplayName}（间隔 {gap}ms，按键保持 {hold}ms）。");

        if (options.ToastOnAction)
            ActionPerformed?.Invoke($"信仰无后摇：已轮切 {options.SwitchToBinding.DisplayName} → {options.SwitchBackBinding.DisplayName}");

        // 轮切本身不做随机抖动（会把时机抖没），随机等待放在动作之后。
        var extra = options.GetDelayAfterAction();
        if (extra > 0)
            FaithNoRecoveryNativeInput.Sleep(extra, token);
    }

    /// <summary>
    /// 只在「识别结果发生变化」时写日志：每 40ms 一条日志会把日志刷爆，
    /// 但第一次实战校准时又确实需要看清环有几个扇区、文案相似度多少。
    /// </summary>
    private void LogDetectionChange(
        FaithNoRecoveryOptions options,
        bool ringPresent,
        int brightSectors,
        double bestContrast,
        bool hintPresent,
        FaithHintHit hint,
        FaithChargeDecision decision)
    {
        var changed = ringPresent != _lastRingPresent
            || hintPresent != _lastHintPresent
            || decision.ShouldSwitch
            || decision.Note is not null;
        if (!changed)
            return;

        _lastRingPresent = ringPresent;
        _lastHintPresent = hintPresent;

        var ringText = $"蓄力环 {(ringPresent ? "在" : "无")}（亮扇区 {brightSectors}"
            + $"/{FaithNoRecoveryOptions.RingSectorsMaximum}，最高对比度 {bestContrast:F0}，"
            + $"判定线 {options.RingMinimumSectors} 扇区 / 对比度 {options.RingContrastThreshold}）";
        var hintText = hintPresent
            ? $"取消提示 在（相似度 {hint.Score:P0}，模板 {hint.Template}）"
            : "取消提示 无";

        if (decision.Note is not null)
            _logger.Info($"{decision.Note} 【{ringText}；{hintText}】");
        else
            _logger.Info($"状态变化：{ringText}；{hintText}。");
    }

    private void WarnAboutAspectOnce(GrabbedFrame frame)
    {
        if (Interlocked.Exchange(ref _aspectWarned, 1) != 0)
            return;

        var width = frame.Bounds.Width;
        var height = frame.Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        var ratio = width / (double)height;
        var is16By9 = Math.Abs(ratio - (16d / 9d)) < 0.02;
        var is16By10 = Math.Abs(ratio - (16d / 10d)) < 0.02;
        if (is16By9 || is16By10)
            return;

        _logger.Warning(
            $"游戏客户区是 {width}×{height}（不是 16:9 / 16:10）：蓄力环的几何位置按高度缩放，"
            + "识别可能会有偏移；建议用 16:9 的窗口化/无边框窗口。");
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

    // ════════════════ 开关热键 ════════════════

    /// <summary>热键状态切换：返回切换后的状态（true = 检测运行中）。</summary>
    public bool ToggleDetection()
    {
        if (Interlocked.CompareExchange(ref _hotkeyBusy, 1, 0) != 0)
            return DetectionEnabled;

        try
        {
            var enabled = Volatile.Read(ref _detectionEnabled) == 0;
            Volatile.Write(ref _detectionEnabled, enabled ? 1 : 0);
            _tracker.Reset();
            if (enabled)
                Interlocked.Exchange(ref _chargedThisRun, 0);
            _logger.Info(enabled
                ? "热键：已开启检测。"
                : "热键：已暂停检测（再按一次恢复）。");
            return enabled;
        }
        finally
        {
            Interlocked.Exchange(ref _hotkeyBusy, 0);
        }
    }

    /// <summary>接入宿主的插件输入通道：热键的按键监听、注入过滤都由宿主负责。</summary>
    public void AttachHotkeys(IPluginInputService? input, string pluginId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ReferenceEquals(_hotkeyService, input)
                && string.Equals(_pluginId, pluginId, StringComparison.Ordinal))
            {
                return;
            }

            StopHotkeys();
            _hotkeyService = input;
            _pluginId = pluginId;
            if (_cancellation is not null)
                StartHotkeys();
        }
    }

    /// <summary>热键改动后重新注册（设置页改键位时调用）。</summary>
    public void RefreshHotkeyBindings() => RegisterHotkeys();

    private void OnHotkeyInvoked(object? sender, PluginInputEventArgs args)
    {
        if (_disposed
            || _cancellation is null
            || !ReferenceEquals(sender, _hotkeyService)
            || args.Timestamp < _hotkeyBindingsUpdatedAt
            || !string.Equals(args.PluginId, _pluginId, StringComparison.OrdinalIgnoreCase)
            || !args.IsDown
            || !string.Equals(args.BindingKey, FaithNoRecoveryOptions.ToggleHotkeyKey, StringComparison.Ordinal))
        {
            return;
        }

        var options = Volatile.Read(ref _options);
        if (!options.ToggleHotkey.IsConfigured)
            return;

        var now = DateTime.UtcNow;
        if (now < _lastHotkeyAt.AddMilliseconds(HotkeyDebounceMilliseconds))
            return;
        _lastHotkeyAt = now;

        var enabled = ToggleDetection();
        DetectionStateChanged?.Invoke(enabled);
    }

    private void StartHotkeys()
    {
        if (_hotkeyService is null)
            return;

        _hotkeyService.BindingInvoked -= OnHotkeyInvoked;
        _hotkeyService.BindingInvoked += OnHotkeyInvoked;
        RegisterHotkeys();
    }

    private void StopHotkeys()
    {
        if (_hotkeyService is null)
            return;

        _hotkeyService.BindingInvoked -= OnHotkeyInvoked;
        _hotkeyService.ClearBindings(_pluginId);
    }

    private void RegisterHotkeys()
    {
        lock (_sync)
        {
            if (_disposed || _cancellation is null || _hotkeyService is null)
                return;

            var options = Volatile.Read(ref _options);
            _hotkeyService.SetBinding(_pluginId, FaithNoRecoveryOptions.ToggleHotkeyKey, options.ToggleHotkey);
            // 宿主事件使用 Stopwatch 时间戳，可能仍在 UI 队列中等待分发。
            _hotkeyBindingsUpdatedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _logger.Info(options.ToggleHotkey.IsConfigured
                ? $"开关热键已注册：{options.ToggleHotkey.DisplayName}。"
                : "开关热键尚未设置（去插件设置里录一个键，检测才会开始工作）。");
        }
    }
}
