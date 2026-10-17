#if IDVB_DEVELOPMENT_BUILD
namespace IDVBuff;

public partial class App
{
    internal void CloseDevelopmentReplayWindow()
    {
        if (!Program.IsDevelopmentInstance)
            throw new InvalidOperationException("只允许正常关闭明确指定 PID 的开发回放客户端。");
        RequestApplicationExit();
    }
}
#endif
