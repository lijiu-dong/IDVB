using System.Diagnostics;
using System.Globalization;

namespace IDVBuff.Lifecycle;

/// <summary>Hands login back to a fresh process after the previous runtime and singleton have exited.</summary>
internal static class LoginReturnProcess
{
    private const string Argument = "--login-after-exit";

    internal static ProcessStartInfo CreateStartInfo(string executable, int processId, long startTicks, bool development,
        string[] originalArguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add(Argument);
        start.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(startTicks.ToString(CultureInfo.InvariantCulture));
        if (development)
        {
            start.ArgumentList.Add("--isolated-dev-instance");
            // An isolated replay must keep using its own data after logout, never the installed account.
            var root = Array.IndexOf(originalArguments, "--idvb-memory-test-root");
            if (root >= 0 && root + 1 < originalArguments.Length)
            {
                start.ArgumentList.Add("--idvb-memory-test-root");
                start.ArgumentList.Add(originalArguments[root + 1]);
            }
            if (originalArguments.Contains("--idvb-background-test")) start.ArgumentList.Add("--idvb-background-test");
        }
        return start;
    }

    internal static bool WaitForPreviousProcess(string[] args)
    {
        var index = Array.IndexOf(args, Argument);
        if (index < 0) return false;
        if (index + 2 >= args.Length || !int.TryParse(args[index + 1], out var pid) || pid <= 0
            || pid == Environment.ProcessId || !long.TryParse(args[index + 2], out var ticks) || ticks <= 0)
            throw new ArgumentException("返回登录页的启动参数无效。");
        Process previous;
        try { previous = Process.GetProcessById(pid); }
        catch (ArgumentException) { return true; } // The old process has already exited.
        using (previous)
        {
            if (previous.StartTime.ToUniversalTime().Ticks != ticks) return true; // PID was reused.
            if (!previous.WaitForExit(15000))
                throw new TimeoutException("主程序尚未完成退出，无法打开登录页。请稍后重新启动。");
        }
        return true;
    }
}
