namespace IDVBuff.Features.Maps;

public static class MapAutomaticRouteCanvas
{
    public static bool Supports(MapDraft draft, string floorKey) => draft.Navigation is { } navigation
        && navigation.Links.Any(link => navigation.Modules.Any(module => module.Id == link.ModuleId && module.FloorKey == floorKey && !module.IsExcludedFromAutomaticRoutes));

    public static bool CanPlaceEndpoint(MapNavigationTopology topology, string floorKey, NormalizedPoint point) =>
        point.IsValid && topology.Modules.Any(module => module.FloorKey == floorKey && !module.IsExcludedFromAutomaticRoutes && module.Cells.Any(cell => cell.Contains(point)));

    public static IEnumerable<MapNavigationLink> PreviewLinks(MapNavigationTopology topology, string floorKey)
    {
        var modules = topology.Modules.Where(module => module.FloorKey == floorKey).ToDictionary(module => module.Id);
        var blocked = topology.Doors.Where(door => !door.Enabled).Select(door => door.NodeId).ToHashSet();
        return topology.Links.Where(link => modules.TryGetValue(link.ModuleId, out var owner)
            && !owner.IsExcludedFromAutomaticRoutes
            && (!owner.IsDeadEnd || link.FromNodeId == owner.CenterNodeId || link.ToNodeId == owner.CenterNodeId)
            && !blocked.Contains(link.FromNodeId) && !blocked.Contains(link.ToNodeId));
    }

    private static NormalizedPoint? ClosestStageEndpoint(NormalizedPoint candidate, IEnumerable<MapAnnotation> stages,
        double screenWidth, double screenHeight, double radius)
    {
        var best = radius * radius; NormalizedPoint? result = null;
        foreach (var stage in stages.Where(stage => stage.Type == MapAnnotationType.Route && stage.IsValid))
        foreach (var point in new[] { stage.Start!, stage.End! })
        {
            var distance = Math.Pow((candidate.X - point.X) * screenWidth, 2) + Math.Pow((candidate.Y - point.Y) * screenHeight, 2);
            if (distance > best) continue;
            best = distance; result = point;
        }
        return result;
    }

    public static NormalizedPoint SnapEndpoint(NormalizedPoint candidate, IEnumerable<MapAnnotation> stages,
        double screenWidth, double screenHeight, double radius = 14) =>
        (ClosestStageEndpoint(candidate, stages, screenWidth, screenHeight, radius) ?? candidate).Clone();

    public static NormalizedPoint SnapPoint(NormalizedPoint candidate, IEnumerable<MapAnnotation> stages,
        MapNavigationTopology topology, string floorKey, double screenWidth, double screenHeight, double radius = 14)
    {
        // An existing stage junction wins over a nearby preview line.
        if (ClosestStageEndpoint(candidate, stages, screenWidth, screenHeight, radius) is { } endpoint) return endpoint.Clone();
        var best = radius * radius; var result = candidate;
        foreach (var link in PreviewLinks(topology, floorKey))
        for (var i = 1; i < link.Points.Count; i++)
        {
            var (_, projected) = Project(candidate, link.Points[i - 1], link.Points[i], screenWidth, screenHeight);
            var distance = Math.Pow((projected.X - candidate.X) * screenWidth, 2) + Math.Pow((projected.Y - candidate.Y) * screenHeight, 2);
            if (distance > best) continue;
            best = distance; result = projected;
        }
        return result.Clone();
    }

    private static (double Ratio, NormalizedPoint Point) Project(NormalizedPoint point, NormalizedPoint a, NormalizedPoint b,
        double width, double height)
    {
        var dx = (b.X - a.X) * width; var dy = (b.Y - a.Y) * height;
        var length = dx * dx + dy * dy;
        var ratio = length <= 1e-16 ? 0 : Math.Clamp(((point.X - a.X) * width * dx + (point.Y - a.Y) * height * dy) / length, 0, 1);
        return (ratio, new() { X = a.X + (b.X - a.X) * ratio, Y = a.Y + (b.Y - a.Y) * ratio });
    }

    private sealed record Edge(string To, double Cost, int Direction);
    private readonly record struct State(string Node, int Direction);

    public static MapAnnotation Find(MapNavigationTopology topology, string floorKey, NormalizedPoint start,
        NormalizedPoint end, string color, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MapNavigationRules.Validate(topology);
        if (!CanPlaceEndpoint(topology, floorKey, start) || !CanPlaceEndpoint(topology, floorKey, end))
            throw new InvalidOperationException("请选择当前楼层可通行区域内的起点和终点。");
        if (MapNavigationRules.Same(start, end)) throw new InvalidOperationException("起点和终点不能重合。");
        var floor = topology.Floors.Single(floor => floor.FloorKey == floorKey);
        var modules = topology.Modules.Where(module => module.FloorKey == floorKey && !module.IsExcludedFromAutomaticRoutes).ToDictionary(module => module.Id);
        var nodes = topology.Nodes.ToDictionary(node => node.Id);
        var ports = modules.Values.ToDictionary(module => module.Id, module => topology.Doors
            .Where(door => door.Enabled && (door.ModuleAId == module.Id || door.ModuleBId == module.Id))
            .Select(door => nodes[door.NodeId]).Append(nodes[module.CenterNodeId]).ToArray());
        var starts = modules.Values.Where(module => module.Cells.Any(cell => cell.Contains(start))).Select(module => module.Id).ToHashSet();
        var ends = modules.Values.Where(module => module.Cells.Any(cell => cell.Contains(end))).Select(module => module.Id).ToHashSet();
        var startOnChannel = new HashSet<string>(); var endOnChannel = new HashSet<string>();
        var first = "@stage-start"; var last = "@stage-end";
        var positions = new Dictionary<string, NormalizedPoint>(); var adjacent = new Dictionary<string, List<Edge>>();
        var pathNumber = 0;
        string Key(string moduleId, NormalizedPoint point, int path)
        {
            var key = starts.Contains(moduleId) && MapNavigationRules.Same(point, start) ? first
                : ends.Contains(moduleId) && MapNavigationRules.Same(point, end) ? last
                : ports[moduleId].FirstOrDefault(node => MapNavigationRules.Same(point, node.Point))?.Id
                    ?? FormattableString.Invariant($"{moduleId}@{point.X:F12},{point.Y:F12}");
            if (modules[moduleId].IsDeadEnd && key != first && key != last
                && !topology.Doors.Any(d => d.NodeId == key)) key += ":terminal-path-" + path;
            if (positions.TryAdd(key, point.Clone())) adjacent[key] = [];
            return key;
        }
        double Length(NormalizedPoint a, NormalizedPoint b) => Math.Sqrt(Math.Pow((b.X - a.X) * floor.ImageWidth, 2)
            + Math.Pow((b.Y - a.Y) * floor.ImageHeight, 2));
        void AddPath(string moduleId, IReadOnlyList<NormalizedPoint> points, double travelCost, bool channel)
        {
            var path = ++pathNumber;
            var total = points.Zip(points.Skip(1), Length).Sum();
            if (total <= 1e-12) return;
            for (var i = 1; i < points.Count; i++)
            {
                var parts = new List<(double Ratio, NormalizedPoint Point)> { (0, points[i - 1]), (1, points[i]) };
                void Split(NormalizedPoint point, HashSet<string> owners, HashSet<string> onChannel)
                {
                    if (!owners.Contains(moduleId)) return;
                    var projection = Project(point, points[i - 1], points[i], floor.ImageWidth, floor.ImageHeight);
                    if (!MapNavigationRules.Same(point, projection.Point)) return;
                    parts.Add((projection.Ratio, point)); if (channel) onChannel.Add(moduleId);
                }
                Split(start, starts, startOnChannel); Split(end, ends, endOnChannel);
                var ordered = parts.OrderBy(part => part.Ratio).ToArray();
                for (var j = 1; j < ordered.Length; j++)
                {
                    var a = ordered[j - 1].Point; var b = ordered[j].Point;
                    var aKey = Key(moduleId, a, path); var bKey = Key(moduleId, b, path);
                    if (aKey == bKey) continue;
                    var cost = travelCost * Length(a, b) / total;
                    adjacent[aKey].Add(new(bKey, cost, Heading(a, b))); adjacent[bKey].Add(new(aKey, cost, Heading(b, a)));
                }
            }
        }
        var links = PreviewLinks(topology, floorKey).Where(link => !modules[link.ModuleId].IsDeadEnd).ToArray();
        foreach (var link in links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bends = 0;
            for (var i = 2; i < link.Points.Count; i++)
                if (Heading(link.Points[i - 2], link.Points[i - 1]) != Heading(link.Points[i - 1], link.Points[i])) bends++;
            AddPath(link.ModuleId, link.Points, Math.Max(link.Length, link.Cost - bends * topology.TurnPenalty), true);
        }
        // Off-channel clicks are terminal nodes, connected to every reachable port
        // in their own room. No nearest-projection waypoint is imposed on arrival.
        var floorLinks = topology.Links.Where(link => modules.ContainsKey(link.ModuleId)).ToArray();
        var pixels = floorLinks.Sum(link => link.Points.Zip(link.Points.Skip(1), Length).Sum());
        var unitsPerPixel = pixels <= 1e-12 ? 1 : floorLinks.Sum(link => link.Length) / pixels;
        void Connect(string moduleId, NormalizedPoint a, NormalizedPoint b)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = MapAutomaticRouteEndpointPath.TryFind(modules[moduleId], floor, a, b, cancellationToken);
            if (path is not null) AddPath(moduleId, path, path.Zip(path.Skip(1), Length).Sum() * unitsPerPixel, false);
        }
        foreach (var moduleId in starts.Except(startOnChannel))
            foreach (var port in ports[moduleId]) Connect(moduleId, start, port.Point);
        foreach (var moduleId in ends.Except(endOnChannel))
            foreach (var port in ports[moduleId]) Connect(moduleId, port.Point, end);
        foreach (var moduleId in starts.Intersect(ends))
            if (!startOnChannel.Contains(moduleId) || !endOnChannel.Contains(moduleId)) Connect(moduleId, start, end);
        positions[first] = start.Clone(); positions[last] = end.Clone();
        adjacent.TryAdd(first, []); adjacent.TryAdd(last, []);
        var initial = new State(first, 0);
        var queue = new PriorityQueue<State, double>(); queue.Enqueue(initial, 0);
        var distance = new Dictionary<State, double> { [initial] = 0 };
        var previous = new Dictionary<State, State>(); State? goal = null;
        while (queue.TryDequeue(out var current, out var cost))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cost > distance[current] + 1e-9) continue;
            if (current.Node == last) { goal = current; break; }
            foreach (var edge in adjacent[current.Node])
            {
                var next = new State(edge.To, edge.Direction);
                var candidate = cost + edge.Cost + (current.Direction != 0 && current.Direction != edge.Direction ? topology.TurnPenalty : 0);
                if (distance.TryGetValue(next, out var old) && candidate >= old - 1e-9) continue;
                distance[next] = candidate; previous[next] = current; queue.Enqueue(next, candidate);
            }
        }
        if (goal is null) throw new InvalidOperationException("这两个端点之间没有可通行路线，请重新选择终点。");
        var routePoints = new List<NormalizedPoint>();
        for (var current = goal.Value;; current = previous[current])
        { routePoints.Add(positions[current.Node].Clone()); if (current == initial) break; }
        routePoints.Reverse(); routePoints[0] = start.Clone(); routePoints[^1] = end.Clone();
        var result = new MapAnnotation { Type = MapAnnotationType.Route, ColorHex = color,
            Start = start.Clone(), End = end.Clone(), Points = routePoints };
        if (!result.IsValid) throw new InvalidOperationException("路线点数超过限制，无法创建这个阶段。");
        return result;
    }

    private static int Heading(NormalizedPoint a, NormalizedPoint b)
    {
        static int Sign(double number) => Math.Abs(number) < 1e-10 ? 0 : Math.Sign(number);
        return (Sign(b.X - a.X) + 1) * 3 + Sign(b.Y - a.Y) + 2;
    }
}
