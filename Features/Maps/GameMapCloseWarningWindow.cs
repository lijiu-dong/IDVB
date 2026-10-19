using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;

/// <summary>A separate centered popup; its native window never owns mouse or keyboard input.</summary>
internal sealed class GameMapCloseWarningWindow : IDisposable
{
    private readonly MapOverlayNativeWindow _window;
    internal IntPtr Handle => _window.Handle;
    internal bool IsVisible => _window.IsVisible;

    internal GameMapCloseWarningWindow(ICaptureProtectionService? captureProtection = null) =>
        _window = new(captureProtection);

    internal void Show(MapScreenRect gameBounds)
    {
        if (!gameBounds.IsValid) return;
        var width = Math.Max(1, (int)Math.Round(Math.Min(760, gameBounds.Width * .86)));
        var height = Math.Max(1, (int)Math.Round(Math.Min(240, gameBounds.Height * .45)));
        using var bitmap = Render(width, height);
        _window.Present(bitmap, new(gameBounds.CenterX - width / 2d,
            gameBounds.CenterY - height / 2d, width, height));
    }

    internal static Bitmap Render(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.ScaleTransform(width / 760f, height / 240f);
        using var background = new SolidBrush(Color.FromArgb(248, 42, 13, 17));
        using var border = new Pen(Color.FromArgb(255, 255, 83, 77), 5);
        graphics.FillRectangle(background, 3, 3, 754, 234);
        graphics.DrawRectangle(border, 3, 3, 754, 234);
        using var titleFont = new Font("Microsoft YaHei UI", 32, FontStyle.Bold, GraphicsUnit.Pixel);
        using var bodyFont = new Font("Microsoft YaHei UI", 23, FontStyle.Bold, GraphicsUnit.Pixel);
        using var smallFont = new Font("Microsoft YaHei UI", 17, FontStyle.Regular, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.White);
        using var warning = new SolidBrush(Color.FromArgb(255, 255, 156, 148));
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString("警告：禁止使用鼠标左键关闭地图", titleFont, warning,
            new RectangleF(18, 22, 724, 62), center);
        graphics.DrawString("请使用开图快捷键或 Esc 关闭地图", bodyFont, white,
            new RectangleF(20, 94, 720, 45), center);
        graphics.DrawString("检测到游戏地图已关闭，IDVB 已同步清除贴合图。", smallFont, white,
            new RectangleF(20, 153, 720, 30), center);
        graphics.DrawString("本提示不影响鼠标和键盘操作，稍后自动消失。", smallFont, white,
            new RectangleF(20, 191, 720, 26), center);
        return bitmap;
    }

    internal void Hide() => _window.Hide();
    public void Dispose() => _window.Dispose();
}
