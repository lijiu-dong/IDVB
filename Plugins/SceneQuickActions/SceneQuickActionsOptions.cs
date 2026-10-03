using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.SceneQuickActions;

/// <summary>
/// 运行时参数快照。设置页改动后整体替换一份新实例，工作线程只在循环开头读一次，
/// 因此磁盘/UI 线程与工作线程之间不需要细粒度锁。
///
/// 「切出鼠标」的行为是固定的：每次动作前都注入一次切出键（旧版的
/// 「切出鼠标方式」下拉框已按用户要求删除，磁盘上残留的旧值一律忽略）。
/// </summary>
public sealed record SceneQuickActionsOptions
{
    /// <summary>设置页里「配置按键」可折叠区的标题。</summary>
    public const string KeyGroupTitle = "配置按键";

    /// <summary>设置页里「间隔设置」可折叠区的标题。</summary>
    public const string TimingGroupTitle = "间隔设置";

    public const string InviteEnabledKey = "invite-enabled";
    public const string MouseWakeBindingKey = "mouse-wake-binding";
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

    /// <summary>「切出鼠标」注入的按键。</summary>
    public PluginInputBinding MouseWakeBinding { get; init; } =
        PluginInputBinding.Mouse(PluginMouseButton.XButton1);

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
