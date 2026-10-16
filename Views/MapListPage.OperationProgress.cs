using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private Task RunMapOperationAsync(string operation, Func<Task> action, CancellationTokenSource? cancellation = null) =>
        RunMapOperationAsync<object?>(operation, async () => { await action(); return null; }, cancellation);

    private async Task<T> RunMapOperationAsync<T>(string operation, Func<Task<T>> action, CancellationTokenSource? cancellation = null)
    {
        using var memory = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("UI.MapOperation", operation);
        using var view = new MapOperationProgressView(operation);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Content = view,
            CloseButtonText = cancellation is null ? "" : "取消" };
        // WinUI's default 184-DIP minimum leaves an empty lower half when
        // there are no commands. Keep the native themed template and padding.
        dialog.Resources["ContentDialogMinHeight"] = 0d;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Opened += (_, _) => opened.TrySetResult();
        var finished = false;
        dialog.Closing += (_, args) =>
        {
            args.Cancel = !finished;
            if (!finished) cancellation?.Cancel();
        };
        // The action begins only after the popup is visible. Await ShowAsync on
        // exit, so completion/error dialogs never race its closing animation.
        var showing = dialog.ShowThemedAsync(this);
        try
        {
            if (await Task.WhenAny(opened.Task, showing) == showing)
                throw new InvalidOperationException("无法显示地图操作进度。", showing.Exception);
            using var scope = MapOperationProgress.Begin(view);
            using var work = MapOperationProgress.Range(0, .99);
            var result = await action();
            view.Complete();
            await Task.Delay(120);
            return result;
        }
        finally
        {
            finished = true;
            view.Dispose();
            dialog.Hide();
            await showing;
        }
    }
}
