using System.Globalization;

namespace IDVBuff.Features.Maps;

/// <summary>Identify a complete old generated plan before replacing any floor.</summary>
public static class MapPlannedRouteReplacement
{
    public static HashSet<Guid> FindAnnotationIds(MapNavigationTopology navigation,
        IReadOnlyDictionary<string, List<MapAnnotation>> floors)
    {
        var ids = floors.Values.SelectMany(a => a).Where(a => a.RoutePlanId.HasValue).Select(a => a.Id).ToHashSet();
        if (navigation.SourceLevelId is null) return ids;
        var stages = floors.Values.SelectMany(a => a).Where(IsLegacyStage).ToArray();
        if (!stages.Any(a => a.Text == "阶段 1")) return ids;
        var frames = new List<MapAnnotation>();
        var markers = new List<(MapAnnotation Annotation, int Number)>();
        foreach (var (key, annotations) in floors)
        {
            var floor = navigation.Floors.SingleOrDefault(f => f.FloorKey == key);
            if (floor is null) continue;
            var rooms = navigation.Modules.Where(m => m.FloorKey == key).ToArray();
            foreach (var annotation in annotations.Where(a => !a.RoutePlanId.HasValue))
            {
                if (annotation.Type == MapAnnotationType.Outline && annotation.Bounds is { } bounds
                    && rooms.SingleOrDefault(m => m.Id == annotation.Text) is { Cells.Count: > 0 } room
                    && Same(bounds, RoomBounds(room)) && stages.Any(s => s.EffectiveColorHex == annotation.EffectiveColorHex))
                    frames.Add(annotation);
                if (annotation.Type != MapAnnotationType.Text || annotation.IsMarker
                    || annotation.FontFamily != "Segoe UI Symbol" || annotation.IsBold != true
                    || annotation.FontSize is not null || annotation.IsItalic == true || annotation.IsStrikethrough == true
                    || annotation.EffectiveColorHex != MapMarkerTool.Color || !MapMarkerTool.IsValidLabel(annotation.Text)
                    || !int.TryParse(annotation.Text!.Split(' ')[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    || number < 1 || annotation.Bounds is null) continue;
                if (rooms.Where(MapExplorationRoutePlanner.IsRoom).Any(m =>
                    navigation.Nodes.SingleOrDefault(n => n.Id == m.CenterNodeId) is { } node
                    && Same(annotation.Bounds, MapMarkerTool.Create(annotation.Text!, node.Point, floor.ImageWidth, floor.ImageHeight).Bounds!)))
                    markers.Add((annotation, number));
            }
        }
        // Labels alone cannot distinguish manual text. Require generated room
        // geometry, font/size, stage colors, transfer frames, and a numbered set.
        var numbers = markers.Select(m => m.Number).ToHashSet();
        var last = 0;
        while (numbers.Contains(last + 1)) last++;
        if (frames.Count == 0 || last < 2) return ids;
        var anchoredStages = stages.Where(stage => floors.Any(floor => floor.Value.Contains(stage)
            && frames.Concat(markers.Where(m => m.Number <= last).Select(m => m.Annotation))
                .Any(a => floor.Value.Contains(a) && a.Bounds is { } bounds
                    && (AtCenter(stage.Start!, bounds) || AtCenter(stage.End!, bounds))))).ToArray();
        if (!anchoredStages.Any(a => a.Text == "阶段 1")) return ids;
        ids.UnionWith(anchoredStages.Select(a => a.Id));
        ids.UnionWith(frames.Select(a => a.Id));
        ids.UnionWith(markers.Where(m => m.Number <= last).Select(m => m.Annotation.Id));
        return ids;
    }

    private static bool IsLegacyStage(MapAnnotation annotation)
    {
        if (annotation.RoutePlanId.HasValue || annotation.Type != MapAnnotationType.Route
            || annotation.Start?.IsValid is not true || annotation.End?.IsValid is not true
            || annotation.Bounds is not null || annotation.Text is not { } text || !text.StartsWith("阶段 ", StringComparison.Ordinal)) return false;
        var suffix = text[3..];
        if (suffix.EndsWith(" 支路", StringComparison.Ordinal)) suffix = suffix[..^3];
        return int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var stage)
            && stage >= 1 && stage <= MapExplorationRoutePlanner.StageColors.Count
            && annotation.EffectiveColorHex == MapExplorationRoutePlanner.StageColors[stage - 1];
    }

    private static NormalizedRectangle RoomBounds(MapNavigationModule room)
    {
        var left = room.Cells.Min(c => c.X); var top = room.Cells.Min(c => c.Y);
        var right = Math.Min(1, Math.Max(room.Cells.Max(c => c.X + c.Width), left + .01));
        var bottom = Math.Min(1, Math.Max(room.Cells.Max(c => c.Y + c.Height), top + .01));
        left = Math.Min(left, right - .01); top = Math.Min(top, bottom - .01);
        return new() { X = left, Y = top, Width = Math.Max(.01, right - left), Height = Math.Max(.01, bottom - top) };
    }

    private static bool Same(NormalizedRectangle a, NormalizedRectangle b) =>
        Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6
        && Math.Abs(a.Width - b.Width) < 1e-6 && Math.Abs(a.Height - b.Height) < 1e-6;

    private static bool AtCenter(NormalizedPoint point, NormalizedRectangle bounds) =>
        Math.Abs(point.X - bounds.X - bounds.Width / 2) < 1e-6
        && Math.Abs(point.Y - bounds.Y - bounds.Height / 2) < 1e-6;
}
