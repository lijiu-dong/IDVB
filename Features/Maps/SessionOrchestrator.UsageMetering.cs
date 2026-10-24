using IDVBuff.Core.Contracts.Online;

namespace IDVBuff.Features.Maps;

/// <summary>All match entry points require authorization before starting match work or plugins.</summary>
public sealed record MatchUsageStatus(UsageStartOutcome Outcome, UsageEndReason? EndReason, UsageSnapshot? Usage, string? Message);

public sealed partial class SessionOrchestrator
{
    private readonly IUsageMeter _usageMeter;
    private MatchUsageAuthorization? _matchUsageAuthorization;
    private CancellationTokenRegistration _matchUsageEnded;
    private const string UsageUnavailableMessage = "当前无法启用";

    private sealed class MatchUsageUnavailableException(Exception inner)
        : InvalidOperationException(UsageUnavailableMessage, inner);

    public MatchUsageStatus? MatchUsage { get; private set; }
    public event EventHandler<MatchUsageStatus>? MatchUsageChanged;
    public IUsageMeter UsageMeter => _usageMeter;
    private bool HasMatchUsageAuthorization
    {
        get
        {
            if (_matchUsageAuthorization?.IsValid != true || _usageMeter.Current is { Enforced: true, RemainingSeconds: <= 0 }) return false;
            try { RecognitionResources.Demand(); return true; }
            catch (RecognitionAuthorizationException) { return false; }
        }
    }

    private void NotifyUsageUnavailable()
    {
        _statusMessage = UsageUnavailableMessage;
        if (!_headless)
            IDVBuff.Features.Notifications.OverlayNotificationCenter.Warning(UsageUnavailableMessage);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool RejectUnauthorizedMatchOperation(string action)
    {
        if (HasMatchUsageAuthorization)
            return false;
        if (!_matchSession.Snapshot.IsStarted && MatchUsage?.EndReason is null
            && MatchUsage?.Outcome is not (UsageStartOutcome.Exhausted or UsageStartOutcome.Unavailable)
            && _usageMeter.Current is not { Enforced: true, RemainingSeconds: <= 0 })
            return false;
        if (MapInputOperationContext.Current is { } input)
        {
            input.Outcome = "rejected";
            input.Reason = "usage-authorization-unavailable";
        }
        ScanRequestDiagnostics.Current?.Complete("rejected", "usage-authorization-unavailable");
        _logCollector.Append(MapLogCategory.Session, MapLogLevel.Warning,
            $"对局操作被时长授权拒绝 · action={action} · end={_matchUsageAuthorization?.EndReason?.ToString() ?? "-"}");
        NotifyUsageUnavailable();
        return true;
    }

    private async Task AuthorizeMatchUsageAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            _matchUsageAuthorization = await MatchUsageAuthorization.AcquireAsync(_usageMeter, timeout.Token);
            RecognitionResources.Demand();
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _matchUsageAuthorization, null) is { } failed)
                await failed.DisposeAsync();
            var reason = exception is OperationCanceledException
                ? "对局时长授权请求已取消或超时，请联网后重试。" : exception.Message;
            _logCollector.Append(MapLogCategory.Session, MapLogLevel.Warning, $"进入对局被拒绝 · {reason}");
            MatchUsage = new(_usageMeter.Current is { Enforced: true, RemainingSeconds: <= 0 }
                ? UsageStartOutcome.Exhausted : UsageStartOutcome.Unavailable, null, _usageMeter.Current, reason);
            NotifyUsageUnavailable();
            throw new MatchUsageUnavailableException(exception);
        }
    }

    private void ObserveMatchUsage(MapMatchSnapshot match)
    {
        var authorization = _matchUsageAuthorization ?? throw new InvalidOperationException("缺少对局时长授权。");
        authorization.Demand();
        var result = authorization.Result;
        PublishMatchUsage(new(result.Outcome, null, result.Usage, result.Message));
        // MatchId survives floor/variant epoch changes. An old match's queued expiry
        // must never end a newer match, while an epoch change must not hide expiry.
        _matchUsageEnded = authorization.Ended.Register(() =>
        {
            if (!_dispatcher.TryEnqueue(async () => await EndUnauthorizedMatchAsync(authorization, match.MatchId)))
                _logCollector.Append(MapLogCategory.Session, MapLogLevel.Error, "对局授权失效，宿主调度器不可用；对局操作已由授权和取消令牌拒绝。");
        });
    }

    private async Task EndUnauthorizedMatchAsync(MatchUsageAuthorization authorization, Guid matchId)
    {
        await _matchLifecycleGate.WaitAsync();
        try
        {
            if (_disposed || !ReferenceEquals(_matchUsageAuthorization, authorization)
                || !_matchSession.Snapshot.IsStarted || _matchSession.Snapshot.MatchId != matchId)
                return;
            var status = new MatchUsageStatus(authorization.Result.Outcome, authorization.EndReason, _usageMeter.Current,
                UsageUnavailableMessage);
            PublishMatchUsage(status);
            await EndMatchCoreAsync(saveAutomaticMapCache: false, notify: false);
            MatchUsage = status;
            NotifyUsageUnavailable();
        }
        catch (Exception exception)
        {
            _logCollector.Append(MapLogCategory.Session, MapLogLevel.Error, $"停止未授权对局失败：{exception}");
        }
        finally { _matchLifecycleGate.Release(); }
    }

    private void PublishMatchUsage(MatchUsageStatus status)
    {
        MatchUsage = status;
        _logCollector.Append(MapLogCategory.Session, status.EndReason is null ? MapLogLevel.Info : MapLogLevel.Warning,
            $"对局计时 · outcome={status.Outcome} · end={status.EndReason?.ToString() ?? "-"} · enforced={status.Usage?.Enforced.ToString() ?? "unknown"}"
            + (status.Message is null ? "" : $" · {status.Message}"));
        if (MatchUsageChanged is { } handlers)
            foreach (EventHandler<MatchUsageStatus> handler in handlers.GetInvocationList())
            {
                try { handler(this, status); }
                catch (Exception exception)
                {
                    // Observers, including plugins, cannot veto enforcement.
                    _logCollector.Append(MapLogCategory.Session, MapLogLevel.Error, $"对局计时状态通知失败：{exception}");
                }
            }
    }

    private async Task StopUsageMeteringAsync()
    {
        _matchUsageEnded.Dispose();
        _matchUsageEnded = default;
        var authorization = Interlocked.Exchange(ref _matchUsageAuthorization, null);
        MatchUsage = null;
        if (authorization is not null)
            await authorization.DisposeAsync();
    }
}
