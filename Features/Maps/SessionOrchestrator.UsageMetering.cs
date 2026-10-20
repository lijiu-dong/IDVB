using IDVBuff.Core.Contracts.Online;

namespace IDVBuff.Features.Maps;

/// <summary>
/// 对局计时：只有“开始对局/结束对局”激活和结束计时（测绘对局不计时）。
/// 计时请求不进入对局启动的关键路径；额度结果只记录并通过 <see cref="MatchUsageChanged"/> 发布，
/// 是否因此停止对局由后续接入方决定。
/// </summary>
public sealed record MatchUsageStatus(UsageStartOutcome Outcome, UsageEndReason? EndReason, UsageSnapshot? Usage, string? Message);

public sealed partial class SessionOrchestrator
{
    private readonly IUsageMeter _usageMeter;
    private Task<UsageStartResult>? _usageStart;

    /// <summary>当前对局最近一次计时结果；未开始对局或未计时时为 null。</summary>
    public MatchUsageStatus? MatchUsage { get; private set; }

    /// <summary>计时开始、额度耗尽或断线超出预授权时触发；可能在后台线程触发。</summary>
    public event EventHandler<MatchUsageStatus>? MatchUsageChanged;

    public IUsageMeter UsageMeter => _usageMeter;

    private void StartUsageMetering(MapMatchSnapshot match)
    {
        var start = _usageMeter.BeginAsync("match");
        _usageStart = start;
        _ = ObserveUsageStartAsync(start, match.Version);
    }

    private async Task ObserveUsageStartAsync(Task<UsageStartResult> start, long matchVersion)
    {
        UsageStartResult result;
        try { result = await start.ConfigureAwait(false); }
        catch (Exception exception)
        {
            _logCollector.Append(MapLogCategory.Session, MapLogLevel.Warning, $"对局计时启动失败：{exception.Message}");
            return;
        }
        PublishMatchUsage(new(result.Outcome, null, result.Usage, result.Message), matchVersion);
        if (result.Ticket is { } ticket && ticket.Ended.CanBeCanceled)
            ticket.Ended.Register(() => PublishMatchUsage(
                new(result.Outcome, ticket.EndReason, _usageMeter.Current, ticket.EndReason == UsageEndReason.Exhausted
                    ? "可用时长已用完。" : "无法连接 IDVB 服务器，预授权时长已用完。"), matchVersion));
    }

    private void PublishMatchUsage(MatchUsageStatus status, long matchVersion)
    {
        if (_matchSession.Snapshot.Version != matchVersion || !_matchSession.Snapshot.IsStarted)
            return;
        MatchUsage = status;
        _logCollector.Append(MapLogCategory.Session,
            status.Outcome is UsageStartOutcome.Exhausted or UsageStartOutcome.Unavailable || status.EndReason is not null
                ? MapLogLevel.Warning : MapLogLevel.Info,
            $"对局计时 · outcome={status.Outcome} · end={status.EndReason?.ToString() ?? "-"} · enforced={status.Usage?.Enforced.ToString() ?? "unknown"}"
            + (status.Message is null ? "" : $" · {status.Message}"));
        MatchUsageChanged?.Invoke(this, status);
    }

    /// <summary>结束计时不阻塞结束对局；停止请求自带超时。</summary>
    private void StopUsageMetering()
    {
        var start = Interlocked.Exchange(ref _usageStart, null);
        MatchUsage = null;
        if (start is null)
            return;
        _ = StopUsageMeteringAsync(start);
    }

    private async Task StopUsageMeteringAsync(Task<UsageStartResult> start)
    {
        try
        {
            if ((await start.ConfigureAwait(false)).Ticket is { } ticket)
                await ticket.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logCollector.Append(MapLogCategory.Session, MapLogLevel.Warning, $"对局计时结束失败：{exception.Message}");
        }
    }
}
