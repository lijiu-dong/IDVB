using System.Diagnostics;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace IDVBuff.Lifecycle;

internal sealed partial class LoginWindow
{
    private Forms.CheckBox? _agreement;
    private Forms.Button? _agreementSubmit;

    private Forms.CheckBox Agreement(ref int y, Forms.Label error)
    {
        var check = new Forms.CheckBox
        {
            Text = "我已阅读并同意", AccessibleName = "我已阅读并同意软件许可和使用条款",
            Font = _smallFont, ForeColor = Title, Checked = false,
            Bounds = new Rectangle(0, y, ContentWidth, Scale(24))
        };
        _content.Controls.Add(check);
        y += Scale(26);
        const string text = "《软件许可》和《使用条款》";
        var links = new Forms.LinkLabel
        {
            Text = text, Font = _smallFont, ForeColor = Muted,
            LinkColor = Accent, ActiveLinkColor = Accent,
            Bounds = new Rectangle(Scale(20), y, ContentWidth - Scale(20), Scale(26))
        };
        links.Links.Clear();
        links.Links.Add(0, 6, "LICENSE");
        links.Links.Add(7, 6, "TERMS");
        links.LinkClicked += (_, args) =>
        {
            try
            {
                var start = CreateAgreementStartInfo((string)args.Link!.LinkData!);
                using var process = Process.Start(start) ?? throw new IOException("无法启动记事本。");
            }
            catch (Exception exception)
            {
                Diagnostics.StartupTimeline.Write("Opening legal document failed.", exception);
                error.ForeColor = Danger;
                error.Text = "无法打开协议，请稍后重试。";
            }
        };
        _content.Controls.Add(links);
        y += Scale(32);
        return check;
    }

    private void BindAgreement(Forms.CheckBox agreement, Forms.Button submit)
    {
        _agreement = agreement;
        _agreementSubmit = submit;
        agreement.CheckedChanged += (_, _) => UpdateAgreementButton();
        UpdateAgreementButton();
    }

    private void UpdateAgreementButton()
    {
        if (_agreementSubmit is { IsDisposed: false } submit)
            submit.Enabled = !_busy && _agreement?.Checked == true;
    }

    internal static ProcessStartInfo CreateAgreementStartInfo(string document) =>
        LegalDocumentReader.CreateStartInfo(document);
}
