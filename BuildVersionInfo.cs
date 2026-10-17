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
// 基线：上游 **1.7.1-preview**（tag b01.7-26.10.17.6717 / 提交 4ad7a18）。
// 自上游 1.7.0-preview.2 起，本修改版原先自带的「背包辅助」「对局快捷键」两个插件
// 已被作者收纳进上游主线（并做了修复与重构），因此本版不再自带这两个插件的实现，
// 它们现在就是上游官方插件。
// 自上游 1.7.1-preview 起，「自定义短语」短语菜单的竖排布局 / 默认高亮第一条 / 滚轮选择
// （Plugins\CustomPhrases\）也已由作者完整收录进上游主线，故本版不再需要单独维护该改动。
//
// 本版额外保留的内容：
//   1) 本修改版新增的内置插件「九歌自用」（Id pickup-escape，源码 Plugins\PickupEscape\）
//      —— 识别到「拾取全部」面板时按 ESC（含「面板需连续出现 250ms」稳定门槛）；
//   1.5) 「背包辅助」三条护栏（背包开着不拾取 + 关背包后宽限 5 秒 + 总开关热键，
//      源码 Plugins\SceneQuickActions\）——上游尚未收纳，仍由本修改版维护；
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
    /// 应用内「关于 / 设置」会显示为 v1.7.1-preview（修改版·非官方）。
    /// </summary>
    public const string ProductVersion =
        "1.7.1-preview（修改版·非官方）";

    /// <summary>
    /// 标明这是本地从源码构建：基线为上游 v1.7.1-preview（build 6717，
    /// 含地图显示区域自动推导、共创地图/订阅变体、地图控制面板过渡、
    /// 侧门扫描与对齐预算分离、CET 启动兼容等一批改动），
    /// 加上「九歌自用」「信仰无后摇」两个插件、背包辅助三条护栏、
    /// 两个已注销插件的护栏源码与构建桩。
    /// </summary>
    public const string BuildVersion =
        "1.7.1-preview+upstream-6717+codeonlyfill";

    public const string NumericVersion = "1.7.1.0";
}
