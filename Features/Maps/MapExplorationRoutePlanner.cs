using System.Numerics;

namespace IDVBuff.Features.Maps;

public enum MapRoomCoverage { HighValue, Balanced, AllRooms }
public enum MapRoutePreference { Shortest, Simplest }
// AllowedFloors retains its API name, but selects loot exploration only.
// All topology floors remain available for transit to targets and the exit.
public sealed record MapExplorationOptions(string EntranceId, string ExitId,
    IReadOnlySet<string> AllowedFloors, MapRoomCoverage Coverage, MapRoutePreference Preference,
    IReadOnlySet<string> HighValueRoomIds, IReadOnlyList<MapRouteGateEndpoint> Gates,
    bool UseSpecialRoomTeleports = false, bool DenseRoutes = true);
public sealed record MapExplorationStep(string From, string To, string? ModuleId,
    IReadOnlyList<NormalizedPoint> Points, double Length, int Turns)
{
    public bool UsesSideLane { get; init; }
}
public sealed record MapExplorationStage(int Number, string FloorKey, string Color,
    IReadOnlyList<MapExplorationStep> Steps, string? TransferRoomId, string? ArrivalRoomId);
public sealed record MapExplorationRoute(IReadOnlyList<MapExplorationStage> Stages,
    IReadOnlyList<string> VisitedRoomIds, double Length, int Turns, string EntranceId, string ExitId,
    IReadOnlyList<string> SkippedTargetRoomIds)
{
    public IReadOnlyList<MapExplorationBranch> Branches { get; init; } = [];
    public IReadOnlyList<MapExplorationRoomVisit> RoomVisits { get; init; } = [];
    public IReadOnlySet<string>? ExplorationFloors { get; init; }
    public bool DenseRoutes { get; init; }
}

/// <summary>Coverage uses bounded beam search over stage walks. Same-stage
/// returns share channels; competing stages use physical lane exclusions.</summary>
public static partial class MapExplorationRoutePlanner
{
    public static IReadOnlyList<string> StageColors { get; } = Array.AsReadOnly(
        new[] { "#FF4545", "#00D4FF", "#00FF5E", "#FF87A5", "#6972FF", "#FFD166", "#FF9F43", "#4D9FFF", "#F2F2F2", "#38B2AC", "#C5E86C" });
    // Connected landings are one physical room; the first actual visit owns it.
    public static string RoomKey(MapNavigationModule module) => module.CanChangeFloors && module.SourceRoomId is { Length: > 0 }
        ? "source-room:" + module.SourceRoomId : module.Id;
    public static bool IsRoom(MapNavigationModule module) => module.Kind != "corridor";
    // Missing loot metadata means unknown, not an empty room. Keep physical
    // room identity separate: empty rooms can still provide required passages.
    public static bool IsLootTarget(MapNavigationTopology topology, MapNavigationModule module) => IsRoom(module) && !module.IsExcludedFromAutomaticRoutes
        && (string.IsNullOrWhiteSpace(topology.LootEvidence) || module.ChestCandidateCount > 0);
    private sealed record Edge(string From, string To, string? ModuleId,
        IReadOnlyList<NormalizedPoint> Points, double Length, int Turns, int First, int Last, ulong TransferRooms,
        BigInteger Occupied, BigInteger Conflicts, BigInteger Crossings, bool SpecialTeleport, bool UsesSideLane, string? SourceLinkId);
    private readonly record struct State(string Node, int Heading, ulong TransferRooms, uint Floors);
    private sealed record SearchLabel(State State, double Length, int Turns, BigInteger Occupied, SearchLabel? Previous, Edge? Edge,
        BigInteger Rooms = default, int Transfers = 0, BigInteger StageOccupied = default);
    private sealed record PathResult(IReadOnlyList<Edge> Edges, double Length, int Turns);

    public static MapExplorationRoute Find(MapNavigationTopology topology, MapExplorationOptions options,
        CancellationToken token = default, IProgress<double>? progress = null)
    {
        MapNavigationRules.ValidateForRouting(topology);
        if (options.UseSpecialRoomTeleports) MapSpecialRoomTeleports.ValidateSelection(topology, topology.Floors.Select(f => f.FloorKey).ToHashSet());
        if (!Enum.IsDefined(options.Coverage) || !Enum.IsDefined(options.Preference))
            throw new InvalidOperationException("路线偏好无效。");
        var modules = topology.Modules.ToDictionary(m => m.Id);
        var nodes = topology.Nodes.ToDictionary(n => n.Id);
        var centerRooms = modules.Values.ToDictionary(m => m.CenterNodeId, m => m.Id);
        var entrance = options.Gates.SingleOrDefault(g => g.Id == options.EntranceId);
        var exit = options.Gates.SingleOrDefault(g => g.Id == options.ExitId);
        if (entrance is null || exit is null || !options.DenseRoutes && (entrance.Id == exit.Id
            || entrance.FloorKey == exit.FloorKey && MapNavigationRules.Same(entrance.Point, exit.Point)))
            throw new InvalidOperationException("请选择不同的入口和出口门标志。");
        // Distinct graph terminals share the selected physical marker. Reaching
        // the exit still requires a nonempty, nonoverlapping route back to it.
        var closed = entrance.FloorKey == exit.FloorKey && MapNavigationRules.Same(entrance.Point, exit.Point);
        if (entrance.Id == exit.Id) exit = exit with { Id = exit.Id + "@return" };
        var linksForRoute = topology.Links.ToList();
        AddGateLinks(topology, modules, nodes, linksForRoute, new[] { entrance, exit }, token);
        static string Original(string id) => id.EndsWith("@lane-a", StringComparison.Ordinal) || id.EndsWith("@lane-b", StringComparison.Ordinal)
            ? id[..^7] : id;
        var sideLanes = options.DenseRoutes ? MapExplorationRouteChannels.Add(topology, nodes, linksForRoute, token) : [];
        var nodeBits = nodes.Keys.Select((id, index) => (id, bit: BigInteger.One << index)).ToDictionary(n => n.id, n => n.bit);
        var linkBits = linksForRoute.Select((link, index) => (link.Id, bit: BigInteger.One << (nodes.Count + index))).ToDictionary(l => l.Id, l => l.bit);
        var (conflicts, crossings) = ChannelConflicts(topology, modules, linksForRoute, linkBits, exit.Id, options.DenseRoutes, token);
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
        if (options.AllowedFloors.Except(topology.Floors.Select(f => f.FloorKey)).Any())
            throw new InvalidOperationException("搜房楼层包含地图中不存在的楼层。");
        var blocked = topology.Doors.Where(d => !d.Enabled).Select(d => d.NodeId).ToHashSet();
        blocked.UnionWith(modules.Values.Where(m => m.IsExcludedFromAutomaticRoutes).Select(m => m.CenterNodeId));
        var adjacent = nodes.Values.Where(n => !blocked.Contains(n.Id))
            .ToDictionary(n => n.Id, _ => new List<Edge>());
        void Add(string from, string to, string? module, IReadOnlyList<NormalizedPoint> points, double length, ulong transferRooms = 0, string? linkId = null, bool specialTeleport = false)
        {
            if (!adjacent.ContainsKey(from) || !adjacent.ContainsKey(to)) return;
            var headings = points.Zip(points.Skip(1), Heading).ToArray();
            adjacent[from].Add(new(from, to, module, points, length,
                headings.Zip(headings.Skip(1), Bend).Sum(), headings.FirstOrDefault(), headings.LastOrDefault(), transferRooms,
                nodeBits[to] | (linkId is null ? BigInteger.Zero : linkBits[linkId]),
                nodeBits[to] | (linkId is null ? BigInteger.Zero : conflicts[linkId]),
                linkId is null ? BigInteger.Zero : crossings[linkId],
                specialTeleport, linkId is not null && sideLanes.Contains(linkId), linkId is null ? null : Original(linkId)));
        }
        foreach (var link in linksForRoute)
        {
            // Entering a terminal room requires returning along the same path,
            // which this single-direction exploration route cannot do.
            if (modules[link.ModuleId].IsDeadEnd || modules[link.ModuleId].IsExcludedFromAutomaticRoutes) continue;
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
        HashSet<string> Reach(string start, bool reverse, Dictionary<string, List<Edge>>? source = null)
        {
            source ??= adjacent;
            var graph = source.Keys.ToDictionary(n => n, _ => new List<string>());
            foreach (var edge in source.Values.SelectMany(e => e))
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
        // Unselected floors are a connectivity fallback, not extra channels
        // for maximizing loot coverage on the selected floors. Prefer a graph
        // confined to search/gate floors whenever it can connect the request.
        var preferredFloors = options.AllowedFloors.Append(entrance.FloorKey).Append(exit.FloorKey).ToHashSet();
        if (options.UseSpecialRoomTeleports)
            preferredFloors.UnionWith(MapSpecialRoomTeleports.RequiredFloors(topology));
        var preferred = adjacent.ToDictionary(pair => pair.Key, pair => pair.Value
            .Where(edge => preferredFloors.Contains(nodes[edge.From].FloorKey)
                && preferredFloors.Contains(nodes[edge.To].FloorKey)).ToList());
        var preferredReach = Reach(entrance.Id, false, preferred);
        preferredReach.IntersectWith(Reach(exit.Id, true, preferred));
        if (preferredReach.Contains(exit.Id) && options.AllowedFloors.All(floor =>
            modules.Values.Any(module => module.FloorKey == floor && preferredReach.Contains(module.CenterNodeId))))
            adjacent = preferred;
        var reachable = Reach(entrance.Id, false);
        reachable.IntersectWith(Reach(exit.Id, true));
        if (!reachable.Contains(exit.Id)) throw new InvalidOperationException("入口与出口不连通，请检查关闭的门和跨楼层连接。");
        foreach (var floor in options.AllowedFloors)
            if (!modules.Values.Any(m => m.FloorKey == floor && reachable.Contains(m.CenterNodeId)))
                throw new InvalidOperationException($"搜房楼层 {floor.ToUpperInvariant()} 无法连通入口和出口；请检查关闭的门或已确认的楼梯连接。");

        (double, double) Score(double length, int turns) => options.Preference == MapRoutePreference.Simplest
            ? (turns, length) : (length, turns);
        var transit = new TransitTurns(adjacent, modules, nodes);
        Func<State, BigInteger, BigInteger>? retainOccupation = null;
        // A stage is one route, including its return trips. Only other stages
        // occupy a competing lane. With dense mode off retain simple paths.
        bool Blocks(BigInteger occupied, BigInteger stageOccupied, Edge edge) =>
            ((options.DenseRoutes ? occupied & ~stageOccupied : occupied) & edge.Conflicts) != BigInteger.Zero
            || options.DenseRoutes && (stageOccupied & edge.Crossings) != BigInteger.Zero;
        SearchLabel Advance(SearchLabel label, State state, Edge edge, double length, int turns, BigInteger rooms = default)
        {
            var occupied = label.Occupied | edge.Occupied;
            if (retainOccupation is not null) occupied = retainOccupation(state, occupied);
            return new(state, length, turns, occupied, label, edge, label.Rooms | rooms,
                label.Transfers + (edge.ModuleId is null ? 1 : 0),
                edge.ModuleId is null ? edge.Occupied : label.StageOccupied | edge.Occupied);
        }
        var cache = new Dictionary<(string, string, int, ulong, BigInteger, BigInteger, bool, BigInteger), PathResult?>();
        PathResult? Path(string from, string to, int heading, ulong usedTransfers, BigInteger occupied,
            BigInteger stageOccupied, bool shortest = false, BigInteger excluded = default)
        {
            token.ThrowIfCancellationRequested();
            var key = (from, to, heading, usedTransfers, occupied, stageOccupied, shortest, excluded);
            if (cache.TryGetValue(key, out var cached)) return cached;
            (double, double) LegScore(double length, int turns) => shortest ? (length, turns) : Score(length, turns);
            var initial = new State(from, heading, usedTransfers, floorBits[nodes[from].FloorKey]);
            var start = new SearchLabel(initial, 0, 0, occupied, null, null, StageOccupied: stageOccupied);
            var queue = new PriorityQueue<SearchLabel, (double, double)>(); queue.Enqueue(start, (0, 0));
            var costs = new Dictionary<State, SearchLabel> { [initial] = start };
            SearchLabel? goal = null;
            while (queue.TryDequeue(out var label, out _))
            {
                token.ThrowIfCancellationRequested();
                var current = label.State;
                if (!ReferenceEquals(costs[current], label)) continue;
                if (current.Node == to)
                {
                    if (closed && from == entrance.Id && label.Length <= 1e-9) continue;
                    goal = label; break;
                }
                foreach (var edge in adjacent.GetValueOrDefault(current.Node) ?? [])
                {
                    // Special transfers are explicit mandatory legs, never optional shortcuts.
                    if (edge.SpecialTeleport) continue;
                    if (transit.Redundant(label.Edge, edge)) continue;
                    if ((edge.Occupied & excluded) != BigInteger.Zero) continue;
                    if (Blocks(label.Occupied, label.StageOccupied, edge)) continue;
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
                    var nextLabel = Advance(label, next, edge, length, turns);
                    costs[next] = nextLabel; queue.Enqueue(nextLabel, score);
                }
            }
            if (goal is null) return cache[key] = null;
            var edges = new List<Edge>();
            for (var label = goal; label.Edge is not null; label = label.Previous!) edges.Add(label.Edge);
            edges.Reverse();
            return cache[key] = new(edges, goal.Length, goal.Turns);
        }
        var prefix = new List<Edge>();
        var prefixNode = entrance.Id; var prefixHeading = 0; var prefixTransfers = 0UL;
        var prefixOccupied = nodeBits[entrance.Id];
        var prefixStageOccupied = prefixOccupied;
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
                var path = Path(prefixNode, transfer.From, prefixHeading, prefixTransfers, prefixOccupied,
                    prefixStageOccupied, shortest: true, excluded: excluded);
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
                var occupied = prefixOccupied; var stageOccupied = prefixStageOccupied;
                var used = prefixTransfers; var valid = true;
                foreach (var edge in candidate.Path.Edges.Append(candidate.Edge))
                {
                    if (Blocks(occupied, stageOccupied, edge) || (used & edge.TransferRooms) != 0) { valid = false; break; }
                    occupied |= edge.Occupied; used |= edge.TransferRooms;
                    stageOccupied = edge.ModuleId is null ? edge.Occupied : stageOccupied | edge.Occupied;
                }
                if (valid && (pendingTeleports.Count > 1 || Path(candidate.Edge.To, exit.Id, 0, used, occupied,
                    stageOccupied, shortest: true) is not null))
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
                if (Blocks(prefixOccupied, prefixStageOccupied, edge) || (prefixTransfers & edge.TransferRooms) != 0)
                    throw new InvalidOperationException("特殊传送路线重复使用通道或转接房间。");
                prefix.Add(edge); prefixOccupied |= edge.Occupied; prefixTransfers |= edge.TransferRooms;
                prefixStageOccupied = edge.ModuleId is null ? edge.Occupied : prefixStageOccupied | edge.Occupied;
                prefixNode = edge.To; prefixHeading = edge.Last;
            }
            pendingTeleports.Remove(approach.Edge);
        }
        var originalEdges = adjacent.Values.SelectMany(e => e).Where(e => e.SourceLinkId is not null && !e.UsesSideLane)
            .ToDictionary(e => (e.SourceLinkId!, e.From, e.To));
        PathResult Layout(PathResult path) => options.DenseRoutes
            ? CenterUnsharedStages(topology, path, originalEdges, transit, closed, exit.Id, token) : CompactTransit(path, transit);
        bool FitsChannels(IReadOnlyList<Edge> edges) => HasLaneCapacity(edges, modules, topology.Floors, exit.Id, options.DenseRoutes)
            && HasNoStageCrossings(edges, token);
        PathResult? Evaluate(IReadOnlyList<string> stops)
        {
            var edges = prefix.ToList(); var length = prefix.Sum(e => e.Length); var turns = 0; var heading = 0; var used = prefixTransfers;
            foreach (var edge in prefix) { turns += edge.Turns + Bend(heading, edge.First); heading = edge.Last; }
            var occupied = prefixOccupied; var stageOccupied = prefixStageOccupied;
            for (var i = 1; i < stops.Count; i++)
            {
                var leg = Path(stops[i - 1], stops[i], heading, used, occupied, stageOccupied)
                    ?? (prefix.Count > 0 ? Path(stops[i - 1], stops[i], heading, used, occupied, stageOccupied, shortest: true) : null);
                if (leg is null) return null;
                edges.AddRange(leg.Edges); length += leg.Length; turns += leg.Turns;
                foreach (var edge in leg.Edges)
                {
                    used |= edge.TransferRooms; occupied |= edge.Occupied;
                    stageOccupied = edge.ModuleId is null ? edge.Occupied : stageOccupied | edge.Occupied;
                }
                if (leg.Edges.Count > 0) heading = leg.Edges[^1].Last;
            }
            return FitsChannels(edges) ? new(edges, length, turns) : null;
        }
        bool IsTarget(MapNavigationModule module) => options.AllowedFloors.Contains(module.FloorKey) && IsLootTarget(topology, module);
        var candidates = modules.Values.Where(IsTarget)
            .GroupBy(RoomKey).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var stops = new List<string> { prefixNode, exit.Id };
        var currentRoute = Evaluate(stops);
        HashSet<string> Visited(PathResult path) => path.Edges.Where(e => e.ModuleId is not null)
            .Select(e => modules[e.ModuleId!])
            .Concat(path.Edges.SelectMany(e => new[] { e.From, e.To }).Where(centerRooms.ContainsKey).Select(id => modules[centerRooms[id]]))
            .Append(modules[entrance.ModuleId]).Append(modules[exit.ModuleId])
            .Where(IsTarget).Select(RoomKey).ToHashSet();
        var highValueRooms = candidates.Where(g => g.Any(m => options.HighValueRoomIds.Contains(m.Id) || m.FloorKey == "b1f"))
            .Select(g => g.Key).ToHashSet();
        var roomBits = candidates.Select((group, index) => (group.Key, Bit: BigInteger.One << index)).ToDictionary(r => r.Key, r => r.Bit);
        BigInteger Mask(IEnumerable<string> ids) => ids.Aggregate(BigInteger.Zero, (mask, id) => mask | roomBits.GetValueOrDefault(id));
        var highMask = Mask(highValueRooms); var allMask = Mask(roomBits.Keys);
        var ordinaryMask = allMask ^ highMask; var ordinaryTarget = ((int)BigInteger.PopCount(ordinaryMask) + 1) / 2;
        var remainingLength = RemainingExitLengths(adjacent, exit.Id, token);
        var occupationByFloor = topology.Floors.ToDictionary(f => f.FloorKey, _ => BigInteger.Zero);
        foreach (var (id, node) in nodes) occupationByFloor[node.FloorKey] |= nodeBits[id];
        foreach (var link in linksForRoute) occupationByFloor[modules[link.ModuleId].FloorKey] |= linkBits[link.Id];
        var completion = new CompletionRooms(adjacent.Values.SelectMany(e => e), nodes, floorBits,
            candidates.SelectMany(g => g).GroupBy(m => m.FloorKey).ToDictionary(g => g.Key, g => Mask(g.Select(RoomKey))),
            occupationByFloor, exit.FloorKey, allMask, connectorBits.Count <= 14, token);
        (int, int, double, double) Quality(BigInteger rooms, double length, int turns)
        {
            var high = (int)BigInteger.PopCount(rooms & highMask);
            var ordinary = (int)BigInteger.PopCount(rooms & ordinaryMask);
            var score = Score(length, turns);
            return options.Coverage == MapRoomCoverage.AllRooms ? (-(high + ordinary), 0, score.Item1, score.Item2)
                : (-high, options.Coverage == MapRoomCoverage.Balanced ? -Math.Min(ordinaryTarget, ordinary) : 0, score.Item1, score.Item2);
        }
        BigInteger RoomsFor(Edge edge) => Mask(new[] { edge.ModuleId, centerRooms.GetValueOrDefault(edge.From), centerRooms.GetValueOrDefault(edge.To) }
            .Where(id => id is not null).Select(id => modules[id!]).Where(IsTarget).Select(RoomKey));
        var roomMasks = adjacent.Values.SelectMany(edges => edges).ToDictionary(edge => edge, RoomsFor);
        var bestQuality = currentRoute is null ? (int.MaxValue, int.MaxValue, double.MaxValue, double.MaxValue)
            : Quality(Mask(Visited(currentRoute)), currentRoute.Length, currentRoute.Turns);
        var seed = new SearchLabel(new(entrance.Id, 0, 0, floorBits[entrance.FloorKey]), 0, 0,
            nodeBits[entrance.Id], null, null, IsTarget(modules[entrance.ModuleId])
                ? Mask(new[] { RoomKey(modules[entrance.ModuleId]) }) : BigInteger.Zero,
            StageOccupied: nodeBits[entrance.Id]);
        foreach (var edge in prefix)
            seed = Advance(seed, new(edge.To, edge.Last, seed.State.TransferRooms | edge.TransferRooms,
                seed.State.Floors | floorBits[nodes[edge.To].FloorKey]), edge, seed.Length + edge.Length,
                seed.Turns + edge.Turns + Bend(seed.State.Heading, edge.First), roomMasks[edge]);
        // Mandatory prefix transfers are now complete. Occupancy on floors
        // which no valid remaining transfer tour can revisit is irrelevant to
        // future exclusions; keep its actual walk in Previous for drawing.
        retainOccupation = completion.RetainOccupation;
        seed = seed with { Occupied = retainOccupation(seed.State, seed.Occupied) };
        var frontier = new List<SearchLabel> { seed };
        var expanded = new ExpandedLabels(Score);
        var exitReachability = new ExitReachability(adjacent, exit.Id, options.DenseRoutes, token);
        // Keep diverse complete-route candidates instead of fixing a sequence
        // of greedy room insertions. Occupied geometry remains immutable in
        // each label; pruning affects coverage quality, never route validity.
        for (var depth = 0; frontier.Count > 0 && depth < adjacent.Count; depth++)
        {
            progress?.Report((double)depth / adjacent.Count);
            var next = new List<SearchLabel>();
            foreach (var label in frontier.Where(label => Quality(completion.UpperBound(label),
                label.Length + remainingLength.GetValueOrDefault(label.State.Node, double.PositiveInfinity), label.Turns).CompareTo(bestQuality) < 0).Where(expanded.Add))
            foreach (var edge in adjacent[label.State.Node])
            {
                token.ThrowIfCancellationRequested();
                if (edge.SpecialTeleport) continue;
                if (transit.Redundant(label.Edge, edge)) continue;
                if (Blocks(label.Occupied, label.StageOccupied, edge) || (label.State.TransferRooms & edge.TransferRooms) != 0) continue;
                var nextRooms = label.Rooms | roomMasks[edge];
                var repeated = false;
                // Returning after visiting a new room is useful. Circling an
                // unchanged room set is not; bound it without inventing lanes.
                for (var prior = label; prior is not null; prior = prior.Previous)
                {
                    if (Original(prior.State.Node) == Original(edge.To) && prior.Rooms == nextRooms) { repeated = true; break; }
                    if (prior.Edge?.ModuleId is null) break;
                }
                if (repeated) continue;
                var transfers = label.Transfers + (edge.ModuleId is null ? 1 : 0);
                if (transfers >= StageColors.Count && edge.To != exit.Id) continue;
                var length = label.Length + edge.Length;
                var turns = label.Turns + edge.Turns + Bend(label.State.Heading, edge.First);
                var candidate = Advance(label, new(edge.To, edge.Last, label.State.TransferRooms | edge.TransferRooms,
                    label.State.Floors | floorBits[nodes[edge.To].FloorKey]),
                    edge, length, turns, roomMasks[edge]);
                if (edge.To != exit.Id) { next.Add(candidate); continue; }
                if (closed && length <= 1e-9) continue;
                var quality = Quality(candidate.Rooms, length, turns);
                if (quality.CompareTo(bestQuality) >= 0) continue;
                var edges = new List<Edge>();
                for (var tail = candidate; tail.Edge is not null; tail = tail.Previous!) edges.Add(tail.Edge);
                edges.Reverse();
                if (!FitsChannels(edges)) continue;
                bestQuality = quality; currentRoute = new(edges, length, turns);
            }
            frontier = next.OrderBy(label => Quality(label.Rooms, label.Length, label.Turns))
                .GroupBy(label => (label.State.Node, label.Rooms)).SelectMany(group => group.Take(2))
                // A global room-count beam discards basement approaches before
                // they reach the drop: upper floors collect rooms sooner. Keep
                // a bounded beam per floor history and current floor instead.
                .GroupBy(label => (label.State.Floors, nodes[label.State.Node].FloorKey))
                .SelectMany(group => group.Where(exitReachability.CanFinish).Take(160)).ToList();
        }
        if (currentRoute is null)
            throw new InvalidOperationException("未找到阶段间不重叠、不重复使用转接房间的完整路线。请更换入口或出口。");
        currentRoute = Layout(currentRoute);
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
                steps.Add(new(edge.From, edge.To, edge.ModuleId, edge.Points, edge.Length, edge.Turns)
                    { UsesSideLane = edge.UsesSideLane });
            else
            {
                Finish(modules.Values.Single(m => m.CenterNodeId == edge.From).Id,
                    modules.Values.Single(m => m.CenterNodeId == edge.To).Id);
                currentFloor = nodes[edge.To].FloorKey;
            }
        }
        if (steps.Count > 0 || stages.Count == 0) Finish(null, null);
        var route = new MapExplorationRoute(stages, [],
            currentRoute.Length, currentRoute.Turns, options.EntranceId, options.ExitId,
            []) { DenseRoutes = options.DenseRoutes };
        return MapExplorationRouteTree.Complete(topology, route, options, token);
    }

    private static int Heading(NormalizedPoint a, NormalizedPoint b)
    {
        static int Sign(double value) => Math.Abs(value) < 1e-9 ? 0 : Math.Sign(value);
        return (Sign(b.X - a.X) + 1) * 3 + Sign(b.Y - a.Y) + 2;
    }
    private static int Bend(int a, int b) => a == 0 || b == 0 || a == b ? 0 : a + b == 10 ? 2 : 1;
}
