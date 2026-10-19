namespace IDVBuff.Features.Maps;

internal readonly record struct Vpsg3_5DragSnapshot(
    long Epoch, bool IsDragging, double Tx, double Ty, long Timestamp,
    long Revision, long StartedAt, long ReleasedAt, double Confidence);

internal readonly record struct Vpsg3_5DragTelemetry(
    long InputSamples, double MaximumInputGapMs, double DragDurationMs,
    double MouseGainX, double MouseGainY, double NativeInputDelayMs,
    double NativeFrameErrorPixels, double MaximumNativeFrameErrorPixels);

/// <summary>
/// Measured native frames own position. Cursor history bridges only the short
/// capture/presentation interval using measured gain and input-to-frame delay.
/// No residual easing, target chasing, or autonomous overlay animation.
/// </summary>
internal sealed class Vpsg3_5DragMotion
{
    private const long MaximumPredictionTicks = 50 * TimeSpan.TicksPerMillisecond;
    private readonly object _gate = new();
    private readonly TimestampedMouseHistory _mouse = new();
    private readonly Vpsg3_5NativeMotionModel _native = new();
    private readonly Vpsg3_5TrackingConfig _config;
    private double _tx;
    private double _ty;
    private double _anchorX;
    private double _anchorY;
    private long _anchorTimeX;
    private long _anchorTimeY;
    private double _confidence = 0.05d;
    private long _epoch;
    private long _revision;
    private long _timestamp;
    private long _startedAt;
    private long _releasedAt;
    private long _visualTimestamp;
    private double _lastVisualX;
    private double _lastVisualY;
    private bool _dragging;
    private bool _settled = true;
    private long _inputSamples;
    private double _maximumInputGapMs;
    private double _dragDurationMs;
    private double _nativeFrameError;
    private double _maximumNativeFrameError;
    private int _blockedX;
    private int _blockedY;
    private int _stalledX;
    private int _stalledY;
    private double _stalledDistanceX;
    private double _stalledDistanceY;

    internal Vpsg3_5DragMotion(double tx, double ty, Vpsg3_5TrackingConfig config)
    {
        _tx = _anchorX = tx;
        _ty = _anchorY = ty;
        _config = config;
    }

    internal Vpsg3_5DragSnapshot SampleInput(bool pressed, bool hasCursor, int x, int y, long timestamp)
    {
        lock (_gate)
        {
            timestamp = Math.Max(timestamp, _timestamp);
            var previousTimestamp = _timestamp;
            _timestamp = timestamp;
            if (!_dragging)
            {
                if (!_settled) UpdatePose(timestamp);
                if (!pressed || !hasCursor) return SnapshotCore();
                _dragging = true;
                _settled = false;
                _epoch++;
                _startedAt = timestamp;
                _releasedAt = 0;
                _visualTimestamp = 0;
                _blockedX = _blockedY = _stalledX = _stalledY = 0;
                _stalledDistanceX = _stalledDistanceY = 0d;
                _anchorX = _tx;
                _anchorY = _ty;
                _anchorTimeX = _anchorTimeY = timestamp;
                _mouse.Reset(x, y, timestamp);
                _native.BeginDrag();
                _revision++;
                return SnapshotCore();
            }

            _inputSamples++;
            _maximumInputGapMs = Math.Max(_maximumInputGapMs,
                (timestamp - previousTimestamp) / (double)TimeSpan.TicksPerMillisecond);
            if (hasCursor)
                _mouse.Record(x, y, timestamp,
                    _config.EnableMouseFeedforward ? _config.MouseScaleRatio : 0d);
            var before = _mouse.PositionAt(previousTimestamp - _native.InputDelayTicks);
            var after = _mouse.PositionAt(timestamp - _native.InputDelayTicks);
            if ((after.X - before.X) * _blockedX < 0d)
            {
                _blockedX = _stalledX = 0;
                _stalledDistanceX = 0d;
                _anchorX = _tx;
                _anchorTimeX = previousTimestamp;
            }
            if ((after.Y - before.Y) * _blockedY < 0d)
            {
                _blockedY = _stalledY = 0;
                _stalledDistanceY = 0d;
                _anchorY = _ty;
                _anchorTimeY = previousTimestamp;
            }
            UpdatePose(timestamp);
            if (!pressed)
            {
                _dragging = false;
                _releasedAt = timestamp;
                _dragDurationMs += (timestamp - _startedAt) / (double)TimeSpan.TicksPerMillisecond;
                _revision++;
            }
            return SnapshotCore();
        }
    }

    internal Vpsg3_5DragSnapshot Snapshot()
    {
        lock (_gate) return SnapshotCore();
    }

    internal Vpsg3_5DragSnapshot SnapshotForPresentation(long timestamp)
    {
        lock (_gate)
        {
            var snapshot = SnapshotCore();
            if (_settled) return snapshot;
            var pose = Project(Math.Max(timestamp, _timestamp));
            return snapshot with { Tx = pose.X, Ty = pose.Y, Timestamp = timestamp };
        }
    }

    internal Vpsg3_5DragTelemetry Telemetry()
    {
        lock (_gate)
            return new(_inputSamples, _maximumInputGapMs, _dragDurationMs
                + (_dragging ? (_timestamp - _startedAt) / (double)TimeSpan.TicksPerMillisecond : 0d),
                _native.GainX, _native.GainY, _native.InputDelayMs,
                _nativeFrameError, _maximumNativeFrameError);
    }

    internal bool TryPredictFrame(long epoch, long timestamp, out double tx, out double ty)
    {
        lock (_gate)
        {
            tx = ty = 0d;
            if (!_dragging || epoch != _epoch || timestamp < _startedAt) return false;
            (tx, ty) = Project(timestamp);
            return true;
        }
    }

    internal bool ObserveVisual(long epoch, long timestamp, double tx, double ty, double confidence)
    {
        lock (_gate)
        {
            if (!_dragging || epoch != _epoch || timestamp < _startedAt
                || timestamp < _visualTimestamp || !double.IsFinite(tx) || !double.IsFinite(ty))
                return false;
            var predicted = Project(timestamp);
            _nativeFrameError = Math.Sqrt(Math.Pow(tx - predicted.X, 2d) + Math.Pow(ty - predicted.Y, 2d));
            _maximumNativeFrameError = Math.Max(_maximumNativeFrameError, _nativeFrameError);
            if (_visualTimestamp > 0 && timestamp > _visualTimestamp)
            {
                var before = _mouse.PositionAt(_visualTimestamp - _native.InputDelayTicks);
                var after = _mouse.PositionAt(timestamp - _native.InputDelayTicks);
                ObserveBoundary((after.X - before.X) * _native.GainX, tx - _lastVisualX,
                    ref _blockedX, ref _stalledX, ref _stalledDistanceX);
                ObserveBoundary((after.Y - before.Y) * _native.GainY, ty - _lastVisualY,
                    ref _blockedY, ref _stalledY, ref _stalledDistanceY);
            }
            if (confidence >= 0.55d) _native.Observe(timestamp, tx, ty, _mouse);
            _visualTimestamp = timestamp;
            _lastVisualX = _anchorX = tx;
            _lastVisualY = _anchorY = ty;
            _anchorTimeX = _anchorTimeY = timestamp;
            _confidence = confidence;
            // Consume the observed native pose now. There is no 32ms residual
            // filter between a verified observation and the presented position.
            UpdatePose(_timestamp);
            return true;
        }
    }

    internal bool TryReleaseSnap(long epoch, double tx, double ty, long timestamp, double confidence)
    {
        lock (_gate)
        {
            if (_dragging || epoch != _epoch || _releasedAt == 0
                || !double.IsFinite(tx) || !double.IsFinite(ty)) return false;
            _tx = _anchorX = tx;
            _ty = _anchorY = ty;
            _anchorTimeX = _anchorTimeY = timestamp;
            _timestamp = Math.Max(timestamp, _timestamp);
            _confidence = confidence;
            _settled = true;
            _revision++;
            return true;
        }
    }

    private (double X, double Y) Project(long timestamp)
    {
        var delay = _native.InputDelayTicks;
        var startX = _mouse.PositionAt(_anchorTimeX - delay);
        var startY = _mouse.PositionAt(_anchorTimeY - delay);
        var endX = _mouse.PositionAt(Math.Min(timestamp, _anchorTimeX + MaximumPredictionTicks) - delay);
        var endY = _mouse.PositionAt(Math.Min(timestamp, _anchorTimeY + MaximumPredictionTicks) - delay);
        return (_anchorX + (_blockedX == 0 ? (endX.X - startX.X) * _native.GainX : 0d),
            _anchorY + (_blockedY == 0 ? (endY.Y - startY.Y) * _native.GainY : 0d));
    }

    private void UpdatePose(long timestamp)
    {
        var pose = Project(timestamp);
        if (Math.Abs(pose.X - _tx) < 0.000001d && Math.Abs(pose.Y - _ty) < 0.000001d) return;
        _tx = pose.X;
        _ty = pose.Y;
        _revision++;
    }

    private Vpsg3_5DragSnapshot SnapshotCore() => new(
        _epoch, _dragging, _tx, _ty, _timestamp, _revision, _startedAt, _releasedAt, _confidence);

    private void ObserveBoundary(double expected, double observed,
        ref int blocked, ref int stalled, ref double stalledDistance)
    {
        if (blocked != 0)
        {
            if (Math.Abs(observed) > 1.5d) { blocked = 0; stalled = 0; stalledDistance = 0d; }
            return;
        }
        if (Math.Abs(expected) >= 2d && Math.Abs(observed) <= 0.75d)
        {
            var direction = Math.Sign(expected);
            stalledDistance = Math.Sign(stalled) == direction ? stalledDistance + expected : expected;
            stalled = Math.Sign(stalled) == direction ? stalled + direction : direction;
            if (Math.Abs(stalled) >= 2
                && Math.Abs(stalledDistance) >= _config.BoundaryCollisionThresholdPixels) blocked = direction;
        }
        else { stalled = 0; stalledDistance = 0d; }
    }
}
