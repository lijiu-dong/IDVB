using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private string _modernMarkerSymbol = "★";

    private void AddModernRoomMarkers(FloorRecognitionProfile profile)
    {
        if (!IsModernItemVisible("graphics", string.Empty)) return;
        foreach (var marker in profile.Annotations.Where(a => a.IsMarker && a.IsValid
            && IsModernItemVisible("graphics", ModernAnnotationKey(a.Id)))) AddModernAnnotation(marker);
    }

    private bool TryAddModernRoomMarker(MapAnnotation annotation)
    {
        if (!annotation.IsMarker || _modernCanvas is null || annotation.Bounds?.IsValid is not true) return false;
        _modernCanvas.Children.Add(CreateModernMarkerVisual(annotation, ToModernPixelRect(annotation.Bounds)));
        return true;
    }

    private void ShowModernMarkerPicker(Button target)
    {
        if (!_modernToolState.CanUse(MapEditorTool.Marker)) return;
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = "选择图案后，在地图上点击放置" });
        var grid = new Grid { RowSpacing = 6, ColumnSpacing = 6 };
        for (var i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        for (var i = 0; i < 3; i++) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.RightEdgeAlignedTop };
        for (var i = 0; i < MapMarkerTool.Symbols.Count; i++)
        {
            var symbol = MapMarkerTool.Symbols[i];
            var button = new Button { Content = symbol, Width = 48, Height = 42,
                FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 24 };
            button.Click += (_, _) => { _modernMarkerSymbol = symbol; SelectModernTool(MapEditorTool.Marker); flyout.Hide(); };
            Grid.SetColumn(button, i % 4); Grid.SetRow(button, i / 4); grid.Children.Add(button);
        }
        panel.Children.Add(grid); flyout.ShowAt(target);
    }

    private void PlaceModernMarker(NormalizedPoint center)
    {
        if (_modernCanvas is null || _draft is null) return;
        var profile = GetActiveFloorProfile();
        var annotation = MapMarkerTool.Create(_modernMarkerSymbol, center, _modernCanvas.Width, _modernCanvas.Height);
        profile.Annotations.Add(annotation);
        _modernSelection = new EditorSelection(MapEditorElementKind.Annotation, annotation.Id);
        RecordModernAnnotationCreation(profile, annotation);
        CompleteModernCreation($"已放置标记 {_modernMarkerSymbol}。", returnToSelect: false);
    }

    private FrameworkElement CreateModernMarkerVisual(MapAnnotation annotation, Rect bounds)
    {
        var grid = new Grid { Width = bounds.Width, Height = bounds.Height, IsHitTestVisible = false,
            Tag = "marker:" + annotation.Id };
        var size = Math.Min(bounds.Height * .8, bounds.Width / Math.Max(1, (annotation.Text?.Length ?? 1) * .65));
        void Label(double x, double y, string color)
        {
            grid.Children.Add(new TextBlock { Text = annotation.Text, FontFamily = new FontFamily("Segoe UI Symbol"),
                FontSize = Math.Max(1, size), FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(ParseEditorColor(color)),
                RenderTransform = new TranslateTransform { X = x, Y = y }, IsHitTestVisible = false });
        }
        var halo = Math.Max(1, size * .07);
        foreach (var x in new[] { -halo, 0, halo })
        foreach (var y in new[] { -halo, 0, halo }) if (x != 0 || y != 0) Label(x, y, "#0A0F16");
        Label(0, 0, annotation.EffectiveColorHex);
        Canvas.SetLeft(grid, bounds.X); Canvas.SetTop(grid, bounds.Y);
        return grid;
    }
}
