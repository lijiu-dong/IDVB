using IDVBuff.Core.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>Exclusive workspace. Views overlap only after a phase's last read.
/// Returned observations own separate pixels.</summary>
public sealed class Vpsg3LiveExtractorScratch : IDisposable
{
    private bool _isDisposed, _fullWorkspace;
    private Size _size;
    private Mat? _storage;
    private readonly List<Mat> _views = [];
    private IDisposable? _ownership;
    public Mat K3 { get; } = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
    public Mat K5 { get; } = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
    public Mat K11 { get; } = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(11, 11));
    public Mat Bgr { get; private set; } = null!;
    public Mat Hsv { get; private set; } = null!;
    public Mat Exclusion { get; private set; } = null!;
    public Mat GreenSeed { get; } = new();
    public Mat WhiteSeed { get; } = new();
    public Mat Room1 => Room;
    public Mat Room2 => Gray;
    public Mat Room { get; private set; } = null!;
    public Mat Corridor { get; private set; } = null!;
    public Mat RoomEdges { get; } = new();
    public Mat CorridorEdges { get; } = new();
    public Mat CandidateEdges { get; private set; } = null!;
    public Mat Gray { get; private set; } = null!;
    public Mat CannyStrong { get; private set; } = null!;
    public Mat CannyWeak { get; private set; } = null!;
    public Mat SmoothedGray { get; private set; } = null!;
    public Mat BrightDetail { get; private set; } = null!;
    public Mat Support { get; private set; } = null!;
    public Mat StrongSupport { get; private set; } = null!;
    public Mat NotSupport => SmoothedGray;
    public Mat UncertainFrontier => BrightDetail;
    public Mat DilatedExclusion => Exclusion;
    public Mat Invalid => SmoothedGray;
    public List<Point[]> ApproxContourBatch { get; } = new(64);
    internal long RetainedPixelBytes => _storage is null ? 0 : checked(_storage.Total() * _storage.ElemSize());

    public void PrepareForSize(Size size)
    {
        PrepareStorage(size, full: true);
        Exclusion.SetTo(Scalar.Black);
        CandidateEdges.SetTo(Scalar.Black);
        ApproxContourBatch.Clear();
    }

    internal void PrepareSupportForSize(Size size)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        // The IDVA support-only consumer needs seven planes, not a full workspace.
        if (_storage is null || _size != size) PrepareStorage(size, full: false);
    }

    private void PrepareStorage(Size size, bool full)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_storage is not null && _size == size && _fullWorkspace == full) return;
        ReleaseStorage();
        var planes = full ? 11 : 7;
        var bytes = checked((long)size.Width * size.Height * planes);
        var request = MemoryJournal.Request("VPSG.Live.Workspace", bytes, "OpenCvSharpExtern.dll");
        try
        {
            _storage = new Mat(checked(size.Height * planes), size.Width, MatType.CV_8UC1);
            _ownership = MemoryJournal.Own("VPSG.Live.Workspace", bytes, "OpenCvSharpExtern.dll", request);
            _size = size;
            _fullWorkspace = full;
            var plane = 0;
            Mat View(int channels)
            {
                var data = new IntPtr(checked(_storage.Data.ToInt64() + (long)plane * size.Width * size.Height));
                plane += channels;
                var view = Mat.FromPixelData(size.Height, size.Width,
                    channels == 3 ? MatType.CV_8UC3 : MatType.CV_8UC1, data, checked((long)size.Width * channels));
                _views.Add(view);
                return view;
            }
            if (full)
            {
                Bgr = View(3);
                Hsv = View(3);
                // HSV is last read by classification. Reuse its three byte planes for
                // blur/ridge/weak edges, then invalid masks, then semantic labels.
                // Independent full-image headers keep OpenCV border extrapolation
                // local to each plane (a parent ROI would read adjacent planes).
                Mat BytePlane(Mat color, int index)
                {
                    var offset = checked((long)size.Width * size.Height * index);
                    var view = Mat.FromPixelData(size.Height, size.Width, MatType.CV_8UC1,
                        new IntPtr(checked(color.Data.ToInt64() + offset)), size.Width);
                    _views.Add(view);
                    return view;
                }
                SmoothedGray = BytePlane(Hsv, 0);
                BrightDetail = BytePlane(Hsv, 1);
                CannyWeak = BytePlane(Hsv, 2);
                // BGR's last read is the conversion into Gray. Support begins
                // afterwards and never reads BGR; reuse those three byte planes.
                CannyStrong = BytePlane(Bgr, 0);
                Support = BytePlane(Bgr, 1);
                StrongSupport = BytePlane(Bgr, 2);
                Room = View(1); Gray = View(1); Corridor = View(1);
                CandidateEdges = View(1); Exclusion = View(1);
            }
            else
            {
                Gray = View(1); CannyStrong = View(1); Support = View(1); StrongSupport = View(1);
                SmoothedGray = View(1); BrightDetail = View(1); CannyWeak = View(1);
            }
        }
        catch { ReleaseStorage(); throw; }
    }

    public static void EnsureMatSized(Mat mat, Size size, MatType type)
    {
        if (mat.Size() != size || mat.Type() != type) mat.Create(size, type);
    }

    private void ReleaseStorage()
    {
        foreach (var view in _views) view.Dispose();
        _views.Clear();
        _storage?.Dispose(); _storage = null;
        _ownership?.Dispose(); _ownership = null;
        ApproxContourBatch.Clear();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        ReleaseStorage();
        K3.Dispose(); K5.Dispose(); K11.Dispose();
        GreenSeed.Dispose(); WhiteSeed.Dispose(); RoomEdges.Dispose(); CorridorEdges.Dispose();
    }
}
