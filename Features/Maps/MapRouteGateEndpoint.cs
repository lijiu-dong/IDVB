namespace IDVBuff.Features.Maps;

public sealed record MapRouteGateEndpoint(string Id, string FloorKey, string AnchorKey,
    string ModuleId, NormalizedPoint Point)
{
    public string DisplayName => $"{FloorKey.ToUpperInvariant()} · {AnchorKey switch
    {
        "main-entrance" => "大门",
        "side-entrance" => "侧门",
        _ => "单门"
    }}";

    public static bool IsAllowed(string floor, string key) =>
        floor == "1f" && key is "main-entrance" or "side-entrance"
        || floor == "2f" && key == "second-floor-primary";

    // Recognition anchors are crop-relative; navigation uses the complete image.
    public static IReadOnlyList<MapRouteGateEndpoint> FromDraft(MapDraft draft, MapNavigationTopology topology)
    {
        var result = new List<MapRouteGateEndpoint>();
        foreach (var floor in topology.Floors)
        {
            var profile = draft.Recognition.GetFloor(floor.FloorKey);
            if (profile is null) continue;
            foreach (var anchor in profile.Anchors.Where(a => IsAllowed(floor.FloorKey, a.Key) && a.Bounds?.IsValid is true)
                .OrderBy(a => a.Key == "main-entrance" ? 0 : 1))
            {
                var bounds = MapRecognitionCoordinates.ToSourceRectangle(anchor.Bounds!, profile.GetEffectiveRecognitionRegion());
                var point = new NormalizedPoint { X = bounds.X + bounds.Width / 2, Y = bounds.Y + bounds.Height / 2 };
                // Single-gate nightmare templates duplicate the main marker in
                // the legacy side-anchor slot; that does not create a side door.
                if (result.Any(g => g.FloorKey == floor.FloorKey && MapNavigationRules.Same(g.Point, point))) continue;
                var owner = topology.Modules.Where(m => m.FloorKey == floor.FloorKey && m.Cells.Any(c => c.Contains(point)))
                    .OrderBy(m => m.Id, StringComparer.Ordinal).FirstOrDefault();
                if (owner is null) throw new InvalidOperationException($"{floor.FloorKey.ToUpperInvariant()} 的门标志未连接到可通行区域，请检查门标注和导航数据。");
                result.Add(new($"@gate:{floor.FloorKey}:{anchor.Key}", floor.FloorKey, anchor.Key, owner.Id, point));
            }
        }
        return result;
    }
}
