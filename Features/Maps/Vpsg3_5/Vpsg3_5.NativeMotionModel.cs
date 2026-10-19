namespace IDVBuff.Features.Maps;

/// <summary>
/// Fits physical cursor motion to measured game-map motion in the WGC clock.
/// The gain and input-to-frame delay belong to this floor's tracking lifetime.
/// This model never interpolates an overlay towards a target pose.
/// </summary>
internal sealed class Vpsg3_5NativeMotionModel
{
    private const int Capacity = 16;
    private readonly VisualSample[] _samples = new VisualSample[Capacity];
    private int _count;
    internal double GainX { get; private set; } = 1d;
    internal double GainY { get; private set; } = 1d;
    internal double InputDelayMs { get; private set; }
    internal long InputDelayTicks => (long)(InputDelayMs * TimeSpan.TicksPerMillisecond);

    internal void BeginDrag() => _count = 0;

    internal void Observe(long timestamp, double tx, double ty, TimestampedMouseHistory mouse)
    {
        if (_count > 0 && timestamp == _samples[_count - 1].Timestamp)
        {
            // An absolute solve refines the same frame's coordinate origin.
            // Shift the whole trajectory so that this refinement cannot look
            // like native motion and distort the learned mouse gain.
            var dx = tx - _samples[_count - 1].X;
            var dy = ty - _samples[_count - 1].Y;
            for (var i = 0; i < _count; i++)
                _samples[i] = _samples[i] with { X = _samples[i].X + dx, Y = _samples[i].Y + dy };
            return;
        }
        if (_count == Capacity)
        {
            Array.Copy(_samples, 1, _samples, 0, Capacity - 1);
            _count--;
        }
        _samples[_count++] = new(timestamp, tx, ty);
        if (_count < 4) return;

        var bestScore = double.PositiveInfinity;
        var bestDelay = InputDelayMs;
        var bestX = GainX;
        var bestY = GainY;
        for (var delayMs = 0; delayMs <= 64; delayMs += 2)
        {
            var delay = delayMs * TimeSpan.TicksPerMillisecond;
            double xx = 0, yy = 0, xyX = 0, xyY = 0, visualX = 0, visualY = 0;
            var pairs = 0;
            var movingPairsX = 0;
            var movingPairsY = 0;
            for (var i = 1; i < _count; i++)
            {
                var left = _samples[i - 1];
                var right = _samples[i];
                if (!mouse.Contains(left.Timestamp - delay) || !mouse.Contains(right.Timestamp - delay))
                    continue;
                var a = mouse.PositionAt(left.Timestamp - delay);
                var b = mouse.PositionAt(right.Timestamp - delay);
                var mx = b.X - a.X;
                var my = b.Y - a.Y;
                var vx = right.X - left.X;
                var vy = right.Y - left.Y;
                // Boundary collisions are not a change in mouse sensitivity.
                if (Math.Abs(vx) <= 0.75d && Math.Abs(mx) >= 2d && IsStationaryRun(i, true)) mx = vx = 0d;
                if (Math.Abs(vy) <= 0.75d && Math.Abs(my) >= 2d && IsStationaryRun(i, false)) my = vy = 0d;
                if (Math.Abs(mx) >= 2d && Math.Abs(vx) > 0.75d) movingPairsX++;
                if (Math.Abs(my) >= 2d && Math.Abs(vy) > 0.75d) movingPairsY++;
                xx += mx * mx;
                yy += my * my;
                xyX += mx * vx;
                xyY += my * vy;
                visualX += vx * vx;
                visualY += vy * vy;
                pairs++;
            }
            if (pairs < 3) continue;
            // One partially clamped movement followed by stationary frames is
            // not enough evidence to relearn an axis's drag sensitivity.
            var useX = movingPairsX >= 3 && xx >= 64d && visualX >= 4d;
            var useY = movingPairsY >= 3 && yy >= 64d && visualY >= 4d;
            if (!useX && !useY) continue;
            var gx = useX ? xyX / xx : GainX;
            var gy = useY ? xyY / yy : GainY;
            if (gx is < 0.25d or > 4d || gy is < 0.25d or > 4d) continue;
            var error = (useX ? Math.Max(0d, visualX - xyX * xyX / xx) : 0d)
                + (useY ? Math.Max(0d, visualY - xyY * xyY / yy) : 0d);
            var energy = (useX ? visualX : 0d) + (useY ? visualY : 0d);
            // Constant velocity does not identify delay. Prefer the last delay
            // when several candidates explain the measured motion equally well.
            var score = error / energy + Math.Abs(delayMs - InputDelayMs) * 0.000001d;
            if (score >= bestScore) continue;
            bestScore = score;
            bestDelay = delayMs;
            bestX = gx;
            bestY = gy;
        }
        if (bestScore <= 0.08d)
        {
            InputDelayMs = bestDelay;
            GainX = bestX;
            GainY = bestY;
        }
    }

    private bool IsStationaryRun(int rightIndex, bool horizontal)
    {
        double Value(int index) => horizontal ? _samples[index].X : _samples[index].Y;
        // A single zero displacement can be the middle of a reversal. It is
        // essential timing evidence, not proof of hitting a map boundary.
        return (rightIndex >= 2 && Math.Abs(Value(rightIndex - 1) - Value(rightIndex - 2)) <= 0.75d)
            || (rightIndex + 1 < _count && Math.Abs(Value(rightIndex + 1) - Value(rightIndex)) <= 0.75d);
    }

    private readonly record struct VisualSample(long Timestamp, double X, double Y);
}
