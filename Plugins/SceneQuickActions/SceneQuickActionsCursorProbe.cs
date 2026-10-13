using System.Runtime.InteropServices;

namespace IDVBuff.Plugins.SceneQuickActions;

/// <summary>
/// 只读地问一句 Windows：现在指针是显示着的吗？
///
/// 正常游玩时游戏把指针藏起来（鼠标被游戏吃掉），一旦打开背包（或任何需要点选的界面）
/// 就把指针切出来 —— 所以「指针可见」是一条不依赖截图、不依赖内存的旁证。
/// 只用 user32 的 GetCursorInfo，不读游戏内存、不注入游戏进程、不修改游戏文件。
/// </summary>
internal static class SceneQuickActionsCursorProbe
{
    private const int CursorShowing = 0x00000001;

    internal static bool IsCursorVisible()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref info)
            && (info.Flags & CursorShowing) != 0
            && info.Handle != IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Handle;
        public NativePoint ScreenPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CursorInfo info);
}
