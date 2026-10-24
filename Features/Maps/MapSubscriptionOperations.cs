namespace IDVBuff.Features.Maps;

internal static class MapSubscriptionOperations
{
    internal static SemaphoreSlim Gate { get; } = new(1, 1);
}
