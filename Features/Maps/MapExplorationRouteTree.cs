namespace IDVBuff.Features.Maps;

/// <summary>Draw each short detour once, oriented from the trunk to one room.
/// Returning from the leaf is implicit; it never creates another drawn channel.</summary>
public static class MapExplorationRouteTree
{
    private sealed record Leg(string From, string To, MapNavigationLink Link, IReadOnlyList<NormalizedPoint> Points);
    private sealed record Label(string Node, HashSet<string> Corridors, HashSet<string> Nodes, List<Leg> Legs, double Length);

    public static MapExplorationRoute Complete(MapNavigationTopology topology, MapExplorationRoute trunk,
        MapExplorationOptions options, CancellationToken token)
    {
        trunk = trunk with { ExplorationFloors = options.AllowedFloors.ToHashSet() };
        var modules = topology.Modules.ToDictionary(m => m.Id);
        var nodes = topology.Nodes.ToDictionary(n => n.Id);
        // Physical ownership also includes empty landings crossed by the trunk.
        var initialVisits = MapExplorationRoomVisits.Create(topology, trunk, includeEmptyPassages: true);
        var owners = initialVisits.GroupBy(v => MapExplorationRoutePlanner.RoomKey(modules[v.ModuleId]))
            .ToDictionary(g => g.Key, g => modules[g.First().ModuleId]);
        var visited = MapExplorationRoomVisits.Create(topology, trunk).Where(v => options.AllowedFloors.Contains(modules[v.ModuleId].FloorKey)
                && MapExplorationRoutePlanner.IsLootTarget(topology, modules[v.ModuleId]))
            .Select(v => MapExplorationRoutePlanner.RoomKey(modules[v.ModuleId])).ToHashSet();
        var targets = modules.Values.Where(m => MapExplorationRoutePlanner.IsLootTarget(topology, m) && options.AllowedFloors.Contains(m.FloorKey))
            .GroupBy(MapExplorationRoutePlanner.RoomKey).ToArray();
        var targetKeys = targets.Select(g => g.Key).ToHashSet();
        var high = targets.Where(g => g.Any(m => options.HighValueRoomIds.Contains(m.Id) || m.FloorKey == "b1f"))
            .Select(g => g.Key).ToHashSet();
        var ordinaryTarget = (targets.Count(g => !high.Contains(g.Key)) + 1) / 2;
        bool Desired(string key) => targetKeys.Contains(key) && (options.Coverage == MapRoomCoverage.AllRooms || high.Contains(key)
            || options.Coverage == MapRoomCoverage.Balanced && visited.Count(id => targetKeys.Contains(id) && !high.Contains(id)) < ordinaryTarget);
        bool Corridor(MapNavigationModule m) => !MapExplorationRoutePlanner.IsRoom(m)
            || m.CanChangeFloors && owners.TryGetValue(MapExplorationRoutePlanner.RoomKey(m), out var owner) && owner.FloorKey != m.FloorKey;
        var blocked = topology.Doors.Where(d => !d.Enabled).Select(d => d.NodeId).ToHashSet();
        blocked.UnionWith(modules.Values.Where(m => m.IsExcludedFromAutomaticRoutes).Select(m => m.CenterNodeId));
        var adjacent = nodes.Keys.ToDictionary(id => id, _ => new List<Leg>());
        foreach (var link in topology.Links)
        {
            if (modules[link.ModuleId].IsExcludedFromAutomaticRoutes || blocked.Contains(link.FromNodeId) || blocked.Contains(link.ToNodeId)
                || !MapExplorationRouteGeometry.IsSimple(link.Points)) continue;
            adjacent[link.FromNodeId].Add(new(link.FromNodeId, link.ToNodeId, link, link.Points));
            adjacent[link.ToNodeId].Add(new(link.ToNodeId, link.FromNodeId, link, link.Points.AsEnumerable().Reverse().ToArray()));
        }
        var proposed = new List<MapExplorationBranch>();
        foreach (var stage in trunk.Stages)
        {
            if (!options.AllowedFloors.Contains(stage.FloorKey)) continue;
            var stageModules = stage.Steps.Select(s => modules[s.ModuleId!]).Where(Corridor).DistinctBy(m => m.Id).ToArray();
            var roots = stageModules.SelectMany(m => topology.Doors.Where(d => d.Enabled && (d.ModuleAId == m.Id || d.ModuleBId == m.Id))
                    .Select(d => d.NodeId).Append(m.CenterNodeId)).Distinct()
                .Where(id => stage.Steps.Any(s => OnPath(nodes[id].Point, s.Points))).ToArray();
            foreach (var root in roots)
            {
                token.ThrowIfCancellationRequested();
                var queue = new PriorityQueue<Label, double>();
                queue.Enqueue(new(root, [], [root], [], 0), 0);
                var best = new Dictionary<string, double>();
                while (queue.TryDequeue(out var label, out _))
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var leg in adjacent[label.Node])
                    {
                        if (label.Nodes.Contains(leg.To)) continue;
                        var module = modules[leg.Link.ModuleId];
                        if (module.FloorKey != stage.FloorKey) continue;
                        var legs = label.Legs.Append(leg).ToList();
                        var length = label.Length + leg.Link.Length;
                        if (!Corridor(module))
                        {
                            var key = MapExplorationRoutePlanner.RoomKey(module);
                            if (visited.Contains(key) || !Desired(key) || leg.To != module.CenterNodeId) continue;
                            var branch = TrimPrefix(topology, trunk, stage, module.Id, legs);
                            if (branch is not null && Clear(topology, trunk, branch, [])) proposed.Add(branch);
                            continue;
                        }
                        var corridors = label.Corridors.Append(module.Id).ToHashSet();
                        if (corridors.Count > 2) continue;
                        var state = leg.To + ":" + string.Join(",", corridors.Order(StringComparer.Ordinal));
                        if (best.TryGetValue(state, out var old) && old <= length) continue;
                        best[state] = length;
                        queue.Enqueue(new(leg.To, corridors, label.Nodes.Append(leg.To).ToHashSet(), legs, length), length);
                    }
                }
            }
        }
        double Length(MapExplorationBranch b) => b.Steps.Sum(s => s.Length);
        int Turns(MapExplorationBranch b) => b.Steps.Sum(s => s.Turns);
        var branches = new List<MapExplorationBranch>();
        foreach (var branch in proposed.OrderBy(b => high.Contains(MapExplorationRoutePlanner.RoomKey(modules[b.TargetRoomId])) ? 0 : 1)
            .ThenBy(b => options.Preference == MapRoutePreference.Simplest ? Turns(b) : Length(b))
            .ThenBy(Length).ThenBy(b => b.StageNumber).ThenBy(b => b.AttachmentStepIndex).ThenBy(b => b.TargetRoomId, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var key = MapExplorationRoutePlanner.RoomKey(modules[branch.TargetRoomId]);
            if (visited.Contains(key) || !Desired(key) || !Clear(topology, trunk, branch, branches)) continue;
            branches.Add(branch); visited.Add(key);
        }
        var tree = trunk with { Branches = branches, Length = trunk.Length + 2 * branches.Sum(Length),
            Turns = trunk.Turns + branches.Sum(b => 2 * Turns(b) + 2) };
        var visits = MapExplorationRoomVisits.Create(topology, tree);
        return tree with { RoomVisits = visits,
            VisitedRoomIds = visits.GroupBy(v => MapExplorationRoutePlanner.RoomKey(modules[v.ModuleId])).Select(g => g.First().ModuleId).ToArray(),
            SkippedTargetRoomIds = targets.Where(g => !visited.Contains(g.Key) && (options.Coverage == MapRoomCoverage.AllRooms || high.Contains(g.Key)))
                .Select(g => g.First().Id).ToArray() };
    }

    public static bool OnPath(NormalizedPoint point, IReadOnlyList<NormalizedPoint> path) =>
        path.Zip(path.Skip(1)).Any(p => OnSegment(point, p.First, p.Second));

    private static bool OnSegment(NormalizedPoint p, NormalizedPoint a, NormalizedPoint b) =>
        Math.Abs((p.X - a.X) * (b.Y - a.Y) - (p.Y - a.Y) * (b.X - a.X)) < 1e-10
        && p.X >= Math.Min(a.X, b.X) - 1e-9 && p.X <= Math.Max(a.X, b.X) + 1e-9
        && p.Y >= Math.Min(a.Y, b.Y) - 1e-9 && p.Y <= Math.Max(a.Y, b.Y) + 1e-9;

    private static MapExplorationBranch? TrimPrefix(MapNavigationTopology topology, MapExplorationRoute trunk,
        MapExplorationStage stage, string target, IReadOnlyList<Leg> legs)
    {
        var floor = topology.Floors.Single(f => f.FloorKey == stage.FloorKey);
        var main = trunk.Stages.Where(s => s.FloorKey == stage.FloorKey).SelectMany(s => s.Steps).SelectMany(s => s.Points.Zip(s.Points.Skip(1))).ToArray();
        var steps = new List<MapExplorationStep>(); var trimming = true;
        foreach (var leg in legs)
        {
            var points = leg.Points.Select(p => p.Clone()).ToList();
            while (trimming && points.Count >= 2)
            {
                var a = points[0]; var b = points[1];
                var dx = b.X - a.X; var dy = b.Y - a.Y; var length = dx * dx + dy * dy;
                var intervals = main.Where(p => Math.Abs(dx * (p.Second.Y - p.First.Y) - dy * (p.Second.X - p.First.X)) < 1e-10
                        && Math.Abs(dx * (p.First.Y - a.Y) - dy * (p.First.X - a.X)) < 1e-10)
                    .Select(p => (A: ((p.First.X - a.X) * dx + (p.First.Y - a.Y) * dy) / length,
                        B: ((p.Second.X - a.X) * dx + (p.Second.Y - a.Y) * dy) / length))
                    .Select(p => (Start: Math.Max(0, Math.Min(p.A, p.B)), End: Math.Min(1, Math.Max(p.A, p.B))))
                    .Where(p => p.End >= p.Start).OrderBy(p => p.Start).ToArray();
                var covered = 0d;
                foreach (var interval in intervals) { if (interval.Start > covered + 1e-9) break; covered = Math.Max(covered, interval.End); }
                if (covered >= 1 - 1e-9) { points.RemoveAt(0); continue; }
                points[0] = new() { X = a.X + covered * dx, Y = a.Y + covered * dy }; trimming = false;
            }
            if (points.Count < 2) continue;
            double Pixels(IReadOnlyList<NormalizedPoint> p) => p.Zip(p.Skip(1), (a, b) =>
                Math.Sqrt(Math.Pow((b.X - a.X) * floor.ImageWidth, 2) + Math.Pow((b.Y - a.Y) * floor.ImageHeight, 2))).Sum();
            var ratio = Pixels(points) / Pixels(leg.Points);
            steps.Add(new(steps.Count == 0 ? "@branch-junction" : leg.From, leg.To, leg.Link.ModuleId, points,
                leg.Link.Length * ratio, leg.Points.Count - 2));
        }
        if (steps.Count == 0 || trimming) return null;
        var junction = steps[0].Points[0];
        var attachment = stage.Steps.Select((step, index) => (step, index)).FirstOrDefault(p => OnPath(junction, p.step.Points));
        if (attachment.step is null) return null;
        return new(stage.Number, attachment.index, junction.Clone(), target, steps);
    }

    public static bool Clear(MapNavigationTopology topology, MapExplorationRoute trunk, MapExplorationBranch branch,
        IReadOnlyList<MapExplorationBranch> accepted)
    {
        var modules = topology.Modules.ToDictionary(m => m.Id);
        var stage = trunk.Stages.Single(s => s.Number == branch.StageNumber);
        var points = Points(branch.Steps);
        if (points.Count < 2 || branch.AttachmentStepIndex < 0 || branch.AttachmentStepIndex >= stage.Steps.Count
            || !modules.TryGetValue(branch.TargetRoomId, out var target) || !MapExplorationRoutePlanner.IsLootTarget(topology, target)
            || trunk.ExplorationFloors is { } floors && !floors.Contains(target.FloorKey)
            || branch.Steps[^1].ModuleId != target.Id
            || !MapNavigationRules.Same(points[^1], topology.Nodes.Single(n => n.Id == target.CenterNodeId).Point)
            || branch.Steps.Zip(branch.Steps.Skip(1)).Any(p => !MapNavigationRules.Same(p.First.Points[^1], p.Second.Points[0]))
            || !MapExplorationRouteGeometry.IsSimple(points) || !MapNavigationRules.Same(branch.Junction, points[0])
            || branch.Steps.Select(s => modules[s.ModuleId!]).Where(m => m.Id != branch.TargetRoomId).DistinctBy(m => m.Id).Count() > 2
            || !OnPath(branch.Junction, stage.Steps[branch.AttachmentStepIndex].Points)) return false;
        foreach (var step in branch.Steps)
        {
            var m = modules[step.ModuleId!]; var cells = m.SafeCells.Count > 0 ? m.SafeCells : m.Cells;
            if (m.FloorKey != stage.FloorKey || step.Points.Zip(step.Points.Skip(1)).Any(p => !MapNavigationRules.ContainsSegment(cells, p.First, p.Second))) return false;
        }
        foreach (var main in trunk.Stages.Where(s => s.FloorKey == stage.FloorKey).SelectMany(s => s.Steps))
            if (OnPath(points[^1], main.Points) || ConflictsExceptAt(points, main.Points, branch.Junction)) return false;
        foreach (var other in accepted.Where(b => trunk.Stages.Single(s => s.Number == b.StageNumber).FloorKey == stage.FloorKey))
            if (ConflictsExceptAt(points, Points(other.Steps), branch.Junction)) return false;
        return true;
    }

    public static IReadOnlyList<NormalizedPoint> Points(IReadOnlyList<MapExplorationStep> steps)
    {
        var points = new List<NormalizedPoint>();
        foreach (var p in steps.SelectMany(s => s.Points))
            if (points.Count == 0 || !MapNavigationRules.Same(points[^1], p)) points.Add(p);
        return points;
    }

    private static bool ConflictsExceptAt(IReadOnlyList<NormalizedPoint> a, IReadOnlyList<NormalizedPoint> b, NormalizedPoint fork)
    {
        foreach (var pair in b.Zip(b.Skip(1)))
        {
            if (OnSegment(fork, pair.First, pair.Second))
            {
                if (!MapNavigationRules.Same(pair.First, fork) && MapExplorationRouteGeometry.Conflicts(a, [pair.First, fork])) return true;
                if (!MapNavigationRules.Same(pair.Second, fork) && MapExplorationRouteGeometry.Conflicts(a, [fork, pair.Second])) return true;
            }
            else if (MapExplorationRouteGeometry.Conflicts(a, [pair.First, pair.Second], false)) return true;
        }
        return false;
    }
}
