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
        // 本修改版新增：场景快捷助手（牵手邀请自动接受 / 可拾取面板拾取全部 + 快捷替换）。
        using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.SceneQuickActions.SceneQuickActionsPlugin"))
            host.Register(new IDVBuff.Plugins.SceneQuickActions.SceneQuickActionsPlugin());
        // 本修改版新增：重新登记上游在 1.6.0-unstable 摘掉的「自动加特林」（源码与工程引用仍在），
        // 并给它加了护栏：只有 dwrg.exe 在前台时才接管绑定的开火/装弹键。
        // 它执行的是自动开火与装弹，请自行确认使用场景符合游戏用户协议与 IDVB LICENSE 第三条。
        using (StartupTimeline.Measure("Built-in construct and register: IDVBuff.Plugins.AutoGatling.AutoGatlingPlugin"))
            host.Register(new IDVBuff.Plugins.AutoGatling.AutoGatlingPlugin());
    }
}
