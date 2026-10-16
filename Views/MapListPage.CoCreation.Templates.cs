using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private readonly TaskCompletionSource _initialListReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task InitialListReady => _initialListReady.Task;
    internal Func<string, Task>? LocalTemplateClassAdded { get; set; }

    internal void SelectInitialClass(string className)
    {
        _selectedClass = className;
        _hasInitializedClassSelection = true;
    }

    internal Task ShowTemplateAddedMessageAsync() => ShowMessageAsync("已将模板添加到本地", _selectedClass);
    internal Task ShowTemplateNavigationErrorAsync(string message) => ShowMessageAsync("模板已添加，地图列表打开失败", message);

    private async Task AddCoCreationTemplateClassAsync(MapCoCreationTemplateClass templateClass, TextBlock status)
    {
        var navigate = LocalTemplateClassAdded ?? throw new InvalidOperationException("无法切换到地图列表。");
        var token = _coCreationLoad?.Token ?? default;
        status.Text = $"正在将“{templateClass.Name}”添加到本地…";
        status.Visibility = Visibility.Visible;
        try
        {
            var result = await RunMapOperationAsync("正在导入共创模板……", async () =>
            {
                var imported = await MapOperationProgress.RunAsync(0, .9, () => Task.Run(() => _coCreationTemplates.AddClassAsync(_repository, templateClass, token), token));
                MapOperationProgress.Report(.9, "正在刷新地图……");
                await MapOperationProgress.RunAsync(.9, 1, RefreshTemplateMapCacheAsync);
                return imported;
            });
            MapClassDiagnosticCoordinator.Instance.Trigger(_repository);
            if (IsLoaded && !token.IsCancellationRequested) await navigate(result.CreatedClasses.Single());
        }
        finally { status.Visibility = Visibility.Collapsed; }
    }

    private static async Task RefreshTemplateMapCacheAsync()
    {
        try
        {
            if (!App.IsSafeMode) await App.Session.RefreshMapCacheAsync();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Template class added but runtime refresh failed: {exception}");
        }
    }
}
