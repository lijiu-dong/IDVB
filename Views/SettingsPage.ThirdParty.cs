using System.IO;
using System.Text.Json;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class SettingsPage
{
    private const string ThirdPartyResourcePrefix = "IDVB.ThirdParty.";

    private Button CreateThirdPartyCard() => CreateSpecificationCard(
        "第三方组件与开源许可",
        "开源致谢",
        "感谢为 IDVB 提供图像处理、图形绘制、数据存储和更新能力的项目与贡献者。查看用途、版本和许可原文。",
        Symbol.Library,
        ThirdPartyCard_Click);

    private async void ThirdPartyCard_Click(object sender, RoutedEventArgs e)
    {
        var content = new StackPanel { Spacing = 12, Padding = new Thickness(24, 0, 24, 16) };
        content.Children.Add(new TextBlock
        {
            Text = "以下列出 IDVB 使用的主要第三方组件。组件及随附代码适用各自的许可，IDVB 的非商业源码许可不会替代或限制这些条款。展开组件可离线阅读随附许可与声明。",
            TextWrapping = TextWrapping.Wrap
        });
        using var catalog = JsonDocument.Parse(ReadThirdPartyResource("catalog.json"));
        foreach (var section in catalog.RootElement.EnumerateArray())
        {
            content.Children.Add(new TextBlock
            {
                Text = section.GetProperty("title").GetString(),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0)
            });
            foreach (var component in section.GetProperty("components").EnumerateArray())
                content.Children.Add(CreateThirdPartyExpander(component));
        }
        var viewport = new ScrollViewer
        {
            // Reach the native dialog's edges while keeping text padding inside the scrolled content.
            Margin = new Thickness(-24, 0, -24, -24),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "第三方组件与开源许可",
            Content = viewport,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close
        };
        var xamlRoot = XamlRoot;
        void UpdateViewportSize()
        {
            var width = Math.Min(760, Math.Max(0, xamlRoot.Size.Width - 48));
            dialog.Resources["ContentDialogMinWidth"] = width;
            dialog.Resources["ContentDialogMaxWidth"] = width;
            // A fixed viewport prevents expanded notices from changing the dialog's measured size.
            viewport.Height = Math.Min(520, Math.Max(0, xamlRoot.Size.Height - 224));
        }
        void RootChanged(XamlRoot _, XamlRootChangedEventArgs args) => UpdateViewportSize();
        UpdateViewportSize();
        xamlRoot.Changed += RootChanged;
        try
        {
            await dialog.ShowThemedAsync();
        }
        finally
        {
            xamlRoot.Changed -= RootChanged;
        }
    }

    private Expander CreateThirdPartyExpander(JsonElement component)
    {
        var name = component.GetProperty("name").GetString()!;
        var license = component.GetProperty("license").GetString()!;
        var header = new StackPanel { Spacing = 3 };
        header.Children.Add(new TextBlock
        {
            Text = name,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        header.Children.Add(new TextBlock
        {
            Text = $"{component.GetProperty("version").GetString()} · {license}",
            FontSize = 12,
            Foreground = SecondaryTextBrush,
            TextWrapping = TextWrapping.Wrap
        });

        // Capture values before the catalog JsonDocument is disposed. Load lengthy notices on demand.
        var purpose = component.GetProperty("purpose").GetString()!;
        var packages = component.GetProperty("packages").GetString()!;
        var projectUri = new Uri(component.GetProperty("projectUrl").GetString()!);
        var licenseUri = new Uri(component.GetProperty("licenseUrl").GetString()!);
        var noticeFiles = component.GetProperty("notices").EnumerateArray()
            .Select(file => file.GetString()!).ToArray();
        var expander = new Expander
        {
            Header = header,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        expander.Expanding += (_, _) =>
        {
            if (expander.Content is not null)
                return;
            var details = new StackPanel { Spacing = 10 };
            details.Children.Add(new TextBlock { Text = purpose, TextWrapping = TextWrapping.Wrap });
            details.Children.Add(new TextBlock
            {
                Text = packages,
                FontSize = 12,
                Foreground = SecondaryTextBrush,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });
            var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            links.Children.Add(new HyperlinkButton
            {
                Content = "项目主页", NavigateUri = projectUri, Padding = new Thickness(0)
            });
            links.Children.Add(new HyperlinkButton
            {
                Content = "许可来源", NavigateUri = licenseUri, Padding = new Thickness(0)
            });
            details.Children.Add(links);
            details.Children.Add(new ScrollViewer
            {
                MaxHeight = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new TextBlock
                {
                    Text = string.Join("\n\n", noticeFiles.Select(ReadThirdPartyResource)),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                }
            });
            expander.Content = details;
        };
        return expander;
    }

    private static string ReadThirdPartyResource(string fileName)
    {
        using var stream = typeof(SettingsPage).Assembly.GetManifestResourceStream(ThirdPartyResourcePrefix + fileName)
            ?? throw new InvalidOperationException($"Missing third-party notice resource: {fileName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
