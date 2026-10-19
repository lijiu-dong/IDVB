// Public contract and unavailable implementation. Private sources are selected by the build.
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed record MapSubscriptionImportedClass(string SourceName, string ImportedClassName);

public sealed record MapSubscriptionPromotionResult(
    IReadOnlyDictionary<string, string> ClassBindings,
    IReadOnlyList<Guid> InstalledMapIds);

public sealed partial class MapRepository
{
    private void RecoverRetiredSubscriptionMaps() { }

    public  Task MarkPublishedMapsAsSubscriptionAsync(
        IReadOnlyCollection<Guid> mapIds, Guid publicationId, string publisherDisplayName,
        string publisherKeyId, string version, bool isOfficialPublisher = false,
        bool isBuilderPublisher = false, CancellationToken cancellationToken = default)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }

    public  Task<MapSubscriptionPromotionResult> PromoteSubscriptionImportAsync(
        IReadOnlyList<MapSubscriptionImportedClass> importedClasses,
        IReadOnlyDictionary<string, string> previousBindings,
        IReadOnlyCollection<Guid> previousMapIds,
        Guid subscriptionId,
        string publisherDisplayName,
        string publisherKeyId,
        string version,
        bool isOfficialPublisher = false,
        bool isBuilderPublisher = false,
        CancellationToken cancellationToken = default)
    { throw new global::IDVBuff.Core.Contracts.Online.OnlineServiceUnavailableException("online-services"); }
}
