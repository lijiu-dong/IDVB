using System.Text.Json;
using IDVBuff.UpdateCore;

namespace IDVBuff.Features.Maps;

internal sealed class MapCoCreationPackage : IAsyncDisposable
{
    private readonly string _root;
    internal IdvmImportPlan Plan { get; }
    internal MapSubscriptionRecord Record { get; }
    internal MapPublicationPayload Publication { get; }
    private MapCoCreationPackage(string root, IdvmImportPlan plan, MapSubscriptionRecord record, MapPublicationPayload publication) =>
        (_root, Plan, Record, Publication) = (root, plan, record, publication);

    internal static async Task<MapCoCreationPackage> OpenAsync(MapCoCreationPublication publication,
        string publisherHandle, IdvmPackageService packages, CancellationToken cancellationToken)
    {
        if (!string.Equals(publication.PublisherHandle, publisherHandle, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("只能编辑当前账号发布的方案。");
        var root = Path.Combine(Path.GetTempPath(), "IDVB-CoCreation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IdvmImportPlan? plan = null;
        try
        {
            var link = MapSubscriptionLink.Parse(publication.SubscriptionLink);
            var record = new MapSubscriptionRecord { Id = publication.Id, Link = publication.SubscriptionLink,
                FeedUri = link.FeedUri.AbsoluteUri, PublisherKeyId = link.PublisherKeyId };
            var store = new MapSubscriptionStore(root);
            store.Save([record]);
            var result = await new MapSubscriptionUpdateEngine().UpdateAllAsync(root,
                MapSubscriptionTrust.LoadOfficialPublicKey(AppContext.BaseDirectory), cancellationToken);
            record = store.Load().Single();
            if (result.Prepared != 1) throw new InvalidDataException(record.LastError ?? "无法读取已发布方案。");
            var receipt = JsonSerializer.Deserialize<PendingMapSubscriptionUpdate>(
                await File.ReadAllTextAsync(Path.Combine(root, "pending", record.Id.ToString("N"), "pending.json"), cancellationToken),
                MapSubscriptionProtocol.JsonOptions) ?? throw new InvalidDataException("方案校验回执为空。");
            if (receipt.Publication.PublicationId != publication.Id
                || !string.Equals(receipt.Publication.PublisherHandle, publisherHandle, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("已签名方案与当前账号不匹配。");
            plan = await packages.InspectAsync(receipt.PackagePath, cancellationToken);
            if (plan.MapCount == 0) throw new InvalidDataException("已发布方案中没有地图。");
            cancellationToken.ThrowIfCancellationRequested();
            return new(root, plan, record, receipt.Publication);
        }
        catch
        {
            if (plan is not null) await plan.DisposeAsync();
            TryDelete(root);
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        await Plan.DisposeAsync();
        TryDelete(_root);
    }
    private static void TryDelete(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
