namespace IDVBuff.Features.Maps;

public sealed record MapAutomaticRouteStage(string FloorKey, string ModuleId,
    IReadOnlyList<NormalizedPoint> Points);
public sealed record MapAutomaticRoute(IReadOnlyList<MapAutomaticRouteStage> Stages,
    double Cost, double Length, int DoorCount, int FloorTransitionCount);

public static class MapAutomaticRoutePlanner
{
    private sealed record Edge(string To, string? ModuleId, IReadOnlyList<NormalizedPoint> Points,
        double Cost, double Length, int StartDirection, int EndDirection);
    private readonly record struct State(string Node, int Direction);

    public static MapAutomaticRoute Find(MapNavigationTopology topology, string fromModuleId,
        string toModuleId, CancellationToken cancellationToken = default)
    {
        MapNavigationRules.ValidateForRouting(topology);
        var modules = topology.Modules.ToDictionary(m => m.Id, StringComparer.Ordinal);
        if (!modules.TryGetValue(fromModuleId, out var from) || !modules.TryGetValue(toModuleId, out var to))
            throw new InvalidOperationException("请选择有效的起点和终点模块。");
        if (from.Id == to.Id) throw new InvalidOperationException("起点和终点需要选择不同模块。");
        var nodes = topology.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var blocked = topology.Doors.Where(d => !d.Enabled).Select(d => d.NodeId).ToHashSet(StringComparer.Ordinal);
        var adjacent = nodes.Keys.ToDictionary(id => id, _ => new List<Edge>(), StringComparer.Ordinal);
        foreach (var link in topology.Links)
        {
            if (blocked.Contains(link.FromNodeId) || blocked.Contains(link.ToNodeId)) continue;
            var owner = modules[link.ModuleId];
            if (owner.IsDeadEnd)
            {
                if (owner.Id == from.Id && link.FromNodeId == from.CenterNodeId)
                    AddChannel(link.FromNodeId, link.ToNodeId, link, link.Points);
                else if (owner.Id == from.Id && link.ToNodeId == from.CenterNodeId)
                    AddChannel(link.ToNodeId, link.FromNodeId, link, link.Points.AsEnumerable().Reverse().ToArray());
                if (owner.Id == to.Id && link.ToNodeId == to.CenterNodeId)
                    AddChannel(link.FromNodeId, link.ToNodeId, link, link.Points);
                else if (owner.Id == to.Id && link.FromNodeId == to.CenterNodeId)
                    AddChannel(link.ToNodeId, link.FromNodeId, link, link.Points.AsEnumerable().Reverse().ToArray());
                continue;
            }
            AddChannel(link.FromNodeId, link.ToNodeId, link, link.Points);
            AddChannel(link.ToNodeId, link.FromNodeId, link, link.Points.AsEnumerable().Reverse().ToArray());
        }
        foreach (var transition in topology.FloorTransitions)
        {
            if (transition.IsSpecialTeleport) continue;
            if (modules.Values.Any(m => m.IsDeadEnd && (m.CenterNodeId == transition.FromNodeId || m.CenterNodeId == transition.ToNodeId))) continue;
            if (blocked.Contains(transition.FromNodeId) || blocked.Contains(transition.ToNodeId)) continue;
            adjacent[transition.FromNodeId].Add(new Edge(transition.ToNodeId, null, [], transition.Cost, 0, 0, 0));
            if (!transition.Directed)
                adjacent[transition.ToNodeId].Add(new Edge(transition.FromNodeId, null, [], transition.Cost, 0, 0, 0));
        }
        void AddChannel(string a, string b, MapNavigationLink link, IReadOnlyList<NormalizedPoint> points) =>
            adjacent[a].Add(new Edge(b, link.ModuleId, points, link.Cost, link.Length,
                Direction(points[0], points[1]), Direction(points[^2], points[^1])));

        // Incoming heading is part of the state so a bend at a shared door or
        // module center is charged correctly. Floor changes reset the heading.
        var start = new State(from.CenterNodeId, 0);
        var queue = new PriorityQueue<State, double>();
        var distances = new Dictionary<State, double> { [start] = 0 };
        var previous = new Dictionary<State, (State From, Edge Edge)>();
        queue.Enqueue(start, 0);
        State? goal = null;
        while (queue.TryDequeue(out var state, out var cost))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cost > distances[state] + 1e-9) continue;
            if (state.Node == to.CenterNodeId) { goal = state; break; }
            foreach (var edge in adjacent[state.Node])
            {
                var bend = state.Direction != 0 && edge.StartDirection != 0
                    && state.Direction != edge.StartDirection ? topology.TurnPenalty : 0;
                var next = new State(edge.To, edge.EndDirection);
                var candidate = cost + edge.Cost + bend;
                if (distances.TryGetValue(next, out var old) && candidate >= old - 1e-9) continue;
                distances[next] = candidate;
                previous[next] = (state, edge);
                queue.Enqueue(next, candidate);
            }
        }
        if (goal is null)
            throw new InvalidOperationException("起终点之间没有可通行的中央通道；请检查门框和跨楼层连接。");
        var edges = new List<Edge>();
        for (var state = goal.Value; state != start;)
        {
            var step = previous[state]; edges.Add(step.Edge); state = step.From;
        }
        edges.Reverse();
        var stages = new List<MapAutomaticRouteStage>();
        foreach (var edge in edges.Where(e => e.ModuleId is not null))
            stages.Add(new MapAutomaticRouteStage(modules[edge.ModuleId!].FloorKey, edge.ModuleId!,
                edge.Points.Select(p => p.Clone()).ToArray()));
        var doorNodes = topology.Doors.Select(d => d.NodeId).ToHashSet(StringComparer.Ordinal);
        return new MapAutomaticRoute(stages, distances[goal.Value], edges.Sum(e => e.Length),
            edges.Count(e => doorNodes.Contains(e.To)), edges.Count(e => e.ModuleId is null));
    }

    private static int Direction(NormalizedPoint a, NormalizedPoint b)
    {
        static int Sign(double value) => Math.Abs(value) < 1e-9 ? 0 : Math.Sign(value);
        return (Sign(b.X - a.X) + 1) * 3 + Sign(b.Y - a.Y) + 2;
    }

    public static IReadOnlyDictionary<string, List<MapAnnotation>> CreateAnnotations(
        MapAutomaticRoute route, string colorHex = "#FF5359")
    {
        var floors = new Dictionary<string, List<MapAnnotation>>(StringComparer.Ordinal);
        foreach (var stage in route.Stages)
        {
            if (!floors.TryGetValue(stage.FloorKey, out var annotations)) floors[stage.FloorKey] = annotations = [];
            for (var i = 1; i < stage.Points.Count; i++)
            {
                if (MapNavigationRules.Same(stage.Points[i - 1], stage.Points[i])) continue;
                annotations.Add(new MapAnnotation { Type = MapAnnotationType.Line, ColorHex = colorHex,
                    Start = stage.Points[i - 1].Clone(), End = stage.Points[i].Clone() });
            }
        }
        return floors;
    }
}
