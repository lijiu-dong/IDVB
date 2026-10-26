using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps;

namespace IDVBuff.Features.Notifications;

/// <summary>
/// <see cref="IGameOverlayToast"/> 的宿主实现：发布前先按「当前前台游戏客户区」把
/// 通知窗定位到游戏窗口上，再走宿主统一的通知中心
/// （与本体 <c>SessionOrchestrator.ToggleMatchStateAsync</c> 里那条提示同一路径）。
///
/// 拿不到游戏客户区（例如游戏不是前台）时照样提示，但会清掉旧定位，
/// 让通知窗退回当前前台窗口，而不是沿用很久以前的坐标。
/// </summary>
public sealed class GameOverlayToast : IGameOverlayToast
{
    private readonly IGameWindowCapture _capture;

    public GameOverlayToast(IGameWindowCapture capture) =>
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));

    public void Notice(string message)
    {
        SyncGameBounds();
        OverlayNotificationCenter.Notice(message);
    }

    public void Warning(string message)
    {
        SyncGameBounds();
        OverlayNotificationCenter.Warning(message);
    }

    public void Error(string message)
    {
        SyncGameBounds();
        OverlayNotificationCenter.Error(message);
    }

    private void SyncGameBounds()
    {
        try
        {
            OverlayNotificationCenter.UpdateGameBounds(GetGameBounds());
        }
        catch
        {
            // 通知窗口不可用时，定位失败也不能阻止提示发布。
        }
    }

    private MapScreenRect GetGameBounds()
    {
        try
        {
            if (_capture.TryGetForegroundClientBounds(out var boundsObject, out _, out _)
                && boundsObject is MapScreenRect bounds
                && bounds.IsValid)
            {
                return bounds;
            }
        }
        catch
        {
            // 捕获异常与返回失败共用回退，不能保留上一次的游戏矩形。
        }
        return default;
    }
}
