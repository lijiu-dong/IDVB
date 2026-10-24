using System.Diagnostics;
using IDVBuff.Lifecycle;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class SettingsPage
{
    private StackPanel CreateLegalReadingLinks()
    {
        var links = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 16
        };
        foreach (var (label, document) in new[] { ("阅读《软件许可》", "LICENSE"), ("阅读《使用条款》", "TERMS") })
        {
            var link = new HyperlinkButton { Content = label };
            link.Click += async (_, _) =>
            {
                try
                {
                    using var process = Process.Start(LegalDocumentReader.CreateStartInfo(document))
                        ?? throw new IOException("无法启动记事本。");
                }
                catch (Exception exception)
                {
                    Diagnostics.StartupTimeline.Write("Opening legal document from About failed.", exception);
                    await new ContentDialog
                    {
                        XamlRoot = XamlRoot,
                        Title = "无法打开协议",
                        Content = "无法启动记事本或读取协议文件，请稍后重试。",
                        CloseButtonText = "知道了"
                    }.ShowThemedAsync();
                }
            };
            links.Children.Add(link);
        }
        return links;
    }
}
