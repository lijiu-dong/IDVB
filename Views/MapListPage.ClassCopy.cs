using IDVBuff.Features.Maps;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private async Task CopyCurrentClassAsync()
    {
        if (_isPackageOperation || string.IsNullOrWhiteSpace(_selectedClass)) return;
        var sourceClass = _selectedClass;
        SetClassEditBusy(true);
        try
        {
            await RunMapOperationAsync("正在复制地图类……", async () =>
            {
                _selectedClass = await MapOperationProgress.RunAsync(0, .9,
                    () => _repository.CopyClassAsync(sourceClass));
                _selectedTagFilters.Clear();
                _selectedMapIds.Clear();
                _lastClickedMapId = null;
                MapOperationProgress.Report(.9, "正在刷新地图……");
                if (!App.IsSafeMode)
                    await MapOperationProgress.RunAsync(.9, .98, () => App.Session.RefreshMapCacheAsync());
                await ShowListAsync();
            });
            MapClassDiagnosticCoordinator.Instance.Trigger(_repository);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("复制地图类失败", exception.Message);
        }
        finally { SetClassEditBusy(false); }
    }
}
