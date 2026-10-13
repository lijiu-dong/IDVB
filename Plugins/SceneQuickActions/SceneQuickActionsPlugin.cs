using IDVBuff.Core.Contracts;
using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Plugins.SceneQuickActions;

/// <summary>
/// 背包辅助：把《第五人格》里两个反复出现的界面操作自动化。
///
/// 自动接受邀请 —— 有人邀请你牵手（右上角出现「××邀请你牵手」+「拒绝 / 接受」）：
///   切出鼠标 → 移到「接受」→ 左键点击。
/// 拾取相关 —— 出现「可拾取」面板（右上角列出物品，下方有「拾取全部」提示）：
///   先「拾取全部」→ 再「快捷替换」；这两步各自可以用按键（可配置）或鼠标点击完成。
///
/// 识别方式是对宿主抓到的客户区画面做多尺度模板匹配，模板来自用户提供的
/// 1920×1080 游戏截图；命中后才注入输入，注入前会再次确认游戏仍在前台。
///
/// 两条「别打扰我」的护栏：
///   ① 背包（鼠标切出）状态 —— 你自己按 Tab 开背包整理 / 丢东西时，
///      「拾取相关」不动作；两个拖拽热键也不再重复按 Tab 把背包关掉；
///   ② 总开关热键 —— 按一下停用整个插件（不抓帧、不自动动作、热键也不响应），再按一下恢复。
/// </summary>
[Plugin(
    "scene-quick-actions",
    DisplayName = "背包辅助",
    Description = "自动识别「邀请你牵手」与「可拾取」两个界面，执行接受邀请、拾取全部、快捷替换；背包开着时不拾取，另带总开关热键。",
    Version = "1.8.0",
    AlwaysActive = true)]
public sealed class SceneQuickActionsPlugin
    : PluginBase, IPluginSettingsProvider, IHandle<SessionStateChangedMessage>
{
    private SceneQuickActionsMatcher? _matcher;
    private SceneQuickActionsRunner? _runner;
    private SceneQuickActionsOptions _options = new();
    private IGameOverlayToast? _toast;
    private bool _enabled;

    public override string Id => "scene-quick-actions";

    public override string DisplayName => "背包辅助";

    public IReadOnlyList<IPluginSetting> Settings { get; } =
    [
        new PluginToggleSetting
        {
            Key = SceneQuickActionsOptions.InviteEnabledKey,
            DisplayName = "自动接受邀请",
            Description = "识别到「接受」按钮时，自动切出鼠标并点击它。",
            DefaultValue = true
        },
        new PluginToggleSetting
        {
            Key = SceneQuickActionsOptions.PickupEnabledKey,
            DisplayName = "拾取相关",
            Description = "识别到「可拾取」面板时，先「拾取全部」，再「快捷替换」。背包开着的时候这一项不动作。",
            DefaultValue = true
        },
        new PluginKeyBindingSetting
        {
            Key = SceneQuickActionsOptions.MasterHotkeyKey,
            DisplayName = "总开关 · 热键（键盘键或鼠标键）",
            Description = "按一下停用整个插件（不抓帧、不自动动作，两个拖拽热键也暂不响应），再按一下恢复；默认「未设置」，点右边按钮录一个键即可（不支持组合键）。",
            DefaultValue = SceneQuickActionsOptions.NoBindingStorageValue,
            AllowedKinds = PluginInputBindingKinds.All
        },
        new PluginKeyBindingSetting
        {
            Key = SceneQuickActionsOptions.DropBagHotkeyKey,
            DisplayName = "丢光背包 · 热键（键盘键或鼠标键）",
            Description = "按下后先切出鼠标（背包已经开着就直接用，不重复按 Tab），再把上排 3 格、中排 3 格依次拖到屏幕正中心松手。点右边按钮即可录制新键；不支持组合键。",
            DefaultValue = PluginInputBinding.Keyboard(SceneQuickActionsOptions.DefaultDropBagVirtualKey).StorageValue,
            AllowedKinds = PluginInputBindingKinds.All
        },
        new PluginKeyBindingSetting
        {
            Key = SceneQuickActionsOptions.DropHotbarHotkeyKey,
            DisplayName = "逐个丢道具栏 · 热键（键盘键或鼠标键）",
            Description = "每按一次，先切出鼠标，再把底部道具栏的下一个格子拖到屏幕正中心松手：第 1 次拖第 1 格，第 2 次拖第 2 格，依次循环。点右边按钮即可录制新键；不支持组合键。",
            DefaultValue = PluginInputBinding.Keyboard(SceneQuickActionsOptions.DefaultDropHotbarVirtualKey).StorageValue,
            AllowedKinds = PluginInputBindingKinds.All
        },
        new PluginKeyBindingSetting
        {
            Key = SceneQuickActionsOptions.PickupAllBindingKey,
            DisplayName = "拾取全部按键",
            Description = "游戏里「拾取全部」提示上显示的按键（默认 B 键）。点「重置按键」清空后，改为用鼠标点击该提示。",
            Group = SceneQuickActionsOptions.KeyGroupTitle,
            DefaultValue = PluginInputBinding.Keyboard(SceneQuickActionsOptions.DefaultPickupAllVirtualKey).StorageValue,
            AllowedKinds = PluginInputBindingKinds.All
        },
        new PluginKeyBindingSetting
        {
            Key = SceneQuickActionsOptions.QuickReplaceBindingKey,
            DisplayName = "快捷替换按键",
            Description = "若你的游戏里「快捷替换」也有按键提示，就在这里录一个；保持「未设置」时用鼠标点击该按钮。",
            Group = SceneQuickActionsOptions.KeyGroupTitle,
            DefaultValue = SceneQuickActionsOptions.NoBindingStorageValue,
            AllowedKinds = PluginInputBindingKinds.All
        },
        new PluginToggleSetting
        {
            Key = SceneQuickActionsOptions.PickupWakeKey,
            DisplayName = "拾取相关动作前先切出鼠标",
            Description = "两步都用按键时不需要。打开后同样用「按 Tab 开背包」切出指针，动作完自动按 Tab 关上。",
            Group = SceneQuickActionsOptions.KeyGroupTitle,
            DefaultValue = false
        },
        new PluginKeyBindingSetting
        {
            Key = SceneQuickActionsOptions.BackpackKeyKey,
            DisplayName = "开背包 / 切出鼠标 · 按键",
            Description = "游戏里打开背包、把鼠标指针切出来的那颗键（默认 Tab）。本插件用它判断「背包是不是开着」：开着的时候不拾取，拖拽也不再重复按 Tab。",
            Group = SceneQuickActionsOptions.BackpackGroupTitle,
            DefaultValue = PluginInputBinding.Keyboard(SceneQuickActionsOptions.DefaultBackpackVirtualKey).StorageValue,
            AllowedKinds = PluginInputBindingKinds.All
        },
        new PluginChoiceSetting
        {
            Key = SceneQuickActionsOptions.BackpackModeKey,
            DisplayName = "背包打开时怎么判断",
            Description = "「按开背包键判断」= 按上面那颗键算打开、再按一次算关闭；「按键 + 指针」= 游戏切出鼠标指针也算打开（更保守，但如果你平时也能看到指针就别用）；「关」= 不做这个判断。",
            Group = SceneQuickActionsOptions.BackpackGroupTitle,
            Options = [.. SceneQuickActionsOptions.BackpackModeOptions],
            DefaultIndex = 0
        },
        new PluginSliderSetting
        {
            Key = SceneQuickActionsOptions.BackpackMaxHoldKey,
            DisplayName = "背包打开状态最长保持（秒）",
            Description = "背包状态一直停在「打开」且这么久没有任何新证据（没按 Tab、指针也没切出）时自动解除，免得漏了一次按键就再也不拾取。",
            Group = SceneQuickActionsOptions.BackpackGroupTitle,
            Minimum = SceneQuickActionsOptions.BackpackMaxHoldMinimum,
            Maximum = SceneQuickActionsOptions.BackpackMaxHoldMaximum,
            StepFrequency = 10,
            DefaultValue = 120
        },
        new PluginSliderSetting
        {
            Key = SceneQuickActionsOptions.WakeDelayKey,
            DisplayName = "切出鼠标后等待（毫秒）",
            Description = "按 Tab 打开背包后，等指针真正出现再移动点击（内部下限 120ms，避免背包还没开就开始点）。",
            Group = SceneQuickActionsOptions.TimingGroupTitle,
            Minimum = SceneQuickActionsOptions.WakeDelayMinimum,
            Maximum = SceneQuickActionsOptions.WakeDelayMaximum,
            StepFrequency = 10,
            DefaultValue = 150
        },
        new PluginSliderSetting
        {
            Key = SceneQuickActionsOptions.BetweenClicksKey,
            DisplayName = "两次动作间隔（毫秒）",
            Description = "「拾取全部」与「快捷替换」之间的等待时间。",
            Group = SceneQuickActionsOptions.TimingGroupTitle,
            Minimum = SceneQuickActionsOptions.BetweenClicksMinimum,
            Maximum = SceneQuickActionsOptions.BetweenClicksMaximum,
            StepFrequency = 20,
            DefaultValue = 260
        },
        new PluginSliderSetting
        {
            Key = SceneQuickActionsOptions.PollIntervalKey,
            DisplayName = "检测间隔（毫秒）",
            Description = "多久抓一帧做识别；只在游戏处于前台时才会真正抓帧。",
            Group = SceneQuickActionsOptions.TimingGroupTitle,
            Minimum = SceneQuickActionsOptions.PollIntervalMinimum,
            Maximum = SceneQuickActionsOptions.PollIntervalMaximum,
            StepFrequency = 50,
            DefaultValue = 400
        },
        new PluginSliderSetting
        {
            Key = SceneQuickActionsOptions.ThresholdKey,
            DisplayName = "识别相似度阈值（%）",
            Description = "越高越不容易误判，但界面被遮挡或分辨率差异大时可能识别不到。",
            Group = SceneQuickActionsOptions.TimingGroupTitle,
            Minimum = SceneQuickActionsOptions.ThresholdMinimum,
            Maximum = SceneQuickActionsOptions.ThresholdMaximum,
            StepFrequency = 1,
            DefaultValue = 80
        },
        new PluginSliderSetting
        {
            Key = SceneQuickActionsOptions.CooldownKey,
            DisplayName = "同一场景动作冷却（毫秒）",
            Description = "同一场景两次动作之间的最小间隔，避免连续重复点击。",
            Group = SceneQuickActionsOptions.TimingGroupTitle,
            Minimum = SceneQuickActionsOptions.CooldownMinimum,
            Maximum = SceneQuickActionsOptions.CooldownMaximum,
            StepFrequency = 100,
            DefaultValue = 1500
        },
        new PluginSliderSetting
        {
            Key = SceneQuickActionsOptions.DropSilenceKey,
            DisplayName = "丢东西后暂停拾取（秒）",
            Description = "按上面两个热键之后，这段时间内「拾取相关」不再动作，免得把自己刚丢出来的东西又捡回去。",
            Group = SceneQuickActionsOptions.TimingGroupTitle,
            Minimum = SceneQuickActionsOptions.DropSilenceMinimum,
            Maximum = SceneQuickActionsOptions.DropSilenceMaximum,
            StepFrequency = 5,
            DefaultValue = 30
        },
        new PluginSliderSetting
        {
            Key = SceneQuickActionsOptions.MinimumRandomDelayKey,
            DisplayName = "随机延迟下限（毫秒）",
            Description = "每段开关背包、移动、按键、点击及拖拽操作后追加的随机等待下限。",
            Group = SceneQuickActionsOptions.TimingGroupTitle,
            Minimum = 30,
            MinimumWhenUnsafe = 0,
            Maximum = SceneQuickActionsOptions.MaximumRandomDelayMillisecondsAllowed,
            StepFrequency = 1,
            DefaultValue = 30
        },
        new PluginSliderSetting
        {
            Key = SceneQuickActionsOptions.MaximumRandomDelayKey,
            DisplayName = "随机延迟上限（毫秒）",
            Description = "每段操作后从下限至上限中独立随机取值，追加在原有等待之后。",
            Group = SceneQuickActionsOptions.TimingGroupTitle,
            Minimum = 50,
            MinimumWhenUnsafe = 0,
            Maximum = SceneQuickActionsOptions.MaximumRandomDelayMillisecondsAllowed,
            StepFrequency = 1,
            DefaultValue = 50
        },
        new PluginChoiceSetting
        {
            Key = SceneQuickActionsOptions.DropSpeedKey,
            DisplayName = "拖拽速度",
            Description = "两个热键拖动时的快慢。嫌慢选「快」，如果丢不出去（拖拽太快游戏没吃到）就换「慢（稳妥）」。",
            Group = SceneQuickActionsOptions.TimingGroupTitle,
            Options = [.. SceneQuickActionsOptions.DropSpeedOptions],
            DefaultIndex = 0
        }
    ];

    public override void OnLoad(IPluginContext context)
    {
        base.OnLoad(context);

        try
        {
            _matcher = new SceneQuickActionsMatcher();
        }
        catch (Exception exception)
        {
            context.Logger.Error($"背包辅助加载识别模板失败，插件不可用：{exception.Message}");
            return;
        }

        var screenshot = context.GetService<IPluginScreenshotService>();
        var gameWindow = context.GetService<IPluginGameWindowService>();
        if (screenshot is null && gameWindow is null)
        {
            context.Logger.Error("宿主没有提供截图能力，背包辅助不可用。");
            return;
        }

        var grabber = new GameFrameGrabber(
            context,
            screenshot,
            gameWindow,
            message => context.Logger.Info(message));
        _toast = context.GetService<IGameOverlayToast>();
        if (_toast is null)
            context.Logger.Warning("宿主没有提供浮层提示通道，切换总开关时屏幕上不会有提示（功能不受影响）。");

        _runner = new SceneQuickActionsRunner(grabber, _matcher, context.Logger);
        _runner.AutomaticStateChanged += OnAutomaticStateChanged;
        _runner.AttachHotkeys(
            context.GetService<IPluginInputService>(),
            Id);
        _runner.SetOptions(_options);
    }

    public override void OnEnable()
    {
        _enabled = true;
        if (_runner is null)
        {
            Context.Logger.Error("背包辅助未成功初始化，无法启动检测。");
            return;
        }

        _runner.SetOptions(_options);
        _runner.ResetAutomatic();
        _runner.Start();

        var backpackText = _options.BackpackMode == SceneQuickActionsBackpackMode.Off
            ? "背包判断 关（不做背包判断）"
            : "背包判断 " + SceneQuickActionsOptions.ToBackpackModeOption(_options.BackpackMode)
              + $"（「{_options.BackpackKey.DisplayName}」开背包算打开，最长保持 {_options.BackpackMaxHoldSeconds} 秒）";
        Context.Logger.Info(
            $"参数：检测间隔 {_options.PollIntervalMilliseconds}ms，阈值 {_options.MatchThresholdPercent}%，"
            + $"自动接受邀请 {(_options.InviteEnabled ? "开" : "关")}，拾取相关 {(_options.PickupEnabled ? "开" : "关")}；"
            + "切出鼠标 按 Tab 开背包（背包已经开着就直接用）；"
            + $"拾取全部 {_options.PickupAllBinding.DisplayName}，"
            + $"快捷替换 {_options.QuickReplaceBinding.DisplayName}；"
            + $"总开关热键 {(_options.MasterHotkey.IsConfigured ? _options.MasterHotkey.DisplayName : "未设置")}；"
            + backpackText + "。");
    }

    public override void OnDisable()
    {
        _enabled = false;
        _runner?.Stop();
    }

    public override void OnUnload()
    {
        _enabled = false;
        if (_runner is not null)
            _runner.AutomaticStateChanged -= OnAutomaticStateChanged;
        _runner?.Dispose();
        _runner = null;
        _matcher?.Dispose();
        _matcher = null;
    }

    /// <summary>热键换了总开关：屏幕上来一条游戏内提示，避免「按下去像没反应」。</summary>
    private void OnAutomaticStateChanged(bool enabled)
    {
        try
        {
            if (enabled)
                _toast?.Notice("背包辅助：已启用");
            else
                _toast?.Warning("背包辅助：已停用");
        }
        catch (Exception exception)
        {
            Context.Logger.Warning($"发布浮层提示失败：{exception.Message}");
        }
    }

    /// <summary>热键键位在设置页改动后，重新注册到宿主输入通道。</summary>
    public void RefreshHotkeys() => _runner?.RefreshHotkeyBindings();

    public void Handle(SessionStateChangedMessage message) =>
        _runner?.SetMapOpen(message.GameMapOpen);

    public object? GetSettingValue(string key) => key switch
    {
        SceneQuickActionsOptions.InviteEnabledKey => _options.InviteEnabled,
        SceneQuickActionsOptions.WakeDelayKey => (double)_options.WakeDelayMilliseconds,
        SceneQuickActionsOptions.PickupEnabledKey => _options.PickupEnabled,
        SceneQuickActionsOptions.MasterHotkeyKey => _options.MasterHotkey.StorageValue,
        SceneQuickActionsOptions.BackpackKeyKey => _options.BackpackKey.StorageValue,
        SceneQuickActionsOptions.BackpackModeKey =>
            SceneQuickActionsOptions.ToBackpackModeOption(_options.BackpackMode),
        SceneQuickActionsOptions.BackpackMaxHoldKey => (double)_options.BackpackMaxHoldSeconds,
        SceneQuickActionsOptions.PickupAllBindingKey => _options.PickupAllBinding.StorageValue,
        SceneQuickActionsOptions.QuickReplaceBindingKey => _options.QuickReplaceBinding.StorageValue,
        SceneQuickActionsOptions.BetweenClicksKey => (double)_options.BetweenClicksMilliseconds,
        SceneQuickActionsOptions.PickupWakeKey => _options.PickupWakeMouse,
        SceneQuickActionsOptions.PollIntervalKey => (double)_options.PollIntervalMilliseconds,
        SceneQuickActionsOptions.ThresholdKey => (double)_options.MatchThresholdPercent,
        SceneQuickActionsOptions.CooldownKey => (double)_options.CooldownMilliseconds,
        SceneQuickActionsOptions.MinimumRandomDelayKey => (double)_options.MinimumRandomDelayMilliseconds,
        SceneQuickActionsOptions.MaximumRandomDelayKey => (double)_options.MaximumRandomDelayMilliseconds,
        SceneQuickActionsOptions.DropBagHotkeyKey => _options.DropBagHotkey.StorageValue,
        SceneQuickActionsOptions.DropHotbarHotkeyKey => _options.DropHotbarHotkey.StorageValue,
        SceneQuickActionsOptions.DropSilenceKey => (double)_options.DropSilenceSeconds,
        SceneQuickActionsOptions.DropSpeedKey =>
            SceneQuickActionsOptions.ToDropSpeedOption(_options.DropSpeedIndex),
        _ => null
    };

    public void SetSettingValue(string key, object? value)
    {
        var updated = BuildOptions(key, value);
        if (ReferenceEquals(updated, _options))
            return;

        _options = updated;
        if (_enabled)
        {
            _runner?.SetOptions(_options);
            _runner?.RefreshHotkeyBindings();
        }
    }

    private SceneQuickActionsOptions BuildOptions(string key, object? value)
    {
        switch (key)
        {
            case SceneQuickActionsOptions.InviteEnabledKey when value is bool inviteEnabled:
                return _options with { InviteEnabled = inviteEnabled };
            case SceneQuickActionsOptions.WakeDelayKey:
                return _options with
                {
                    WakeDelayMilliseconds = SceneQuickActionsOptions.FromDouble(
                        value,
                        _options.WakeDelayMilliseconds,
                        SceneQuickActionsOptions.WakeDelayMinimum,
                        SceneQuickActionsOptions.WakeDelayMaximum)
                };
            case SceneQuickActionsOptions.PickupEnabledKey when value is bool pickupEnabled:
                return _options with { PickupEnabled = pickupEnabled };
            case SceneQuickActionsOptions.MasterHotkeyKey when value is string masterHotkey:
                // 「none」= 用户点了重置：允许把热键清成「未设置」，之后不再注册任何绑定。
                if (string.Equals(masterHotkey, SceneQuickActionsOptions.NoBindingStorageValue, StringComparison.Ordinal))
                    return _options with { MasterHotkey = new PluginInputBinding() };
                return TryParseHotkey(masterHotkey, out var parsedMaster)
                    ? _options with { MasterHotkey = parsedMaster }
                    : _options;
            case SceneQuickActionsOptions.BackpackKeyKey when value is string backpackKey:
                if (string.Equals(backpackKey, SceneQuickActionsOptions.NoBindingStorageValue, StringComparison.Ordinal))
                    return _options with { BackpackKey = new PluginInputBinding() };
                return TryParseBinding(backpackKey, out var parsedBackpack)
                    ? _options with { BackpackKey = parsedBackpack }
                    : _options;
            case SceneQuickActionsOptions.BackpackModeKey when value is string backpackMode:
                return _options with { BackpackMode = SceneQuickActionsOptions.ParseBackpackMode(backpackMode) };
            case SceneQuickActionsOptions.BackpackMaxHoldKey:
                return _options with
                {
                    BackpackMaxHoldSeconds = SceneQuickActionsOptions.FromDouble(
                        value,
                        _options.BackpackMaxHoldSeconds,
                        SceneQuickActionsOptions.BackpackMaxHoldMinimum,
                        SceneQuickActionsOptions.BackpackMaxHoldMaximum)
                };
            case SceneQuickActionsOptions.PickupAllBindingKey when value is string pickupBinding:
                return TryParseBinding(pickupBinding, out var parsedPickup)
                    ? _options with { PickupAllBinding = parsedPickup }
                    : _options;
            case SceneQuickActionsOptions.QuickReplaceBindingKey when value is string replaceBinding:
                return TryParseBinding(replaceBinding, out var parsedReplace)
                    ? _options with { QuickReplaceBinding = parsedReplace }
                    : _options;
            case SceneQuickActionsOptions.BetweenClicksKey:
                return _options with
                {
                    BetweenClicksMilliseconds = SceneQuickActionsOptions.FromDouble(
                        value,
                        _options.BetweenClicksMilliseconds,
                        SceneQuickActionsOptions.BetweenClicksMinimum,
                        SceneQuickActionsOptions.BetweenClicksMaximum)
                };
            case SceneQuickActionsOptions.PickupWakeKey when value is bool pickupWake:
                return _options with { PickupWakeMouse = pickupWake };
            case SceneQuickActionsOptions.PollIntervalKey:
                return _options with
                {
                    PollIntervalMilliseconds = SceneQuickActionsOptions.FromDouble(
                        value,
                        _options.PollIntervalMilliseconds,
                        SceneQuickActionsOptions.PollIntervalMinimum,
                        SceneQuickActionsOptions.PollIntervalMaximum)
                };
            case SceneQuickActionsOptions.ThresholdKey:
                return _options with
                {
                    MatchThresholdPercent = SceneQuickActionsOptions.FromDouble(
                        value,
                        _options.MatchThresholdPercent,
                        SceneQuickActionsOptions.ThresholdMinimum,
                        SceneQuickActionsOptions.ThresholdMaximum)
                };
            case SceneQuickActionsOptions.CooldownKey:
                return _options with
                {
                    CooldownMilliseconds = SceneQuickActionsOptions.FromDouble(
                        value,
                        _options.CooldownMilliseconds,
                        SceneQuickActionsOptions.CooldownMinimum,
                        SceneQuickActionsOptions.CooldownMaximum)
                };
            case SceneQuickActionsOptions.DropBagHotkeyKey when value is string bagHotkey:
                return TryParseHotkey(bagHotkey, out var parsedBag)
                    ? _options with { DropBagHotkey = parsedBag }
                    : _options;
            case SceneQuickActionsOptions.DropHotbarHotkeyKey when value is string hotbarHotkey:
                return TryParseHotkey(hotbarHotkey, out var parsedHotbar)
                    ? _options with { DropHotbarHotkey = parsedHotbar }
                    : _options;
            case SceneQuickActionsOptions.DropSilenceKey:
                return _options with
                {
                    DropSilenceSeconds = SceneQuickActionsOptions.FromDouble(
                        value,
                        _options.DropSilenceSeconds,
                        SceneQuickActionsOptions.DropSilenceMinimum,
                        SceneQuickActionsOptions.DropSilenceMaximum)
                };
            case SceneQuickActionsOptions.DropSpeedKey when value is string dropSpeed:
                return _options with { DropSpeedIndex = SceneQuickActionsOptions.ParseDropSpeed(dropSpeed) };
            case SceneQuickActionsOptions.MinimumRandomDelayKey:
                return _options with
                {
                    MinimumRandomDelayMilliseconds = SceneQuickActionsOptions.FromDouble(value,
                        _options.MinimumRandomDelayMilliseconds, PluginRandomDelayPolicy.GetMinimum(30),
                        SceneQuickActionsOptions.MaximumRandomDelayMillisecondsAllowed)
                };
            case SceneQuickActionsOptions.MaximumRandomDelayKey:
                return _options with
                {
                    MaximumRandomDelayMilliseconds = SceneQuickActionsOptions.FromDouble(value,
                        _options.MaximumRandomDelayMilliseconds, PluginRandomDelayPolicy.GetMinimum(50),
                        SceneQuickActionsOptions.MaximumRandomDelayMillisecondsAllowed)
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
            && SceneQuickActionsOptions.TryAsHotkey(parsed, out hotkey);
    }
}
