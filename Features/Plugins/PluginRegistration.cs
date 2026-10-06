using IDVBuff.Diagnostics;
using IDVBuff.PluginContracts;

namespace IDVBuff.Features.Plugins;

/// <summary>
/// 插件组合根，与 <see cref="IDVBuff.Modules.ModuleRegistration"/> 平行。
/// 内置与导入插件在此编译时登记。
/// </summary>
public static class PluginRegistration
{
    public static void Register(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        // 内置/导入插件在此登记。
        using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.AutoClicker.AutoClickerPlugin"))
            host.Register(new IDVBuff.Plugins.AutoClicker.AutoClickerPlugin());
        using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.NightVision.NightVisionPlugin"))
            host.Register(new IDVBuff.Plugins.NightVision.NightVisionPlugin());
        using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.DynamicMiniMapZoom.DynamicMiniMapZoomPlugin"))
            host.Register(new IDVBuff.Plugins.DynamicMiniMapZoom.DynamicMiniMapZoomPlugin());
        using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.LiveMode.LiveModePlugin"))
            host.Register(new IDVBuff.Plugins.LiveMode.LiveModePlugin());
        using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.CustomPhrases.CustomPhrasePlugin"))
            host.Register(new IDVBuff.Plugins.CustomPhrases.CustomPhrasePlugin());
        using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.SceneQuickActions.SceneQuickActionsPlugin"))
            host.Register(new IDVBuff.Plugins.SceneQuickActions.SceneQuickActionsPlugin());
        // 对局快捷键：两个可自定义快捷键，各自一键进入 / 结束指定地图模式的对局。
        // 常驻（AlwaysActive）：开局前宿主会关掉非常驻插件的运行时闸门，而它正是用来开局的。
        using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.MatchHotkeys.MatchHotkeysPlugin"))
            host.Register(new IDVBuff.Plugins.MatchHotkeys.MatchHotkeysPlugin());
        // 拾取 ESC：识别到「可拾取」面板出现就按一下 ESC 关掉它（热键可暂停/恢复检测）。
        // 常驻（AlwaysActive）：热键要在局外也按得动，与「背包辅助」保持一致。
        using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.PickupEscape.PickupEscapePlugin"))
            host.Register(new IDVBuff.Plugins.PickupEscape.PickupEscapePlugin());
        // 信仰无后摇：识别「信仰」蓄力结束（准心下的蓄力环 + 右下「点击ESC，取消操作」双双消失、
        // 且这次蓄力期间玩家没按过 ESC）之后，用道具轮切（先切到别的道具栏、再切回原栏）顶掉后摇。
        // 常驻（AlwaysActive）：开关热键要在局外也按得动；热键默认「未设置」，没录就不动作。
        using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.FaithNoRecovery.FaithNoRecoveryPlugin"))
            host.Register(new IDVBuff.Plugins.FaithNoRecovery.FaithNoRecoveryPlugin());
        // 本修改版已注销「自动加特林」（Id auto-gatling）：它的源码与工程引用仍留在
        // Plugins\AutoGatling\，只是不再登记，与上游一致（上游自 1.6.0-unstable 起就没登记它）。
        // 需要时把下面两行注释放开即可恢复。
        // using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.AutoGatling.AutoGatlingPlugin"))
        //     host.Register(new IDVBuff.Plugins.AutoGatling.AutoGatlingPlugin());
        // 本修改版已注销「无后摇信仰」（Id no-recovery-delay）：上游一直没登记它，
        // 我们试着登记过一版、确认不需要，于是同样回到「源码保留、不登记」的状态。
        // 它的源码与工程引用仍在 Plugins\NoRecoveryDelay\（并且已带上「仅游戏内生效」护栏），
        // 需要时把下面两行注释放开即可恢复。
        // using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.NoRecoveryDelay.NoRecoveryDelayPlugin"))
        //     host.Register(new IDVBuff.Plugins.NoRecoveryDelay.NoRecoveryDelayPlugin());
    }
}
