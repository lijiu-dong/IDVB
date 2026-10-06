using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.FaithNoRecovery;

/// <summary>
/// 「信仰无后摇」的运行时参数快照。设置页改动后整体替换一份新实例，
/// 工作线程只在循环开头读一次，因此磁盘/UI 线程与工作线程之间不需要细粒度锁。
/// </summary>
public sealed record FaithNoRecoveryOptions
{
    public const string PluginId = "faith-no-recovery";

    /// <summary>设置页里「道具轮切」可折叠区的标题。</summary>
    public const string ActionGroupTitle = "道具轮切";

    /// <summary>设置页里「识别」可折叠区的标题。</summary>
    public const string DetectionGroupTitle = "识别";

    /// <summary>设置页里「间隔设置」可折叠区的标题。</summary>
    public const string TimingGroupTitle = "间隔设置";

    public const string ToggleHotkeyKey = "toggle-hotkey";
    public const string SwitchToBindingKey = "switch-to-binding";
    public const string SwitchBackBindingKey = "switch-back-binding";
    public const string ActionDelayKey = "action-delay-ms";
    public const string SwitchGapKey = "switch-gap-ms";
    public const string KeyHoldKey = "key-hold-ms";
    public const string ToastOnActionKey = "toast-on-action";
    public const string MatchThresholdKey = "match-threshold-percent";
    public const string RingContrastKey = "ring-contrast-threshold";
    public const string RingSectorsKey = "ring-minimum-sectors";
    public const string RequireBothGoneKey = "require-both-gone";
    public const string OtherCueGraceKey = "other-cue-grace-ms";
    public const string MinimumChargeKey = "minimum-charge-ms";
    public const string PollIntervalKey = "poll-interval-ms";
    public const string CooldownKey = "cooldown-ms";
    public const string MinimumRandomDelayKey = "minimum-random-delay-ms";
    public const string MaximumRandomDelayKey = "maximum-random-delay-ms";

    /// <summary>插件绑定存储层里表示「未设置」的稳定字符串。</summary>
    public const string NoBindingStorageValue = "none";

    /// <summary>
    /// 先按的道具栏（切到「别的」道具栏）：默认 4（VK 0x34）。
    /// 截图里 3 号栏是法杖（信仰），4 号栏是道具箱 —— 先切过去、再切回来，靠切换动作顶掉后摇。
    /// </summary>
    public const uint DefaultSwitchToVirtualKey = 0x34;

    /// <summary>再按的道具栏（切回「原来那栏」）：默认 3（VK 0x33）。</summary>
    public const uint DefaultSwitchBackVirtualKey = 0x33;

    public const int PollIntervalMinimum = 20;
    public const int PollIntervalMaximum = 300;
    public const int ThresholdMinimum = 55;
    public const int ThresholdMaximum = 95;
    public const int RingContrastMinimum = 15;
    public const int RingContrastMaximum = 90;
    public const int RingSectorsMinimum = 1;
    public const int RingSectorsMaximum = 10;
    public const int MinimumChargeMinimum = 0;
    public const int MinimumChargeMaximum = 1000;
    public const int ActionDelayMinimum = 0;
    public const int ActionDelayMaximum = 1000;
    public const int SwitchGapMinimum = 5;
    public const int SwitchGapMaximum = 200;
    public const int KeyHoldMinimum = 5;
    public const int KeyHoldMaximum = 200;
    public const int OtherCueGraceMinimum = 0;
    public const int OtherCueGraceMaximum = 2000;
    public const int CooldownMinimum = 100;
    public const int CooldownMaximum = 5000;
    public const int MaximumRandomDelayMillisecondsAllowed = 10000;

    /// <summary>两次检测之间的间隔；后摇窗口很短，默认给得比其它插件密。</summary>
    public int PollIntervalMilliseconds { get; init; } = 40;

    /// <summary>「点击ESC，取消操作」文案的模板匹配相似度阈值（百分比）。</summary>
    public int MatchThresholdPercent { get; init; } = 72;

    /// <summary>蓄力环单个扇区要高出背景多少灰度才算「亮」（实测：亮扇区 ≈ 90+，背景 ≤ 25）。</summary>
    public int RingContrastThreshold { get; init; } = 40;

    /// <summary>蓄力环至少要有几个亮扇区才算「蓄力环在场」（实测：未蓄满 5 个、蓄满 10 个）。</summary>
    public int RingMinimumSectors { get; init; } = 2;

    /// <summary>两个标志要一起出现这么久（毫秒）才算一次真蓄力；一闪而过的不算。</summary>
    public int MinimumChargeMilliseconds { get; init; } = 120;

    /// <summary>是否要求「蓄力环 + 取消提示」两个标志都消失才动作（默认要求）。</summary>
    public bool RequireBothGone { get; init; } = true;

    /// <summary>只剩一个标志没消失时，再等这么久（毫秒）就按「已经结束」处理。</summary>
    public int OtherCueGraceMilliseconds { get; init; } = 400;

    /// <summary>标志消失后延迟多久执行道具轮切（毫秒）。</summary>
    public int ActionDelayMilliseconds { get; init; }

    /// <summary>两次道具栏切换之间的间隔（毫秒）。</summary>
    public int SwitchGapMilliseconds { get; init; } = 25;

    /// <summary>每个按键按下后保持多久（毫秒）。</summary>
    public int KeyHoldMilliseconds { get; init; } = 20;

    /// <summary>一次动作之后的冷却（毫秒），避免连着轮切。</summary>
    public int CooldownMilliseconds { get; init; } = 500;

    /// <summary>动作时是否在游戏内浮层上提示（默认关，省得刷屏）。</summary>
    public bool ToastOnAction { get; init; }

    public int MinimumRandomDelayMilliseconds { get; init; } = 30;

    public int MaximumRandomDelayMilliseconds { get; init; } = 50;

    /// <summary>切换「检测运行 / 暂停」的热键；默认「未设置」，需要用户在设置页自己录。</summary>
    public PluginInputBinding ToggleHotkey { get; init; } = new();

    /// <summary>先按的道具栏（切到别的栏）；默认 4。</summary>
    public PluginInputBinding SwitchToBinding { get; init; } =
        PluginInputBinding.Keyboard(DefaultSwitchToVirtualKey);

    /// <summary>再按的道具栏（切回原栏）；默认 3。</summary>
    public PluginInputBinding SwitchBackBinding { get; init; } =
        PluginInputBinding.Keyboard(DefaultSwitchBackVirtualKey);

    public (int Minimum, int Maximum) GetOrderedRandomDelayRange()
    {
        var lower = Math.Clamp(MinimumRandomDelayMilliseconds,
            PluginRandomDelayPolicy.GetMinimum(30), MaximumRandomDelayMillisecondsAllowed);
        var upper = Math.Clamp(MaximumRandomDelayMilliseconds,
            PluginRandomDelayPolicy.GetMinimum(50), MaximumRandomDelayMillisecondsAllowed);
        return (Math.Min(lower, upper), Math.Max(lower, upper));
    }

    /// <summary>动作之后追加一次随机等待（不参与轮切本身的两键间隔，免得把时机抖没）。</summary>
    public int GetDelayAfterAction()
    {
        var (minimum, maximum) = GetOrderedRandomDelayRange();
        return Random.Shared.Next(minimum, maximum + 1);
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
}
