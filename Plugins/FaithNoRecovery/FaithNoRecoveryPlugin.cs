using IDVBuff.Core.Contracts;
using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Plugins.FaithNoRecovery;

/// <summary>
/// 「信仰无后摇」：把「信仰」蓄力动作打完之后的那个后摇，用道具轮切顶掉。
///
/// 判定用游戏里现成的两个标志（都是 1920×1080 实拍截图里量出来的）：
///   ① 准心下方那个蓄力环（下半圆，未蓄满时只有一部分亮、蓄满时整圈白）；
///   ② 右侧那句「点击ESC，取消操作」。
/// 两个都在 ⇒ 正在蓄力；两个都消失 ⇒ 蓄力结束（冲刺打完了）。
/// 若这次蓄力期间玩家按过 ESC（自己取消的），一次都不动作。
///
/// 结束之后按设置延迟若干毫秒，依次按「先切到别的道具栏」（默认 4）
/// 与「切回原来那栏」（默认 3），用这一次来回切换顶掉后摇。
///
/// 识别方式是对宿主抓到的客户区画面做几何检测 + 多尺度模板匹配（模板来自玩家提供的
/// 1920×1080 游戏截图）；命中后才注入输入，注入前会再次确认游戏仍在前台。
/// 不读内存、不注入游戏进程、不改游戏文件。
///
/// 常驻（<c>AlwaysActive</c>）：开关热键要在局外也按得动，与「九歌自用」保持一致；
/// 而热键默认「未设置」，没录热键时检测就是暂停的。
/// </summary>
[Plugin(
    FaithNoRecoveryOptions.PluginId,
    DisplayName = "信仰无后摇",
    Description = "识别「信仰」蓄力结束（蓄力环 + 「点击ESC，取消操作」双双消失、且不是按 ESC 取消的）后，用道具轮切（先切到别的道具栏再切回原栏）顶掉后摇。",
    Version = "1.0.1",
    AlwaysActive = true)]
public sealed class FaithNoRecoveryPlugin
    : PluginBase, IPluginSettingsProvider, IHandle<SessionStateChangedMessage>
{
    private FaithNoRecoveryMatcher? _matcher;
    private FaithChargeRingDetector? _ringDetector;
    private FaithNoRecoveryEscMonitor? _escMonitor;
    private FaithNoRecoveryRunner? _runner;
    private FaithNoRecoveryOptions _options = new();
    private IGameOverlayToast? _toast;
    private bool _enabled;

    public override string Id => FaithNoRecoveryOptions.PluginId;

    public override string DisplayName => "信仰无后摇";

    public IReadOnlyList<IPluginSetting> Settings { get; } =
    [
        new PluginKeyBindingSetting
        {
            Key = FaithNoRecoveryOptions.ToggleHotkeyKey,
            DisplayName = "开关检测 · 热键（键盘键或鼠标键）",
            Description = "按一下开始检测、再按一下暂停；默认「未设置」——没录之前插件完全不动作（想省掉这一步就打开下面那一项）。",
            DefaultValue = FaithNoRecoveryOptions.NoBindingStorageValue,
            AllowedKinds = PluginInputBindingKinds.All
        },
        new PluginToggleSetting
        {
            Key = FaithNoRecoveryOptions.AutoArmKey,
            DisplayName = "未录热键也自动开始检测",
            Description = "打开后：插件页一开就自动工作（不用先录热键、也不用按一下）；热键只用来临时暂停/恢复。默认关。",
            DefaultValue = false
        },
        new PluginKeyBindingSetting
        {
            Key = FaithNoRecoveryOptions.SwitchToBindingKey,
            DisplayName = "先按的道具栏（切到「别的」栏）",
            Description = "游戏里切到另一个道具栏的键，默认 4 —— 截图里 3 号栏是法杖（信仰）、4 号栏是道具箱。",
            Group = FaithNoRecoveryOptions.ActionGroupTitle,
            DefaultValue = PluginInputBinding.Keyboard(FaithNoRecoveryOptions.DefaultSwitchToVirtualKey).StorageValue,
            AllowedKinds = PluginInputBindingKinds.All
        },
        new PluginKeyBindingSetting
        {
            Key = FaithNoRecoveryOptions.SwitchBackBindingKey,
            DisplayName = "再按的道具栏（切回「原来那栏」）",
            Description = "切回原道具栏的键，默认 3（法杖/信仰那一栏）。",
            Group = FaithNoRecoveryOptions.ActionGroupTitle,
            DefaultValue = PluginInputBinding.Keyboard(FaithNoRecoveryOptions.DefaultSwitchBackVirtualKey).StorageValue,
            AllowedKinds = PluginInputBindingKinds.All
        },
        new PluginSliderSetting
        {
            Key = FaithNoRecoveryOptions.ActionDelayKey,
            DisplayName = "标志消失后延迟（毫秒）",
            Description = "两个标志都消失之后，再等这么久才轮切 —— 就是「冲刺打完 → 后摇开始」那一段。先给 0，实战里微调。",
            Group = FaithNoRecoveryOptions.ActionGroupTitle,
            Minimum = FaithNoRecoveryOptions.ActionDelayMinimum,
            Maximum = FaithNoRecoveryOptions.ActionDelayMaximum,
            StepFrequency = 5,
            DefaultValue = 0
        },
        new PluginSliderSetting
        {
            Key = FaithNoRecoveryOptions.SwitchGapKey,
            DisplayName = "两次切换之间的间隔（毫秒）",
            Description = "先按一下「别的栏」，停这么久，再按「原来那栏」。太大等于没在轮切，太小可能被游戏吃掉一次。",
            Group = FaithNoRecoveryOptions.ActionGroupTitle,
            Minimum = FaithNoRecoveryOptions.SwitchGapMinimum,
            Maximum = FaithNoRecoveryOptions.SwitchGapMaximum,
            StepFrequency = 5,
            DefaultValue = 25
        },
        new PluginSliderSetting
        {
            Key = FaithNoRecoveryOptions.KeyHoldKey,
            DisplayName = "按键保持（毫秒）",
            Description = "每个道具栏按键按下后保持多久再抬起。",
            Group = FaithNoRecoveryOptions.ActionGroupTitle,
            Minimum = FaithNoRecoveryOptions.KeyHoldMinimum,
            Maximum = FaithNoRecoveryOptions.KeyHoldMaximum,
            StepFrequency = 5,
            DefaultValue = 20
        },
        new PluginToggleSetting
        {
            Key = FaithNoRecoveryOptions.ToastOnActionKey,
            DisplayName = "轮切时在游戏内提示一下",
            Description = "默认关；校准时打开，能在画面上看到「确实触发了一次」，不抢焦点。",
            Group = FaithNoRecoveryOptions.ActionGroupTitle,
            DefaultValue = false
        },
        new PluginSliderSetting
        {
            Key = FaithNoRecoveryOptions.MatchThresholdKey,
            DisplayName = "「点击ESC，取消操作」相似度阈值（%）",
            Description = "越高越不容易误判；界面被遮挡、分辨率差异大时调低一点。",
            Group = FaithNoRecoveryOptions.DetectionGroupTitle,
            Minimum = FaithNoRecoveryOptions.ThresholdMinimum,
            Maximum = FaithNoRecoveryOptions.ThresholdMaximum,
            StepFrequency = 1,
            DefaultValue = 72
        },
        new PluginSliderSetting
        {
            Key = FaithNoRecoveryOptions.RingContrastKey,
            DisplayName = "蓄力环亮度对比阈值",
            Description = "蓄力环每个扇区要比环外背景亮多少灰度才算「亮」。实测亮扇区 ≈ 90+、画面其它位置 ≤ 25，默认 40。",
            Group = FaithNoRecoveryOptions.DetectionGroupTitle,
            Minimum = FaithNoRecoveryOptions.RingContrastMinimum,
            Maximum = FaithNoRecoveryOptions.RingContrastMaximum,
            StepFrequency = 1,
            DefaultValue = 40
        },
        new PluginSliderSetting
        {
            Key = FaithNoRecoveryOptions.RingSectorsKey,
            DisplayName = "蓄力环亮扇区数门槛",
            Description = "至少要有几个扇区比背景亮才算「蓄力环在场」。实测未蓄满 5 个、蓄满 10 个，默认 2（很靠前就能认出来）。",
            Group = FaithNoRecoveryOptions.DetectionGroupTitle,
            Minimum = FaithNoRecoveryOptions.RingSectorsMinimum,
            Maximum = FaithNoRecoveryOptions.RingSectorsMaximum,
            StepFrequency = 1,
            DefaultValue = 2
        },
        new PluginToggleSetting
        {
            Key = FaithNoRecoveryOptions.RequireBothGoneKey,
            DisplayName = "要求两个标志都消失才算结束",
            Description = "默认开：蓄力环与取消提示都消失才动作。关掉的话，只要取消提示没了就动作。",
            Group = FaithNoRecoveryOptions.DetectionGroupTitle,
            DefaultValue = true
        },
        new PluginSliderSetting
        {
            Key = FaithNoRecoveryOptions.OtherCueGraceKey,
            DisplayName = "只剩一个标志时的宽限（毫秒）",
            Description = "上一项开着、但两个标志不是同帧消失时，最多再等这么久；等不到也按「已经结束」处理。",
            Group = FaithNoRecoveryOptions.DetectionGroupTitle,
            Minimum = FaithNoRecoveryOptions.OtherCueGraceMinimum,
            Maximum = FaithNoRecoveryOptions.OtherCueGraceMaximum,
            StepFrequency = 50,
            DefaultValue = 400
        },
        new PluginSliderSetting
        {
            Key = FaithNoRecoveryOptions.MinimumChargeKey,
            DisplayName = "蓄力至少持续（毫秒）",
            Description = "两个标志一起出现不足这么久就消失了，按「一闪而过」忽略，避免误触。",
            Group = FaithNoRecoveryOptions.DetectionGroupTitle,
            Minimum = FaithNoRecoveryOptions.MinimumChargeMinimum,
            Maximum = FaithNoRecoveryOptions.MinimumChargeMaximum,
            StepFrequency = 10,
            DefaultValue = 120
        },
        new PluginSliderSetting
        {
            Key = FaithNoRecoveryOptions.PollIntervalKey,
            DisplayName = "检测间隔（毫秒）",
            Description = "多久抓一帧做识别。后摇窗口很短，默认给得比别的插件密；越小越准、越吃性能。",
            Group = FaithNoRecoveryOptions.TimingGroupTitle,
            Minimum = FaithNoRecoveryOptions.PollIntervalMinimum,
            Maximum = FaithNoRecoveryOptions.PollIntervalMaximum,
            StepFrequency = 10,
            DefaultValue = 40
        },
        new PluginSliderSetting
        {
            Key = FaithNoRecoveryOptions.CooldownKey,
            DisplayName = "动作冷却（毫秒）",
            Description = "一次轮切之后这么久内不再轮切，避免连着切。",
            Group = FaithNoRecoveryOptions.TimingGroupTitle,
            Minimum = FaithNoRecoveryOptions.CooldownMinimum,
            Maximum = FaithNoRecoveryOptions.CooldownMaximum,
            StepFrequency = 100,
            DefaultValue = 500
        },
        new PluginSliderSetting
        {
            Key = FaithNoRecoveryOptions.MinimumRandomDelayKey,
            DisplayName = "随机延迟下限（毫秒）",
            Description = "轮切动作完成之后追加的随机等待下限（不参与两键之间的间隔，免得把时机抖没）。",
            Group = FaithNoRecoveryOptions.TimingGroupTitle,
            Minimum = 30,
            MinimumWhenUnsafe = 0,
            Maximum = FaithNoRecoveryOptions.MaximumRandomDelayMillisecondsAllowed,
            StepFrequency = 1,
            DefaultValue = 30
        },
        new PluginSliderSetting
        {
            Key = FaithNoRecoveryOptions.MaximumRandomDelayKey,
            DisplayName = "随机延迟上限（毫秒）",
            Description = "轮切动作完成之后从下限至上限中独立随机取值。",
            Group = FaithNoRecoveryOptions.TimingGroupTitle,
            Minimum = 50,
            MinimumWhenUnsafe = 0,
            Maximum = FaithNoRecoveryOptions.MaximumRandomDelayMillisecondsAllowed,
            StepFrequency = 1,
            DefaultValue = 50
        }
    ];

    public override void OnLoad(IPluginContext context)
    {
        base.OnLoad(context);

        try
        {
            _matcher = new FaithNoRecoveryMatcher();
        }
        catch (Exception exception)
        {
            context.Logger.Error($"信仰无后摇加载识别模板失败，插件不可用：{exception.Message}");
            return;
        }

        var screenshot = context.GetService<IPluginScreenshotService>();
        var gameWindow = context.GetService<IPluginGameWindowService>();
        if (screenshot is null && gameWindow is null)
        {
            context.Logger.Error("宿主没有提供截图能力，信仰无后摇不可用。");
            return;
        }

        _toast = context.GetService<IGameOverlayToast>();
        if (_toast is null)
            context.Logger.Warning("宿主没有提供浮层提示通道，开关检测时屏幕上不会有提示（功能不受影响）。");

        _ringDetector = new FaithChargeRingDetector();
        _escMonitor = new FaithNoRecoveryEscMonitor();
        var grabber = new FaithNoRecoveryFrameGrabber(
            context,
            screenshot,
            gameWindow,
            message => context.Logger.Info(message));
        _runner = new FaithNoRecoveryRunner(grabber, _matcher, _ringDetector, _escMonitor, context.Logger);
        _runner.DetectionStateChanged += OnDetectionStateChanged;
        _runner.ActionPerformed += OnActionPerformed;
        _runner.AttachHotkeys(context.GetService<IPluginInputService>(), Id);
        _runner.SetOptions(_options);
    }

    public override void OnEnable()
    {
        _enabled = true;
        if (_runner is null)
        {
            Context.Logger.Error("信仰无后摇未成功初始化，无法启动检测。");
            return;
        }

        _runner.SetOptions(_options);
        try
        {
            _escMonitor?.Start();
        }
        catch (Exception exception)
        {
            Context.Logger.Error($"ESC 监听启动失败，为避免误判「玩家取消了动作」，本次不启动检测：{exception.Message}");
            return;
        }

        _runner.ResetDetection();
        _runner.Start();
        if (!_runner.DetectionEnabled)
        {
            // 关键：这种状态下插件**不抓帧、不动作**，必须在画面上说清楚，
            // 否则就会变成"我明明打开了，怎么一点反应都没有"。
            Context.Logger.Warning(
                "检测处于暂停：没有开关热键，也没有打开「未录热键也自动开始检测」——插件不抓帧、不动作。");
            try
            {
                _toast?.Warning("信仰无后摇：检测未开始 —— 先录「开关检测」热键并按一下（或打开「未录热键也自动开始检测」）");
            }
            catch (Exception exception)
            {
                Context.Logger.Warning($"发布浮层提示失败：{exception.Message}");
            }
        }

        Context.Logger.Info(
            $"参数：检测间隔 {_options.PollIntervalMilliseconds}ms，文案阈值 {_options.MatchThresholdPercent}%，"
            + $"蓄力环门槛 {_options.RingMinimumSectors} 扇区 / 对比度 {_options.RingContrastThreshold}，"
            + $"两标志都消失才动作 {(_options.RequireBothGone ? "是" : "否")}，"
            + $"标志消失后延迟 {_options.ActionDelayMilliseconds}ms，"
            + $"轮切 {_options.SwitchToBinding.DisplayName} → {_options.SwitchBackBinding.DisplayName}"
            + $"（间隔 {_options.SwitchGapMilliseconds}ms，保持 {_options.KeyHoldMilliseconds}ms），"
            + $"动作冷却 {_options.CooldownMilliseconds}ms，"
            + $"未录热键也自动开始 {(_options.AutoArmWithoutHotkey ? "是" : "否")}。");
    }

    public override void OnDisable()
    {
        _enabled = false;
        _runner?.Stop();
        _escMonitor?.Stop();
    }

    public override void OnUnload()
    {
        _enabled = false;
        if (_runner is not null)
        {
            _runner.DetectionStateChanged -= OnDetectionStateChanged;
            _runner.ActionPerformed -= OnActionPerformed;
        }

        _runner?.Dispose();
        _runner = null;
        _escMonitor?.Dispose();
        _escMonitor = null;
        _ringDetector?.Dispose();
        _ringDetector = null;
        _matcher?.Dispose();
        _matcher = null;
    }

    /// <summary>热键键位在设置页改动后，重新注册到宿主输入通道。</summary>
    public void RefreshHotkeys() => _runner?.RefreshHotkeyBindings();

    public void Handle(SessionStateChangedMessage message) =>
        _runner?.SetMapOpen(message.GameMapOpen);

    /// <summary>热键切换了检测开关：屏幕上来一条游戏内提示，避免「按下去像没反应」。</summary>
    private void OnDetectionStateChanged(bool enabled)
    {
        try
        {
            if (enabled)
                _toast?.Notice("信仰无后摇：检测已开启");
            else
                _toast?.Warning("信仰无后摇：检测已暂停");
        }
        catch (Exception exception)
        {
            Context.Logger.Warning($"发布浮层提示失败：{exception.Message}");
        }
    }

    private void OnActionPerformed(string message)
    {
        try
        {
            _toast?.Notice(message);
        }
        catch (Exception exception)
        {
            Context.Logger.Warning($"发布浮层提示失败：{exception.Message}");
        }
    }

    public object? GetSettingValue(string key) => key switch
    {
        FaithNoRecoveryOptions.ToggleHotkeyKey => _options.ToggleHotkey.StorageValue,
        FaithNoRecoveryOptions.AutoArmKey => _options.AutoArmWithoutHotkey,
        FaithNoRecoveryOptions.SwitchToBindingKey => _options.SwitchToBinding.StorageValue,
        FaithNoRecoveryOptions.SwitchBackBindingKey => _options.SwitchBackBinding.StorageValue,
        FaithNoRecoveryOptions.ActionDelayKey => (double)_options.ActionDelayMilliseconds,
        FaithNoRecoveryOptions.SwitchGapKey => (double)_options.SwitchGapMilliseconds,
        FaithNoRecoveryOptions.KeyHoldKey => (double)_options.KeyHoldMilliseconds,
        FaithNoRecoveryOptions.ToastOnActionKey => _options.ToastOnAction,
        FaithNoRecoveryOptions.MatchThresholdKey => (double)_options.MatchThresholdPercent,
        FaithNoRecoveryOptions.RingContrastKey => (double)_options.RingContrastThreshold,
        FaithNoRecoveryOptions.RingSectorsKey => (double)_options.RingMinimumSectors,
        FaithNoRecoveryOptions.RequireBothGoneKey => _options.RequireBothGone,
        FaithNoRecoveryOptions.OtherCueGraceKey => (double)_options.OtherCueGraceMilliseconds,
        FaithNoRecoveryOptions.MinimumChargeKey => (double)_options.MinimumChargeMilliseconds,
        FaithNoRecoveryOptions.PollIntervalKey => (double)_options.PollIntervalMilliseconds,
        FaithNoRecoveryOptions.CooldownKey => (double)_options.CooldownMilliseconds,
        FaithNoRecoveryOptions.MinimumRandomDelayKey => (double)_options.MinimumRandomDelayMilliseconds,
        FaithNoRecoveryOptions.MaximumRandomDelayKey => (double)_options.MaximumRandomDelayMilliseconds,
        _ => null
    };

    public void SetSettingValue(string key, object? value)
    {
        var updated = BuildOptions(key, value);
        if (ReferenceEquals(updated, _options))
            return;

        var hotkeyChanged = !Equals(updated.ToggleHotkey, _options.ToggleHotkey);
        var autoArmChanged = updated.AutoArmWithoutHotkey != _options.AutoArmWithoutHotkey;
        _options = updated;
        if (!_enabled)
            return;

        _runner?.SetOptions(_options);
        if (hotkeyChanged)
        {
            // 刚录上第一个热键：注册它，但检测仍保持暂停，等玩家自己按一下开启。
            _runner?.RefreshHotkeyBindings();
        }

        if (autoArmChanged)
            _runner?.ReevaluateArming();
    }

    private FaithNoRecoveryOptions BuildOptions(string key, object? value)
    {
        switch (key)
        {
            case FaithNoRecoveryOptions.ToggleHotkeyKey when value is string hotkey:
                // 「none」= 用户点了重置：允许把热键清成「未设置」，之后不会再注册任何绑定。
                if (string.Equals(hotkey, FaithNoRecoveryOptions.NoBindingStorageValue, StringComparison.Ordinal))
                    return _options with { ToggleHotkey = new PluginInputBinding() };
                return TryParseHotkey(hotkey, out var parsedHotkey)
                    ? _options with { ToggleHotkey = parsedHotkey }
                    : _options;
            case FaithNoRecoveryOptions.AutoArmKey when value is bool autoArm:
                return _options with { AutoArmWithoutHotkey = autoArm };
            case FaithNoRecoveryOptions.SwitchToBindingKey when value is string switchTo:
                return TryParseBinding(switchTo, out var parsedSwitchTo)
                    ? _options with { SwitchToBinding = parsedSwitchTo }
                    : _options;
            case FaithNoRecoveryOptions.SwitchBackBindingKey when value is string switchBack:
                return TryParseBinding(switchBack, out var parsedSwitchBack)
                    ? _options with { SwitchBackBinding = parsedSwitchBack }
                    : _options;
            case FaithNoRecoveryOptions.ActionDelayKey:
                return _options with
                {
                    ActionDelayMilliseconds = FaithNoRecoveryOptions.FromDouble(value,
                        _options.ActionDelayMilliseconds,
                        FaithNoRecoveryOptions.ActionDelayMinimum,
                        FaithNoRecoveryOptions.ActionDelayMaximum)
                };
            case FaithNoRecoveryOptions.SwitchGapKey:
                return _options with
                {
                    SwitchGapMilliseconds = FaithNoRecoveryOptions.FromDouble(value,
                        _options.SwitchGapMilliseconds,
                        FaithNoRecoveryOptions.SwitchGapMinimum,
                        FaithNoRecoveryOptions.SwitchGapMaximum)
                };
            case FaithNoRecoveryOptions.KeyHoldKey:
                return _options with
                {
                    KeyHoldMilliseconds = FaithNoRecoveryOptions.FromDouble(value,
                        _options.KeyHoldMilliseconds,
                        FaithNoRecoveryOptions.KeyHoldMinimum,
                        FaithNoRecoveryOptions.KeyHoldMaximum)
                };
            case FaithNoRecoveryOptions.ToastOnActionKey when value is bool toast:
                return _options with { ToastOnAction = toast };
            case FaithNoRecoveryOptions.MatchThresholdKey:
                return _options with
                {
                    MatchThresholdPercent = FaithNoRecoveryOptions.FromDouble(value,
                        _options.MatchThresholdPercent,
                        FaithNoRecoveryOptions.ThresholdMinimum,
                        FaithNoRecoveryOptions.ThresholdMaximum)
                };
            case FaithNoRecoveryOptions.RingContrastKey:
                return _options with
                {
                    RingContrastThreshold = FaithNoRecoveryOptions.FromDouble(value,
                        _options.RingContrastThreshold,
                        FaithNoRecoveryOptions.RingContrastMinimum,
                        FaithNoRecoveryOptions.RingContrastMaximum)
                };
            case FaithNoRecoveryOptions.RingSectorsKey:
                return _options with
                {
                    RingMinimumSectors = FaithNoRecoveryOptions.FromDouble(value,
                        _options.RingMinimumSectors,
                        FaithNoRecoveryOptions.RingSectorsMinimum,
                        FaithNoRecoveryOptions.RingSectorsMaximum)
                };
            case FaithNoRecoveryOptions.RequireBothGoneKey when value is bool requireBoth:
                return _options with { RequireBothGone = requireBoth };
            case FaithNoRecoveryOptions.OtherCueGraceKey:
                return _options with
                {
                    OtherCueGraceMilliseconds = FaithNoRecoveryOptions.FromDouble(value,
                        _options.OtherCueGraceMilliseconds,
                        FaithNoRecoveryOptions.OtherCueGraceMinimum,
                        FaithNoRecoveryOptions.OtherCueGraceMaximum)
                };
            case FaithNoRecoveryOptions.MinimumChargeKey:
                return _options with
                {
                    MinimumChargeMilliseconds = FaithNoRecoveryOptions.FromDouble(value,
                        _options.MinimumChargeMilliseconds,
                        FaithNoRecoveryOptions.MinimumChargeMinimum,
                        FaithNoRecoveryOptions.MinimumChargeMaximum)
                };
            case FaithNoRecoveryOptions.PollIntervalKey:
                return _options with
                {
                    PollIntervalMilliseconds = FaithNoRecoveryOptions.FromDouble(value,
                        _options.PollIntervalMilliseconds,
                        FaithNoRecoveryOptions.PollIntervalMinimum,
                        FaithNoRecoveryOptions.PollIntervalMaximum)
                };
            case FaithNoRecoveryOptions.CooldownKey:
                return _options with
                {
                    CooldownMilliseconds = FaithNoRecoveryOptions.FromDouble(value,
                        _options.CooldownMilliseconds,
                        FaithNoRecoveryOptions.CooldownMinimum,
                        FaithNoRecoveryOptions.CooldownMaximum)
                };
            case FaithNoRecoveryOptions.MinimumRandomDelayKey:
                return _options with
                {
                    MinimumRandomDelayMilliseconds = FaithNoRecoveryOptions.FromDouble(value,
                        _options.MinimumRandomDelayMilliseconds, PluginRandomDelayPolicy.GetMinimum(30),
                        FaithNoRecoveryOptions.MaximumRandomDelayMillisecondsAllowed)
                };
            case FaithNoRecoveryOptions.MaximumRandomDelayKey:
                return _options with
                {
                    MaximumRandomDelayMilliseconds = FaithNoRecoveryOptions.FromDouble(value,
                        _options.MaximumRandomDelayMilliseconds, PluginRandomDelayPolicy.GetMinimum(50),
                        FaithNoRecoveryOptions.MaximumRandomDelayMillisecondsAllowed)
                };
            default:
                return _options;
        }
    }

    private static bool TryParseBinding(string text, out PluginInputBinding binding) =>
        PluginInputBinding.TryParse(text, PluginInputBindingKinds.All, out binding);

    /// <summary>热键只接受「单一按键」：组合键无法在钩子里可靠地当触发器，直接拒绝。</summary>
    private static bool TryParseHotkey(string text, out PluginInputBinding hotkey)
    {
        hotkey = new PluginInputBinding();
        return PluginInputBinding.TryParse(text, PluginInputBindingKinds.All, out var parsed)
            && FaithNoRecoveryOptions.TryAsHotkey(parsed, out hotkey);
    }
}
