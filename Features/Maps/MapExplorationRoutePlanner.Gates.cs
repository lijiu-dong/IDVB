namespace IDVBuff.Features.Maps;

public static partial class MapExplorationRoutePlanner
{
    private static void AddGateLinks(MapNavigationTopology topology, IReadOnlyDictionary<string, MapNavigationModule> modules,
        Dictionary<string, MapNavigationNode> nodes, List<MapNavigationLink> linksForRoute,
        IEnumerable<MapRouteGateEndpoint> gates, CancellationToken token)
    {
        foreach (var gate in gates)
        {
            if (!MapRouteGateEndpoint.IsAllowed(gate.FloorKey, gate.AnchorKey) || !gate.Point.IsValid
                || !modules.TryGetValue(gate.ModuleId, out var owner) || owner.IsExcludedFromAutomaticRoutes || owner.FloorKey != gate.FloorKey
                || !owner.Cells.Any(c => c.Contains(gate.Point)) || nodes.ContainsKey(gate.Id))
                throw new InvalidOperationException("入口或出口不是有效的 1F 大门、侧门或 2F 单门。");
            nodes.Add(gate.Id, new() { Id = gate.Id, FloorKey = gate.FloorKey, Point = gate.Point });
            var floor = topology.Floors.Single(f => f.FloorKey == gate.FloorKey);
            var cells = owner.SafeCells.Count > 0 ? owner.SafeCells : owner.Cells;
            var access = new MapNavigationModule { Cells = cells };
            double Pixels(IReadOnlyList<NormalizedPoint> points) => points.Zip(points.Skip(1), (a, b) =>
                Math.Sqrt(Math.Pow((b.X - a.X) * floor.ImageWidth, 2) + Math.Pow((b.Y - a.Y) * floor.ImageHeight, 2))).Sum();
            var floorLinks = topology.Links.Where(l => modules[l.ModuleId].FloorKey == gate.FloorKey).ToArray();
            var pixelLength = floorLinks.Sum(l => Pixels(l.Points));
            var unitsPerPixel = pixelLength <= 1e-9 ? 1 : floorLinks.Sum(l => l.Length) / pixelLength;
            var ports = topology.Doors.Where(d => d.Enabled && (d.ModuleAId == owner.Id || d.ModuleBId == owner.Id))
                .Select(d => d.NodeId).Append(owner.CenterNodeId);
            foreach (var port in ports)
            {
                var path = MapAutomaticRouteEndpointPath.TryFind(access, floor, gate.Point, nodes[port].Point, token);
                if (path is null) continue;
                var length = Pixels(path) * unitsPerPixel;
                if (length <= 1e-9) path = [gate.Point.Clone()];
                linksForRoute.Add(new() { Id = gate.Id + ":" + port, ModuleId = owner.Id,
                    FromNodeId = gate.Id, ToNodeId = port, Points = path, Length = length, Cost = length });
            }
        }
    }
}
