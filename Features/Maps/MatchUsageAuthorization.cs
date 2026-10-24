using IDVBuff.Core.Contracts.Online;

namespace IDVBuff.Features.Maps;

/// <summary>Owns the authorization required before committing a match or activating its plugins.</summary>
internal sealed class MatchUsageAuthorization : IAsyncDisposable
{
    private IUsageTicket? _ticket;

    private MatchUsageAuthorization(UsageStartResult result)
    {
        Result = result;
        _ticket = result.Ticket;
    }

    internal UsageStartResult Result { get; }
    internal bool IsValid => Volatile.Read(ref _ticket) is { } ticket
        && !ticket.Ended.IsCancellationRequested && ticket.EndReason is null;
    internal CancellationToken Ended => Volatile.Read(ref _ticket)?.Ended ?? new CancellationToken(true);
    internal UsageEndReason? EndReason => Result.Ticket?.EndReason;

    internal static async Task<MatchUsageAuthorization> AcquireAsync(IUsageMeter meter, CancellationToken cancellationToken)
    {
        var result = await meter.BeginAsync("match", cancellationToken).ConfigureAwait(false);
        var authorization = new MatchUsageAuthorization(result);
        // Only the explicit public/offline host may omit a server snapshot. Private hosts
        // must obtain a fresh server decision, including when enforcement is switched off.
        var allowed = result.Outcome == UsageStartOutcome.Started && result.Usage is not null;
#if !IDVB_PRIVATE_ONLINE_SERVICES
        allowed |= result.Outcome == UsageStartOutcome.Unmetered
            && (meter is UnavailableUsageMeter || result.Usage?.Enforced == false);
#endif
        if (!cancellationToken.IsCancellationRequested && allowed && authorization.IsValid
            && result.Usage is not { Enforced: true, RemainingSeconds: <= 0 })
        {
            return authorization;
        }

        await authorization.DisposeAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(result.Outcome == UsageStartOutcome.Exhausted
            || result.Usage is { Enforced: true, RemainingSeconds: <= 0 }
                ? "可用时长已用完，无法进入对局。"
                : result.Message ?? "未能取得有效的对局时长授权，请联网后重试。");
    }

    internal void Demand()
    {
        if (!IsValid)
            throw new InvalidOperationException(EndReason == UsageEndReason.Exhausted
                ? "可用时长已用完，无法继续对局。" : "对局时长授权已失效，请联网后重试。");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _ticket, null) is { } ticket)
            await ticket.DisposeAsync().ConfigureAwait(false);
    }
}
