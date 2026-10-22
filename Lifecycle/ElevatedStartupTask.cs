using System.Diagnostics;
using IDVBuff.Features.Maps;

namespace IDVBuff.Lifecycle;

public static class ElevatedStartupTask
{
    private const string TaskName = "Identity Vision Bridge";

    public static async Task SetEnabledAsync(bool enabled)
    {
        var arguments = enabled
            ? $"/Create /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /F /TR \"\\\"{Environment.ProcessPath}\\\" --startup\""
            : $"/Delete /TN \"{TaskName}\" /F";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
            Arguments = arguments,
            UseShellExecute = true,
            Verb = GameProcessIntegrityService.Check().CurrentProcessIsElevated ? string.Empty : "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("无法启动 Windows 计划任务管理器。");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(enabled
                ? "未能创建管理员自启动任务。请检查 Windows 计划任务服务和账户权限。"
                : "未能删除管理员自启动任务。请检查 Windows 计划任务服务和账户权限。");
    }
}
