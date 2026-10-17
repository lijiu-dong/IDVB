namespace IDVBuff.Features.Maps;

public static partial class MapExplorationRouteGeometry
{
    // Repeated travel has the same physical rays. A short branch creates a T;
    // crossing an existing route creates four distinct rays, even when the
    // intersection was split into graph nodes or separate annotations.
    public static bool HasCrossing(IEnumerable<IReadOnlyList<NormalizedPoint>> paths, CancellationToken token = default)
    {
        var segments = paths.SelectMany(points => points.Zip(points.Skip(1)))
            .Where(pair => !MapNavigationRules.Same(pair.First, pair.Second)).ToArray();
        var junctions = new Dictionary<(long X, long Y), List<(double X, double Y)>>();
        for (var i = 0; i < segments.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var (p, q) = segments[i];
            for (var j = i + 1; j < segments.Length; j++)
            {
                var (r, s) = segments[j];
                if (Math.Max(p.X, q.X) < Math.Min(r.X, s.X) - 1e-9 || Math.Max(r.X, s.X) < Math.Min(p.X, q.X) - 1e-9
                    || Math.Max(p.Y, q.Y) < Math.Min(r.Y, s.Y) - 1e-9 || Math.Max(r.Y, s.Y) < Math.Min(p.Y, q.Y) - 1e-9) continue;
                var dx = q.X - p.X; var dy = q.Y - p.Y; var ex = s.X - r.X; var ey = s.Y - r.Y;
                var cross = dx * ey - dy * ex;
                if (Math.Abs(cross) < 1e-12) continue;
                var t = ((r.X - p.X) * ey - (r.Y - p.Y) * ex) / cross;
                var u = ((r.X - p.X) * dy - (r.Y - p.Y) * dx) / cross;
                if (t < -1e-9 || t > 1 + 1e-9 || u < -1e-9 || u > 1 + 1e-9) continue;
                var x = p.X + t * dx; var y = p.Y + t * dy;
                var key = ((long)Math.Round(x * 1e8), (long)Math.Round(y * 1e8));
                if (!junctions.TryGetValue(key, out var rays)) junctions[key] = rays = [];
                foreach (var point in new[] { p, q, r, s })
                {
                    var rx = point.X - x; var ry = point.Y - y; var length = Math.Sqrt(rx * rx + ry * ry);
                    if (length < 1e-9) continue;
                    rx /= length; ry /= length;
                    if (!rays.Any(ray => ray.X * rx + ray.Y * ry > 1 - 1e-9)) rays.Add((rx, ry));
                    if (rays.Count >= 4) return true;
                }
            }
        }
        return false;
    }
}
