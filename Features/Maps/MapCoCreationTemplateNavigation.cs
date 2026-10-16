using System.Security.Cryptography;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCoCreationTemplateCatalog
{
    // Channels are baked against the complete horizontal artwork. Recognition
    // crops must never be used as the coordinate space for these points.
    private async Task<MapNavigationTopology?> LoadNavigationAsync(MapCoCreationTemplate template,
        CancellationToken cancellationToken)
    {
        var path = ResolvePath($"navigation/{template.Id}.json");
        if (!File.Exists(path)) return null;
        await using var input = File.OpenRead(path);
        if (input.Length > MapNavigationRules.MaxJsonBytes)
            throw new InvalidDataException("模板导航数据超过 8 MiB。");
        var bytes = new byte[(int)input.Length];
        await input.ReadExactlyAsync(bytes, cancellationToken);
        var navigation = await Task.Run(() => MapNavigationRules.Read(bytes), cancellationToken);
        if (navigation.SourceLevelId != template.SourceLevelId
            || navigation.Floors.Count != template.Floors.Count)
            throw new InvalidDataException("模板导航数据与地图来源不一致。");
        foreach (var floor in template.Floors)
        {
            var bound = navigation.Floors.SingleOrDefault(item => item.FloorKey == floor.FloorKey);
            if (bound is null || bound.ImageSha256 != floor.ImageSha256
                || bound.ImageWidth != floor.ImageWidth || bound.ImageHeight != floor.ImageHeight)
                throw new InvalidDataException("模板导航数据与完整原图不一致。");
        }
        return navigation;
    }

    public async Task<MapNavigationTopology?> LoadNavigationForDraftAsync(MapDraft draft,
        CancellationToken cancellationToken = default)
    {
        var existing = draft.Navigation;
        // Local maps own their exact base version. Import/update is the only writer
        // allowed to upgrade it; a route fallback must never select the newest package.
        if (_installedStore) return existing;
        if (!IsTemplateDraft(draft) || existing is { RoutingStyle: MapNavigationTopology.CurrentRoutingStyle,
            RouteMetadataVersion: >= MapNavigationTopology.CurrentRouteMetadataVersion }
            && MapSpecialRoomTeleports.HasCurrentMetadata(existing)) return existing;
        var templates = await LoadAsync(cancellationToken);
        var template = templates.FirstOrDefault(item => item.Floors[0].ImageSha256 == draft.SourceVisualSha256);
        if (template is null) return existing;
        var navigation = await LoadNavigationAsync(template, cancellationToken);
        if (navigation is null) return existing;
        // An earlier imported template can have been edited or resampled. Only
        // exact artwork bindings are recovered; never relabel a changed image.
        foreach (var floor in navigation.Floors)
        {
            if (!draft.FloorPaths.TryGetValue(floor.FloorKey, out var imagePath)) return existing;
            await using var input = File.OpenRead(imagePath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
            if (!hash.Equals(floor.ImageSha256, StringComparison.OrdinalIgnoreCase)) return existing;
        }
        await MapNavigationRules.ValidateImagesAsync(navigation, draft.FloorPaths, cancellationToken);
        // Keep the user's disabled doors when refreshing an older template's channels.
        if (existing is not null)
        {
            var disabled = existing.Doors.Where(door => !door.Enabled).Select(door => door.SourceKey).ToHashSet();
            foreach (var door in navigation.Doors)
                if (door.SourceKey is not null && disabled.Contains(door.SourceKey)) door.Enabled = false;
            var nodes = navigation.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var transition in existing.FloorTransitions.Where(t => t.Id.StartsWith("editor-", StringComparison.Ordinal)
                && nodes.Contains(t.FromNodeId) && nodes.Contains(t.ToNodeId)))
                if (!navigation.FloorTransitions.Any(t => t.Id == transition.Id)) navigation.FloorTransitions.Add(transition);
        }
        return navigation;
    }
}
