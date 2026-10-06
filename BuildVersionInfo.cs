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
// 基线：上游 **1.7.0**（tag b01.7-26.10.05.6129 / 提交 67f8a27）。
// 自上游 1.7.0-preview.2 起，本修改版原先自带的「背包辅助」「对局快捷键」两个插件
// 已被作者收纳进上游主线（并做了修复与重构），因此本版不再自带这两个插件的实现，
// 它们现在就是上游官方插件。
//
// 本版额外保留的内容：
//   1) 本修改版新增的内置插件「九歌自用」（Id pickup-escape，源码 Plugins\PickupEscape\）
//      —— 识别到「拾取全部」面板时按 ESC（含「面板需连续出现 250ms」稳定门槛），
//      以及「自定义短语」短语菜单竖排 + 默认高亮第一条 + 滚轮选择；
//   2) 本修改版新增的内置插件「信仰无后摇」（Id faith-no-recovery，源码 Plugins\FaithNoRecovery\）
//      —— 识别「信仰」蓄力结束（准心下的蓄力环 + 右下「点击ESC，取消操作」双双消失、
//      且这次蓄力期间玩家没按过 ESC）之后，用道具轮切（先切到别的道具栏、再切回原栏）顶掉后摇；
//   3) 两个已注销自动开火类插件（自动加特林 / 无后摇信仰）的「仅游戏内生效」护栏源码
//      （源码保留、不登记、不参与运行）；
//   4) 本构建桩、.gitignore 与交付文档。

namespace IDVBuff;

internal static class BuildVersionInfo
{
    /// <summary>
    /// 在上游版本号后显式标注修改，满足 LICENSE 第一条的要求：
    /// 应用内「关于 / 设置」会显示为 v1.7.0（修改版·非官方）。
    /// </summary>
    public const string ProductVersion =
        "1.7.0（修改版·非官方）";

    /// <summary>
    /// 标明这是本地从源码构建：基线为上游 v1.7.0（build 6129，
    /// 含画面捕获设备复用 / 帧流读回 / 会话恢复的一批修复），
    /// 加上「九歌自用」「信仰无后摇」两个插件、自定义短语竖排菜单、
    /// 两个已注销插件的护栏源码与构建桩。
    /// </summary>
    public const string BuildVersion =
        "1.7.0+upstream-6129+codeonlyfill";

    public const string NumericVersion = "1.7.0.0";
}
