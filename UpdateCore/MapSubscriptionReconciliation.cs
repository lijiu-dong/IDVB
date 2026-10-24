// Public contract and unavailable implementation. Private sources are selected by the build.
namespace IDVBuff.UpdateCore;

public enum MapSubscriptionReconciliationAction
{
    None,
    RemoveSubscription,
    ForceReapply
}

public static class MapSubscriptionReconciliation
{
    public static MapSubscriptionReconciliationAction Evaluate(
        MapSubscriptionRecord record,
        IReadOnlySet<Guid> installedMapIds)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }
}
