namespace IDVBuff.Features.Maps;

// Connect a clicked point to a baked channel using only this module's cells.
// Shared-edge portals prevent diagonal shortcuts across an empty corner.
public static class MapAutomaticRouteEndpointPath
{
    public static bool ContainsSegment(IReadOnlyList<MapNavigationCell> cells, NormalizedPoint a, NormalizedPoint b)
    {
        var intervals = new List<(double Start, double End)>();
        foreach (var cell in cells)
        {
            var begin = 0d; var end = 1d;
            bool Clip(double origin, double delta, double minimum, double maximum)
            {
                if (Math.Abs(delta) < 1e-14) return origin >= minimum - 1e-9 && origin <= maximum + 1e-9;
                var first = (minimum - origin) / delta; var last = (maximum - origin) / delta;
                if (first > last) (first, last) = (last, first);
                begin = Math.Max(begin, first); end = Math.Min(end, last);
                return begin <= end + 1e-9;
            }
            if (Clip(a.X, b.X - a.X, cell.X, cell.X + cell.Width)
                && Clip(a.Y, b.Y - a.Y, cell.Y, cell.Y + cell.Height)) intervals.Add((begin, end));
        }
        var covered = 0d;
        foreach (var interval in intervals.OrderBy(interval => interval.Start))
        {
            if (interval.Start > covered + 1e-9) return false;
            covered = Math.Max(covered, interval.End);
            if (covered >= 1 - 1e-9) return true;
        }
        return false;
    }

    public static List<NormalizedPoint> Find(MapNavigationModule module, MapNavigationFloor floor,
        NormalizedPoint start, NormalizedPoint end, CancellationToken cancellationToken)
        => TryFind(module, floor, start, end, cancellationToken)
            ?? throw new InvalidOperationException("这个端点无法连接到可通行路线，请在连通的房间区域内重新选择。");

    public static List<NormalizedPoint>? TryFind(MapNavigationModule module, MapNavigationFloor floor,
        NormalizedPoint start, NormalizedPoint end, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ContainsSegment(module.Cells, start, end)) return [start.Clone(), end.Clone()];
        var cells = module.Cells;
        var centers = cells.Select(cell => new NormalizedPoint { X = cell.X + cell.Width / 2, Y = cell.Y + cell.Height / 2 }).ToArray();
        double Length(NormalizedPoint a, NormalizedPoint b) => Math.Sqrt(Math.Pow((b.X - a.X) * floor.ImageWidth, 2)
            + Math.Pow((b.Y - a.Y) * floor.ImageHeight, 2));
        var distance = new double[cells.Count]; Array.Fill(distance, double.PositiveInfinity);
        var previous = new Dictionary<int, (int Cell, NormalizedPoint Portal)>();
        var queue = new PriorityQueue<int, double>();
        for (var i = 0; i < cells.Count; i++)
            if (cells[i].Contains(start)) { distance[i] = Length(start, centers[i]); queue.Enqueue(i, distance[i]); }
        var goal = -1; var best = double.PositiveInfinity;
        while (queue.TryDequeue(out var current, out var cost))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cost > distance[current] + 1e-9 || cost >= best) continue;
            if (cells[current].Contains(end))
            {
                var candidate = cost + Length(centers[current], end);
                if (candidate < best) { best = candidate; goal = current; }
            }
            for (var next = 0; next < cells.Count; next++)
            {
                if (next == current || Portal(cells[current], cells[next]) is not { } portal) continue;
                var candidate = cost + Length(centers[current], portal) + Length(portal, centers[next]);
                if (candidate >= distance[next] - 1e-9) continue;
                distance[next] = candidate; previous[next] = (current, portal); queue.Enqueue(next, candidate);
            }
        }
        if (goal < 0) return null;
        var result = new List<NormalizedPoint> { end.Clone(), centers[goal] };
        for (var current = goal; previous.TryGetValue(current, out var predecessor); current = predecessor.Cell)
        { result.Add(predecessor.Portal); result.Add(centers[predecessor.Cell]); }
        result.Add(start.Clone()); result.Reverse();
        // Simplify only when the complete segment is proven inside the cell union.
        var simplified = new List<NormalizedPoint> { result[0] };
        for (var i = 0; i < result.Count - 1;)
        {
            var next = result.Count - 1;
            while (next > i + 1 && !ContainsSegment(cells, result[i], result[next])) next--;
            if (!MapNavigationRules.Same(simplified[^1], result[next])) simplified.Add(result[next]);
            i = next;
        }
        return simplified;
    }

    private static NormalizedPoint? Portal(MapNavigationCell a, MapNavigationCell b)
    {
        var left = Math.Max(a.X, b.X); var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var top = Math.Max(a.Y, b.Y); var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (right < left - 1e-9 || bottom < top - 1e-9 || right - left <= 1e-9 && bottom - top <= 1e-9) return null;
        return new() { X = (left + right) / 2, Y = (top + bottom) / 2 };
    }
}

