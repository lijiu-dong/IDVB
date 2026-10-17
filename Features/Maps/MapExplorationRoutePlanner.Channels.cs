using System.Numerics;

namespace IDVBuff.Features.Maps;

public static partial class MapExplorationRoutePlanner
{
    private static (Dictionary<string, BigInteger> Conflicts, Dictionary<string, BigInteger> Crossings,
        Dictionary<string, BigInteger> Overlaps) ChannelConflicts(
        MapNavigationTopology topology, IReadOnlyDictionary<string, MapNavigationModule> modules, IReadOnlyList<MapNavigationLink> source,
        IReadOnlyDictionary<string, BigInteger> bits, string exit, bool dense, CancellationToken token)
    {
        var conflicts = source.ToDictionary(l => l.Id, l => bits[l.Id]);
        var crossings = source.ToDictionary(l => l.Id, _ => BigInteger.Zero);
        var overlaps = source.ToDictionary(l => l.Id, l => bits[l.Id]);
        foreach (var group in source.GroupBy(l => modules[l.ModuleId].FloorKey))
        {
            var links = group.ToArray(); var floor = topology.Floors.Single(f => f.FloorKey == group.Key);
            var spacing = dense ? MapExplorationRouteChannels.Offset(floor) * 1.5 : 0;
            var bounds = links.Select(l => (Left: l.Points.Min(p => p.X), Right: l.Points.Max(p => p.X),
                Top: l.Points.Min(p => p.Y), Bottom: l.Points.Max(p => p.Y))).ToArray();
            for (var i = 0; i < links.Length; i++)
            for (var j = i + 1; j < links.Length; j++)
            {
                token.ThrowIfCancellationRequested();
                if (bounds[i].Left > bounds[j].Right + spacing / floor.ImageWidth + 1e-9
                    || bounds[j].Left > bounds[i].Right + spacing / floor.ImageWidth + 1e-9
                    || bounds[i].Top > bounds[j].Bottom + spacing / floor.ImageHeight + 1e-9
                    || bounds[j].Top > bounds[i].Bottom + spacing / floor.ImageHeight + 1e-9) continue;
                var finalAccess = links[i].FromNodeId == exit || links[j].FromNodeId == exit;
                var intersects = MapExplorationRouteGeometry.Conflicts(links[i].Points, links[j].Points);
                if (dense && intersects && MapExplorationRouteGeometry.HasCrossing([links[i].Points, links[j].Points], token))
                {
                    crossings[links[i].Id] |= bits[links[j].Id]; crossings[links[j].Id] |= bits[links[i].Id];
                }
                if (intersects) { overlaps[links[i].Id] |= bits[links[j].Id]; overlaps[links[j].Id] |= bits[links[i].Id]; }
                if (!intersects && (!dense || finalAccess || !MapExplorationRouteChannels.Crowded(links[i].Points, links[j].Points, floor))) continue;
                conflicts[links[i].Id] |= bits[links[j].Id]; conflicts[links[j].Id] |= bits[links[i].Id];
            }
        }
        return (conflicts, crossings, overlaps);
    }

    private sealed class TransitTurns(IReadOnlyDictionary<string, List<Edge>> adjacent,
        IReadOnlyDictionary<string, MapNavigationModule> modules, IReadOnlyDictionary<string, MapNavigationNode> nodes)
    {
        private readonly Dictionary<(Edge Incoming, Edge Outgoing), Edge?> _cache = [];

        public bool Redundant(Edge? incoming, Edge outgoing) => Shortcut(incoming, outgoing) is not null;

        public Edge? Shortcut(Edge? incoming, Edge outgoing)
        {
            if (incoming?.ModuleId is null || incoming.ModuleId != outgoing.ModuleId || incoming.From == outgoing.To) return null;
            if (_cache.TryGetValue((incoming, outgoing), out var found)) return found;
            var joined = Simplify(incoming.Points.Concat(outgoing.Points.Skip(1)));
            foreach (var direct in adjacent[incoming.From].Where(e => e.To == outgoing.To && e.ModuleId == outgoing.ModuleId
                && e.Length <= incoming.Length + outgoing.Length + 1e-9))
            {
                var points = Simplify(direct.Points);
                if (points.Count == joined.Count && points.Zip(joined).All(p => MapNavigationRules.Same(p.First, p.Second)))
                    return _cache[(incoming, outgoing)] = direct;
            }
            return _cache[(incoming, outgoing)] = null;
        }

        public Edge? Compact(Edge incoming, Edge outgoing)
        {
            if (Shortcut(incoming, outgoing) is { } direct) return direct;
            if (incoming.ModuleId is null || incoming.ModuleId != outgoing.ModuleId || incoming.From == outgoing.To
                || modules[incoming.ModuleId].Kind != "corridor" && nodes[incoming.To].Kind != "door") return null;
            var joined = incoming.Points.Concat(outgoing.Points.Skip(1)).ToArray();
            var points = Simplify(joined);
            static double Distance(NormalizedPoint a, NormalizedPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
            if (points.Count < 2 || points.Zip(points.Skip(1), Distance).Sum() >= joined.Zip(joined.Skip(1), Distance).Sum() - 1e-9) return null;
            var headings = points.Zip(points.Skip(1), Heading).ToArray();
            // Some side-lane port combinations have no separate audited link.
            // Coalesce their existing in-module walk and erase only retraced
            // transit access. Keep its source cost units, as for lane offsets.
            return new(incoming.From, outgoing.To, incoming.ModuleId, points, incoming.Length + outgoing.Length,
                headings.Zip(headings.Skip(1), Bend).Sum(), headings[0], headings[^1], 0,
                incoming.Occupied | outgoing.Occupied, incoming.Conflicts | outgoing.Conflicts, incoming.Crossings | outgoing.Crossings,
                false, incoming.UsesSideLane || outgoing.UsesSideLane, null);
        }

        private static List<NormalizedPoint> Simplify(IEnumerable<NormalizedPoint> points)
        {
            var result = new List<NormalizedPoint>();
            foreach (var point in points)
            {
                if (result.Count > 0 && MapNavigationRules.Same(result[^1], point)) continue;
                result.Add(point);
                while (result.Count >= 3)
                {
                    var a = result[^3]; var b = result[^2]; var c = result[^1];
                    var dx = b.X - a.X; var dy = b.Y - a.Y; var ex = c.X - b.X; var ey = c.Y - b.Y;
                    if (Math.Abs(dx * ey - dy * ex) > 1e-8 * Math.Sqrt((dx * dx + dy * dy) * (ex * ex + ey * ey))) break;
                    if (MapNavigationRules.Same(a, c)) result.RemoveRange(result.Count - 2, 2);
                    else result.RemoveAt(result.Count - 2);
                }
            }
            return result;
        }
    }

    private static PathResult CompactTransit(PathResult path, TransitTurns transit)
    {
        var edges = new List<Edge>();
        foreach (var edge in path.Edges)
        {
            edges.Add(edge);
            while (edges.Count >= 2 && transit.Compact(edges[^2], edges[^1]) is { } direct)
            {
                edges.RemoveRange(edges.Count - 2, 2); edges.Add(direct);
            }
        }
        if (edges.Count == path.Edges.Count) return path;
        var turns = 0; var heading = 0;
        foreach (var edge in edges) { turns += edge.Turns + Bend(heading, edge.First); heading = edge.Last; }
        return new(edges, edges.Sum(e => e.Length), turns);
    }
}
