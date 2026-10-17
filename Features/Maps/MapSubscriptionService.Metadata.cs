namespace IDVBuff.Features.Maps;

public sealed partial class MapSubscriptionService
{
    public async Task RegisterMetadataAsync(MapPublicationMetadataUpdate update, CancellationToken cancellationToken = default)
    {
        await OperationGate.WaitAsync(cancellationToken);
        try
        {
            var records = _store.Load().ToList();
            var record = records.SingleOrDefault(item => item.Id == update.PublicationId
                && string.Equals(item.PublisherHandle, update.PublisherHandle, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("地图包资料已更新，但本地发布记录不存在，请刷新后确认。");
            record.PackageName = update.PackageName;
            if (update.CoverPath is not null) record.CoverPath = update.CoverPath;
            // Local drafts may be newer or older than the cloud package. This
            // operation must not mark them as published or change installed IDs.
            _store.Save(records);
        }
        finally { OperationGate.Release(); }
    }
}
