using IDVBuff.ModuleContracts;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Modules;

internal sealed class CoCreationMapsModule : IAppModule
{
    public string Id => "co-creation-maps";
    public string DisplayName => "共创地图";
    public string IconKey => nameof(Symbol.Edit);
    public object CreateView() => new IDVBuff.Views.MapListPage(coCreationMode: true);
}
