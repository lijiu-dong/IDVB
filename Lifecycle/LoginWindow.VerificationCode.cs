using System.Drawing;
using Forms = System.Windows.Forms;

namespace IDVBuff.Lifecycle;

internal sealed partial class LoginWindow
{
    private sealed class VerificationCodeInput : Forms.Panel
    {
        private readonly Forms.TextBox[] _digits = new Forms.TextBox[6];
        private bool _updating;
        internal string Value => string.Concat(_digits.Select(edit => edit.Text));

        internal VerificationCodeInput()
        {
            DoubleBuffered = true;
            BackColor = Background;
            TabStop = false;
            for (var i = 0; i < _digits.Length; i++)
            {
                var index = i;
                var edit = new CodeDigit { BorderStyle = Forms.BorderStyle.None,
                    BackColor = Background, ForeColor = Title, TextAlign = Forms.HorizontalAlignment.Center,
                    MaxLength = 6, ShortcutsEnabled = true, ImeMode = Forms.ImeMode.Disable,
                    AccessibleName = $"验证码第 {i + 1} 位", TabIndex = i };
                _digits[i] = edit;
                Controls.Add(edit);
                edit.Enter += (_, _) => { edit.SelectAll(); Invalidate(); };
                edit.Leave += (_, _) => Invalidate();
                edit.KeyPress += (_, e) =>
                {
                    if (!char.IsControl(e.KeyChar) && e.KeyChar is not (>= '0' and <= '9')) e.Handled = true;
                    else if (!char.IsControl(e.KeyChar)) edit.SelectAll();
                };
                edit.KeyDown += (_, e) =>
                {
                    var target = e.KeyCode switch
                    {
                        Forms.Keys.Left => index - 1,
                        Forms.Keys.Right => index + 1,
                        Forms.Keys.Back when edit.Text.Length == 0 => index - 1,
                        _ => -1
                    };
                    if (target < 0 || target >= _digits.Length) return;
                    if (e.KeyCode == Forms.Keys.Back) _digits[target].Clear();
                    FocusDigit(target);
                    e.SuppressKeyPress = true;
                };
                edit.TextChanged += (_, _) => UpdateDigits(index);
            }
        }

        internal void FocusFirstEmpty()
        {
            var index = Array.FindIndex(_digits, edit => edit.Text.Length == 0);
            FocusDigit(index >= 0 ? index : 5);
        }

        private void FocusDigit(int index)
        {
            _digits[index].Select();
            _digits[index].SelectAll();
        }

        private void UpdateDigits(int index)
        {
            if (_updating) return;
            _updating = true;
            try
            {
                var input = new string(_digits[index].Text.Where(c => c is >= '0' and <= '9').ToArray());
                // A complete pasted code replaces all six slots, even when pasted in a later slot.
                var start = input.Length == 6 ? 0 : index;
                _digits[index].Text = "";
                for (var i = 0; i < input.Length && start + i < _digits.Length; i++)
                    _digits[start + i].Text = input[i].ToString();
                if (input.Length > 0) FocusDigit(Math.Min(start + input.Length, 5));
                Invalidate();
            }
            finally { _updating = false; }
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutDigits(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); LayoutDigits(); }

        private void LayoutDigits()
        {
            if (_digits[0] is null) return;
            var slot = Math.Min(Width / 6, (int)Math.Round(40 * Font.Size / 14));
            var left = (Width - slot * 6) / 2;
            var gap = Math.Max(4, slot / 5);
            for (var i = 0; i < _digits.Length; i++)
            {
                var edit = _digits[i];
                edit.Font = Font;
                edit.Bounds = new(left + slot * i + gap / 2, Math.Max(0, (Height - edit.PreferredHeight) / 2 - 2),
                    Math.Max(1, slot - gap), edit.PreferredHeight);
            }
            Invalidate();
        }

        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            base.OnPaint(e);
            foreach (var edit in _digits)
            {
                using var pen = new Pen(edit.Focused ? Accent : Muted, Math.Max(1, Height / 20f));
                e.Graphics.DrawLine(pen, edit.Left, Height - 2, edit.Right, Height - 2);
            }
        }

        private sealed class CodeDigit : Forms.TextBox
        {
            protected override void WndProc(ref Forms.Message m)
            {
                if (m.Msg == 0x0302) SelectAll(); // WM_PASTE: replace the current slot before distributing digits.
                base.WndProc(ref m);
            }
        }
    }
}
