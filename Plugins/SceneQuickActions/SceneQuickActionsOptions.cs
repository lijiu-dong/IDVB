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

    /// <summary>设置页里「间隔设置」可折叠区的标题。</summary>
    public const string TimingGroupTitle = "间隔设置";

    /// <summary>「切出鼠标」固定用的键：Tab（打开 / 关闭背包）。</summary>
    public const uint WakeVirtualKey = 0x09;

    /// <summary>「丢光背包」热键，默认 F5。</summary>
    public const uint DefaultDropBagVirtualKey = 0x74;

    /// <summary>「逐个丢道具栏」热键，默认 F6。</summary>
    public const uint DefaultDropHotbarVirtualKey = 0x75;

    /// <summary>热键下拉选项：功能键 F1–F12（索引 + 0x70 即虚拟键码）。</summary>
    public static IReadOnlyList<string> HotkeyOptions { get; } =
        ["F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"];

    /// <summary>把热键选项文本转成虚拟键码；无法识别时返回 0。</summary>
    public static uint ParseHotkey(string? option)
    {
        var index = -1;
        for (var candidate = 0; candidate < HotkeyOptions.Count; candidate++)
        {
            if (string.Equals(HotkeyOptions[candidate], option, StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }

        return index < 0 ? 0u : (uint)(0x70 + index);
    }

    /// <summary>把虚拟键码转回热键选项文本；不是功能键时返回 null。</summary>
    public static string? ToHotkeyOption(uint virtualKey) =>
        virtualKey is >= 0x70 and <= 0x7B
            ? HotkeyOptions[(int)(virtualKey - 0x70)]
            : null;

    public const string DropBagHotkeyKey = "drop-bag-hotkey";
    public const string DropHotbarHotkeyKey = "drop-hotbar-hotkey";
    public const string DropSilenceKey = "drop-silence-seconds";

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

    /// <summary>「丢光背包」热键（默认 F5）。</summary>
    public uint DropBagHotkey { get; init; } = DefaultDropBagVirtualKey;

    /// <summary>「逐个丢道具栏」热键（默认 F6）。</summary>
    public uint DropHotbarHotkey { get; init; } = DefaultDropHotbarVirtualKey;

    /// <summary>触发丢东西后暂停「拾取相关」的秒数。</summary>
    public int DropSilenceSeconds { get; init; } = 30;

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
