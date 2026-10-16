using IDVBuff.Appearance;
using IDVBuff.Features.Accounts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private Grid CreateCoCreationGalleryViewport(StackPanel content)
    {
        _workflowHost.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _workflowHost.VerticalContentAlignment = VerticalAlignment.Stretch;
        var root = new Grid();
        // Bound the scroll viewport to the shell, rather than a gallery's desired width.
        void PageSizeChanged(object sender, SizeChangedEventArgs args) => root.Width = args.NewSize.Width;
        root.Loaded += (_, _) =>
        {
            SizeChanged -= PageSizeChanged;
            SizeChanged += PageSizeChanged;
            root.Width = ActualWidth;
        };
        root.Unloaded += (_, _) => SizeChanged -= PageSizeChanged;
        if (ActualWidth > 0) root.Width = ActualWidth;
        ApplyViewportConstraint(root);
        content.MaxWidth = 1120;
        content.HorizontalAlignment = HorizontalAlignment.Center;
        var scroll = new ScrollViewer
        {
            Content = content, Padding = new Thickness(40, 28, 40, 44),
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        void UpdateWidth()
        {
            if (scroll.ActualWidth <= 0) return;
            content.Width = Math.Max(1, Math.Min(1120, scroll.ActualWidth - 104));
        }
        scroll.Loaded += (_, _) => UpdateWidth();
        scroll.SizeChanged += (_, _) => UpdateWidth();
        root.Children.Add(scroll);
        return root;
    }

    private Button CreateCoCreationToolbarButton(object content, string name)
    {
        var button = new Button
        {
            Content = content, Padding = new Thickness(10, 7, 10, 7), MinHeight = 32,
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(7),
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            button.Resources["ButtonBackground" + state] = state switch
            {
                "PointerOver" => FluentTheme.Brush(this, ThemeToken.ControlHover),
                "Pressed" => FluentTheme.Brush(this, ThemeToken.ControlPressed),
                _ => transparent
            };
            button.Resources["ButtonBorderBrush" + state] = transparent;
            button.Resources["ButtonForeground" + state] = FluentTheme.Brush(this,
                state == "Disabled" ? ThemeToken.TextDisabled : ThemeToken.TextSecondary);
        }
        button.Background = transparent;
        button.BorderBrush = transparent;
        button.Foreground = FluentTheme.Brush(this, ThemeToken.TextSecondary);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    private static string CoCreationPublisherLabel(string? handle, string? publishedName)
    {
        if (IsCoCreationOwner(handle) && AccountSession.Identity is { DisplayName: { } displayName }
            && !string.IsNullOrWhiteSpace(displayName)) return displayName.Trim();
        return !string.IsNullOrWhiteSpace(publishedName) ? publishedName.Trim() : "地图作者";
    }
}
