using IDVBuff.ModuleContracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Modules;

/// <summary>
/// WinUI-facing adapter for a module imported through the framework-neutral contracts project.
/// </summary>
public sealed class AppModule
{
    private readonly IAppModule _source;

    public AppModule(IAppModule source)
    {
        _source = source;
        Id = source.Id;
        DisplayName = source.DisplayName;
        Icon = Enum.TryParse<Symbol>(source.IconKey, ignoreCase: true, out var icon) ? icon : Symbol.Page;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public Symbol Icon { get; }

    /// <summary>Creates a fresh WinUI root view whenever the user enters this module.</summary>
    public FrameworkElement CreateView()
    {
        using var operation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("UI.Module.Create", Id);
        var view = _source.CreateView() as FrameworkElement
            ?? throw new InvalidOperationException($"Module '{Id}' must return a WinUI FrameworkElement from CreateView().");
        var resource = "UI.Module/" + Id;
        IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedObject(view, resource);
        var mounted = false;
        view.Loaded += (_, _) =>
        {
            if (mounted) return;
            mounted = true;
            IDVBuff.Core.Diagnostics.MemoryJournal.TrackResource(view, resource, null, "Microsoft.UI.Xaml.dll");
        };
        view.Unloaded += (_, _) =>
        {
            mounted = false;
            IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(view);
        };
        return view;
    }
}
