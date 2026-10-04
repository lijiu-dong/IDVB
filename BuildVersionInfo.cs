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
// 上游 1.7.0-preview.2（b01.7-26.10.04.6084）起，本修改版原先自带的
// 「背包辅助」「对局快捷键」两个插件已被作者收纳进上游，并做了修复与重构：
//   背包辅助 —— 背包坐标改为数据文件（Resources/InventoryCoordinates.json）、
//               新增「随机延迟下限 / 上限」两项设置、Runner 拆成 4 个 partial；
//   对局快捷键 —— 设置弹窗位置修复、拆出 MatchHotkeysPlugin.Settings.cs。
// 因此本版不再自带这两个插件的实现，只额外保留：
//   1) 两个已注销的自动开火类插件（自动加特林 / 无后摇信仰）的「仅游戏内生效」护栏源码；
//   2) 本构建桩；
//   3) .gitignore 与交付文档。

namespace IDVBuff;

internal static class BuildVersionInfo
{
    /// <summary>
    /// 在上游版本号后显式标注修改，满足 LICENSE 第一条的要求：
    /// 应用内「关于 / 设置」会显示为 v1.7.0-preview.2（修改版·非官方）。
    /// </summary>
    public const string ProductVersion =
        "1.7.0-preview.2（修改版·非官方）";

    /// <summary>
    /// 标明这是本地从源码构建：基线为上游 v1.7.0-preview.2（build 6084），
    /// 含上游收纳后的背包辅助与对局快捷键、两个已注销自动开火类插件的护栏源码
    /// （不登记、不参与运行），以及构建桩。
    /// </summary>
    public const string BuildVersion =
        "1.7.0-preview.2+upstream-6084+codeonlyfill";

    public const string NumericVersion = "1.7.0.0";
}
