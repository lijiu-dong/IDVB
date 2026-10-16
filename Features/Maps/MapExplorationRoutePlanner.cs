using System.Numerics;

namespace IDVBuff.Features.Maps;

public enum MapRoomCoverage { HighValue, Balanced, AllRooms }
public enum MapRoutePreference { Shortest, Simplest }
public sealed record MapExplorationOptions(string EntranceId, string ExitId,
    IReadOnlySet<string> AllowedFloors, MapRoomCoverage Coverage, MapRoutePreference Preference,
    IReadOnlySet<string> HighValueRoomIds, IReadOnlyList<MapRouteGateEndpoint> Gates,
    bool UseSpecialRoomTeleports = false);
public sealed record MapExplorationStep(string From, string To, string? ModuleId,
    IReadOnlyList<NormalizedPoint> Points, double Length, int Turns);
public sealed record MapExplorationStage(int Number, string FloorKey, string Color,
    IReadOnlyList<MapExplorationStep> Steps, string? TransferRoomId, string? ArrivalRoomId);
public sealed record MapExplorationRoute(IReadOnlyList<MapExplorationStage> Stages,
    IReadOnlyList<string> VisitedRoomIds, double Length, int Turns, string EntranceId, string ExitId,
    IReadOnlyList<string> SkippedTargetRoomIds)
{
    public IReadOnlyList<MapExplorationBranch> Branches { get; init; } = [];
    public IReadOnlyList<MapExplorationRoomVisit> RoomVisits { get; init; } = [];
}

/// <summary>Coverage uses bounded beam search over simple paths. This is a
/// deterministic heuristic; legs use direction-aware search with occupied-channel exclusions.</summary>
public static class MapExplorationRoutePlanner
{
    public static IReadOnlyList<string> StageColors { get; } = Array.AsReadOnly(
        new[] { "#FF4545", "#00D4FF", "#00FF5E", "#FF87A5", "#6972FF", "#FFD166", "#FF9F43", "#4D9FFF", "#F2F2F2", "#38B2AC", "#C5E86C" });
    // Connected landings are one physical room; the first actual visit owns it.
    public static string RoomKey(MapNavigationModule module) => module.CanChangeFloors && module.SourceRoomId is { Length: > 0 }
        ? "source-room:" + module.SourceRoomId : module.Id;
    public static bool IsRoom(MapNavigationModule module) => module.Kind != "corridor";
    private sealed record Edge(string From, string To, string? ModuleId,
        IReadOnlyList<NormalizedPoint> Points, double Length, int Turns, int First, int Last, ulong TransferRooms,
        BigInteger Occupied, BigInteger Conflicts, bool SpecialTeleport);
    private readonly record struct State(string Node, int Heading, ulong TransferRooms, uint Floors);
    private sealed record SearchLabel(State State, double Length, int Turns, BigInteger Occupied, SearchLabel? Previous, Edge? Edge, BigInteger Rooms = default, int Transfers = 0);
    private sealed record PathResult(IReadOnlyList<Edge> Edges, double Length, int Turns);

    public static MapExplorationRoute Find(MapNavigationTopology topology, MapExplorationOptions options,
        CancellationToken token = default, IProgress<double>? progress = null)
    {
        MapNavigationRules.ValidateForRouting(topology);
        if (options.UseSpecialRoomTeleports) MapSpecialRoomTeleports.ValidateSelection(topology, options.AllowedFloors);
        if (!Enum.IsDefined(options.Coverage) || !Enum.IsDefined(options.Preference))
            throw new InvalidOperationException("路线偏好无效。");
        var modules = topology.Modules.ToDictionary(m => m.Id);
        var nodes = topology.Nodes.ToDictionary(n => n.Id);
        var centerRooms = modules.Values.ToDictionary(m => m.CenterNodeId, m => m.Id);
        var entrance = options.Gates.SingleOrDefault(g => g.Id == options.EntranceId);
        var exit = options.Gates.SingleOrDefault(g => g.Id == options.ExitId);
        if (entrance is null || exit is null || entrance.Id == exit.Id
            || entrance.FloorKey == exit.FloorKey && MapNavigationRules.Same(entrance.Point, exit.Point))
            throw new InvalidOperationException("请选择不同的入口和出口门标志。");
        var linksForRoute = topology.Links.ToList();
        foreach (var gate in new[] { entrance, exit })
        {
            if (!MapRouteGateEndpoint.IsAllowed(gate.FloorKey, gate.AnchorKey) || !gate.Point.IsValid
                || !modules.TryGetValue(gate.ModuleId, out var owner) || owner.FloorKey != gate.FloorKey
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
        var nodeBits = nodes.Keys.Select((id, index) => (id, bit: BigInteger.One << index)).ToDictionary(n => n.id, n => n.bit);
        var linkBits = linksForRoute.Select((link, index) => (link.Id, bit: BigInteger.One << (nodes.Count + index))).ToDictionary(l => l.Id, l => l.bit);
        var conflicts = linksForRoute.ToDictionary(l => l.Id, l => linkBits[l.Id]);
        // Different graph links can share only part of a drawn channel. Block
        // the physical geometry, not merely the source/destination node pair.
        foreach (var group in linksForRoute.GroupBy(l => modules[l.ModuleId].FloorKey))
        {
            var links = group.ToArray();
            for (var i = 0; i < links.Length; i++)
            for (var j = i + 1; j < links.Length; j++)
            {
                token.ThrowIfCancellationRequested();
                if (!MapExplorationRouteGeometry.Conflicts(links[i].Points, links[j].Points)) continue;
                conflicts[links[i].Id] |= linkBits[links[j].Id];
                conflicts[links[j].Id] |= linkBits[links[i].Id];
            }
        }
        if (topology.Floors.Count > 32) throw new InvalidOperationException("楼层超过当前路线规划支持的数量。");
        var floorBits = topology.Floors.Select((floor, index) => (floor.FloorKey, Bit: 1u << index)).ToDictionary(f => f.FloorKey, f => f.Bit);
        var connectorBits = new Dictionary<string, ulong>();
        ulong ConnectorBit(string node)
        {
            var room = modules[centerRooms[node]]; var key = room.SourceRoomId ?? room.Id;
            if (!connectorBits.TryGetValue(key, out var bit))
            {
                if (connectorBits.Count >= 63) throw new InvalidOperationException("转接房间超过当前路线规划支持的数量。");
                connectorBits[key] = bit = 1UL << connectorBits.Count;
            }
            return bit;
        }
        if (options.AllowedFloors.Count == 0 || options.AllowedFloors.Except(topology.Floors.Select(f => f.FloorKey)).Any()
            || !options.AllowedFloors.Contains(entrance.FloorKey) || !options.AllowedFloors.Contains(exit.FloorKey))
            throw new InvalidOperationException("允许的楼层必须包含入口和出口。");
        var blocked = topology.Doors.Where(d => !d.Enabled).Select(d => d.NodeId).ToHashSet();
        var adjacent = nodes.Values.Where(n => options.AllowedFloors.Contains(n.FloorKey) && !blocked.Contains(n.Id))
            .ToDictionary(n => n.Id, _ => new List<Edge>());
        void Add(string from, string to, string? module, IReadOnlyList<NormalizedPoint> points, double length, ulong transferRooms = 0, string? linkId = null, bool specialTeleport = false)
        {
            if (!adjacent.ContainsKey(from) || !adjacent.ContainsKey(to)) return;
            var headings = points.Zip(points.Skip(1), Heading).ToArray();
            adjacent[from].Add(new(from, to, module, points, length,
                headings.Zip(headings.Skip(1), Bend).Sum(), headings.FirstOrDefault(), headings.LastOrDefault(), transferRooms,
                nodeBits[to] | (linkId is null ? BigInteger.Zero : linkBits[linkId]),
                nodeBits[to] | (linkId is null ? BigInteger.Zero : conflicts[linkId]), specialTeleport));
        }
        foreach (var link in linksForRoute)
        {
            // Entering a terminal room requires returning along the same path,
            // which this single-direction exploration route cannot do.
            if (modules[link.ModuleId].IsDeadEnd) continue;
            if (link.Length > 1e-9 && !MapExplorationRouteGeometry.IsSimple(link.Points)) continue;
            if (link.FromNodeId == exit.Id)
            {
                Add(link.ToNodeId, link.FromNodeId, link.ModuleId, link.Points.AsEnumerable().Reverse().ToArray(), link.Length, linkId: link.Id);
                continue;
            }
            Add(link.FromNodeId, link.ToNodeId, link.ModuleId, link.Points, link.Length, linkId: link.Id);
            if (link.FromNodeId != entrance.Id)
                Add(link.ToNodeId, link.FromNodeId, link.ModuleId, link.Points.AsEnumerable().Reverse().ToArray(), link.Length, linkId: link.Id);
        }
        foreach (var link in topology.FloorTransitions)
        {
            if (link.IsSpecialTeleport && !options.UseSpecialRoomTeleports) continue;
            // Stage boundaries need a real room on both sides, not an arbitrary
            // landing which cannot be framed for the player.
            if (!modules.Values.Any(m => m.CenterNodeId == link.FromNodeId && IsRoom(m) && !m.IsDeadEnd)
                || !modules.Values.Any(m => m.CenterNodeId == link.ToNodeId && IsRoom(m) && !m.IsDeadEnd)) continue;
            var transferRooms = ConnectorBit(link.FromNodeId) | ConnectorBit(link.ToNodeId);
            Add(link.FromNodeId, link.ToNodeId, null, [], link.Cost, transferRooms, specialTeleport: link.IsSpecialTeleport);
            if (!link.Directed) Add(link.ToNodeId, link.FromNodeId, null, [], link.Cost, transferRooms);
        }
        HashSet<string> Reach(string start, bool reverse)
        {
            var graph = adjacent.Keys.ToDictionary(n => n, _ => new List<string>());
            foreach (var edge in adjacent.Values.SelectMany(e => e))
                graph[reverse ? edge.To : edge.From].Add(reverse ? edge.From : edge.To);
            var reached = new HashSet<string>(); var pending = new Stack<string>(); pending.Push(start);
            while (pending.TryPop(out var node))
            {
                token.ThrowIfCancellationRequested();
                if (!reached.Add(node) || !graph.TryGetValue(node, out var next)) continue;
                foreach (var item in next) pending.Push(item);
            }
            return reached;
        }
        var reachable = Reach(entrance.Id, false);
        reachable.IntersectWith(Reach(exit.Id, true));
        if (!reachable.Contains(exit.Id)) throw new InvalidOperationException("入口与出口不连通，请检查关闭的门和跨楼层连接。");
        foreach (var floor in options.AllowedFloors)
            if (!modules.Values.Any(m => m.FloorKey == floor && reachable.Contains(m.CenterNodeId)))
                throw new InvalidOperationException($"{floor.ToUpperInvariant()} 无法经所选楼层连通入口和出口；请包含中间楼层或补充已确认的楼梯连接。");

        (double, double) Score(double length, int turns) => options.Preference == MapRoutePreference.Simplest
            ? (turns, length) : (length, turns);
        var cache = new Dictionary<(string, string, int, ulong, BigInteger, bool, BigInteger), PathResult?>();
        PathResult? Path(string from, string to, int heading, ulong usedTransfers, BigInteger occupied, bool shortest = false, BigInteger excluded = default)
        {
            token.ThrowIfCancellationRequested();
            if (cache.TryGetValue((from, to, heading, usedTransfers, occupied, shortest, excluded), out var cached)) return cached;
            (double, double) LegScore(double length, int turns) => shortest ? (length, turns) : Score(length, turns);
            var initial = new State(from, heading, usedTransfers, floorBits[nodes[from].FloorKey]);
            var start = new SearchLabel(initial, 0, 0, occupied, null, null);
            var queue = new PriorityQueue<SearchLabel, (double, double)>(); queue.Enqueue(start, (0, 0));
            var costs = new Dictionary<State, SearchLabel> { [initial] = start };
            SearchLabel? goal = null;
            while (queue.TryDequeue(out var label, out _))
            {
                token.ThrowIfCancellationRequested();
                var current = label.State;
                if (!ReferenceEquals(costs[current], label)) continue;
                if (current.Node == to) { goal = label; break; }
                foreach (var edge in adjacent.GetValueOrDefault(current.Node) ?? [])
                {
                    // Special transfers are explicit mandatory legs, never optional shortcuts.
                    if (edge.SpecialTeleport) continue;
                    if ((edge.Occupied & excluded) != BigInteger.Zero) continue;
                    if ((label.Occupied & edge.Conflicts) != BigInteger.Zero) continue;
                    var floorBit = floorBits[nodes[edge.To].FloorKey];
                    // A physical transfer room belongs to one stage only. A leg
                    // also cannot leave and return to its starting floor just to
                    // reset heading or create an unreadable stair shortcut.
                    if (edge.TransferRooms != 0 && ((current.TransferRooms & edge.TransferRooms) != 0 || (current.Floors & floorBit) != 0)) continue;
                    var next = new State(edge.To, edge.Last, current.TransferRooms | edge.TransferRooms, current.Floors | floorBit);
                    var length = label.Length + edge.Length;
                    var turns = label.Turns + edge.Turns + Bend(current.Heading, edge.First);
                    var score = LegScore(length, turns);
                    if (costs.TryGetValue(next, out var old) && score.CompareTo(LegScore(old.Length, old.Turns)) >= 0) continue;
                    var nextLabel = new SearchLabel(next, length, turns, label.Occupied | edge.Occupied, label, edge);
                    costs[next] = nextLabel; queue.Enqueue(nextLabel, score);
                }
            }
            if (goal is null) return cache[(from, to, heading, usedTransfers, occupied, shortest, excluded)] = null;
            var edges = new List<Edge>();
            for (var label = goal; label.Edge is not null; label = label.Previous!) edges.Add(label.Edge);
            edges.Reverse();
            return cache[(from, to, heading, usedTransfers, occupied, shortest, excluded)] = new(edges, goal.Length, goal.Turns);
        }
        var prefix = new List<Edge>();
        var prefixNode = entrance.Id; var prefixHeading = 0; var prefixTransfers = 0UL;
        var prefixOccupied = nodeBits[entrance.Id];
        var pendingTeleports = adjacent.Values.SelectMany(edges => edges).Where(e => e.SpecialTeleport).ToList();
        var channelMask = ((BigInteger.One << linksForRoute.Count) - 1) << nodes.Count;
        if (options.UseSpecialRoomTeleports && pendingTeleports.Count != topology.FloorTransitions.Count(t => t.IsSpecialTeleport))
            throw new InvalidOperationException("特殊传送通道不可用，无法生成必经全部传送的路线。");
        while (pendingTeleports.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var approaches = new PriorityQueue<(Edge Edge, PathResult Path, BigInteger Excluded), (double, int, string)>();
            var exclusions = new HashSet<(Edge, BigInteger)>();
            void EnqueueApproach(Edge transfer, BigInteger excluded)
            {
                if (!exclusions.Add((transfer, excluded))) return;
                var path = Path(prefixNode, transfer.From, prefixHeading, prefixTransfers, prefixOccupied, shortest: true, excluded: excluded);
                if (path is not null) approaches.Enqueue((transfer, path, excluded), (path.Length, path.Turns, transfer.From));
            }
            foreach (var transfer in pendingTeleports) EnqueueApproach(transfer, BigInteger.Zero);
            (Edge? Edge, PathResult? Path) approach = default;
            var triedPaths = new HashSet<string>(StringComparer.Ordinal);
            // Try the absolute shortest approach first. If its occupied geometry
            // blocks the exit, search bounded physical-channel exclusions in distance
            // order. A longer prefix is accepted only with a valid continuation.
            for (var attempt = 0; attempt < 256 && approaches.TryDequeue(out var candidate, out _); attempt++)
            {
                token.ThrowIfCancellationRequested();
                var signature = candidate.Edge.From + "->" + candidate.Edge.To + ":" + string.Join(",", candidate.Path.Edges.Select(e => e.Occupied));
                if (!triedPaths.Add(signature)) continue;
                var occupied = prefixOccupied; var used = prefixTransfers; var valid = true;
                foreach (var edge in candidate.Path.Edges.Append(candidate.Edge))
                {
                    if ((occupied & edge.Conflicts) != BigInteger.Zero || (used & edge.TransferRooms) != 0) { valid = false; break; }
                    occupied |= edge.Occupied; used |= edge.TransferRooms;
                }
                if (valid && (pendingTeleports.Count > 1 || Path(candidate.Edge.To, exit.Id, 0, used, occupied, shortest: true) is not null))
                {
                    approach = (candidate.Edge, candidate.Path); break;
                }
                foreach (var edge in candidate.Path.Edges)
                {
                    // Links can describe overlapping portions of one physical
                    // corridor. Exclude that corridor's conflicting links together
                    // instead of spending the candidate budget on the same geometry.
                    var channel = edge.Conflicts & channelMask;
                    if (channel != BigInteger.Zero) EnqueueApproach(candidate.Edge, candidate.Excluded | channel);
                }
            }
            if (approach.Path is null || approach.Edge is null)
                throw new InvalidOperationException("未找到可到达出口的特殊传送前段，请检查楼层和关闭的门，或更换入口、出口。");
            foreach (var edge in approach.Path.Edges.Append(approach.Edge))
            {
                if ((prefixOccupied & edge.Conflicts) != BigInteger.Zero || (prefixTransfers & edge.TransferRooms) != 0)
                    throw new InvalidOperationException("特殊传送路线重复使用通道或转接房间。");
                prefix.Add(edge); prefixOccupied |= edge.Occupied; prefixTransfers |= edge.TransferRooms;
                prefixNode = edge.To; prefixHeading = edge.Last;
            }
            pendingTeleports.Remove(approach.Edge);
        }
        PathResult? Evaluate(IReadOnlyList<string> stops)
        {
            var edges = prefix.ToList(); var length = prefix.Sum(e => e.Length); var turns = 0; var heading = 0; var used = prefixTransfers;
            foreach (var edge in prefix) { turns += edge.Turns + Bend(heading, edge.First); heading = edge.Last; }
            var occupied = prefixOccupied;
            for (var i = 1; i < stops.Count; i++)
            {
                var leg = Path(stops[i - 1], stops[i], heading, used, occupied)
                    ?? (prefix.Count > 0 ? Path(stops[i - 1], stops[i], heading, used, occupied, shortest: true) : null);
                if (leg is null) return null;
                edges.AddRange(leg.Edges); length += leg.Length; turns += leg.Turns;
                foreach (var edge in leg.Edges) { used |= edge.TransferRooms; occupied |= edge.Occupied; }
                if (leg.Edges.Count > 0) heading = leg.Edges[^1].Last;
            }
            return new(edges, length, turns);
        }
        var candidates = modules.Values.Where(m => IsRoom(m) && options.AllowedFloors.Contains(m.FloorKey))
            .GroupBy(RoomKey).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var stops = new List<string> { prefixNode, exit.Id };
        var currentRoute = Evaluate(stops);
        HashSet<string> Visited(PathResult path) => path.Edges.Where(e => e.ModuleId is not null)
            .Select(e => RoomKey(modules[e.ModuleId!]))
            .Concat(path.Edges.SelectMany(e => new[] { e.From, e.To }).Where(centerRooms.ContainsKey).Select(id => RoomKey(modules[centerRooms[id]])))
            .Append(RoomKey(modules[entrance.ModuleId])).Append(RoomKey(modules[exit.ModuleId])).ToHashSet();
        var highValueRooms = options.HighValueRoomIds.Concat(candidates.SelectMany(g => g)
            .Where(m => m.FloorKey == "b1f").Select(m => m.Id)).Where(modules.ContainsKey).Select(id => RoomKey(modules[id])).ToHashSet();
        var required = candidates.Where(g => options.Coverage == MapRoomCoverage.AllRooms
            || highValueRooms.Contains(g.Key)).ToList();
        var roomBits = candidates.Select((group, index) => (group.Key, Bit: BigInteger.One << index)).ToDictionary(r => r.Key, r => r.Bit);
        BigInteger Mask(IEnumerable<string> ids) => ids.Aggregate(BigInteger.Zero, (mask, id) => mask | roomBits.GetValueOrDefault(id));
        var highMask = Mask(highValueRooms); var allMask = Mask(roomBits.Keys);
        var ordinaryMask = allMask ^ highMask; var ordinaryTarget = ((int)BigInteger.PopCount(ordinaryMask) + 1) / 2;
        (int, int, double, double) Quality(BigInteger rooms, double length, int turns)
        {
            var high = (int)BigInteger.PopCount(rooms & highMask);
            var ordinary = (int)BigInteger.PopCount(rooms & ordinaryMask);
            var score = Score(length, turns);
            return options.Coverage == MapRoomCoverage.AllRooms ? (-(high + ordinary), 0, score.Item1, score.Item2)
                : (-high, options.Coverage == MapRoomCoverage.Balanced ? -Math.Min(ordinaryTarget, ordinary) : 0, score.Item1, score.Item2);
        }
        BigInteger RoomsFor(Edge edge) => Mask(new[] { edge.ModuleId, centerRooms.GetValueOrDefault(edge.From), centerRooms.GetValueOrDefault(edge.To) }
            .Where(id => id is not null).Select(id => RoomKey(modules[id!])));
        var roomMasks = adjacent.Values.SelectMany(edges => edges).ToDictionary(edge => edge, RoomsFor);
        var bestQuality = currentRoute is null ? (int.MaxValue, int.MaxValue, double.MaxValue, double.MaxValue)
            : Quality(Mask(Visited(currentRoute)), currentRoute.Length, currentRoute.Turns);
        var seed = new SearchLabel(new(entrance.Id, 0, 0, floorBits[entrance.FloorKey]), 0, 0,
            nodeBits[entrance.Id], null, null, Mask(new[] { RoomKey(modules[entrance.ModuleId]) }));
        foreach (var edge in prefix)
            seed = new(new(edge.To, edge.Last, seed.State.TransferRooms | edge.TransferRooms,
                seed.State.Floors | floorBits[nodes[edge.To].FloorKey]), seed.Length + edge.Length,
                seed.Turns + edge.Turns + Bend(seed.State.Heading, edge.First), seed.Occupied | edge.Occupied,
                seed, edge, seed.Rooms | roomMasks[edge], seed.Transfers + (edge.ModuleId is null ? 1 : 0));
        var frontier = new List<SearchLabel> { seed };
        // Keep diverse complete-route candidates instead of fixing a sequence
        // of greedy room insertions. Occupied geometry remains immutable in
        // each label; pruning affects coverage quality, never route validity.
        for (var depth = 0; frontier.Count > 0 && depth < adjacent.Count; depth++)
        {
            progress?.Report((double)depth / adjacent.Count);
            var next = new List<SearchLabel>();
            foreach (var label in frontier)
            foreach (var edge in adjacent[label.State.Node])
            {
                token.ThrowIfCancellationRequested();
                if (edge.SpecialTeleport) continue;
                if ((label.Occupied & edge.Conflicts) != BigInteger.Zero || (label.State.TransferRooms & edge.TransferRooms) != 0) continue;
                var transfers = label.Transfers + (edge.ModuleId is null ? 1 : 0);
                if (transfers >= StageColors.Count && edge.To != exit.Id) continue;
                var length = label.Length + edge.Length;
                var turns = label.Turns + edge.Turns + Bend(label.State.Heading, edge.First);
                var candidate = new SearchLabel(new(edge.To, edge.Last, label.State.TransferRooms | edge.TransferRooms,
                    label.State.Floors | floorBits[nodes[edge.To].FloorKey]),
                    length, turns, label.Occupied | edge.Occupied, label, edge, label.Rooms | roomMasks[edge], transfers);
                if (edge.To != exit.Id) { next.Add(candidate); continue; }
                var quality = Quality(candidate.Rooms, length, turns);
                if (quality.CompareTo(bestQuality) >= 0) continue;
                bestQuality = quality;
                var edges = new List<Edge>();
                for (var tail = candidate; tail.Edge is not null; tail = tail.Previous!) edges.Add(tail.Edge);
                edges.Reverse(); currentRoute = new(edges, length, turns);
            }
            frontier = next.OrderBy(label => Quality(label.Rooms, label.Length, label.Turns))
                .GroupBy(label => (label.State.Node, label.Rooms)).SelectMany(group => group.Take(2))
                // A global room-count beam discards basement approaches before
                // they reach the drop: upper floors collect rooms sooner. Keep
                // a bounded beam per floor history and current floor instead.
                .GroupBy(label => (label.State.Floors, nodes[label.State.Node].FloorKey))
                .SelectMany(group => group.Where(CanFinish).Take(160)).ToList();
        }
        bool CanFinish(SearchLabel label)
        {
            var seen = new HashSet<string> { label.State.Node }; var pending = new Stack<string>(); pending.Push(label.State.Node);
            while (pending.TryPop(out var node))
            {
                token.ThrowIfCancellationRequested();
                foreach (var edge in adjacent[node])
                {
                    if (edge.SpecialTeleport) continue;
                    if ((label.Occupied & edge.Conflicts) != BigInteger.Zero || (label.State.TransferRooms & edge.TransferRooms) != 0) continue;
                    if (edge.To == exit.Id) return true;
                    if (seen.Add(edge.To)) pending.Push(edge.To);
                }
            }
            return false;
        }
        if (currentRoute is null)
            throw new InvalidOperationException("未找到不重叠、不重复使用转接房间的完整路线。请更换入口或出口。");
        var stages = new List<MapExplorationStage>(); var steps = new List<MapExplorationStep>();
        var currentFloor = entrance.FloorKey;
        void Finish(string? transfer, string? arrival)
        {
            var number = stages.Count + 1;
            if (number > StageColors.Count) throw new InvalidOperationException("路线阶段超过统一配色的数量，请减少经过房间或楼层。");
            stages.Add(new(number, currentFloor, StageColors[number - 1], steps.ToArray(), transfer, arrival));
            steps.Clear();
        }
        foreach (var edge in currentRoute.Edges)
        {
            if (edge.ModuleId is not null)
                steps.Add(new(edge.From, edge.To, edge.ModuleId, edge.Points, edge.Length, edge.Turns));
            else
            {
                Finish(modules.Values.Single(m => m.CenterNodeId == edge.From).Id,
                    modules.Values.Single(m => m.CenterNodeId == edge.To).Id);
                currentFloor = nodes[edge.To].FloorKey;
            }
        }
        if (steps.Count > 0 || stages.Count == 0) Finish(null, null);
        var route = new MapExplorationRoute(stages, [],
            currentRoute.Length, currentRoute.Turns, entrance.Id, exit.Id,
            []);
        return MapExplorationRouteTree.Complete(topology, route, options, token);
    }

    private static int Heading(NormalizedPoint a, NormalizedPoint b)
    {
        static int Sign(double value) => Math.Abs(value) < 1e-9 ? 0 : Math.Sign(value);
        return (Sign(b.X - a.X) + 1) * 3 + Sign(b.Y - a.Y) + 2;
    }
    private static int Bend(int a, int b) => a == 0 || b == 0 || a == b ? 0 : a + b == 10 ? 2 : 1;
}
