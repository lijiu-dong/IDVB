using IDVBuff.Core.Contracts.Online;
using IDVBuff.UpdateCore;

namespace IDVBuff.Features.Maps;

internal sealed class MapCoCreationPackage : IAsyncDisposable
{
    internal IdvmImportPlan Plan => throw new OnlineServiceUnavailableException("community.edit");
    internal MapSubscriptionRecord Record => throw new OnlineServiceUnavailableException("community.edit");
    internal MapPublicationPayload Publication => throw new OnlineServiceUnavailableException("community.edit");
    internal static Task<MapCoCreationPackage> OpenAsync(MapCoCreationPublication publication,
        string publisherHandle, IdvmPackageService packages, CancellationToken cancellationToken) =>
        Task.FromException<MapCoCreationPackage>(new OnlineServiceUnavailableException("community.edit"));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
