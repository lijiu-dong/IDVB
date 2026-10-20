using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Forms = System.Windows.Forms;

namespace IDVBuff.Lifecycle;

internal sealed partial class LoginWindow
{
    private readonly Forms.Timer _transitionTimer = new() { Interval = 16 };
    private readonly TransitionSurface _transitionSurface = new();
    private long _transitionStarted;
    private Rectangle _transitionFrom, _transitionTo;
    private Forms.Control? _transitionFocus;
    private bool _preparingTransition;

    private void InitializeTransition()
    {
        _transitionSurface.Visible = false;
        Controls.Add(_transitionSurface);
        _transitionTimer.Tick += (_, _) =>
        {
            var elapsed = Stopwatch.GetElapsedTime(_transitionStarted).TotalMilliseconds;
            var progress = Math.Clamp(elapsed / 170, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            int Between(int a, int b) => (int)Math.Round(a + (b - a) * eased);
            Bounds = new(Between(_transitionFrom.X, _transitionTo.X), Between(_transitionFrom.Y, _transitionTo.Y),
                Between(_transitionFrom.Width, _transitionTo.Width), Between(_transitionFrom.Height, _transitionTo.Height));
            _transitionSurface.Progress = (float)eased;
            _transitionSurface.Invalidate();
            if (progress >= 1) StopTransition();
        };
    }

    private void Navigate(Action showPage)
    {
        StopTransition();
        if (!Visible || _content.Width < 1 || _content.Height < 1)
        {
            showPage();
            return;
        }
        var from = Bounds;
        var version = _pageVersion;
        var before = new Bitmap(_content.Width, _content.Height);
        try
        {
            _content.DrawToBitmap(before, _content.ClientRectangle);
            _transitionFrom = _transitionTo = from;
            _transitionSurface.SetFrames(before, null);
            before = null!;
            _transitionSurface.Bounds = _content.Bounds;
            _transitionSurface.Progress = 0;
            _transitionSurface.Visible = true;
            _transitionSurface.BringToFront();
            _closeButton.BringToFront();
            // Build the destination behind the snapshot. Applying its native window bounds here
            // would resize to the destination, jump back, and only then start the animation.
            _preparingTransition = true;
            try { showPage(); }
            finally { _preparingTransition = false; }
            if (IsDisposed || version == _pageVersion) { StopTransition(); return; }
            var after = new Bitmap(_content.Width, _content.Height);
            try
            {
                _content.DrawToBitmap(after, _content.ClientRectangle);
                _transitionFocus = ActiveControl;
                _transitionSurface.SetAfterFrame(after);
                after = null!;
                _content.Visible = false;
                _transitionSurface.Size = new(Math.Max(_transitionSurface.Width, _content.Width),
                    Math.Max(_transitionSurface.Height, _content.Height));
                _transitionStarted = Stopwatch.GetTimestamp();
                _transitionTimer.Start();
            }
            finally { after?.Dispose(); }
        }
        catch { StopTransition(); throw; }
        finally { before?.Dispose(); }
    }

    private void StopTransition()
    {
        _transitionTimer.Stop();
        if (_transitionSurface.Visible)
        {
            Bounds = _transitionTo;
            _content.Visible = true;
            _transitionSurface.Visible = false;
            if (_transitionFocus is { IsDisposed: false } focus) focus.Focus();
        }
        _transitionSurface.ClearFrames();
        _transitionFocus = null;
    }

    private sealed class TransitionSurface : Forms.Control
    {
        private Bitmap? _before, _after;
        internal float Progress;
        internal TransitionSurface()
        {
            BackColor = Background;
            SetStyle(Forms.ControlStyles.UserPaint | Forms.ControlStyles.AllPaintingInWmPaint
                | Forms.ControlStyles.OptimizedDoubleBuffer, true);
        }
        internal void SetFrames(Bitmap before, Bitmap? after) { ClearFrames(); _before = before; _after = after; }
        internal void SetAfterFrame(Bitmap after) { _after?.Dispose(); _after = after; }
        internal void ClearFrames() { _before?.Dispose(); _after?.Dispose(); _before = _after = null; }
        protected override void Dispose(bool disposing) { if (disposing) ClearFrames(); base.Dispose(disposing); }
        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            e.Graphics.Clear(Background);
            void Draw(Bitmap? bitmap, float opacity, int offset)
            {
                if (bitmap is null || opacity <= 0) return;
                using var attributes = new ImageAttributes();
                attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
                e.Graphics.DrawImage(bitmap, new Rectangle(0, offset, bitmap.Width, bitmap.Height),
                    0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, attributes);
            }
            Draw(_before, 1 - Progress, -(int)(8 * Progress));
            Draw(_after, Progress, (int)(8 * (1 - Progress)));
        }
    }
}
