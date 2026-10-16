using IDVBuff.Appearance;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Numerics;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private TextBlock CoCreationHeading(string text, double size = 17) => new()
    {
        Text = text, FontSize = size, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = FluentTheme.Brush(this, "TextFillColorPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private Border CreateCoCreationBetaBadge() => new()
    {
        CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 3, 7, 3),
        Background = FluentTheme.Brush(this, "AccentFillColorDefaultBrush"), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = "BETA", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = FluentTheme.Brush(this, "TextOnAccentFillColorPrimaryBrush") }
    };

    private TextBlock CreateCoCreationDescription(string text) => new()
    {
        Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap,
        Foreground = FluentTheme.Brush(this, ThemeToken.TextSecondary)
    };

    private Button CreateCoCreationCard(string title, string subtitle, string? previewPath, Func<Task> open, bool opensClass = false,
        string? actionLabel = null, string errorTitle = "无法打开地图", string? badgeText = null)
    {
        var content = new StackPanel { Spacing = 10 };
        var preview = new Image { Stretch = Stretch.Fill };
        var placeholder = new FontIcon { Glyph = "\uE8B9", FontSize = 36,
            Foreground = FluentTheme.Brush(this, ThemeToken.TextMuted) };
        var scene = new Grid();
        scene.Children.Add(placeholder);
        var imagePlane = new Canvas();
        imagePlane.Children.Add(preview);
        scene.Children.Add(imagePlane);
        // A gallery cover uses a crop; the source map and editor geometry are untouched.
        ClipCoCreationCover(scene);
        var cover = new Grid { Height = 192, Background = FluentTheme.CardBrush(this) };
        cover.Children.Add(scene);
        void UpdatePreviewCrop()
        {
            if (preview.Source is not BitmapImage { PixelWidth: > 0, PixelHeight: > 0 } bitmap
                || cover.ActualWidth <= 0 || cover.ActualHeight <= 0) return;
            // Fit the decoded cover around its centre. Canvas owns the
            // crop offset, independently of Image's intrinsic measure/arrange size.
            var scale = Math.Max(cover.ActualWidth / bitmap.PixelWidth, cover.ActualHeight / bitmap.PixelHeight);
            preview.Width = bitmap.PixelWidth * scale;
            preview.Height = bitmap.PixelHeight * scale;
            Canvas.SetLeft(preview, (cover.ActualWidth - preview.Width) / 2);
            Canvas.SetTop(preview, (cover.ActualHeight - preview.Height) / 2);
        }
        cover.SizeChanged += (_, args) =>
        {
            scene.Width = Math.Max(1, args.NewSize.Width);
            scene.Height = Math.Max(1, args.NewSize.Height);
            UpdatePreviewCrop();
        };
        var focusOutline = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(2),
            BorderBrush = FluentTheme.Brush(this, ThemeToken.TextMuted), Visibility = Visibility.Collapsed,
            IsHitTestVisible = false };
        cover.Children.Add(focusOutline);
        ClipCoCreationCover(cover);
        content.Children.Add(cover);
        var metadata = new StackPanel { Spacing = 5 };
        var titleBlock = new TextBlock { Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, Foreground = FluentTheme.Brush(this, ThemeToken.Text) };
        metadata.Children.Add(titleBlock);
        var byline = new Grid { ColumnSpacing = 12 };
        byline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        byline.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        byline.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = FluentTheme.Brush(this, ThemeToken.TextMuted) });
        if (!string.IsNullOrWhiteSpace(badgeText))
        {
            var count = new TextBlock { Text = badgeText, FontSize = 12,
                Foreground = FluentTheme.Brush(this, ThemeToken.TextMuted) };
            Grid.SetColumn(count, 1);
            byline.Children.Add(count);
        }
        metadata.Children.Add(byline);
        content.Children.Add(metadata);
        var button = new Button { Content = content, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top,
            // Only the cover is rounded; rounding the button also clips the metadata at its bottom corners.
            HorizontalContentAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(0), UseSystemFocusVisuals = false };
        var state = new CoCreationCardContent { Cover = scene, Title = titleBlock, Byline = byline,
            CoverReady = string.IsNullOrEmpty(previewPath) };
        button.Tag = state;
        void Ready(bool loaded)
        {
            if (loaded) UpdatePreviewCrop();
            placeholder.Visibility = loaded ? Visibility.Collapsed : Visibility.Visible;
            state.CoverReady = true;
            state.RevealCover?.Invoke();
        }
        preview.ImageOpened += (_, _) => Ready(true);
        preview.ImageFailed += (_, _) => Ready(false);
        if (!string.IsNullOrEmpty(previewPath))
        {
            MemoryImageBinding.Attach(preview, new Uri(previewPath), 640, "CoCreation.Cover/" + previewPath);
        }
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var stateName in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            button.Resources["ButtonBackground" + stateName] = transparent;
            button.Resources["ButtonBorderBrush" + stateName] = transparent;
            button.Resources["ButtonForeground" + stateName] = FluentTheme.Brush(this, ThemeToken.Text);
        }
        button.Background = transparent;
        button.BorderBrush = transparent;
        button.SizeChanged += (_, args) => cover.Height = Math.Max(1, args.NewSize.Width * .75);
        AttachHoverFeedback(button);
        button.GotFocus += (_, _) => focusOutline.Visibility = button.FocusState == FocusState.Keyboard
            ? Visibility.Visible : Visibility.Collapsed;
        button.LostFocus += (_, _) => focusOutline.Visibility = Visibility.Collapsed;
        button.IsEnabledChanged += (_, _) =>
        {
            button.Opacity = button.IsEnabled ? 1 : .55;
            preview.Opacity = 1;
        };
        var label = actionLabel ?? (opensClass ? $"查看{title}中的地图" : $"打开{title}的地图编辑器");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{label}，{subtitle} {badgeText}".Trim());
        ToolTipService.SetToolTip(button, $"{title}\n{subtitle}" + (badgeText is null ? string.Empty : $" · {badgeText}"));
        button.Click += async (_, _) =>
        {
            if (_coCreationOpening) return;
            var revision = _coCreationViewRevision;
            _coCreationOpening = true;
            button.IsEnabled = false;
            try { await open(); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { if (IsLoaded && revision == _coCreationViewRevision) await ShowMessageAsync(errorTitle, exception.Message); }
            finally
            {
                if (revision == _coCreationViewRevision) _coCreationOpening = false;
                button.IsEnabled = true;
            }
        };
        return button;
    }

    private static void ClipCoCreationCover(FrameworkElement element)
    {
        Microsoft.UI.Composition.CompositionRoundedRectangleGeometry? geometry = null;
        void UpdateClip()
        {
            if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
            var visual = ElementCompositionPreview.GetElementVisual(element);
            if (geometry is null)
            {
                geometry = visual.Compositor.CreateRoundedRectangleGeometry();
                geometry.CornerRadius = new Vector2(14);
                visual.Clip = visual.Compositor.CreateGeometricClip(geometry);
            }
            geometry.Size = new Vector2((float)element.ActualWidth, (float)element.ActualHeight);
        }
        element.Loaded += (_, _) => UpdateClip();
        element.SizeChanged += (_, _) => UpdateClip();
        element.Unloaded += (_, _) =>
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var clip = visual.Clip;
            visual.Clip = null;
            clip?.Dispose();
            geometry?.Dispose();
            geometry = null;
        };
    }

    private static string? ResolveCoCreationCover(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var uri = new Uri(new Uri("https://community.idvb.xgflee.com/"), path);
        return uri.Scheme == "https" && uri.Host == "community.idvb.xgflee.com" ? uri.AbsoluteUri : null;
    }
}
