using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private bool _classAppearanceBusy;
    private bool _classScreenPickBusy;

    private async Task ShowClassAppearanceAsync()
    {
        if (_isPackageOperation || _classAppearanceBusy || string.IsNullOrWhiteSpace(_selectedClass)) return;
        var className = _selectedClass;
        var maps = _loadedMaps.Where(map => string.Equals(map.Class, className,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        if (maps.Length == 0 || maps.Any(map => !map.IsCoCreationTemplate)) return;
        var current = maps[0].AppearancePalette ?? new MapAppearancePalette();
        var content = new StackPanel { Spacing = 16, MinWidth = 300 };
        var pickers = new List<ColorPicker>();
        using var screenPickerLifetime = new CancellationTokenSource();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "自定义配色", Content = content,
            PrimaryButtonText = "一键重新渲染", SecondaryButtonText = "全部重置", CloseButtonText = "取消"
        };
        dialog.Closing += (_, _) => screenPickerLifetime.Cancel();
        foreach (var (label, color) in new[] { ("房间", current.Room), ("走廊", current.Corridor),
            ("房间轮廓", current.RoomOutline), ("走廊轮廓", current.CorridorOutline) })
        {
            var row = new Grid { ColumnSpacing = 24 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var swatch = new Border { Width = 72, Height = 24, CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(ParseEditorColor(color)) };
            var button = new Button { Content = swatch, Padding = new Thickness(5) };
            AutomationProperties.SetName(button, label);
            var picker = new ColorPicker
            {
                Color = ParseEditorColor(color), IsAlphaEnabled = false,
                IsAlphaSliderVisible = false, IsAlphaTextInputVisible = false
            };
            picker.ColorChanged += (_, args) => swatch.Background = new SolidColorBrush(args.NewColor);
            button.Flyout = new Flyout { Content = picker };
            Grid.SetColumn(button, 1);
            row.Children.Add(button);
            var eyedropper = new Button
            {
                Content = new FontIcon { Glyph = "\uEF3C", FontSize = 18 },
                Padding = new Thickness(8), VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(eyedropper, $"屏幕取色：{label}");
            ToolTipService.SetToolTip(eyedropper, "屏幕取色 · Esc 取消");
            eyedropper.Click += async (_, _) =>
                await PickClassScreenColorAsync(dialog, picker, screenPickerLifetime.Token);
            Grid.SetColumn(eyedropper, 2);
            row.Children.Add(eyedropper);
            content.Children.Add(row);
            pickers.Add(picker);
        }
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            var defaults = new MapAppearancePalette();
            var colors = new[] { defaults.Room, defaults.Corridor, defaults.RoomOutline, defaults.CorridorOutline };
            for (var index = 0; index < pickers.Count; index++) pickers[index].Color = ParseEditorColor(colors[index]);
        };
        RoutedEventHandler cancelPicking = (_, _) => screenPickerLifetime.Cancel();
        Unloaded += cancelPicking;
        ContentDialogResult result;
        try { result = await dialog.ShowThemedAsync(this); }
        finally { screenPickerLifetime.Cancel(); Unloaded -= cancelPicking; }
        if (result != ContentDialogResult.Primary) return;
        _classAppearanceBusy = true;
        SetClassEditBusy(true);
        try
        {
            await RunMapOperationAsync("正在更新配色……", async () =>
            {
                await MapOperationProgress.RunAsync(0, .9, () => _repository.SetClassAppearanceAsync(className, new MapAppearancePalette
                {
                    Room = ToEditorColorHex(pickers[0].Color), Corridor = ToEditorColorHex(pickers[1].Color),
                    RoomOutline = ToEditorColorHex(pickers[2].Color), CorridorOutline = ToEditorColorHex(pickers[3].Color)
                }));
                MapOperationProgress.Report(.9, "正在刷新配色……");
                if (!App.IsSafeMode) await MapOperationProgress.RunAsync(.9, 1, () => App.Session.RefreshMapAppearanceAsync(className));
                await ShowListAsync();
            });
        }
        catch (Exception exception) { await ShowMessageAsync("配色更新失败", exception.Message); }
        finally { _classAppearanceBusy = false; SetClassEditBusy(false); }
    }

    private async Task PickClassScreenColorAsync(ContentDialog dialog, ColorPicker picker, CancellationToken token)
    {
        if (_classScreenPickBusy || token.IsCancellationRequested) return;
        _classScreenPickBusy = true;
        dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = false;
        try
        {
            var color = await ScreenColorPicker.PickAsync(token);
            if (!token.IsCancellationRequested && color is { } selected)
                picker.Color = Windows.UI.Color.FromArgb(255, selected.R, selected.G, selected.B);
        }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
            {
                var error = new TeachingTip { Title = "屏幕取色失败", Subtitle = exception.Message,
                    Target = dialog, IsLightDismissEnabled = true };
                if (dialog.Content is Panel panel) panel.Children.Add(error);
                error.IsOpen = true;
            }
        }
        finally
        {
            _classScreenPickBusy = false;
            dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = true;
        }
    }
}
