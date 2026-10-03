// IDVB 修改版 —— code-only 仓库缺失文件的构建桩。
//
// 上游的 code-only 源码发布里，BuildVersionInfo 定义在
// IDVBuff.RealCLI/BuildVersionInfo.cs，而该目录被 IDVBuff.csproj 的
// <Compile Remove="IDVBuff.RealCLI\**\*.cs" /> 显式排除，导致主程序
// 编译时找不到该类型（CS0103）。此文件仅补齐该类型以便从源码构建。
//
// 注意：这是「已修改」的构建，版本号里显式标注修改，以符合 LICENSE 第一条
// 「清楚标明其所作修改、不得暗示获得作者认可」的要求。
//
// 上游 1.6.6 已在 CustomPhrasePlugin 里自行把绑定放开为 PluginInputBindingKinds.All，
// 因此本版不再需要 1.6.5 时期的「侧键补丁」。

namespace IDVBuff;

internal static class BuildVersionInfo
{
    /// <summary>
    /// 在上游版本号后显式标注修改，满足 LICENSE 第一条的要求：
    /// 应用内「关于 / 设置」会显示为 v1.6.6-unstable（场景助手+自动加特林版·非官方）。
    /// </summary>
    public const string ProductVersion = "1.6.6-unstable（场景助手+自动加特林版·非官方）";

    /// <summary>
    /// 标明这是本地从源码构建，且带：场景快捷助手、自动加特林（含仅游戏内生效护栏）与构建桩。
    /// </summary>
    public const string BuildVersion =
        "1.6.6-unstable+scene-quick-actions+auto-gatling-guard+codeonlyfill";

    public const string NumericVersion = "1.6.6.0";
}
