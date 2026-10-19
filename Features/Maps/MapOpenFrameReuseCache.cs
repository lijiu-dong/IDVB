using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal readonly record struct MapOpenFrameReuseKey(
    MapAlignmentContextKey Alignment,
    object CatalogRevision,
    string SettingsFingerprint);

internal sealed record MapOpenFrameReuseEntry(
    RuntimeMapRecognition Recognition,
    MapAlignmentSession Session);

/// <summary>
/// One already validated view. Reopening compares pixels at the original static
/// walls; it never extracts another contour or solves another transform.
/// </summary>
internal sealed class MapOpenFrameReuseCache : IDisposable
{
    private readonly object _gate = new();
    private Mat? _image;
    private Mat? _wallMask;
    private MapOpenFrameReuseKey _key;
    private MapScreenRect _clientBounds;
    private MapScreenRect _viewportBounds;
    private IntPtr _window;
    private string? _detectedFloor;
    private MapOpenFrameReuseEntry? _entry;

    public bool HasEntry
    {
        get { lock (_gate) return _entry is not null; }
    }

    public MapOpenFrameReuseEntry? TryGet(
        MapOpenFrameReuseKey key, CapturedGameFrame frame)
    {
        lock (_gate)
        {
            if (_entry is null || _image is null || _wallMask is null
                || _key != key
                || _window != frame.WindowHandle
                || _clientBounds != frame.ClientBounds
                || _viewportBounds != frame.ViewportBounds
                || !string.Equals(_detectedFloor, frame.DetectedFloorKey,
                    StringComparison.OrdinalIgnoreCase)
                || _image.Size() != frame.Image.Size()
                || _image.Type() != frame.Image.Type())
                return null;

            // No resizing, translation search or edge extraction. A one-pixel
            // move changes the wall neighborhoods and must take the normal path.
            // Two intensity levels tolerate capture quantization, not geometry.
            if (Cv2.Norm(_image, frame.Image, NormTypes.INF, _wallMask) > 2d)
                return null;
            // Dynamic glyphs are excluded from walls, but a materially changed
            // overall view must still be reconsidered by the normal pipeline.
            var meanDifference = Cv2.Norm(_image, frame.Image, NormTypes.L1)
                / (_image.Total() * _image.Channels());
            return meanDifference <= 1d ? _entry : null;
        }
    }

    public void Remember(MapOpenFrameReuseKey key, CapturedGameFrame frame,
        RuntimeMapRecognition recognition, MapAlignmentSession session)
    {
        lock (_gate)
        {
            ResetCore();
            if (recognition.Result.ReusedLastTransform
                || recognition.Result.OverlayTransform is not { } transform
                || !MapSimilarityTransform.FromOverlay(transform).IsValid
                || key.Alignment.MapId != recognition.Map.Id
                || !string.Equals(key.Alignment.FloorKey,
                    recognition.Result.Floor, StringComparison.OrdinalIgnoreCase))
                return;

            // This belongs to the successfully aligned frame and is normally
            // already resident. Never retain the disposable capture itself.
            var edges = frame.GetOrCreateVpsg3Observation().ObservedEdges;
            var bounds = Cv2.BoundingRect(edges);
            if (Cv2.CountNonZero(edges) < 128
                || bounds.Width < 32 || bounds.Height < 32)
                return;

            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
            var mask = new Mat();
            Mat? image = null;
            try
            {
                Cv2.Dilate(edges, mask, kernel);
                image = frame.Image.Clone();
                _image = image;
                _wallMask = mask;
                _key = key;
                _clientBounds = frame.ClientBounds;
                _viewportBounds = frame.ViewportBounds;
                _window = frame.WindowHandle;
                _detectedFloor = frame.DetectedFloorKey;
                _entry = new(recognition, session);
            }
            catch
            {
                image?.Dispose();
                mask.Dispose();
                throw;
            }
        }
    }

    public void Reset()
    {
        lock (_gate) ResetCore();
    }

    private void ResetCore()
    {
        _image?.Dispose();
        _wallMask?.Dispose();
        _image = null;
        _wallMask = null;
        _entry = null;
        _key = default;
    }

    public void Dispose() => Reset();
}
