namespace IDVBuff.Features.Maps;

// Bake two physical side lanes before searching. Lane identity is geometric,
// never the direction of travel; a turn cannot cut across the adjacent lane.
internal static class MapExplorationRouteChannels
{
    internal static double Offset(MapNavigationFloor floor) => Math.Max(6, floor.ImageWidth / 256d);
    internal static double Margin(MapNavigationFloor floor) => Math.Max(3, floor.ImageWidth / 640d);

    internal static HashSet<string> Add(MapNavigationTopology topology, Dictionary<string, MapNavigationNode> nodes,
        List<MapNavigationLink> links, CancellationToken token)
    {
        var dense = new HashSet<string>();
        var modules = topology.Modules.ToDictionary(m => m.Id);
        var doors = topology.Doors.Where(d => d.Enabled).ToDictionary(d => d.NodeId);
        var lanes = new Dictionary<string, (string Id, NormalizedPoint Point)[]>();
        foreach (var (id, door) in doors)
        {
            token.ThrowIfCancellationRequested();
            var node = nodes[id]; var floor = topology.Floors.Single(f => f.FloorKey == node.FloorKey);
            var a = modules[door.ModuleAId]; var b = modules[door.ModuleBId];
            // Shared cell edges describe the doorway, including L-shaped rooms
            // whose centers do not lie on opposite sides of the door normal.
            var vertical = a.Cells.Any(x => b.Cells.Any(y =>
                (Math.Abs(x.X + x.Width - y.X) < 1e-8 || Math.Abs(y.X + y.Width - x.X) < 1e-8)
                && node.Point.Y >= Math.Max(x.Y, y.Y) - 1e-9
                && node.Point.Y <= Math.Min(x.Y + x.Height, y.Y + y.Height) + 1e-9));
            var distance = Offset(floor);
            lanes[id] = new[] { -1, 1 }.Select(side =>
            {
                var point = new NormalizedPoint { X = node.Point.X + (vertical ? 0 : side * distance / floor.ImageWidth),
                    Y = node.Point.Y + (vertical ? side * distance / floor.ImageHeight : 0) };
                var laneId = id + (side < 0 ? "@lane-a" : "@lane-b");
                nodes.Add(laneId, new() { Id = laneId, FloorKey = node.FloorKey, Kind = node.Kind, Point = point });
                return (laneId, point);
            }).ToArray();
        }
        var safeFloors = topology.Floors.ToDictionary(f => f.FloorKey, f => topology.Modules
            .Where(m => m.FloorKey == f.FloorKey && !m.IsExcludedFromAutomaticRoutes)
            .SelectMany(m => m.SafeCells.Count > 0 ? m.SafeCells : m.Cells).ToArray());
        foreach (var link in links.ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (link.Points.Count < 2 || modules[link.ModuleId].IsDeadEnd || modules[link.ModuleId].IsExcludedFromAutomaticRoutes) continue;
            var module = modules[link.ModuleId];
            var floor = topology.Floors.Single(f => f.FloorKey == module.FloorKey);
            foreach (var side in new[] { -1, 1 })
            {
                var points = Shift(link.Points, floor, side * Offset(floor));
                if (points is null) continue;
                (string Id, NormalizedPoint Point) Port(string id, NormalizedPoint shifted)
                    => lanes.TryGetValue(id, out var pair) ? pair.MinBy(p => Distance(p.Point, shifted, floor)) : (id, nodes[id].Point);
                var from = Port(link.FromNodeId, points[0]); var to = Port(link.ToNodeId, points[^1]);
                // Fixed centers and gate markers remain exact. The short fan-in
                // belongs to this link and participates in all crossing checks.
                if (lanes.ContainsKey(link.FromNodeId)) points[0] = from.Point;
                else points.Insert(0, from.Point);
                if (lanes.ContainsKey(link.ToNodeId)) points[^1] = to.Point;
                else points.Add(to.Point);
                points = points.Where((p, index) => index == 0 || !MapNavigationRules.Same(p, points[index - 1])).ToList();
                if (!MapExplorationRouteGeometry.IsSimple(points)
                    || !Safe(module, floor, safeFloors[floor.FloorKey], points)) continue;
                var id = link.Id + (side < 0 ? "@lane-a" : "@lane-b");
                // Length is the audited module traversal distance, not image
                // pixels. Moving its drawing sideways must not change that unit
                // or make a rounded corner win the mandatory shortest prefix.
                links.Add(new() { Id = id, ModuleId = link.ModuleId, FromNodeId = from.Id, ToNodeId = to.Id,
                    Points = points, Length = link.Length, Cost = link.Cost });
                dense.Add(id);
            }
        }
        return dense;
    }

    internal static bool Safe(MapNavigationModule module, MapNavigationFloor floor,
        IReadOnlyList<MapNavigationCell> floorCells, IReadOnlyList<NormalizedPoint> points)
    {
        var cells = module.SafeCells.Count > 0 ? module.SafeCells : module.Cells;
        var mx = Margin(floor) / floor.ImageWidth; var my = Margin(floor) / floor.ImageHeight;
        foreach (var (a, b) in points.Zip(points.Skip(1)))
        {
            if (!MapNavigationRules.ContainsSegment(cells, a, b)) return false;
            var left = Math.Min(a.X, b.X) - mx; var right = Math.Max(a.X, b.X) + mx;
            var top = Math.Min(a.Y, b.Y) - my; var bottom = Math.Max(a.Y, b.Y) + my;
            var local = floorCells.Where(c => c.X <= right && c.X + c.Width >= left && c.Y <= bottom && c.Y + c.Height >= top).ToArray();
            // Prove the entire swept square is covered. Within each horizontal
            // cell slab the free-space intervals are constant and the sweep's
            // sides are linear. This catches holes between offset sample lines.
            var cuts = local.SelectMany(c => new[] { c.Y, c.Y + c.Height })
                .Concat(new[] { a.Y - my, a.Y + my, b.Y - my, b.Y + my })
                .Where(y => y >= top && y <= bottom).Distinct().Order().ToArray();
            (double Left, double Right) Span(double y)
            {
                var lo = 0d; var hi = 1d; var dy = b.Y - a.Y;
                if (Math.Abs(dy) > 1e-14)
                {
                    var first = (y - my - a.Y) / dy; var last = (y + my - a.Y) / dy;
                    lo = Math.Max(0, Math.Min(first, last)); hi = Math.Min(1, Math.Max(first, last));
                }
                var x1 = a.X + lo * (b.X - a.X); var x2 = a.X + hi * (b.X - a.X);
                return (Math.Min(x1, x2) - mx, Math.Max(x1, x2) + mx);
            }
            for (var i = 1; i < cuts.Length; i++)
            {
                var first = Span(cuts[i - 1]); var last = Span(cuts[i]); var y = (cuts[i - 1] + cuts[i]) / 2;
                if (!MapNavigationRules.ContainsSegment(local,
                    new() { X = Math.Min(first.Left, last.Left), Y = y },
                    new() { X = Math.Max(first.Right, last.Right), Y = y })) return false;
            }
        }
        return true;
    }

    // The middle channel excludes both side channels. Only the two outer
    // lanes can run together; a third parallel traversal has no free slot.
    internal static bool Crowded(IReadOnlyList<NormalizedPoint> a, IReadOnlyList<NormalizedPoint> b, MapNavigationFloor floor)
    {
        var spacing = Offset(floor) * 1.5;
        foreach (var (p, q) in a.Zip(a.Skip(1)))
        foreach (var (r, s) in b.Zip(b.Skip(1)))
        {
            var dx = (q.X - p.X) * floor.ImageWidth; var dy = (q.Y - p.Y) * floor.ImageHeight;
            var ex = (s.X - r.X) * floor.ImageWidth; var ey = (s.Y - r.Y) * floor.ImageHeight;
            var length = Math.Sqrt(dx * dx + dy * dy); var otherLength = Math.Sqrt(ex * ex + ey * ey);
            if (length < 1e-8 || otherLength < 1e-8 || Math.Abs(dx * ey - dy * ex) > 1e-6 * length * otherLength) continue;
            var rx = (r.X - p.X) * floor.ImageWidth; var ry = (r.Y - p.Y) * floor.ImageHeight;
            if (Math.Abs(rx * dy - ry * dx) / length >= spacing - 1e-6) continue;
            var first = (rx * dx + ry * dy) / length; var last = first + (ex * dx + ey * dy) / length;
            if (Math.Min(length, Math.Max(first, last)) - Math.Max(0, Math.Min(first, last)) > spacing) return true;
        }
        return false;
    }

    private static List<NormalizedPoint>? Shift(IReadOnlyList<NormalizedPoint> points, MapNavigationFloor floor, double offset)
    {
        var result = new List<NormalizedPoint>();
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[Math.Max(0, i - 1)]; var q = points[Math.Min(points.Count - 1, i + 1)];
            (double X, double Y) Normal(NormalizedPoint a, NormalizedPoint b)
            {
                var dx = (b.X - a.X) * floor.ImageWidth; var dy = (b.Y - a.Y) * floor.ImageHeight;
                var length = Math.Sqrt(dx * dx + dy * dy);
                return length < 1e-9 ? (0, 0) : (-dy / length, dx / length);
            }
            var n = Normal(i == 0 ? points[i] : p, i == 0 ? q : points[i]);
            var m = Normal(i == points.Count - 1 ? p : points[i], i == points.Count - 1 ? points[i] : q);
            var denominator = 1 + n.X * m.X + n.Y * m.Y;
            if (denominator < .25) return null;
            result.Add(new() { X = points[i].X + offset * (n.X + m.X) / denominator / floor.ImageWidth,
                Y = points[i].Y + offset * (n.Y + m.Y) / denominator / floor.ImageHeight });
        }
        return result;
    }

    private static double Distance(NormalizedPoint a, NormalizedPoint b, MapNavigationFloor floor) =>
        Math.Sqrt(Math.Pow((a.X - b.X) * floor.ImageWidth, 2) + Math.Pow((a.Y - b.Y) * floor.ImageHeight, 2));
}
