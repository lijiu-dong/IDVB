namespace IDVBuff.Features.Maps;

/// <summary>Intersections are forbidden except where consecutive channels meet.
/// Works in the original floor coordinate space, before any view scaling.</summary>
public static partial class MapExplorationRouteGeometry
{


    public static bool ExceedsLaneCapacity(IEnumerable<IReadOnlyList<NormalizedPoint>> paths, MapNavigationFloor floor)
        => ExceedsLaneCapacity(paths.Select((points, stage) => (stage, points)), floor);

    public static bool ExceedsLaneCapacity(IEnumerable<(int Stage, IReadOnlyList<NormalizedPoint> Points)> paths, MapNavigationFloor floor)
    {
        var segments = paths.SelectMany(path => path.Points.Zip(path.Points.Skip(1)).Select(p =>
            (X: p.First.X * floor.ImageWidth, Y: p.First.Y * floor.ImageHeight,
                Dx: (p.Second.X - p.First.X) * floor.ImageWidth, Dy: (p.Second.Y - p.First.Y) * floor.ImageHeight,
                path.Stage))).ToArray();
        var spacing = MapExplorationRouteChannels.Offset(floor) * 1.5;
        for (var i = 0; i < segments.Length; i++)
        {
            var a = segments[i]; var length = Math.Sqrt(a.Dx * a.Dx + a.Dy * a.Dy);
            if (length <= spacing) continue;
            var nearby = new List<(double Start, double End, double Side, int Stage)>();
            for (var j = i + 1; j < segments.Length; j++)
            {
                var b = segments[j]; var otherLength = Math.Sqrt(b.Dx * b.Dx + b.Dy * b.Dy);
                if (a.Stage == b.Stage) continue;
                if (otherLength <= spacing || Math.Abs(a.Dx * b.Dy - a.Dy * b.Dx) > 1e-6 * length * otherLength) continue;
                var side = ((b.X - a.X) * a.Dy - (b.Y - a.Y) * a.Dx) / length;
                if (Math.Abs(side) > spacing * 3) continue;
                var start = ((b.X - a.X) * a.Dx + (b.Y - a.Y) * a.Dy) / length;
                var end = start + (b.Dx * a.Dx + b.Dy * a.Dy) / length;
                var lo = Math.Max(0, Math.Min(start, end)); var hi = Math.Min(length, Math.Max(start, end));
                if (hi - lo <= spacing) continue;
                foreach (var other in nearby)
                    if (other.Stage != b.Stage && Math.Min(hi, other.End) - Math.Max(lo, other.Start) > spacing
                        && Math.Max(0, Math.Max(side, other.Side)) - Math.Min(0, Math.Min(side, other.Side)) <= spacing * 3)
                        return true;
                nearby.Add((lo, hi, side, b.Stage));
            }
        }
        return false;
    }

    public static bool IsSimple(IReadOnlyList<NormalizedPoint> points, bool allowClosed = false) =>
        MapNavigationRules.IsSimpleRoute(points, allowClosed);

    public static bool Conflicts(IReadOnlyList<NormalizedPoint> a, IReadOnlyList<NormalizedPoint> b, bool allowSharedEndpoints = true) =>
        MapNavigationRules.RouteSegmentsConflict(a, b, allowSharedEndpoints);
}
