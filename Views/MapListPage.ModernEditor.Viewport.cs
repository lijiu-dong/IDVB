using IDVBuff.Features.Maps;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapListPage : UserControl
{
    private long _modernViewRevision;
    private double _modernRenderedZoom = double.NaN;
    private long _modernRenderCount;

    private void ModernViewportViewChanged(ScrollViewer viewport)
    {
        if (!ReferenceEquals(viewport, _modernViewport)) return;
        UpdateModernZoomText();
        // Coordinates live in the source canvas. Panning translates the same
        // visuals; only zoom changes their screen-sized strokes and handles.
        if (double.IsNaN(_modernRenderedZoom) || Math.Abs(_modernRenderedZoom - ModernZoomFactor) > .000001)
            RenderModernEditor();
    }

    private void QueueAutomaticModernCanvasFit(long? expectedRevision = null)
    {
        var revision = expectedRevision ?? _modernViewRevision;
        var viewport = _modernViewport;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (revision == _modernViewRevision && ReferenceEquals(viewport, _modernViewport)) FitModernCanvas();
        });
    }

    private void CancelModernCanvasFit()
    {
        ++_modernViewRevision;
        _modernFitPending = false;
    }
    private void ModernEditorRoot_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateModernResponsiveLayout(e.NewSize.Width);

    private void ModernParentViewport_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyModernViewportSize(e.NewSize.Width, e.NewSize.Height);

    private void ApplyModernViewportSize(double availableWidth, double availableHeight)
    {
        if (_modernEditorRoot is null)
            return;
        var width = Math.Max(1, availableWidth);
        var height = Math.Max(1, availableHeight);
        if (Math.Abs(_modernEditorRoot.Width - width) > .5)
            _modernEditorRoot.Width = width;
        if (Math.Abs(_modernEditorRoot.Height - height) > .5)
            _modernEditorRoot.Height = height;
        UpdateModernResponsiveLayout(width);
        QueueAutomaticModernCanvasFit();
    }

    private void UpdateModernResponsiveLayout(double width)
    {
        var narrow = width < 1050;
        _modernLayersAreDrawer = narrow;
        if (_modernLayerDrawerButton is not null)
            _modernLayerDrawerButton.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        if (_modernFocusMode)
            return;
        if (_modernLayerColumn is not null)
            _modernLayerColumn.Width = narrow && !_modernLayerDrawerOpen ? new GridLength(0) : new GridLength(286);
        if (_modernLayerPane is not null)
            _modernLayerPane.Visibility = narrow && !_modernLayerDrawerOpen ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ToggleModernLayerDrawer()
    {
        _modernLayerDrawerOpen = !_modernLayerDrawerOpen;
        if (_modernLayerColumn is not null)
            _modernLayerColumn.Width = _modernLayerDrawerOpen ? new GridLength(286) : new GridLength(0);
        if (_modernLayerPane is not null)
            _modernLayerPane.Visibility = _modernLayerDrawerOpen ? Visibility.Visible : Visibility.Collapsed;
        QueueAutomaticModernCanvasFit();
    }

    private void ToggleModernFocusMode()
    {
        _modernFocusMode = !_modernFocusMode;
        if (_modernEditorHeader is not null)
            _modernEditorHeader.Visibility = _modernFocusMode ? Visibility.Collapsed : Visibility.Visible;
        var showLayerPane = !_modernFocusMode && (!_modernLayersAreDrawer || _modernLayerDrawerOpen);
        if (_modernLayerPane is not null)
            _modernLayerPane.Visibility = showLayerPane ? Visibility.Visible : Visibility.Collapsed;
        if (_modernLayerColumn is not null)
            _modernLayerColumn.Width = showLayerPane ? new GridLength(286) : new GridLength(0);
        SetModernStatus(_modernFocusMode ? "已进入专注模式，按 Esc 退出。" : "已退出专注模式。", false);
        QueueAutomaticModernCanvasFit();
    }

    private void FitModernCanvas()
    {
        ++_modernViewRevision;
        _modernFitPending = true;
        QueueModernCanvasFit();
    }

    private void ModernViewport_LayoutUpdated(object? sender, object e)
    {
        if (_modernFitPending) QueueModernCanvasFit();
    }

    private void ModernViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _modernViewport) && _modernBitmap is { PixelWidth: > 0 })
            QueueAutomaticModernCanvasFit();
    }

    private void QueueModernCanvasFit()
    {
        if (_modernFitQueued || _modernViewport is not { } viewport) return;
        _modernFitQueued = true;
        // LayoutUpdated fires within a layout pass. Changing the content extent
        // or zoom there feeds back into layout, especially while the shell's
        // navigation column animates. Coalesce requests after that pass instead.
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            // A closed editor's queued callback must not own a replacement viewport.
            if (!ReferenceEquals(viewport, _modernViewport)) return;
            _modernFitQueued = false;
            if (_modernFitPending) TryFitModernCanvas();
        })) _modernFitQueued = false;
    }

    private void TryFitModernCanvas()
    {
        if (_modernViewport is null || !_modernViewport.IsLoaded || _modernScene is null || _modernSceneHost is null
            || _modernBitmap is not { PixelWidth: > 0 } || _modernViewport.ViewportWidth <= 0 || _modernViewport.ViewportHeight <= 0) return;
        var fit = MapEditorViewportFit.Calculate(_modernScene.Width, _modernScene.Height,
            Math.Min(_modernViewport.ActualWidth, _modernViewport.ViewportWidth),
            Math.Min(_modernViewport.ActualHeight, _modernViewport.ViewportHeight));
        if (fit is not { } target) return;
        var width = _modernScene.Width * target.DisplayScale;
        var height = _modernScene.Height * target.DisplayScale;
        _modernDisplayScale = target.DisplayScale;
        if (Math.Abs(_modernSceneHost.Width - width) > .01 || Math.Abs(_modernSceneHost.Height - height) > .01)
        {
            _modernSceneHost.Width = width;
            _modernSceneHost.Height = height;
            return; // ChangeView must use the realized content extent.
        }
        _modernFitPending = false;
        if (Math.Abs(_modernViewport.ZoomFactor - target.ZoomFactor) >= .00001
            || _modernViewport.HorizontalOffset >= .01 || _modernViewport.VerticalOffset >= .01)
        {
            // ChangeView can synchronously raise ViewChanged and invalidate the
            // canvas; clear ownership first so those callbacks cannot reenter fit.
            if (!_modernViewport.ChangeView(0, 0, target.ZoomFactor, true))
                _modernFitPending = true;
        }
        ModernViewportViewChanged(_modernViewport);
    }

    private void ChangeModernZoom(float multiplier)
    {
        if (_modernViewport is null)
            return;
        CancelModernCanvasFit();
        var zoom = Math.Clamp(_modernViewport.ZoomFactor * multiplier, .1f, 8f);
        var centerX = _modernViewport.HorizontalOffset + _modernViewport.ViewportWidth / 2;
        var centerY = _modernViewport.VerticalOffset + _modernViewport.ViewportHeight / 2;
        var scale = zoom / _modernViewport.ZoomFactor;
        _modernViewport.ChangeView(
            Math.Max(0, centerX * scale - _modernViewport.ViewportWidth / 2),
            Math.Max(0, centerY * scale - _modernViewport.ViewportHeight / 2),
            zoom,
            false);
    }

    private void UpdateModernZoomText()
    {
        if (_modernZoomText is not null && _modernViewport is not null)
            _modernZoomText.Text = $"{Math.Round(_modernViewport.ZoomFactor * _modernDisplayScale * 100):0}%";
    }

    private void SetModernStatus(string text, bool isError = false)
    {
        if (_modernStatusText is null)
            return;
        _modernStatusText.Text = text;
        _modernStatusText.Foreground = isError ? EditorBrush(IDVBuff.Appearance.ThemeToken.ErrorText) : EditorMuted;
    }

    private static Color ParseEditorColor(string? value)
    {
        if (!MapAnnotationColor.TryNormalize(value, out var normalized))
            normalized = MapAnnotationColor.Default;
        return Color.FromArgb(
            255,
            Convert.ToByte(normalized.Substring(1, 2), 16),
            Convert.ToByte(normalized.Substring(3, 2), 16),
            Convert.ToByte(normalized.Substring(5, 2), 16));
    }

    private static string ToEditorColorHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
                yield return typed;
            foreach (var descendant in FindDescendants<T>(child))
                yield return descendant;
        }
    }
}
