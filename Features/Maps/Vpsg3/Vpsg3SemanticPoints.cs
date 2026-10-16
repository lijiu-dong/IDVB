using System.Collections;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>Keep each sampled point's class attached through search, refinement and verification.</summary>
internal sealed class Vpsg3SemanticPoints(Point[] points, Mat labels) : IReadOnlyList<Point>
{
    internal byte[] Classes { get; } = points.Select(p => labels.At<byte>(p.Y, p.X)).ToArray();
    public int Count => points.Length;
    public Point this[int index] => points[index];
    public IEnumerator<Point> GetEnumerator() => ((IEnumerable<Point>)points).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

}
