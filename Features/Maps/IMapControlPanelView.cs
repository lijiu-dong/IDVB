namespace IDVBuff.Features.Maps;

public interface IMapControlPanelView : IDisposable
{
    bool IsVisible { get; }
    Task ShowAsync(MapScreenRect gameBounds, IntPtr gameWindowHandle, MapMatchSnapshot snapshot);
    void Hide();
}
