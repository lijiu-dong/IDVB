using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps;

namespace IDVBuff.Features.Notifications;

/// <summary>One match-start operation owns one card, including failures and late worker reports.</summary>
internal sealed class MatchLoadingNotification(IOverlayNotification? notification) : IProgress<MapOperationUpdate>, IDisposable
{
    private readonly object _gate = new();
    private double _fraction;
    private bool _finished;

    public void Report(MapOperationUpdate update)
    {
        lock (_gate)
        {
            if (_finished || !double.IsFinite(update.Fraction)) return;
            _fraction = Math.Max(_fraction, Math.Clamp(update.Fraction, 0, .99));
            notification?.UpdateProgress(_fraction, $"正在加载…\n{update.Operation} · {_fraction:P0}");
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;
            if (notification is OverlayNotificationItem item)
                item.CompletionDelay = TimeSpan.FromMilliseconds(500);
            notification?.UpdateProgress(1, "正在加载…\n加载完成 · 100%");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;
            notification?.Dismiss();
        }
    }
}
