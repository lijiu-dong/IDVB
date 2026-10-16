namespace IDVBuff.Features.Maps;

/// <summary>Portable stage lines, short branches, transfer frames and room markers.</summary>
public static class MapExplorationRouteDrawing
{
    public static IReadOnlyDictionary<string, List<MapAnnotation>> Create(MapNavigationTopology topology,
        MapExplorationRoute route, CancellationToken token = default)
    {
        if (topology.SourceLevelId is not null || topology.Modules.Any(m => m.SourceRoomConfigId > 0))
            MapNavigationRules.ValidateForRouting(topology);
        var modules = topology.Modules.ToDictionary(m => m.Id);
        var result = topology.Floors.ToDictionary(f => f.FloorKey, _ => new List<MapAnnotation>());
        var previous = new List<(string Floor, IReadOnlyList<NormalizedPoint> Points)>();
        foreach (var stage in route.Stages)
        {
            if (stage.TransferRoomId is not null || stage.ArrivalRoomId is not null)
            {
                if (stage.TransferRoomId is not { } departureId || stage.ArrivalRoomId is not { } arrivalId
                    || !modules.TryGetValue(departureId, out var departureRoom)
                    || !modules.TryGetValue(arrivalId, out var arrivalRoom)
                    || !topology.FloorTransitions.Any(t => (t.IsSpecialTeleport || departureRoom.CanChangeFloors && arrivalRoom.CanChangeFloors)
                        && (t.FromNodeId == departureRoom.CenterNodeId && t.ToNodeId == arrivalRoom.CenterNodeId
                        || !t.Directed && t.FromNodeId == arrivalRoom.CenterNodeId && t.ToNodeId == departureRoom.CenterNodeId)))
                    throw new InvalidOperationException("阶段转接房间没有可按此方向通行的楼梯或下落连接，未写入地图。");
            }
            var points = new List<NormalizedPoint>();
            foreach (var step in stage.Steps)
            {
                token.ThrowIfCancellationRequested();
                var room = modules[step.ModuleId!];
                var safe = room.SafeCells.Count > 0 ? room.SafeCells : room.Cells;
                if (step.Points.Zip(step.Points.Skip(1)).Any(p => !MapNavigationRules.ContainsSegment(safe, p.First, p.Second)))
                    throw new InvalidOperationException("路线超出了可通行通道，未写入地图。");
                if (previous.Select((p, index) => (p, index)).Any(item => item.p.Floor == stage.FloorKey
                    && MapExplorationRouteGeometry.Conflicts(item.p.Points, step.Points,
                        item.index == previous.Count - 1 && MapNavigationRules.Same(item.p.Points[^1], step.Points[0]))))
                    throw new InvalidOperationException("路线存在交叉或重叠，未写入地图。请调整入口、出口或经过房间。");
                previous.Add((stage.FloorKey, step.Points));
                foreach (var point in step.Points)
                    if (points.Count == 0 || !MapNavigationRules.Same(points[^1], point)) points.Add(point.Clone());
            }
            if (!MapExplorationRouteGeometry.IsSimple(points)) throw new InvalidOperationException("路线发生了回头或自交，未写入地图。");
            if (points.Count >= 2)
                result[stage.FloorKey].Add(new() { Type = MapAnnotationType.Route, ColorHex = stage.Color,
                    Start = points[0].Clone(), End = points[^1].Clone(), Points = points, Text = $"阶段 {stage.Number}" });
            if (stage.TransferRoomId is { } departure) Frame(modules[departure], stage.Color);
            if (stage.ArrivalRoomId is { } arrival) Frame(modules[arrival], stage.Color);
        }
        var accepted = new List<MapExplorationBranch>();
        foreach (var branch in route.Branches)
        {
            token.ThrowIfCancellationRequested();
            if (!MapExplorationRouteTree.Clear(topology, route, branch, accepted))
                throw new InvalidOperationException("支路交叉、越界或超过两个走廊模块，未写入地图。");
            var stage = route.Stages.Single(s => s.Number == branch.StageNumber);
            var points = MapExplorationRouteTree.Points(branch.Steps).Select(p => p.Clone()).ToList();
            result[stage.FloorKey].Add(new() { Type = MapAnnotationType.Route, ColorHex = stage.Color,
                Start = points[0].Clone(), End = points[^1].Clone(), Points = points, Text = $"阶段 {stage.Number} 支路" });
            accepted.Add(branch);
        }
        foreach (var visit in MapExplorationRoomVisits.Create(topology, route))
        {
            token.ThrowIfCancellationRequested();
            var room = modules[visit.ModuleId];
            var floor = topology.Floors.Single(f => f.FloorKey == room.FloorKey);
            var transferSymbol = room.FloorTraversalKind switch
            {
                MapFloorTraversalKind.Drop => "↓",
                _ => "⇅"
            };
            var label = MapMarkerTool.RoomLabel(room.CanChangeFloors, room.IsHighValue || room.FloorKey == "b1f", visit.Number, transferSymbol);
            var center = topology.Nodes.Single(n => n.Id == room.CenterNodeId).Point;
            result[room.FloorKey].Add(MapMarkerTool.Create(label, center, floor.ImageWidth, floor.ImageHeight));
        }
        var planId = Guid.NewGuid();
        foreach (var annotation in result.Values.SelectMany(items => items))
        {
            annotation.RoutePlanId = planId;
            if (!annotation.IsValid) throw new InvalidOperationException("路线无法保存为有效标注，未写入地图。");
        }
        return result;

        void Frame(MapNavigationModule room, string color)
        {
            if (result[room.FloorKey].Any(a => a.Type == MapAnnotationType.Outline && a.Text == room.Id))
                throw new InvalidOperationException("路线重复使用了转接房间，无法生成唯一颜色的框线。");
            var cells = room.Cells; var left = cells.Min(c => c.X); var top = cells.Min(c => c.Y);
            var right = Math.Min(1, Math.Max(cells.Max(c => c.X + c.Width), left + .01));
            var bottom = Math.Min(1, Math.Max(cells.Max(c => c.Y + c.Height), top + .01));
            left = Math.Min(left, right - .01); top = Math.Min(top, bottom - .01);
            result[room.FloorKey].Add(new() { Type = MapAnnotationType.Outline, ColorHex = color, Text = room.Id,
                Bounds = new() { X = left, Y = top, Width = Math.Max(.01, right - left), Height = Math.Max(.01, bottom - top) } });
        }
    }
}
