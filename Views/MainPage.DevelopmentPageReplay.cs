#if IDVB_DEVELOPMENT_BUILD
using IDVBuff.Core.Diagnostics;
using IDVBuff.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class MainPage
{
    internal async Task<object> ReplayDevelopmentPagesAsync(int rounds, CancellationToken cancellation, int? moduleLimit = null, int? randomSeed = null, string? moduleIds = null)
    {
        if (!Program.IsDevelopmentInstance || App.IsSafeMode)
            throw new InvalidOperationException("页面回放要求正常模式的完整开发客户端。");
        await App.ServicesReadyTask.WaitAsync(cancellation);
        await InitialReady.WaitAsync(cancellation);
        await ActivateDevelopmentReplayWindowAsync(cancellation);
        var requestedIds = moduleIds?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (requestedIds is { Length: 0 } or { Length: > 100 })
            throw new ArgumentException("Explicit replay module IDs must contain 1 to 100 entries.", nameof(moduleIds));
        if (requestedIds is not null && moduleLimit.HasValue)
            throw new ArgumentException("Choose explicit module IDs or a module limit, not both.");
        var modules = requestedIds is null
            ? _catalog.Modules.Take(moduleLimit ?? int.MaxValue).ToArray()
            : requestedIds.Distinct(StringComparer.Ordinal).Select(id => _catalog.GetRequired(id)).ToArray();
        var runId = Guid.NewGuid();
        var random = randomSeed.HasValue ? new Random(randomSeed.Value) : null;
        MemoryJournal.Current?.Record("full-client-pages-start", new { runId, rounds, moduleLimit, randomSeed, requestedIds, modules = modules.Select(module => new { module.Id, module.DisplayName }),
            build = BuildVersionInfo.BuildVersion, pid = Environment.ProcessId, fullApplication = true, safeMode = App.IsSafeMode,
            scope = "Real module navigation and control inventory; activating every control is a separate required coverage step." }, durable: true);
        for (var round = 0; round < rounds; round++)
        {
            if (random is not null) random.Shuffle(modules);
            foreach (var module in modules)
            {
                cancellation.ThrowIfCancellationRequested();
                MemoryBudget.Process.ThrowIfExceeded();
                await NavigateToAsync(module.Id);
                if (ModuleContentHost.Content is MapListPage maps) await maps.InitialListReady.WaitAsync(cancellation);
                await Task.Delay(700, cancellation);
                var buttons = PageReplayDescendants(ModuleContentHost.Content as DependencyObject).OfType<Button>()
                    .Select(button => new { button.Name, label = button.Content?.ToString(), button.IsEnabled, visibility = button.Visibility.ToString() }).ToArray();
                MemoryJournal.Current?.Record("full-client-page-step", new { runId, round, module.Id, module.DisplayName,
                    root = ModuleContentHost.Content?.GetType().FullName, buttons, reading = ProcessMemoryReader.Read(),
                    managedLiveBytes = GC.GetTotalMemory(false), allocatedTotalBytes = GC.GetTotalAllocatedBytes(false) });
            }
        }
        await NavigateToAsync("home");
        MemoryJournal.Current?.Record("full-client-pages-end", new { runId, rounds, moduleCount = modules.Length, reading = ProcessMemoryReader.Read() }, durable: true);
        return new { runId, rounds, randomSeed, moduleCount = modules.Length, reading = ProcessMemoryReader.Read(), scope = "Module navigation only; not complete event coverage." };
    }

    private static IEnumerable<DependencyObject> PageReplayDescendants(DependencyObject? root)
    {
        if (root is null) yield break;
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in PageReplayDescendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
#endif
