namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    // Route-only updates avoid regenerating recognition assets during a class
    // batch. Each completed map is committed by the catalog's atomic replacement.
    public async Task SavePlannedRouteAsync(MapDraft original, MapNavigationTopology navigation,
        IReadOnlyDictionary<string, List<MapAnnotation>> annotations, CancellationToken token = default)
    {
        await Gate.WaitAsync(token);
        try
        {
            var catalog = await ReadCatalogAsync();
            var map = catalog.Maps.SingleOrDefault(m => m.Id == original.Id)
                ?? throw new InvalidOperationException("批处理期间地图已被移除。");
            if (map.ContentVersion != original.ContentVersion || map.Class != original.Class)
                throw new InvalidOperationException("批处理期间地图已被修改，请重新规划。");
            await MapNavigationRules.ValidateImagesAsync(navigation,
                map.Floors.ToDictionary(f => f.Key, f => GetEditableFloorImagePath(map, f.Key)), token);
            var profiles = annotations.Keys.ToDictionary(key => key, key => map.Recognition.GetFloor(key)
                ?? throw new InvalidOperationException($"地图缺少楼层 {key}。"));
            if (annotations.Values.SelectMany(a => a).Any(a => !a.IsValid || !a.RoutePlanId.HasValue))
                throw new InvalidDataException("规划路线标注无效。");
            var existing = map.Recognition.Floors.ToDictionary(pair => pair.Key, pair => pair.Value.Annotations);
            var replacedIds = MapPlannedRouteReplacement.FindAnnotationIds(navigation, existing);
            foreach (var profile in map.Recognition.Floors.Values)
                profile.Annotations.RemoveAll(a => replacedIds.Contains(a.Id));
            foreach (var (key, items) in annotations) profiles[key].Annotations.AddRange(items.Select(a => a.Clone()));
            map.Navigation = navigation.Clone();
            map.ContentVersion++;
            map.UpdatedAt = DateTimeOffset.UtcNow;
            token.ThrowIfCancellationRequested();
            await WriteCatalogAsync(catalog);
        }
        finally { Gate.Release(); }
    }
}
