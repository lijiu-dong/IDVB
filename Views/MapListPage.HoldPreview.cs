using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Numerics;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private const int HoldPreviewDelayMilliseconds = 280;
    private Popup? _holdPreviewPopup;
    private FrameworkElement? _holdPreviewImageHost;
    private CancellationTokenSource? _holdPreviewDelayCancellation;
    private int _holdPreviewRevision;

    private void AttachHoldPreview(Border card)
    {
        var pointerDown = false;
        var pointerInside = true;

        card.PointerPressed += async (_, args) =>
        {
            var point = args.GetCurrentPoint(card);
            if (!point.Properties.IsLeftButtonPressed)
                return;

            pointerDown = true;
            pointerInside = true;
            _holdPreviewDelayCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            _holdPreviewDelayCancellation = cancellation;
            try
            {
                await Task.Delay(HoldPreviewDelayMilliseconds, cancellation.Token);
                if (pointerDown && pointerInside && !cancellation.IsCancellationRequested && card.Tag is MapRecord current)
                    ShowHoldPreview(current);
            }
            catch (OperationCanceledException)
            {
            }
        };
        card.PointerMoved += (_, args) =>
        {
            if (!pointerDown)
                return;
            var position = args.GetCurrentPoint(card).Position;
            pointerInside = position.X >= 0
                && position.Y >= 0
                && position.X <= card.ActualWidth
                && position.Y <= card.ActualHeight;
            if (!pointerInside)
            {
                _holdPreviewDelayCancellation?.Cancel();
                _ = HideHoldPreviewAsync();
            }
        };
        card.PointerReleased += (_, _) => EndHoldPreview();
        card.PointerCanceled += (_, _) => EndHoldPreview();
        card.PointerCaptureLost += (_, _) => EndHoldPreview();

        void EndHoldPreview()
        {
            pointerDown = false;
            _holdPreviewDelayCancellation?.Cancel();
            _ = HideHoldPreviewAsync();
        }
    }

    private void ShowHoldPreview(MapRecord map)
    {
        var floorKey = MapScanFloorRules.ResolveScanFloorKey(map);
        if (MapFloorRules.GetFloorProfile(map, floorKey) is null)
            return;
        var path = _repository.GetFloorRecognitionPath(map, floorKey);
        if (!File.Exists(path) || XamlRoot is null)
            return;

        CloseHoldPreviewImmediately();
        var revision = ++_holdPreviewRevision;
        var image = new Image
        {
            Stretch = Stretch.Uniform,
            MaxWidth = Math.Min(1100, XamlRoot.Size.Width * 0.78),
            MaxHeight = XamlRoot.Size.Height * 0.82
        };
        MemoryImageBinding.Attach(image, new Uri(path), 0, "UI.MapList.HoldPreview");
        var imageHost = new Border
        {
            Child = image,
            Padding = new Thickness(8),
            Background = new SolidColorBrush(Color.FromArgb(255, 18, 18, 18)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 84, 84, 84)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            Scale = new Vector3(0.94f, 0.94f, 1f)
        };
        imageHost.OpacityTransition = new ScalarTransition
        {
            Duration = TimeSpan.FromMilliseconds(150)
        };
        imageHost.ScaleTransition = new Vector3Transition
        {
            Duration = TimeSpan.FromMilliseconds(180)
        };
        var overlay = new Grid
        {
            Width = XamlRoot.Size.Width,
            Height = XamlRoot.Size.Height,
            Background = new SolidColorBrush(Color.FromArgb(205, 42, 42, 42)),
            Opacity = 0,
            IsHitTestVisible = false,
            Children = { imageHost }
        };
        overlay.OpacityTransition = new ScalarTransition
        {
            Duration = TimeSpan.FromMilliseconds(130)
        };
        _holdPreviewImageHost = imageHost;
        _holdPreviewPopup = new Popup
        {
            XamlRoot = XamlRoot,
            Child = overlay,
            IsLightDismissEnabled = false,
            IsOpen = true
        };
        overlay.Opacity = 1;
        imageHost.Opacity = 1;
        imageHost.Scale = Vector3.One;
        _ = revision;
    }

    private async Task HideHoldPreviewAsync()
    {
        var popup = _holdPreviewPopup;
        if (popup is null)
            return;
        var revision = ++_holdPreviewRevision;
        if (popup.Child is FrameworkElement overlay)
            overlay.Opacity = 0;
        if (_holdPreviewImageHost is { } imageHost)
        {
            imageHost.Opacity = 0;
            imageHost.Scale = new Vector3(0.97f, 0.97f, 1f);
        }
        await Task.Delay(150);
        if (revision != _holdPreviewRevision || !ReferenceEquals(popup, _holdPreviewPopup))
            return;
        ReleaseHoldPreviewImage(popup);
        popup.IsOpen = false;
        _holdPreviewPopup = null;
        _holdPreviewImageHost = null;
    }

    private void CloseHoldPreviewImmediately()
    {
        _holdPreviewDelayCancellation?.Cancel();
        _holdPreviewDelayCancellation = null;
        _holdPreviewRevision++;
        if (_holdPreviewPopup is { } popup)
        {
            ReleaseHoldPreviewImage(popup);
            popup.IsOpen = false;
        }
        _holdPreviewPopup = null;
        _holdPreviewImageHost = null;
    }

    private static void ReleaseHoldPreviewImage(Popup popup)
    {
        if (popup.Child is Grid overlay && overlay.Children.FirstOrDefault() is Border { Child: Image image })
            MemoryImageBinding.Release(image);
        popup.Child = null;
    }
}
