#if IDVB_DEVELOPMENT_BUILD
using System.Diagnostics;

namespace IDVBuff.Cli;

internal sealed partial class RealCliHost
{
    private static async Task<CliCommandResult> ExecuteDevelopmentBloomReplayAsync(string input,
        Stopwatch stopwatch, CancellationToken cancellation)
    {
        if (Microsoft.UI.Xaml.Application.Current is not App application
            || application.MainWindow.Content is not Microsoft.UI.Xaml.Controls.Frame { Content: IDVBuff.Views.MainPage main })
            throw new InvalidOperationException("光效回放必须连接完整开发客户端。");
        var result = await main.ReplayDevelopmentBloomAsync(cancellation);
        return CliCommandResult.Ok(input, "真实光效像素与释放回放结束。", stopwatch.Elapsed.TotalMilliseconds, result);
    }
}
#endif
