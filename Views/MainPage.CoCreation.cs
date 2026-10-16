using IDVBuff.Modules;

namespace IDVBuff.Views;

public sealed partial class MainPage
{
    private async Task NavigateToLocalTemplateClassAsync(MapListPage source, string className)
    {
        if (!source.IsLoaded || !ReferenceEquals(ModuleContentHost.Content, source)) return;
        var entry = ExpandNavigationTo("map-list");
        var expectedRevision = _navigationRevision + 1;
        await NavigateToAsync("map-list", entry, initialMapClass: className);
        if (expectedRevision != _navigationRevision || ModuleContentHost.Content is not MapListPage target) return;
        try { await target.InitialListReady; }
        catch (OperationCanceledException) { return; }
        catch (Exception exception)
        {
            if (ReferenceEquals(ModuleContentHost.Content, target) && target.IsLoaded)
                await target.ShowTemplateNavigationErrorAsync(exception.Message);
            return;
        }
        if (ReferenceEquals(ModuleContentHost.Content, target) && target.IsLoaded)
            await target.ShowTemplateAddedMessageAsync();
    }

    /// <summary>Opens the co-creation gallery, which starts on its community section.</summary>
    private Task NavigateToCoCreationCommunityAsync() =>
        NavigateToAsync("co-creation-maps", ExpandNavigationTo("co-creation-maps"));

    private NavigationEntry ExpandNavigationTo(string moduleId)
    {
        var entry = NavigationItems.SelectMany(root => root.Children).First(child => child.ModuleId == moduleId);
        for (var parent = entry.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.Node.IsExpanded) continue;
            parent.Node.ToggleExpanded();
            AnimateNavigationExpansion(parent);
        }
        return entry;
    }
}
