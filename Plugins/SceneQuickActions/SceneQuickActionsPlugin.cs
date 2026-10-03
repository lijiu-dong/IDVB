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
/// </summary>
[Plugin(
    "scene-quick-actions",
    DisplayName = "背包辅助",
    Description = "自动识别「邀请你牵手」与「可拾取」两个界面，执行接受邀请、拾取全部、快捷替换。",
    Version = "1.3.0",
    AlwaysActive = true)]
public sealed class SceneQuickActionsPlugin
    : PluginBase, IPluginSettingsProvider, IHandle<SessionStateChangedMessage>
{
    private SceneQuickActionsMatcher? _matcher;
    private SceneQuickActionsRunner? _runner;
    private SceneQuickActionsOptions _options = new();
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
            Description = "识别到「可拾取」面板时，先「拾取全部」，再「快捷替换」。",
            DefaultValue = true
        },
        new PluginKeyBindingSetting
        {
            Key = SceneQuickActionsOptions.MouseWakeBindingKey,
            DisplayName = "切出鼠标按键",
            Description = "唤醒游戏鼠标指针的按键（默认鼠标侧键 1，与「启用鼠标按键」一致）。",
            Group = SceneQuickActionsOptions.KeyGroupTitle,
            DefaultValue = PluginInputBinding.Mouse(PluginMouseButton.XButton1).StorageValue,
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
            Description = "两步都用按键时不需要；只有用鼠标点击「快捷替换」且指针没切出时才打开。",
            Group = SceneQuickActionsOptions.KeyGroupTitle,
            DefaultValue = false
        },
        new PluginSliderSetting
        {
            Key = SceneQuickActionsOptions.WakeDelayKey,
            DisplayName = "切出鼠标后等待（毫秒）",
            Description = "按下切出键后，等指针真正出现再移动点击。",
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
        _runner = new SceneQuickActionsRunner(grabber, _matcher, context.Logger);
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
        _runner.Start();
        Context.Logger.Info(
            $"参数：检测间隔 {_options.PollIntervalMilliseconds}ms，阈值 {_options.MatchThresholdPercent}%，"
            + $"自动接受邀请 {(_options.InviteEnabled ? "开" : "关")}，拾取相关 {(_options.PickupEnabled ? "开" : "关")}；"
            + $"切出鼠标 {_options.MouseWakeBinding.DisplayName}（每次动作前注入一次）；"
            + $"拾取全部 {_options.PickupAllBinding.DisplayName}，"
            + $"快捷替换 {_options.QuickReplaceBinding.DisplayName}。");
    }

    public override void OnDisable()
    {
        _enabled = false;
        _runner?.Stop();
    }

    public override void OnUnload()
    {
        _enabled = false;
        _runner?.Dispose();
        _runner = null;
        _matcher?.Dispose();
        _matcher = null;
    }

    public void Handle(SessionStateChangedMessage message) =>
        _runner?.SetMapOpen(message.GameMapOpen);

    public object? GetSettingValue(string key) => key switch
    {
        SceneQuickActionsOptions.InviteEnabledKey => _options.InviteEnabled,
        SceneQuickActionsOptions.MouseWakeBindingKey => _options.MouseWakeBinding.StorageValue,
        SceneQuickActionsOptions.WakeDelayKey => (double)_options.WakeDelayMilliseconds,
        SceneQuickActionsOptions.PickupEnabledKey => _options.PickupEnabled,
        SceneQuickActionsOptions.PickupAllBindingKey => _options.PickupAllBinding.StorageValue,
        SceneQuickActionsOptions.QuickReplaceBindingKey => _options.QuickReplaceBinding.StorageValue,
        SceneQuickActionsOptions.BetweenClicksKey => (double)_options.BetweenClicksMilliseconds,
        SceneQuickActionsOptions.PickupWakeKey => _options.PickupWakeMouse,
        SceneQuickActionsOptions.PollIntervalKey => (double)_options.PollIntervalMilliseconds,
        SceneQuickActionsOptions.ThresholdKey => (double)_options.MatchThresholdPercent,
        SceneQuickActionsOptions.CooldownKey => (double)_options.CooldownMilliseconds,
        _ => null
    };

    public void SetSettingValue(string key, object? value)
    {
        var updated = BuildOptions(key, value);
        if (ReferenceEquals(updated, _options))
            return;

        _options = updated;
        if (_enabled)
            _runner?.SetOptions(_options);
    }

    private SceneQuickActionsOptions BuildOptions(string key, object? value)
    {
        switch (key)
        {
            case SceneQuickActionsOptions.InviteEnabledKey when value is bool inviteEnabled:
                return _options with { InviteEnabled = inviteEnabled };
            case SceneQuickActionsOptions.MouseWakeBindingKey when value is string wakeBinding:
                return TryParseBinding(wakeBinding, out var parsedWake)
                    ? _options with { MouseWakeBinding = parsedWake }
                    : _options;
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
            default:
                return _options;
        }
    }

    private static bool TryParseBinding(string text, out PluginInputBinding binding) =>
        PluginInputBinding.TryParse(text, PluginInputBindingKinds.All, out binding);
}
