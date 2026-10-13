using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.SceneQuickActions;

/// <summary>
/// 运行时参数快照。设置页改动后整体替换一份新实例，工作线程只在循环开头读一次，
/// 因此磁盘/UI 线程与工作线程之间不需要细粒度锁。
///
/// 「切出鼠标」的行为是固定的：按一次 Tab 打开背包把系统指针切出来，动作做完再按
/// 一次 Tab 关掉背包。旧版的「切出鼠标方式」下拉框与「切出鼠标按键」都已按用户要求
/// 删除——实测合成的鼠标侧键游戏不认，而 Tab 这类游戏内键位走得通。
/// </summary>
public sealed record SceneQuickActionsOptions
{
    /// <summary>设置页里「配置按键」可折叠区的标题。</summary>
    public const string KeyGroupTitle = "配置按键";

    /// <summary>设置页里「背包检测」可折叠区的标题。</summary>
    public const string BackpackGroupTitle = "背包检测";

    /// <summary>设置页里「间隔设置」可折叠区的标题。</summary>
    public const string TimingGroupTitle = "间隔设置";

    /// <summary>「切出鼠标」固定用的键：Tab（打开 / 关闭背包）。</summary>
    public const uint WakeVirtualKey = 0x09;

    /// <summary>「开背包 / 切出鼠标」按键的默认值：Tab。</summary>
    public const uint DefaultBackpackVirtualKey = WakeVirtualKey;

    /// <summary>「总开关」热键的键位键名（默认「未设置」）。</summary>
    public const string MasterHotkeyKey = "master-hotkey";

    /// <summary>「开背包 / 切出鼠标」按键的键名；用来判断背包是不是开着。</summary>
    public const string BackpackKeyKey = "backpack-key";

    /// <summary>背包判断方式的键名（下拉框）。</summary>
    public const string BackpackModeKey = "backpack-detect-mode";

    /// <summary>「打开状态最长保持」秒数的键名（兜底释放）。</summary>
    public const string BackpackMaxHoldKey = "backpack-max-hold-seconds";

    /// <summary>「关掉背包后再宽限几秒不拾取」的键名。</summary>
    public const string BackpackCloseGraceKey = "backpack-close-grace-seconds";

    /// <summary>「关掉背包后再宽限几秒」的范围（0 = 不做这件事）。</summary>
    public const int BackpackCloseGraceMinimum = 0;
    public const int BackpackCloseGraceMaximum = 60;

    /// <summary>背包判断方式下拉框的选项文案。</summary>
    public static IReadOnlyList<string> BackpackModeOptions { get; } =
        ["按「开背包键」判断（推荐）", "按键 + 指针（更保守）", "关（不判断背包）"];

    /// <summary>把设置页存下来的文案还原成枚举；不认得的退回「按开背包键判断」。</summary>
    public static SceneQuickActionsBackpackMode ParseBackpackMode(string? option)
    {
        var index = BackpackModeOptions
            .ToList()
            .FindIndex(candidate => string.Equals(candidate, option, StringComparison.Ordinal));
        return index switch
        {
            1 => SceneQuickActionsBackpackMode.KeyAndCursor,
            2 => SceneQuickActionsBackpackMode.Off,
            _ => SceneQuickActionsBackpackMode.KeyOnly
        };
    }

    public static string ToBackpackModeOption(SceneQuickActionsBackpackMode mode) =>
        BackpackModeOptions[Math.Clamp((int)mode, 0, BackpackModeOptions.Count - 1)];

    /// <summary>「打开状态最长保持」秒数的范围。</summary>
    public const int BackpackMaxHoldMinimum = 10;
    public const int BackpackMaxHoldMaximum = 600;

    /// <summary>「丢光背包」热键默认值：F5。</summary>
    public const uint DefaultDropBagVirtualKey = 0x74;

    /// <summary>「逐个丢道具栏」热键默认值：F6。</summary>
    public const uint DefaultDropHotbarVirtualKey = 0x75;

    public const string DropBagHotkeyKey = "drop-bag-hotkey";
    public const string DropHotbarHotkeyKey = "drop-hotbar-hotkey";
    public const string DropSilenceKey = "drop-silence-seconds";
    public const string DropSpeedKey = "drop-speed";

    /// <summary>拖拽速度档位文案。</summary>
    public static IReadOnlyList<string> DropSpeedOptions { get; } = ["快", "标准", "慢（稳妥）"];

    /// <summary>拖拽各段时长（毫秒）：整条轨迹时长 / 到位后停顿 / 每格之间间隔。</summary>
    public static (int Move, int Settle, int Interval) GetDropTiming(int speedIndex) =>
        speedIndex switch
        {
            0 => (150, 60, 180),
            1 => (260, 90, 300),
            _ => (400, 150, 600)
        };

    /// <summary>把设置页存下来的档位文案还原成下标；不认得的回退到「快」。</summary>
    public static int ParseDropSpeed(string? option)
    {
        var index = DropSpeedOptions
            .ToList()
            .FindIndex(candidate => string.Equals(candidate, option, StringComparison.Ordinal));
        return index < 0 ? 0 : index;
    }

    public static string ToDropSpeedOption(int speedIndex) =>
        DropSpeedOptions[Math.Clamp(speedIndex, 0, DropSpeedOptions.Count - 1)];

    /// <summary>触发丢东西后暂停「拾取相关」检测的秒数范围。</summary>
    public const int DropSilenceMinimum = 5;
    public const int DropSilenceMaximum = 120;

    public const string InviteEnabledKey = "invite-enabled";
    public const string WakeDelayKey = "wake-delay-ms";
    public const string PickupEnabledKey = "pickup-enabled";
    public const string PickupAllBindingKey = "pickup-all-binding";
    public const string QuickReplaceBindingKey = "quick-replace-binding";
    public const string BetweenClicksKey = "between-clicks-ms";
    public const string PickupWakeKey = "pickup-wake-mouse";
    public const string PollIntervalKey = "poll-interval-ms";
    public const string ThresholdKey = "match-threshold-percent";
    public const string CooldownKey = "cooldown-ms";
    public const string MinimumRandomDelayKey = "minimum-random-delay-ms";
    public const string MaximumRandomDelayKey = "maximum-random-delay-ms";
    public const int MaximumRandomDelayMillisecondsAllowed = 10000;

    public int MinimumRandomDelayMilliseconds { get; init; } = 30;
    public int MaximumRandomDelayMilliseconds { get; init; } = 50;

    public (int Minimum, int Maximum) GetOrderedRandomDelayRange()
    {
        var lower = Math.Clamp(MinimumRandomDelayMilliseconds,
            PluginRandomDelayPolicy.GetMinimum(30), MaximumRandomDelayMillisecondsAllowed);
        var upper = Math.Clamp(MaximumRandomDelayMilliseconds,
            PluginRandomDelayPolicy.GetMinimum(50), MaximumRandomDelayMillisecondsAllowed);
        return (Math.Min(lower, upper), Math.Max(lower, upper));
    }

    /// <summary>每段动作后保留原有等待，再独立追加一次随机延迟。</summary>
    public int GetDelayAfterStep(int baseMilliseconds = 0)
    {
        var (minimum, maximum) = GetOrderedRandomDelayRange();
        return Math.Clamp(baseMilliseconds, 0, 10000) + Random.Shared.Next(minimum, maximum + 1);
    }

    /// <summary>插件绑定存储层里表示「未设置」的稳定字符串。</summary>
    public const string NoBindingStorageValue = "none";

    /// <summary>「拾取全部」的游戏默认按键（B）。</summary>
    public const uint DefaultPickupAllVirtualKey = 0x42;

    public const int WakeDelayMinimum = 40;
    public const int WakeDelayMaximum = 600;
    public const int BetweenClicksMinimum = 60;
    public const int BetweenClicksMaximum = 1200;
    public const int PollIntervalMinimum = 200;
    public const int PollIntervalMaximum = 2000;
    public const int ThresholdMinimum = 60;
    public const int ThresholdMaximum = 95;
    public const int CooldownMinimum = 300;
    public const int CooldownMaximum = 10000;

    /// <summary>自动接受邀请：识别到「邀请你牵手」的接受按钮时自动点「接受」。</summary>
    public bool InviteEnabled { get; init; } = true;

    /// <summary>总开关热键（默认「未设置」）：按一下停用整个插件，再按一下恢复。</summary>
    public PluginInputBinding MasterHotkey { get; init; } = new();

    /// <summary>游戏里「开背包 / 切出鼠标」的按键（默认 Tab），用来判断背包是不是开着。</summary>
    public PluginInputBinding BackpackKey { get; init; } =
        PluginInputBinding.Keyboard(DefaultBackpackVirtualKey);

    /// <summary>背包判断方式。</summary>
    public SceneQuickActionsBackpackMode BackpackMode { get; init; } =
        SceneQuickActionsBackpackMode.KeyOnly;

    /// <summary>「打开」状态的最长保持秒数：这段时间里没有任何新证据就自动解除，免得一直不拾取。</summary>
    public int BackpackMaxHoldSeconds { get; init; } = 120;

    /// <summary>
    /// 背包从「打开」翻回「关闭」之后的宽限秒数：这段时间内「拾取相关」仍然不动作。
    /// 你手动丢东西的时候是「开着背包拖出去 → 关上背包」，刚丢出去的东西可能立刻出现在
    /// 「可拾取」面板里，不给点宽限就会被自己捡回去。0 = 不做这件事。
    /// </summary>
    public int BackpackCloseGraceSeconds { get; init; } = 5;

    /// <summary>切出鼠标后、移动指针前的等待时间。</summary>
    public int WakeDelayMilliseconds { get; init; } = 150;

    /// <summary>拾取相关：识别到「可拾取」面板时自动「拾取全部」→「快捷替换」。</summary>
    public bool PickupEnabled { get; init; } = true;

    /// <summary>「拾取全部」用的按键；未设置（none）时改为鼠标点击该提示。</summary>
    public PluginInputBinding PickupAllBinding { get; init; } =
        PluginInputBinding.Keyboard(DefaultPickupAllVirtualKey);

    /// <summary>「快捷替换」用的按键；未设置（none，默认）时改为鼠标点击该按钮。</summary>
    public PluginInputBinding QuickReplaceBinding { get; init; } = new();

    /// <summary>「拾取全部」与「快捷替换」两次动作之间的间隔。</summary>
    public int BetweenClicksMilliseconds { get; init; } = 260;

    /// <summary>拾取相关是否也需要先切出鼠标（只有用鼠标点击时才需要）。</summary>
    public bool PickupWakeMouse { get; init; }

    /// <summary>两次检测之间的间隔。</summary>
    public int PollIntervalMilliseconds { get; init; } = 400;

    /// <summary>模板匹配相似度阈值（百分比）。</summary>
    public int MatchThresholdPercent { get; init; } = 80;

    /// <summary>同一场景两次动作之间的最小间隔。</summary>
    public int CooldownMilliseconds { get; init; } = 1500;

    /// <summary>「丢光背包」热键（默认 F5；可录制成键盘键或鼠标键）。</summary>
    public PluginInputBinding DropBagHotkey { get; init; } =
        PluginInputBinding.Keyboard(DefaultDropBagVirtualKey);

    /// <summary>「逐个丢道具栏」热键（默认 F6；可录制成键盘键或鼠标键）。</summary>
    public PluginInputBinding DropHotbarHotkey { get; init; } =
        PluginInputBinding.Keyboard(DefaultDropHotbarVirtualKey);

    /// <summary>触发丢东西后暂停「拾取相关」的秒数。</summary>
    public int DropSilenceSeconds { get; init; } = 30;

    /// <summary>拖拽速度档位下标（0 快 / 1 标准 / 2 慢）。</summary>
    public int DropSpeedIndex { get; init; }

    /// <summary>把一份绑定收敛成「单一按键」的热键：带修饰键或伴随键的一律不采用。</summary>
    public static bool TryAsHotkey(PluginInputBinding? binding, out PluginInputBinding hotkey)
    {
        hotkey = new PluginInputBinding();
        if (binding is null
            || !binding.IsConfigured
            || binding.Modifiers != PluginInputModifiers.None
            || (binding.CompanionVirtualKeys?.Count ?? 0) > 0)
        {
            return false;
        }

        hotkey = binding;
        return true;
    }

    public double MatchThreshold =>
        Clamp(MatchThresholdPercent, ThresholdMinimum, ThresholdMaximum) / 100d;

    public static int Clamp(int value, int minimum, int maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;

    public static int FromDouble(object? value, int fallback, int minimum, int maximum)
    {
        var parsed = value switch
        {
            double number => number,
            float number => number,
            int number => number,
            long number => number,
            _ => double.NaN
        };
        return double.IsFinite(parsed)
            ? Clamp((int)Math.Round(parsed), minimum, maximum)
            : fallback;
    }
}
