using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace IDVBuff.Lifecycle;

internal sealed partial class LoginWindow
{
    private readonly ActionButton _closeButton;
    private float _layoutScale = 1;
    private int _pageHeight = 420;
    private readonly List<Font> _retiredFonts = [];
    private int _pageVersion;

    internal static Rectangle CalculateBounds(Rectangle workArea, int dpi, int pageHeight)
    {
        var scale = Math.Min(Math.Max(1, dpi / 96d), Math.Min(workArea.Width * .92 / BaseWidth, workArea.Height * .92 / pageHeight));
        var size = new Size((int)Math.Round(BaseWidth * scale), (int)Math.Round(pageHeight * scale));
        return new(workArea.X + (workArea.Width - size.Width) / 2, workArea.Y + (workArea.Height - size.Height) / 2, size.Width, size.Height);
    }

    protected override void OnLoad(EventArgs e)
    {
        ApplyPageSize(_pageHeight);
        base.OnLoad(e);
    }

    protected override void OnDpiChanged(Forms.DpiChangedEventArgs e)
    {
        StopTransition();
        var oldScale = _layoutScale;
        base.OnDpiChanged(e);
        ApplyPageSize(_pageHeight, Forms.Screen.FromRectangle(e.SuggestedRectangle).WorkingArea);
        var ratio = _layoutScale / oldScale;
        void ResizeControls(Forms.Control parent)
        {
            foreach (Forms.Control control in parent.Controls)
            {
                control.Font = control is Forms.Label { Tag: "heading" } ? _titleFont : control is Forms.Label or Forms.LinkLabel ? _smallFont : _bodyFont;
                control.Bounds = new Rectangle((int)Math.Round(control.Left * ratio), (int)Math.Round(control.Top * ratio),
                    (int)Math.Round(control.Width * ratio), (int)Math.Round(control.Height * ratio));
                // InputFrame positions its native edit itself, keeping the caret vertically centered.
                if (control is not InputFrame and not VerificationCodeInput) ResizeControls(control);
            }
        }
        ResizeControls(_content);
        Invalidate(true);
    }

    private void ApplyPageSize(int height, Rectangle? area = null)
    {
        _pageHeight = height;
        var workArea = area ?? Forms.Screen.FromControl(this).WorkingArea;
        var targetBounds = CalculateBounds(workArea, DeviceDpi, height);
        if (_preparingTransition) _transitionTo = targetBounds;
        else Bounds = targetBounds;
        var scale = targetBounds.Width / (float)BaseWidth;
        if (Math.Abs(scale - _layoutScale) > .001f)
        {
            // WinForms may retain an equal Font instance instead of the replacement passed to its setter.
            // Keep fonts alive until every control has been disposed, including during DPI transitions.
            _retiredFonts.AddRange([_titleFont, _bodyFont, _smallFont]);
            _layoutScale = scale;
            _titleFont = new("Segoe UI", 20 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            _bodyFont = new("Segoe UI", 14 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _smallFont = new("Segoe UI", 12 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        }
        Font = _bodyFont;
        _closeButton.Font = _titleFont;
        _content.Bounds = new(Scale(32), Scale(76), Scale(BaseWidth - 64), Scale(height - 104));
        _closeButton.Bounds = new(Scale(BaseWidth - 44), Scale(8), Scale(32), Scale(32));
        Invalidate();
    }

    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var border = new Pen(Border);
        g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        using var muted = new SolidBrush(Muted);
        g.DrawString("Identity Vision Bridge", _bodyFont, muted, Scale(31), Scale(27));
    }

    private int Scale(int value) => (int)Math.Round(value * _layoutScale);
    private int ContentWidth => _content.ClientSize.Width;

    private int Page(string heading, string message, int height = 420, int messageHeight = 36)
    {
        if (!_preparingTransition) StopTransition();
        _pageVersion++;
        AcceptButton = null;
        _content.SuspendLayout();
        foreach (Forms.Control control in _content.Controls.Cast<Forms.Control>().ToArray()) control.Dispose();
        _content.Controls.Clear();
        var hasMessage = !string.IsNullOrWhiteSpace(message);
        ApplyPageSize(height + (hasMessage && messageHeight == 36 ? messageHeight : 0));
        _content.Controls.Add(new Forms.Label { Text = heading, Font = _titleFont, Tag = "heading", ForeColor = Title,
            Bounds = new(0, 0, ContentWidth, Scale(28)) });
        if (hasMessage)
            _content.Controls.Add(new Forms.Label { Text = message, Font = _smallFont, ForeColor = Muted,
                Bounds = new(0, Scale(34), ContentWidth, Scale(messageHeight)) });
        _content.ResumeLayout();
        return Scale(48 + (hasMessage ? messageHeight : 0));
    }

    private Forms.TextBox Input(string label, ref int y, string value, bool secret = false, int? width = null)
    {
        _content.Controls.Add(new Forms.Label { Text = label, Font = _smallFont, ForeColor = Muted,
            Bounds = new(0, y, ContentWidth, Scale(18)) });
        var box = new Forms.TextBox { Text = value, Font = _bodyFont, BorderStyle = Forms.BorderStyle.None,
            BackColor = Color.White, ForeColor = Title, UseSystemPasswordChar = secret, AccessibleName = label,
            MaxLength = secret ? 128 : 254 };
        var frame = new InputFrame(box) { Bounds = new(0, y + Scale(22), width ?? ContentWidth, Scale(40)), Font = _bodyFont };
        _content.Controls.Add(frame);
        y += Scale(72);
        return box;
    }

    private Forms.Label ErrorLabel(ref int y)
    {
        var label = new Forms.Label { Font = _smallFont, ForeColor = Danger, Bounds = new(0, y, ContentWidth, Scale(30)) };
        _content.Controls.Add(label);
        y += Scale(32);
        return label;
    }

    private Forms.Button Button(string text, ref int y, int x = 0, int? width = null)
    {
        var button = new ActionButton { Text = text, Font = _bodyFont, Bounds = new(x, y, width ?? ContentWidth, Scale(42)) };
        _content.Controls.Add(button);
        y += Scale(54);
        return button;
    }

    private void Link(string text, int x, ref int y, Action action)
    {
        var link = new Forms.LinkLabel { Text = text, Font = _smallFont, LinkColor = Accent, ActiveLinkColor = Accent,
            LinkBehavior = Forms.LinkBehavior.HoverUnderline, AutoSize = true, Location = new(x, y), Padding = new(0, Scale(2), 0, 0) };
        link.LinkClicked += (_, _) => { if (!_busy) Navigate(action); };
        _content.Controls.Add(link);
        y += Scale(30);
    }

    private sealed class InputFrame : Forms.Panel
    {
        private readonly Forms.TextBox _edit;
        public InputFrame(Forms.TextBox edit)
        {
            _edit = edit;
            BackColor = Background;
            DoubleBuffered = true;
            Controls.Add(edit);
            edit.GotFocus += (_, _) => Invalidate();
            edit.LostFocus += (_, _) => Invalidate();
            Click += (_, _) => edit.Focus();
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            var inset = Math.Max(8, Height / 3);
            _edit.Font = Font;
            _edit.Bounds = new(inset, (Height - _edit.PreferredHeight) / 2, Math.Max(1, Width - inset * 2), _edit.PreferredHeight);
        }
        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_edit is not null) OnResize(EventArgs.Empty);
        }
        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var outline = Rounded(new(1, 1, Width - 3, Height - 3), Math.Max(4, Height / 6));
            using var fill = new SolidBrush(Color.White);
            using var pen = new Pen(_edit.Focused ? Accent : Border, _edit.Focused ? 2 : 1);
            e.Graphics.FillPath(fill, outline);
            e.Graphics.DrawPath(pen, outline);
        }
    }

    private sealed class ActionButton : Forms.Button
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool IsQuiet { get; init; }
        public ActionButton()
        {
            FlatStyle = Forms.FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Forms.Cursors.Hand;
            SetStyle(Forms.ControlStyles.UserPaint | Forms.ControlStyles.AllPaintingInWmPaint | Forms.ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Background);
            var hover = ClientRectangle.Contains(PointToClient(Forms.Cursor.Position));
            if (!IsQuiet || hover)
            {
                using var path = Rounded(new(0, 0, Width - 1, Height - 1), Math.Max(4, Height / 7));
                using var fill = new SolidBrush(IsQuiet ? Color.FromArgb(234, 238, 244) : !Enabled ? Color.FromArgb(163, 185, 221)
                    : hover ? Color.FromArgb(36, 96, 206) : Accent);
                e.Graphics.FillPath(fill, path);
            }
            Forms.TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, IsQuiet ? Muted : Color.White,
                Forms.TextFormatFlags.HorizontalCenter | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues) Forms.ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5));
        }
    }

    private sealed class DomainSelector : Forms.Button
    {
        private readonly Forms.ContextMenuStrip _menu = new();
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal string? SelectedDomain { get; private set; }
        internal DomainSelector(IReadOnlyList<string> domains)
        {
            FlatStyle = Forms.FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Forms.Cursors.Hand;
            SetStyle(Forms.ControlStyles.UserPaint | Forms.ControlStyles.AllPaintingInWmPaint | Forms.ControlStyles.OptimizedDoubleBuffer, true);
            foreach (var domain in domains)
                _menu.Items.Add("@" + domain, null, (_, _) => SelectDomain(domain));
            SelectDomain(domains.FirstOrDefault());
            Click += (_, _) => { _menu.Font = Font; _menu.Show(this, new Point(0, Height)); };
        }
        private void SelectDomain(string? domain)
        {
            SelectedDomain = domain;
            Text = domain is null ? "选择后缀" : "@" + domain;
            Invalidate();
        }
        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            e.Graphics.Clear(Background);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var outline = Rounded(new(1, 1, Width - 3, Height - 3), Math.Max(4, Height / 6));
            using var fill = new SolidBrush(Color.White);
            using var pen = new Pen(Focused ? Accent : Border, Focused ? 2 : 1);
            e.Graphics.FillPath(fill, outline);
            e.Graphics.DrawPath(pen, outline);
            var inset = Math.Max(8, Height / 3);
            Forms.TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(inset, 0, Width - inset * 3, Height), Title,
                Forms.TextFormatFlags.Left | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.SingleLine);
            var x = Width - inset; var y = Height / 2; var half = Math.Max(3, Height / 12);
            using var arrow = new SolidBrush(Muted);
            e.Graphics.FillPolygon(arrow, [new(x - half, y - half / 2), new(x + half, y - half / 2), new(x, y + half)]);
        }
        protected override void Dispose(bool disposing) { if (disposing) _menu.Dispose(); base.Dispose(disposing); }
    }

    private static GraphicsPath Rounded(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
