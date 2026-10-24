namespace IDVBuff.Core.Contracts.Online;

public sealed record UsageClearResult(bool Succeeded, UsageSnapshot? Usage = null, string? Message = null);

/// <summary>Optional developer action. The captured account must still be current before sending.</summary>
public interface IUsageQuotaClearing
{
    Task<UsageClearResult> ClearCurrentAccountAsync(AccountSessionSnapshot account, CancellationToken cancellationToken = default);
}
