using IDVBuff.Appearance;
using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private bool _templateImportRunning;

    private Button CreateTemplateImportButton()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        content.Children.Add(new FontIcon { Glyph = "\uE8B5", FontSize = 13 });
        content.Children.Add(new TextBlock { Text = "导入", FontSize = 12 });
        var button = CreateCoCreationToolbarButton(content, "导入共创模板");
        button.IsEnabled = !_templateImportRunning;
        button.Click += async (_, _) => await ImportCoCreationTemplateAsync();
        return button;
    }

    private Grid CreateTemplateEmptyState(Grid viewport)
    {
        var state = new Grid { MinHeight = 200 };
        var content = new StackPanel { Spacing = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new FontIcon { Glyph = "\uE8B9", FontSize = 48,
            Foreground = FluentTheme.Brush(this, ThemeToken.TextMuted) });
        var description = CreateCoCreationDescription("目前没有共创模板，你可以点击下方导入一个");
        description.FontSize = 13;
        description.TextAlignment = TextAlignment.Center;
        content.Children.Add(description);
        var import = CreateTemplateImportButton();
        import.HorizontalAlignment = HorizontalAlignment.Center;
        content.Children.Add(import);
        state.Children.Add(content);
        void UpdateHeight()
        {
            if (!state.IsLoaded || viewport.ActualHeight <= 0) return;
            var top = state.TransformToVisual(viewport).TransformPoint(new Windows.Foundation.Point()).Y;
            state.Height = Math.Max(200, viewport.ActualHeight - top - 44);
        }
        void Resize(object sender, SizeChangedEventArgs args) => UpdateHeight();
        state.Loaded += (_, _) => { viewport.SizeChanged += Resize; UpdateHeight(); };
        state.Unloaded += (_, _) => viewport.SizeChanged -= Resize;
        return state;
    }

    private async Task ImportCoCreationTemplateAsync()
    {
        if (_templateImportRunning) return;
        _templateImportRunning = true;
        try
        {
            var picker = new FileOpenPicker(((App)Application.Current).MainWindow.AppWindow.Id)
            {
                CommitButtonText = "导入", SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".idvmx");
            var file = await picker.PickSingleFileAsync();
            if (file is null || string.IsNullOrWhiteSpace(file.Path)) return;
            var token = _coCreationLoad?.Token ?? default;
            var result = await RunMapOperationAsync("正在导入共创模板……", () => Task.Run(async () =>
                await new IdvmxTemplatePackageService().ImportAsync(file.Path, _repository, token), token));
            await RefreshTemplateMapCacheAsync();
            if (!IsLoaded || token.IsCancellationRequested) return;
            _coCreationSection = CoCreationSection.Templates;
            _templateImportRunning = false;
            await ShowListAsync();
            await ShowMessageAsync(result.AlreadyInstalled ? "模板已导入" : "模板导入完成",
                result.AlreadyInstalled ? "此版本已在模板库中。" : $"已更新 {result.UpdatedMaps} 张地图，新增 {result.AddedMaps} 张地图。用户路线与标注已保留。"
                    + (result.SkippedMaps.Count == 0 ? string.Empty : "\n\n" + string.Join("\n", result.SkippedMaps)));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (IsLoaded) await ShowMessageAsync("模板导入失败", exception.Message); }
        finally { _templateImportRunning = false; }
    }
}
