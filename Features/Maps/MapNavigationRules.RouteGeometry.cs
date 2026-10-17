namespace IDVBuff.Features.Maps;

// Shared by lightweight map models, IDVM validation and route planning.
public static partial class MapNavigationRules
{
    private const double RouteGeometryEpsilon = 1e-9;

    public static bool IsSimpleRoute(IReadOnlyList<NormalizedPoint> points, bool allowClosed = false)
    {
        for (var i = 1; i < points.Count; i++)
        {
            if (Same(points[i - 1], points[i])) return false;
            for (var j = i + 1; j < points.Count; j++)
                if (RouteSegmentsConflict([points[i - 1], points[i]], [points[j - 1], points[j]],
                    j == i + 1 || allowClosed && i == 1 && j == points.Count - 1 && Same(points[0], points[^1]))) return false;
        }
        return true;
    }

    public static bool RouteSegmentsConflict(IReadOnlyList<NormalizedPoint> a, IReadOnlyList<NormalizedPoint> b, bool allowSharedEndpoints = true)
    {
        if (a.Count < 2 || b.Count < 2) return false;
        if (a.Max(p => p.X) < b.Min(p => p.X) - RouteGeometryEpsilon || b.Max(p => p.X) < a.Min(p => p.X) - RouteGeometryEpsilon
            || a.Max(p => p.Y) < b.Min(p => p.Y) - RouteGeometryEpsilon || b.Max(p => p.Y) < a.Min(p => p.Y) - RouteGeometryEpsilon) return false;
        for (var i = 1; i < a.Count; i++)
        for (var j = 1; j < b.Count; j++)
        {
            var p = a[i - 1]; var q = a[i]; var r = b[j - 1]; var s = b[j];
            var dx = q.X - p.X; var dy = q.Y - p.Y;
            var ex = s.X - r.X; var ey = s.Y - r.Y;
            var cross = dx * ey - dy * ex;
            if (Math.Abs(cross) < RouteGeometryEpsilon)
            {
                if (Math.Abs((r.X - p.X) * dy - (r.Y - p.Y) * dx) > RouteGeometryEpsilon) continue;
                var useX = Math.Abs(dx) >= Math.Abs(dy);
                double Axis(NormalizedPoint point) => useX ? point.X : point.Y;
                var low = Math.Max(Math.Min(Axis(p), Axis(q)), Math.Min(Axis(r), Axis(s)));
                var high = Math.Min(Math.Max(Axis(p), Axis(q)), Math.Max(Axis(r), Axis(s)));
                if (high < low - RouteGeometryEpsilon) continue;
                if (high > low + RouteGeometryEpsilon) return true; // Includes partial overlaps and opposite travel.
                var touch = new[] { p, q, r, s }.FirstOrDefault(point => Math.Abs(Axis(point) - low) < RouteGeometryEpsilon);
                if (touch is not null && !(allowSharedEndpoints && RouteEndpoint(a, touch) && RouteEndpoint(b, touch))) return true;
            }
            else
            {
                var t = ((r.X - p.X) * ey - (r.Y - p.Y) * ex) / cross;
                var u = ((r.X - p.X) * dy - (r.Y - p.Y) * dx) / cross;
                if (t < -RouteGeometryEpsilon || t > 1 + RouteGeometryEpsilon || u < -RouteGeometryEpsilon || u > 1 + RouteGeometryEpsilon) continue;
                var touch = new NormalizedPoint { X = p.X + t * dx, Y = p.Y + t * dy };
                if (!(allowSharedEndpoints && RouteEndpoint(a, touch) && RouteEndpoint(b, touch))) return true;
            }
        }
        return false;
    }

    private static bool RouteEndpoint(IReadOnlyList<NormalizedPoint> points, NormalizedPoint point) =>
        Same(points[0], point) || Same(points[^1], point);
}
