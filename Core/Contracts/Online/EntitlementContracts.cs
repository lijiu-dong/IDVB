namespace IDVBuff.Core.Contracts.Online;

public sealed record EntitlementGrant(string GrantId, string TierKey, string DisplayName,
    DateTimeOffset StartsAt, DateTimeOffset? EndsAt, DateTimeOffset? RevokedAt);

public sealed record EntitlementRedemptionResult(EntitlementGrant? Grant, bool AlreadyRedeemed = false,
    string? ErrorCode = null, string? Message = null, PointRedemptionGrant? Points = null);

public sealed record PointRedemptionGrant(string GrantId, bool IsFree, decimal Points, DateTimeOffset CreatedAt);

public interface IEntitlementRedemptionService
{
    Task<EntitlementRedemptionResult> RedeemAsync(string code, CancellationToken cancellationToken = default);
}

public sealed class UnavailableEntitlementRedemptionService : IEntitlementRedemptionService
{
    public Task<EntitlementRedemptionResult> RedeemAsync(string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(new EntitlementRedemptionResult(null, ErrorCode: "unavailable", Message: "当前构建未配置权益兑换服务。"));
}
