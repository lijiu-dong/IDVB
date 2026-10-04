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
        // 本修改版新增：背包辅助（牵手邀请自动接受 / 可拾取面板拾取全部 + 快捷替换）。
        using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.SceneQuickActions.SceneQuickActionsPlugin"))
            host.Register(new IDVBuff.Plugins.SceneQuickActions.SceneQuickActionsPlugin());
        // 本修改版已注销「自动加特林」（Id auto-gatling）：它的源码与工程引用仍留在
        // Plugins\AutoGatling\，只是不再登记，回到与上游 1.6.6 一致的状态（上游自
        // 1.6.0-unstable 起就没登记它）。需要时把下面两行注释放开即可恢复。
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
