using System.Numerics;

namespace IDVBuff.Features.Maps;

public static partial class MapExplorationRoutePlanner
{
    private static bool HasNoStageCrossings(IEnumerable<Edge> edges, CancellationToken token)
    {
        var points = new List<IReadOnlyList<NormalizedPoint>>();
        foreach (var edge in edges)
            if (edge.ModuleId is null)
            {
                if (MapExplorationRouteGeometry.HasCrossing(points, token)) return false;
                points.Clear();
            }
            else points.Add(edge.Points);
        return !MapExplorationRouteGeometry.HasCrossing(points, token);
    }

    private sealed class ExpandedLabels(Func<double, int, (double, double)> score)
    {
        private readonly Dictionary<(State State, BigInteger Rooms, BigInteger Past), List<SearchLabel>> _expanded = [];
        public bool Add(SearchLabel label)
        {
            var key = (label.State, label.Rooms, label.Occupied & ~label.StageOccupied);
            if (!_expanded.TryGetValue(key, out var previous)) _expanded[key] = previous = [];
            var value = score(label.Length, label.Turns);
            if (previous.Any(old => (old.StageOccupied & label.StageOccupied) == old.StageOccupied
                && score(old.Length, old.Turns).CompareTo(value) <= 0)) return false;
            // Apply the existing two-label beam across depths, so return walks
            // do not enumerate every order of the same collected-room set.
            if (previous.Count >= 2 && score(previous[^1].Length, previous[^1].Turns).CompareTo(value) <= 0) return false;
            previous.RemoveAll(old => (old.StageOccupied & label.StageOccupied) == label.StageOccupied
                && value.CompareTo(score(old.Length, old.Turns)) <= 0);
            previous.Add(label); previous.Sort((a, b) => score(a.Length, a.Turns).CompareTo(score(b.Length, b.Turns)));
            if (previous.Count > 2) previous.RemoveRange(2, previous.Count - 2);
            return true;
        }
    }

    private static Dictionary<string, double> RemainingExitLengths(Dictionary<string, List<Edge>> adjacent, string exit, CancellationToken token)
    {
        var reverse = adjacent.Keys.ToDictionary(id => id, _ => new List<Edge>());
        foreach (var edge in adjacent.Values.SelectMany(e => e).Where(e => !e.SpecialTeleport)) reverse[edge.To].Add(edge);
        var remaining = new Dictionary<string, double> { [exit] = 0 };
        var queue = new PriorityQueue<string, double>(); queue.Enqueue(exit, 0);
        while (queue.TryDequeue(out var node, out var distance))
        {
            token.ThrowIfCancellationRequested();
            if (distance > remaining[node]) continue;
            foreach (var edge in reverse[node])
                if (!remaining.TryGetValue(edge.From, out var old) || old > distance + edge.Length)
                { remaining[edge.From] = distance + edge.Length; queue.Enqueue(edge.From, distance + edge.Length); }
        }
        return remaining;
    }

    private sealed class CompletionRooms
    {
        private readonly ILookup<string, (string From, string To, ulong Rooms)> _edges;
        private readonly IReadOnlyDictionary<string, MapNavigationNode> _nodes;
        private readonly IReadOnlyDictionary<string, uint> _floorBits;
        private readonly IReadOnlyDictionary<string, BigInteger> _rooms;
        private readonly IReadOnlyDictionary<string, BigInteger> _occupation;
        private readonly Dictionary<uint, BigInteger> _occupationMasks = [];
        private readonly Dictionary<(string Floor, ulong Used), uint> _cache = [];
        private readonly string _exitFloor;
        private readonly BigInteger _all;
        private readonly bool _bounded;
        private readonly CancellationToken _token;

        public CompletionRooms(IEnumerable<Edge> edges, IReadOnlyDictionary<string, MapNavigationNode> nodes,
            IReadOnlyDictionary<string, uint> floorBits, IReadOnlyDictionary<string, BigInteger> rooms,
            IReadOnlyDictionary<string, BigInteger> occupation,
            string exitFloor, BigInteger all, bool bounded, CancellationToken token)
        {
            _nodes = nodes; _floorBits = floorBits; _rooms = rooms; _occupation = occupation;
            _exitFloor = exitFloor; _all = all; _bounded = bounded; _token = token;
            _edges = edges.Where(e => e.ModuleId is null && !e.SpecialTeleport)
                .Select(e => (From: nodes[e.From].FloorKey, To: nodes[e.To].FloorKey, Rooms: e.TransferRooms)).Distinct().ToLookup(e => e.From);
        }

        public BigInteger UpperBound(SearchLabel label)
        {
            if (!_bounded) return _all;
            var floors = Future(_nodes[label.State.Node].FloorKey, label.State.TransferRooms);
            var rooms = label.Rooms;
            foreach (var (floor, mask) in _rooms) if ((floors & _floorBits[floor]) != 0) rooms |= mask;
            return rooms;
        }

        public BigInteger RetainOccupation(State state, BigInteger occupied)
        {
            if (!_bounded) return occupied;
            var floors = Future(_nodes[state.Node].FloorKey, state.TransferRooms);
            if (!_occupationMasks.TryGetValue(floors, out var mask))
            {
                foreach (var (floor, bits) in _occupation) if ((floors & _floorBits[floor]) != 0) mask |= bits;
                _occupationMasks[floors] = mask;
            }
            return occupied & mask;
        }

        private uint Future(string floor, ulong used)
        {
            _token.ThrowIfCancellationRequested();
            if (_cache.TryGetValue((floor, used), out var found)) return found;
            var result = floor == _exitFloor ? _floorBits[floor] : 0u;
            foreach (var edge in _edges[floor])
            {
                if ((used & edge.Rooms) != 0) continue;
                var continuation = Future(edge.To, used | edge.Rooms);
                if (continuation != 0) result |= continuation | _floorBits[floor];
            }
            return _cache[(floor, used)] = result;
        }
    }

    private static bool HasLaneCapacity(IReadOnlyList<Edge> edges, IReadOnlyDictionary<string, MapNavigationModule> modules,
        IReadOnlyList<MapNavigationFloor> floors, string exit, bool dense)
    {
        if (!dense) return true;
        var stage = 0;
        var paths = new List<(int Stage, string Floor, IReadOnlyList<NormalizedPoint> Points)>();
        foreach (var edge in edges)
            if (edge.ModuleId is null) stage++;
            else if (edge.To != exit) paths.Add((stage, modules[edge.ModuleId].FloorKey, edge.Points));
        return !paths.GroupBy(p => p.Floor).Any(group => MapExplorationRouteGeometry.ExceedsLaneCapacity(
            group.Select(p => (p.Stage, p.Points)), floors.Single(f => f.FloorKey == group.Key)));
    }
}
