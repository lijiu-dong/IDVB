using System.Diagnostics;
using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Plugins.PickupEscape;

/// <summary>
/// 「拾取 ESC」的工作线程：按检测间隔抓一帧 → 用「背包辅助」那两张模板确认
/// 「可拾取」面板在不在 → 在（且是刚出现）就按一下 ESC 把面板关掉。
///
/// 只有 dwrg.exe 是前台窗口时才抓帧；宿主地图界面打开时暂停检测；
/// 每次注入前重新校验前台，游戏切走了就放弃这一次。
///
/// 另外负责那个开关热键：按一下暂停检测、再按一下恢复（默认未设置键位）。
/// 线程只在插件启用期间存在，暂停检测时只空转等待、不抓帧。
/// </summary>
internal sealed class PickupEscapeRunner : IDisposable
{
    private const int KeyHoldMilliseconds = 30;
    private const int WarningThrottleSeconds = 20;
    private const int HotkeyDebounceMilliseconds = 400;

    private readonly object _sync = new();
    private readonly PickupEscapeFrameGrabber _grabber;
    private readonly PickupEscapeMatcher _matcher;
    private readonly IPluginLogger _logger;

    private PickupEscapeOptions _options = new();
    private CancellationTokenSource? _cancellation;
    private Thread? _worker;
    private volatile bool _mapOpen;
    private int _detectionEnabled = 1;
    /// <summary>「面板刚出现」才动作：面板消失后重新武装，避免面板还在时被反复按 ESC。</summary>
    private bool _armed = true;
    private DateTime _readyAt = DateTime.MinValue;
    private string _lastWarning = string.Empty;
    private DateTime _lastWarningAt = DateTime.MinValue;
    private bool _disposed;

    // 热键状态（走宿主的 IPluginInputService，键盘键与鼠标键都能绑）
    private IPluginInputService? _hotkeyService;
    private string _pluginId = PickupEscapeOptions.PluginId;
    private long _hotkeyBindingsUpdatedAt;
    private DateTime _lastHotkeyAt = DateTime.MinValue;
    private int _hotkeyBusy;

    public PickupEscapeRunner(
        PickupEscapeFrameGrabber grabber,
        PickupEscapeMatcher matcher,
        IPluginLogger logger)
    {
        _grabber = grabber ?? throw new ArgumentNullException(nameof(grabber));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>检测开关被热键切换后触发，交给插件去发游戏内浮层提示。</summary>
    public event Action<bool>? DetectionStateChanged;

    public bool DetectionEnabled => Volatile.Read(ref _detectionEnabled) == 1;

    public void SetOptions(PickupEscapeOptions options) =>
        Volatile.Write(ref _options, options ?? new PickupEscapeOptions());

    /// <summary>宿主地图界面打开时暂停检测：那时不可能出现拾取面板，也避免与识别流程抢抓帧。</summary>
    public void SetMapOpen(bool mapOpen) => _mapOpen = mapOpen;

    /// <summary>插件刚启用 / 重新启用时回到「检测运行中」。</summary>
    public void ResetDetection()
    {
        Volatile.Write(ref _detectionEnabled, 1);
        _armed = true;
        _readyAt = DateTime.MinValue;
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
                Name = "IDVB pickup-escape"
            };
            _worker.Start();
            _lastHotkeyAt = DateTime.MinValue;
            StartHotkeys();
        }

        _logger.Info($"拾取 ESC 已启动（抓帧路径：{(_grabber.HasFastPath ? "宿主快速抓帧" : "PNG 截图")}）。");
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

        _armed = true;
        _readyAt = DateTime.MinValue;
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

    private void ProcessOnce(PickupEscapeOptions options, CancellationToken token)
    {
        if (!_grabber.TryGrab(out var frame, out var failure, token) || frame is null)
        {
            if (!IsIdleFailure(failure))
                LogThrottled($"抓帧失败：{failure}");
            return;
        }

        using (frame)
        {
            // 「拾取全部」文案在任何键位下都成立（模板已去掉按键框）；「快捷替换」按钮也算面板存在的证据。
            var hasAnchor = _matcher.TryMatch(
                frame.Gray,
                PickupEscapeTemplates.PickupAll,
                options.MatchThreshold,
                out var anchorHit);
            var hasQuickReplace = _matcher.TryMatch(
                frame.Gray,
                PickupEscapeTemplates.QuickReplace,
                options.MatchThreshold,
                out var quickReplaceHit);

            if (!hasAnchor && !hasQuickReplace)
            {
                // 面板不在了：重新武装，等它下次出现再动作。
                _armed = true;
                return;
            }

            var now = DateTime.UtcNow;
            if (!_armed || now < _readyAt)
                return;

            _armed = false;
            _readyAt = now.AddMilliseconds(options.CooldownMilliseconds);
            PerformEscape(
                frame,
                hasAnchor ? anchorHit : quickReplaceHit,
                options,
                token);
        }
    }

    /// <summary>面板确认在 → 按一下 ESC（或用户在设置页录的键）。</summary>
    private void PerformEscape(
        GrabbedFrame frame,
        MatchHit hit,
        PickupEscapeOptions options,
        CancellationToken token)
    {
        if (!options.ActionBinding.IsConfigured)
        {
            LogThrottled("识别到「可拾取」面板，但设置页里没有可用的按键，本次不动作。");
            return;
        }

        EnsureForeground(frame.WindowHandle);
        PickupEscapeNativeInput.InjectBinding(options.ActionBinding, KeyHoldMilliseconds);
        token.ThrowIfCancellationRequested();
        PickupEscapeNativeInput.Sleep(options.GetDelayAfterStep(0), token);
        _logger.Info(
            $"识别到「可拾取」面板（相似度 {hit.Score:P0}），已按下 {options.ActionBinding.DisplayName}。");
    }

    private static void EnsureForeground(IntPtr windowHandle)
    {
        if (!PickupEscapeNativeInput.IsForegroundWindow(windowHandle))
            throw new InvalidOperationException("游戏已不在前台，已放弃本次按键。");
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
            // 恢复检测时让下一次命中可以立即动作，不必等上一轮的冷却。
            _readyAt = DateTime.MinValue;
            _logger.Info(enabled
                ? "热键：已恢复检测。"
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
            || !args.IsDown
            || !string.Equals(args.PluginId, _pluginId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var options = Volatile.Read(ref _options);
        if (!options.ToggleHotkey.IsConfigured
            || !string.Equals(args.BindingKey, PickupEscapeOptions.ToggleHotkeyKey, StringComparison.Ordinal))
        {
            return;
        }

        // 防抖：按住不放时只认第一次
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
            _hotkeyService.SetBinding(_pluginId, PickupEscapeOptions.ToggleHotkeyKey, options.ToggleHotkey);
            // 宿主事件使用 Stopwatch 时间戳，可能仍在 UI 队列中等待分发。
            _hotkeyBindingsUpdatedAt = Stopwatch.GetTimestamp();
            _logger.Info(options.ToggleHotkey.IsConfigured
                ? $"开关检测热键已注册：{options.ToggleHotkey.DisplayName}。"
                : "开关检测热键尚未设置（去插件设置里录一个键，就能随时暂停/恢复检测）。");
        }
    }
}
