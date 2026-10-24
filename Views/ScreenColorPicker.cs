using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace IDVBuff.Views;

internal static class ScreenColorPicker
{
    public static Task<Color?> PickAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<Color?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            // Keep capture, cursor and window coordinates in physical pixels,
            // including negative monitor origins and mixed-DPI desktops.
            var previousDpi = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try
            {
                if (previousDpi == IntPtr.Zero)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法启用屏幕像素坐标。");
                if (cancellationToken.IsCancellationRequested) { completion.TrySetResult(null); return; }
                var bounds = Forms.SystemInformation.VirtualScreen;
                using var snapshot = CaptureDesktop(bounds);
                using var window = new ScreenColorPickerWindow(snapshot, bounds, cancellationToken);
                Forms.Application.Run(window);
                completion.TrySetResult(cancellationToken.IsCancellationRequested ? null : window.SelectedColor);
            }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally { if (previousDpi != IntPtr.Zero) SetThreadDpiAwarenessContext(previousDpi); }
        }) { IsBackground = true, Name = "IDVB screen color picker" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    private static Bitmap CaptureDesktop(Rectangle bounds)
    {
        // Graphics.CopyFromScreen rejects combined raster-operation flags. BitBlt
        // accepts CAPTUREBLT so layered windows are included as well.
        var snapshot = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(snapshot);
            var desktop = GetDC(IntPtr.Zero);
            if (desktop == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var destination = graphics.GetHdc();
                try
                {
                    if (!BitBlt(destination, 0, 0, bounds.Width, bounds.Height, desktop,
                        bounds.X, bounds.Y, 0x00CC0020 | 0x40000000))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                finally { graphics.ReleaseHdc(destination); }
            }
            finally { ReleaseDC(IntPtr.Zero, desktop); }
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);
}

/// <summary>One temporary, unscaled desktop snapshot. Clicks never reach the window below.</summary>
internal sealed class ScreenColorPickerWindow : Forms.Form
{
    private readonly Bitmap _snapshot;
    private readonly Rectangle _screenBounds;
    private readonly CancellationToken _cancellationToken;
    private readonly Forms.Timer _timer = new() { Interval = 40 };
    private Point _cursor;
    private bool _shown;
    internal Color? SelectedColor { get; private set; }

    internal ScreenColorPickerWindow(Bitmap snapshot, Rectangle screenBounds, CancellationToken cancellationToken)
    {
        if (snapshot.Size != screenBounds.Size) throw new ArgumentException("取色截图尺寸与屏幕不一致。");
        _snapshot = snapshot;
        _screenBounds = screenBounds;
        _cancellationToken = cancellationToken;
        FormBorderStyle = Forms.FormBorderStyle.None;
        StartPosition = Forms.FormStartPosition.Manual;
        AutoScaleMode = Forms.AutoScaleMode.None;
        Bounds = screenBounds;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Forms.Cursors.Cross;
        Text = "屏幕取色";
        _timer.Tick += (_, _) =>
        {
            if (_cancellationToken.IsCancellationRequested) { Close(); return; }
            var screen = Forms.Cursor.Position;
            var next = new Point(screen.X - _screenBounds.X, screen.Y - _screenBounds.Y);
            if (_cursor != next) { _cursor = next; Invalidate(); }
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = _screenBounds;
        _shown = true;
        if (_cancellationToken.IsCancellationRequested) { Close(); return; }
        _timer.Start();
        Activate();
    }

    internal static Color SampleAtScreenPoint(Bitmap snapshot, Rectangle screenBounds, Point screenPoint)
    {
        if (!screenBounds.Contains(screenPoint)) throw new ArgumentOutOfRangeException(nameof(screenPoint));
        return snapshot.GetPixel(screenPoint.X - screenBounds.X, screenPoint.Y - screenBounds.Y);
    }

    internal void SelectAtScreenPoint(Point screenPoint)
    {
        if (_screenBounds.Contains(screenPoint) && !_cancellationToken.IsCancellationRequested)
            SelectedColor = SampleAtScreenPoint(_snapshot, _screenBounds, screenPoint);
        Close();
    }

    protected override void OnMouseUp(Forms.MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == Forms.MouseButtons.Left) SelectAtScreenPoint(Forms.Cursor.Position);
        else if (e.Button == Forms.MouseButtons.Right) Close();
    }

    protected override bool ProcessCmdKey(ref Forms.Message message, Forms.Keys keyData)
    {
        if (keyData == Forms.Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (_shown) Close();
    }

    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        e.Graphics.DrawImageUnscaled(_snapshot, Point.Empty);
        var point = new Point(Math.Clamp(_cursor.X, 0, _snapshot.Width - 1), Math.Clamp(_cursor.Y, 0, _snapshot.Height - 1));
        var sample = _snapshot.GetPixel(point.X, point.Y);
        const int width = 132, height = 164;
        var x = Math.Clamp(point.X + 24, 0, Math.Max(0, ClientSize.Width - width));
        var y = Math.Clamp(point.Y + 24, 0, Math.Max(0, ClientSize.Height - height));
        e.Graphics.FillRectangle(Brushes.Black, x, y, width, height);
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        var crop = Rectangle.Intersect(new Rectangle(point.X - 5, point.Y - 5, 11, 11),
            new Rectangle(Point.Empty, _snapshot.Size));
        var zoom = new Rectangle(x + 11 + (crop.X - point.X + 5) * 10,
            y + 8 + (crop.Y - point.Y + 5) * 10, crop.Width * 10, crop.Height * 10);
        e.Graphics.DrawImage(_snapshot, zoom, crop, GraphicsUnit.Pixel);
        e.Graphics.DrawRectangle(Pens.Black, x + 60, y + 57, 11, 11);
        e.Graphics.DrawRectangle(Pens.White, x + 61, y + 58, 9, 9);
        using var colorBrush = new SolidBrush(sample);
        e.Graphics.FillRectangle(colorBrush, x + 10, y + 124, 22, 22);
        using var font = new Font("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
        e.Graphics.DrawString($"#{sample.R:X2}{sample.G:X2}{sample.B:X2}", font, Brushes.White, x + 40, y + 122);
        e.Graphics.DrawString("Esc · 取消", font, Brushes.White, x + 40, y + 141);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
