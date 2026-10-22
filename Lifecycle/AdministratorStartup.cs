using System.ComponentModel;
using System.Diagnostics;
using IDVBuff.Features.Maps;

namespace IDVBuff.Lifecycle;

/// <summary>Obtains administrator privileges before the normal GUI starts.</summary>
internal static class AdministratorStartup
{
    private const string RelaunchArgument = "--idvb-elevation-requested";
    private const string UpdatedRelaunchArgument = "--idvb-elevation-after-update";
    private const int ErrorCancelled = 1223;

    // True means this unelevated process must exit, including when UAC is cancelled.
    internal static bool TryRelaunch(string[] arguments)
    {
        if (GameProcessIntegrityService.Check().CurrentProcessIsElevated)
        {
            if (arguments.Contains(UpdatedRelaunchArgument, StringComparer.OrdinalIgnoreCase))
                UpdateLifecycleState.WasRestartedAfterUpdate = true;
            return false;
        }

        if (arguments.Contains(RelaunchArgument, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Windows 未授予管理员权限，已停止启动，避免重复请求提权。");

        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定当前程序路径。"),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.ArgumentList.Add(RelaunchArgument);
        if (UpdateLifecycleState.WasRestartedAfterUpdate)
            start.ArgumentList.Add(UpdatedRelaunchArgument);

        try
        {
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("无法启动管理员进程。");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            // Respect cancellation; never continue into the normal runtime without elevation.
        }
        return true;
    }
}
