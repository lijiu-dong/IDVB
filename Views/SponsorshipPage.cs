using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.System;
using IDVBuff.Core.Diagnostics;
using IDVBuff.Features.Maps;
using Windows.Graphics.Imaging;

namespace IDVBuff.Views;

public sealed class SponsorshipPage : Page
{
    private readonly Image _wechat = new();
    private readonly Image _alipay = new();
    private readonly Border _banner = new();
    private readonly Grid _payments = new() { ColumnSpacing = 16, RowSpacing = 16 };
    private readonly InfoBar _error = new()
    {
        Severity = InfoBarSeverity.Error, IsClosable = false,
        Title = "收款码不可用",
        Message = "收款资源无法验证，已停止显示。请从 IDVB 官方渠道重新获取程序。"
    };
    private int _loadRevision;
    private CancellationTokenSource? _artworkCancellation;
    private PaymentArtwork? _wechatArtwork, _alipayArtwork;
    private readonly List<Action> _retireActions = [];
    private GentleSponsorshipMotion? _decorationMotion;
    private bool _retired;

    internal bool HasVerifiedArtwork => _wechatArtwork is { Ready: true } && _alipayArtwork is { Ready: true }
        && ReferenceEquals(_wechat.Source, _wechatArtwork.Source) && ReferenceEquals(_alipay.Source, _alipayArtwork.Source);

    public event EventHandler? BackRequested;

    public SponsorshipPage()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Content = CreateContent();
        Loaded += LoadArtwork;
        Unloaded += PageUnloaded;
    }

    private void PageUnloaded(object sender, RoutedEventArgs args) => DeactivateArtwork();

    // The shell creates a fresh page on every visit. Permanent retirement is
    // distinct from a temporary Unloaded/reload and ends all page-owned cycles.
    internal void Retire()
    {
        if (_retired) return;
        _retired = true;
        MemoryImageBinding.ReleaseOwned(this);
        DeactivateArtwork();
        Loaded -= LoadArtwork;
        Unloaded -= PageUnloaded;
        _decorationMotion?.Dispose();
        _decorationMotion = null;
        foreach (var release in _retireActions) release();
        _retireActions.Clear();
        BackRequested = null;
        Content = null;
        _banner.Child = null;
        _payments.Children.Clear();
        MemoryJournal.Current?.Record("sponsorship-page-retired", new { revision = _loadRevision,
            meaning = "Permanent shell replacement; artwork, animation subscriptions and child event cycles released" });
    }

    internal void DeactivateArtwork()
    {
        ++_loadRevision;
        _artworkCancellation?.Cancel();
        ReleaseArtwork();
        _payments.Visibility = Visibility.Collapsed;
        MemoryJournal.Current?.Record("sponsorship-artwork-deactivated", new { revision = _loadRevision, IsLoaded });
    }

    private FrameworkElement CreateContent()
    {
        var root = new StackPanel
        {
            Margin = new Thickness(32, 24, 32, 36), MaxWidth = 900, Spacing = 16,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        var back = new Button { Content = new SymbolIcon(Symbol.Back), Padding = new Thickness(8) };
        AutomationProperties.SetName(back, "返回");
        ToolTipService.SetToolTip(back, "返回");
        RoutedEventHandler backClicked = (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        back.Click += backClicked;
        _retireActions.Add(() => back.Click -= backClicked);
        heading.Children.Add(back);
        heading.Children.Add(Label("赞助支持", 28, primary: true));
        root.Children.Add(heading);
        root.Children.Add(CreateBanner());
        root.Children.Add(Label("每一份支持，都会成为 IDVB 继续维护与探索的动力。赞助完全自愿，感谢你的陪伴。", 14));
        _payments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _payments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _payments.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _payments.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var wechat = CreatePaymentCard("微信支付", "佳霖（**东）", "使用微信扫一扫", _wechat,
            new Rect(208, 262, 412, 412), 828, 1124);
        var alipay = CreatePaymentCard("支付宝", "对镜自演（**东）", "使用支付宝扫一扫", _alipay,
            new Rect(296, 922, 1116, 1116), 1708, 2560);
        Grid.SetColumn(alipay, 1);
        _payments.Children.Add(wechat);
        _payments.Children.Add(alipay);
        _payments.Visibility = Visibility.Collapsed;
        SizeChangedEventHandler paymentSizeChanged = (_, args) =>
        {
            var narrow = args.NewSize.Width < 560;
            _payments.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(alipay, narrow ? 0 : 1);
            Grid.SetRow(alipay, narrow ? 1 : 0);
        };
        _payments.SizeChanged += paymentSizeChanged;
        _retireActions.Add(() => _payments.SizeChanged -= paymentSizeChanged);
        root.Children.Add(_payments);
        root.Children.Add(_error);
        root.Children.Add(Label("付款前，请核对支付应用显示的收款人信息与上方一致。", 13));
        root.Children.Add(CreateAfdianCard());
        return root;
    }

    private Border CreateBanner()
    {
        var surface = new Grid { MinHeight = 160, Padding = new Thickness(28, 22, 28, 22) };
        var glow = new Border
        {
            Opacity = .12, CornerRadius = new CornerRadius(16), IsHitTestVisible = false
        };
        var accent = (SolidColorBrush)FluentTheme.Brush(this, IDVBuff.Appearance.ThemeToken.Accent);
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(1, 0) };
        var clear = new GradientStop { Offset = 0 };
        var color = new GradientStop { Offset = 1 };
        gradient.GradientStops.Add(clear);
        gradient.GradientStops.Add(color);
        glow.Background = gradient;
        void RefreshGradient()
        {
            color.Color = accent.Color;
            clear.Color = Windows.UI.Color.FromArgb(0, accent.Color.R, accent.Color.G, accent.Color.B);
            glow.Visibility = FluentTheme.Snapshot(this).IsHighContrast
                ? Visibility.Collapsed : Visibility.Visible;
        }
        void ThemeChanged(IDVBuff.Appearance.ThemeSnapshot snapshot) => RefreshGradient();
        long accentToken = 0;
        bool gradientActive = false;
        void ReleaseGradient()
        {
            if (!gradientActive) return;
            gradientActive = false;
            accent.UnregisterPropertyChangedCallback(SolidColorBrush.ColorProperty, accentToken);
            IDVBuff.Presentation.Theming.ThemeService.For(this).Changed -= ThemeChanged;
        }
        RoutedEventHandler gradientLoaded = (_, _) =>
        {
            if (gradientActive || _retired) return;
            gradientActive = true;
            accentToken = accent.RegisterPropertyChangedCallback(SolidColorBrush.ColorProperty, (_, _) => RefreshGradient());
            IDVBuff.Presentation.Theming.ThemeService.For(this).Changed += ThemeChanged;
            RefreshGradient();
        };
        RoutedEventHandler gradientUnloaded = (_, _) => ReleaseGradient();
        glow.Loaded += gradientLoaded;
        glow.Unloaded += gradientUnloaded;
        _retireActions.Add(() =>
        {
            ReleaseGradient();
            glow.Loaded -= gradientLoaded;
            glow.Unloaded -= gradientUnloaded;
        });
        var decoration = new Grid { IsHitTestVisible = false, Margin = new Thickness(-20) };
        decoration.Children.Add(glow);
        surface.Children.Add(decoration);
        var words = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var brandImage = new Image
        {
            Width = 40, Height = 40, Stretch = Stretch.Uniform
        };
        MemoryImageBinding.AttachOwned(this, brandImage,
            new Uri("ms-appx:///Assets/Square44x44Logo.scale-200.png"), "UI.Sponsorship.BrandArtwork");
        brand.Children.Add(brandImage);
        brand.Children.Add(Label("IDVB", 24, primary: true));
        words.Children.Add(brand);
        words.Children.Add(Label("让探索继续。", 34, primary: true));
        surface.Children.Add(words);
        _banner.Background = FluentTheme.CardBrush(this);
        _banner.BorderBrush = FluentTheme.Brush(this, "CardStrokeColorDefaultBrush");
        _banner.BorderThickness = new Thickness(1);
        _banner.CornerRadius = new CornerRadius(12);
        _banner.Child = surface;
        SizeChangedEventHandler bannerSizeChanged = (_, args) => _banner.Clip = new RectangleGeometry
            { Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height) };
        _banner.SizeChanged += bannerSizeChanged;
        _retireActions.Add(() => _banner.SizeChanged -= bannerSizeChanged);
        // Only the subtle accent decoration breathes; real text remains still and readable.
        _decorationMotion = new GentleSponsorshipMotion(decoration);
        return _banner;
    }

    private Border CreatePaymentCard(string title, string recipient, string instruction, Image image,
        Rect crop, double originalWidth, double originalHeight)
    {
        var body = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(Label(title, 18, primary: true));
        // Canvas measures the full-sized image independently of the crop viewport.
        // Grid would constrain the image's layout slot and clip it BEFORE a render
        // translation, leaving only the top-left fragment of the payment code.
        // Only the Canvas viewport clips; verified PNG bytes remain untouched.
        image.Width = originalWidth;
        image.Height = originalHeight;
        image.Stretch = Stretch.Fill;
        image.HorizontalAlignment = HorizontalAlignment.Left;
        image.VerticalAlignment = VerticalAlignment.Top;
        Canvas.SetLeft(image, -crop.X);
        Canvas.SetTop(image, -crop.Y);
        AutomationProperties.SetName(image, title + "收款码，收款人 " + recipient);
        var viewport = new Canvas
        {
            Width = crop.Width, Height = crop.Height,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, crop.Width, crop.Height) }
        };
        viewport.Children.Add(image);
        body.Children.Add(new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Viewbox { MaxWidth = 224, MaxHeight = 224, Stretch = Stretch.Uniform, Child = viewport }
        });
        var name = Label(recipient, 14, primary: true);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(name);
        var hint = Label(instruction, 12);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(hint);
        return Card(body);
    }

    private Border CreateAfdianCard()
    {
        var content = new Grid { ColumnSpacing = 16 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var copy = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(Label("也可以通过爱发电支持", 15, primary: true));
        copy.Children.Add(Label("选择你习惯的方式，点击后在浏览器打开。", 12));
        content.Children.Add(copy);
        var button = new Button { Content = "前往爱发电 ↗", VerticalAlignment = VerticalAlignment.Center };
        var action = new SponsorshipAction(async uri => await Launcher.LaunchUriAsync(uri),
            enabled => button.IsEnabled = enabled, async () => await new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "链接未打开",
                Content = "无法打开浏览器，请稍后重试。", CloseButtonText = "知道了"
            }.ShowThemedAsync());
        RoutedEventHandler actionClicked = async (_, _) => await action.ClickAsync();
        button.Click += actionClicked;
        _retireActions.Add(() => button.Click -= actionClicked);
        Grid.SetColumn(button, 1);
        content.Children.Add(button);
        return Card(content);
    }

    private Border Card(UIElement content) => new()
    {
        Padding = new Thickness(18, 14, 18, 14), CornerRadius = new CornerRadius(10),
        Background = FluentTheme.CardBrush(this), BorderThickness = new Thickness(1),
        BorderBrush = FluentTheme.Brush(this, "CardStrokeColorDefaultBrush"), Child = content
    };

    private TextBlock Label(string text, double size, bool primary = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = FluentTheme.Brush(this, primary ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush")
    };

    private async void LoadArtwork(object sender, RoutedEventArgs args)
    {
        if (_retired) return;
        var revision = ++_loadRevision;
        _artworkCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _artworkCancellation = cancellation;
        var token = cancellation.Token;
        ReleaseArtwork();
        _error.IsOpen = false;
        _payments.Visibility = Visibility.Collapsed;
        PaymentArtwork? wechatBitmap = null, alipayBitmap = null;
        try
        {
            using var operation = MemoryJournal.BeginOperation("UI.Sponsorship.Artwork", "payments");
            // Verify both recipients before exposing either payment code.
            var wechat = SponsorshipAssets.ReadVerified(SponsorshipAsset.WeChat);
            var alipay = SponsorshipAssets.ReadVerified(SponsorshipAsset.Alipay);

            wechatBitmap = await DecodeAsync(wechat, "WeChat", token);
            token.ThrowIfCancellationRequested();
            alipayBitmap = await DecodeAsync(alipay, "Alipay", token);
            token.ThrowIfCancellationRequested();

            if (revision != _loadRevision) return;
            _wechat.Source = wechatBitmap.Source;
            _alipay.Source = alipayBitmap.Source;
            _wechatArtwork = wechatBitmap;
            _alipayArtwork = alipayBitmap;

            _payments.Visibility = Visibility.Visible;
            wechatBitmap = alipayBitmap = null; // Ownership transferred to the page's images.
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (revision != _loadRevision) return;
            ReleaseArtwork();
            _payments.Visibility = Visibility.Collapsed;
            _error.IsOpen = true;
            System.Diagnostics.Debug.WriteLine($"[Sponsorship] Artwork rejected: {exception}");
        }
        finally
        {
            wechatBitmap?.Dispose();
            alipayBitmap?.Dispose();
            if (ReferenceEquals(_artworkCancellation, cancellation)) _artworkCancellation = null;
        }
    }

    private void ReleaseArtwork()
    {
        _wechat.Source = _alipay.Source = null;
        _wechatArtwork?.Dispose();
        _alipayArtwork?.Dispose();
        _wechatArtwork = _alipayArtwork = null;
    }

    private static async Task<PaymentArtwork> DecodeAsync(byte[] verifiedBytes, string name, CancellationToken token)
    {
        var resource = "Sponsorship.PaymentImage/" + name;
        // Decode these verified bytes, without reopening a replaceable file.
        var pixels = await Task.Run(() => MapEditorBitmapDecoder.DecodeVerifiedBytes(
            verifiedBytes, 640, token, resource + ".Pixels"), token);
        PaymentArtwork? artwork = null;
        try
        {
            token.ThrowIfCancellationRequested();
            artwork = new PaymentArtwork(pixels, resource);
            var upload = artwork.Source.SetBitmapAsync(pixels);
            artwork.SourceOwnsPixels = true;
            await upload.AsTask(token);
            token.ThrowIfCancellationRequested();
            artwork.Ready = true;
            MemoryJournal.Current?.Record("image-decoded", new
            {
                resource, pixels.PixelWidth, pixels.PixelHeight, maximumDecodeWidth = 640,
                meaning = "WIC and pinned input close after decode; display source explicitly owns recovery pixels"
            });
            return artwork;
        }
        catch
        {
            if (artwork is null) MapEditorBitmapDecoder.Release(pixels);
            else artwork.Dispose();
            throw;
        }
    }

    private sealed class PaymentArtwork : IDisposable
    {
        internal SoftwareBitmapSource Source { get; } = new();
        private SoftwareBitmap? _pixels;
        internal bool SourceOwnsPixels { get; set; }
        internal bool Ready { get; set; }
        internal PaymentArtwork(SoftwareBitmap pixels, string resource)
        {
            _pixels = pixels;
            var request = MemoryJournal.Request(resource, nativeModule: "Microsoft.UI.Xaml.dll");
            MemoryJournal.TrackResource(Source, resource, null, "Microsoft.UI.Xaml.dll", request);
            MemoryJournal.ObserveManagedObject(Source, resource);
        }
        public void Dispose()
        {
            if (_pixels is not { } pixels) return;
            _pixels = null;
            Ready = false;
            // Close defers input release while SetBitmapAsync is copying.
            // Closing recovery pixels separately would invalidate that upload.
            Source.Dispose();
            if (SourceOwnsPixels) MemoryJournal.ReleaseResource(pixels);
            else MapEditorBitmapDecoder.Release(pixels);
            MemoryJournal.ReleaseResource(Source);
        }
    }
}
