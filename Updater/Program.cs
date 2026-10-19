namespace IDVBuff.Updater;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Console.Error.WriteLine("在线更新服务未配置。此源码构建包含不可用的公开实现。");
        return 4;
    }
}
