using System.Runtime.InteropServices;
using OpenCvSharp;
using IDVBuff.Core.Imaging;

namespace IDVBuff.Features.Maps;

/// <summary>Semantic prebuilt PNG contract: black background, white cool structure,
/// orange warm structure. Labels are categories, never grayscale intensities.</summary>
public static class SemanticStructureContours
{
    public const byte Cool = 255;
    public const byte Warm = 127;
    public static readonly Scalar OrangeBgr = new(0, 140, 255);

    public static Mat Decode(Mat image) => Decode(image, geometry: false);

    private static Mat Decode(Mat image, bool geometry)
    {
        if (image.Empty()) throw new InvalidDataException("语义轮廓为空。");
        if (image.Type() == MatType.CV_8UC1)
        {
            var binary = new Mat();
            try { Cv2.Threshold(image, binary, 128, 255, ThresholdTypes.Binary); return binary; }
            catch { binary.Dispose(); throw; }
        }
        if (image.Type() != MatType.CV_8UC3)
            throw new InvalidDataException("预制语义轮廓必须为 RGB PNG。");
        // Only the result and one temporary mask are live. Once orange has
        // been applied, reuse that mask to validate black pixels. Preserve
        // OpenCV's native vectorized classification, including strided tiles.
        var labels = new Mat(image.Size(), MatType.CV_8UC1);
        try
        {
            using var mask = new Mat();
            Cv2.InRange(image, Scalar.White, Scalar.White, labels);
            Cv2.InRange(image, OrangeBgr, OrangeBgr, mask);
            if (geometry) Cv2.BitwiseOr(labels, mask, labels);
            else labels.SetTo(Warm, mask);
            Cv2.InRange(image, Scalar.Black, Scalar.Black, mask);
            if (Cv2.CountNonZero(labels) + Cv2.CountNonZero(mask) != image.Total())
                throw new InvalidDataException("预制语义轮廓含非黑/白/橙像素，拒绝灰度降级。");
            return labels;
        }
        catch { labels.Dispose(); throw; }
    }

    public static Mat Encode(Mat labels)
    {
        var color = new Mat(labels.Size(), MatType.CV_8UC3, Scalar.Black);
        using var mask = new Mat();
        Cv2.InRange(labels, new Scalar(Cool), new Scalar(Cool), mask); color.SetTo(Scalar.White, mask);
        Cv2.InRange(labels, new Scalar(Warm), new Scalar(Warm), mask); color.SetTo(OrangeBgr, mask);
        return color;
    }

    public static Mat Geometry(Mat image) => Decode(image, geometry: true);

    // Assign only edges supported by an unambiguous native floor class. Shared
    // boundaries, HUD/fog exclusions and unclassified physical details stay unknown.
    public static Mat LabelLive(Mat observed, Mat room, Mat corridor)
    {
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        using var warm = new Mat(); using var cool = new Mat(); using var overlap = new Mat();
        return LabelLive(observed, room, corridor, warm, cool, overlap, kernel);
    }

    internal static Mat LabelLive(Mat observed, Mat room, Mat corridor,
        Mat warm, Mat cool, Mat overlap, Mat kernel)
    {
        Cv2.Dilate(room, warm, kernel); Cv2.Dilate(corridor, cool, kernel);
        Cv2.BitwiseAnd(warm, cool, overlap);
        Cv2.BitwiseAnd(warm, observed, warm); Cv2.BitwiseAnd(cool, observed, cool);
        var labels = new Mat(observed.Size(), MatType.CV_8UC1, Scalar.Black);
        labels.SetTo(Warm, warm); labels.SetTo(Cool, cool); labels.SetTo(0, overlap);
        RemoveMarkerArtifacts(labels, Warm, warm);
        RemoveMarkerArtifacts(labels, Cool, warm);
        return labels;
    }

    // Player, teammate and hunter markers are floor-coloured blobs, so their
    // rims receive a floor class. On a sparsely explored floor one marker can
    // be the only warm or cool evidence and veto a correct pose. Markers are
    // compact while walls are long: clear compact blobs and specks from the
    // class labels only. Observed structure edges keep every pixel.
    private static void RemoveMarkerArtifacts(Mat labels, byte kind, Mat scratch)
    {
        var speck = Math.Max(3, (int)Math.Round(labels.Height * .0095));
        var markerLong = Math.Max(6, (int)Math.Round(labels.Height * .03));
        var markerShort = Math.Max(3, (int)Math.Round(labels.Height * .0076));
        Cv2.InRange(labels, new Scalar(kind), new Scalar(kind), scratch);
        Cv2.FindContours(scratch, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var artifacts = new List<Point[]>();
        foreach (var contour in contours)
        {
            var box = Cv2.BoundingRect(contour);
            var longSide = Math.Max(box.Width, box.Height);
            if (longSide <= speck || (longSide <= markerLong && Math.Min(box.Width, box.Height) >= markerShort))
                artifacts.Add(contour);
        }
        if (artifacts.Count > 0)
            Cv2.DrawContours(labels, artifacts, -1, Scalar.Black, -1);
    }
}

/// <summary>Immutable per-floor class support. Native readers own independent
/// leases; managed readers retain their original shared lifetime.</summary>
public sealed class SemanticStructureIndex : IDisposable
{
    private readonly SemanticDistanceField? _warm, _cool;
    private readonly NativeSemanticField? _nativeWarm, _nativeCool;
    private int _nativeReaderReleased;
    public bool UsesNativeStorage => _nativeWarm is not null;
    internal NativeSemanticField? NativeWarm => _nativeWarm;
    internal NativeSemanticField? NativeCool => _nativeCool;
    private readonly SemanticCorrelationBits _warmK3, _coolK3;
    public int Width { get; }
    public int Height { get; }
    public long MemoryBytes => (_nativeWarm?.MemoryBytes ?? _warm!.MemoryBytes) + (_nativeCool?.MemoryBytes ?? _cool!.MemoryBytes) + _warmK3.MemoryBytes + _coolK3.MemoryBytes;
    public long SharedDistanceBytes { get; }

    // Integer Chebyshev support is zero exactly at its class seeds. Compare
    // every category, including absent classes and the last row/column, before
    // borrowing an already prepared immutable index from this same floor.
    internal bool HasSameLabelsAs(Mat labels)
    {
        if (labels.Type() != MatType.CV_8UC1 || labels.Width != Width || labels.Height != Height)
            return false;
        for (var y = 0; y < Height; y++)
        {
            var row = MemoryMarshal.CreateReadOnlySpan(ref labels.At<byte>(y, 0), Width);
            for (var x = 0; x < Width; x++)
            {
                var expected = ReadDistancePixel(x, y, SemanticStructureContours.Warm) == 0 ? SemanticStructureContours.Warm
                    : ReadDistancePixel(x, y, SemanticStructureContours.Cool) == 0 ? SemanticStructureContours.Cool : (byte)0;
                if (row[x] != expected) return false;
            }
        }
        return true;
    }

    public SemanticStructureIndex(Mat labels, bool prepareCorrelationBits = true,
        SemanticStructureIndex? reuseDistancesFrom = null, bool nativeStorage = false)
    {
        Width = labels.Width; Height = labels.Height;
        var source = reuseDistancesFrom is { } candidate && candidate.Width == Width && candidate.Height == Height
            ? candidate : null;
        try
        {
            _warm = BuildSupport(labels, SemanticStructureContours.Warm, prepareCorrelationBits,
                source?._warm, out _warmK3, nativeStorage, source?._nativeWarm, out _nativeWarm);
            _cool = BuildSupport(labels, SemanticStructureContours.Cool, prepareCorrelationBits,
                source?._cool, out _coolK3, nativeStorage, source?._nativeCool, out _nativeCool);
        }
        catch { _nativeWarm?.Dispose(); _nativeCool?.Dispose(); _warmK3?.Dispose(); _coolK3?.Dispose(); throw; }
        // The scan mask can erase a gate region. Never assume a common source
        // PNG means equal distance fields: compare every quantized pixel before
        // requesting another compressed buffer.
        // Correlation bits and all floor pose/scale state remain independent.
        if (source is not null)
        {
            SharedDistanceBytes += _nativeWarm is not null && source._nativeWarm is not null
                ? _nativeWarm.SharedBytesFrom(source._nativeWarm) : _warm is not null && source._warm is not null ? _warm.SharedBytesFrom(source._warm) : 0;
            SharedDistanceBytes += _nativeCool is not null && source._nativeCool is not null
                ? _nativeCool.SharedBytesFrom(source._nativeCool) : _cool is not null && source._cool is not null ? _cool.SharedBytesFrom(source._cool) : 0;
            if (SharedDistanceBytes > 0)
                IDVBuff.Core.Diagnostics.MemoryJournal.Current?.Record(nativeStorage ? "native-semantic-storage-shared" : "managed-storage-shared",
                    new { resource = "Semantic.Distance", bytes = SharedDistanceBytes, Width, Height,
                        meaning = "exact immutable field storage shared by same-floor scan/alignment consumers; release requires both owners" });
        }
    }

    // A caller rents while its source cache/owner is still held. Disposal of
    // another independently borrowed reader cannot revoke this reader's pixels.
    internal SemanticStructureIndex BorrowReader() => UsesNativeStorage ? new(this) : this;
    private SemanticStructureIndex(SemanticStructureIndex source)
    {
        Width=source.Width; Height=source.Height;
        _warm=source._warm; _cool=source._cool;
        SharedDistanceBytes=source.SharedDistanceBytes;
        try
        {
            _nativeWarm=source._nativeWarm!.Clone(); _nativeCool=source._nativeCool!.Clone();
            _warmK3=source._warmK3.BorrowReader(); _coolK3=source._coolK3.BorrowReader();
        }
        catch { _nativeWarm?.Dispose(); _nativeCool?.Dispose(); _warmK3?.Dispose(); _coolK3?.Dispose(); throw; }
    }
    public void Dispose()
    {
        if(!UsesNativeStorage || Interlocked.Exchange(ref _nativeReaderReleased,1)!=0)return;
        try { _nativeWarm?.Dispose(); }
        finally { try { _nativeCool?.Dispose(); } finally { _warmK3.Dispose(); _coolK3.Dispose(); } }
    }
    private byte ReadDistancePixel(int x,int y,byte kind)
    {
        var native=kind==SemanticStructureContours.Warm ? _nativeWarm : _nativeCool;
        return native is not null ? native.AtCompact(x,y)
            : (kind==SemanticStructureContours.Warm ? _warm! : _cool!).AtCompact(x,y);
    }

    internal SemanticCorrelationBits BitsK3(byte kind) =>
        kind == SemanticStructureContours.Warm ? _warmK3 : _coolK3;

    internal double Distance(double x, double y, byte kind)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)
            || x < 0 || y < 0 || x >= Width - 1 || y >= Height - 1)
            return byte.MaxValue;
        var ix = (int)x; var iy = (int)y;
        var fx = x - ix; var fy = y - iy;
        var native = kind == SemanticStructureContours.Warm ? _nativeWarm : _nativeCool;
        if (native is not null)
        {
            var quad = native.ReadQuad(ix,iy);
            return ((byte)quad * (1-fx) + (byte)(quad>>8) * fx) * (1-fy)
                + ((byte)(quad>>16) * (1-fx) + (byte)(quad>>24) * fx) * fy;
        }
        var field = kind == SemanticStructureContours.Warm ? _warm! : _cool!;
        return (field.AtCompact(ix, iy) * (1 - fx) + field.AtCompact(ix + 1, iy) * fx) * (1 - fy)
            + (field.AtCompact(ix, iy + 1) * (1 - fx) + field.AtCompact(ix + 1, iy + 1) * fx) * fy;
    }

    private static SemanticDistanceField? BuildSupport(Mat labels, byte kind, bool prepareBits,
        SemanticDistanceField? reuse,
        out SemanticCorrelationBits bits, bool nativeStorage,
        NativeSemanticField? nativeReuse, out NativeSemanticField? nativeField)
    {
        var resource = kind == SemanticStructureContours.Warm ? "Semantic.Warm.Distance" : "Semantic.Cool.Distance";
        var bitResource = kind == SemanticStructureContours.Warm ? "Semantic.Warm.K3" : "Semantic.Cool.K3";
        bits = SemanticCorrelationBits.Empty;
        nativeField = null;
        ScanDistanceRaster.EncodedRaster? encoded = null;
        try
        {
            using (var mask = new Mat())
            {
                Cv2.InRange(labels, new Scalar(kind), new Scalar(kind), mask);
                if (Cv2.CountNonZero(mask) == 0)
                {
                    // Preserve absent-class handling without constructing a raster.
                    if (prepareBits) bits = SemanticCorrelationBits.Zero(checked(((labels.Width + 63) / 64) * labels.Height), nativeStorage);
                    if (nativeStorage)
                    {
                        nativeField = nativeReuse?.HasSameConstantPixelsAs(byte.MaxValue, labels.Width, labels.Height) == true
                            ? nativeReuse.Clone() : NativeSemanticField.Filled(labels.Width, labels.Height, byte.MaxValue);
                        return null;
                    }
                    if (reuse?.HasSameConstantPixelsAs(byte.MaxValue, labels.Width, labels.Height) == true) return reuse;
                    return SemanticDistanceField.Filled(labels.Width, labels.Height, byte.MaxValue, resource);
                }
                // The exact integer metric needs one byte per pixel, without
                // inverse and float rasters. Finish borrowing the mask here.
                encoded = ScanDistanceRaster.EncodeChebyshev(mask, resource + ".Raster");
            }
            var quantized = encoded.Image;
            if (!quantized.IsContinuous()) throw new InvalidOperationException("语义距离场必须连续存储。");
            // Compression consumes the native raster synchronously and owns its
            // resulting bytes. No full managed scratch copy survives each floor.
            var pixels = MemoryMarshal.CreateReadOnlySpan(ref quantized.At<byte>(0, 0), checked(labels.Width * labels.Height));
            if (prepareBits) bits = SemanticCorrelationBits.Create(pixels, labels.Width, labels.Height, 1, bitResource, nativeStorage);
            if (nativeStorage)
            {
                nativeField = NativeSemanticField.Create(pixels, labels.Width, labels.Height, resource, nativeReuse);
                return null;
            }
            return SemanticDistanceField.Create(pixels, labels.Width, labels.Height, resource, reuse);
        }
        finally { encoded?.Dispose(); }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool IsHit(int x, int y, byte kind, double maxDistance = 2d)
    {
        if ((uint)x >= Width || (uint)y >= Height
            || kind is not (SemanticStructureContours.Warm or SemanticStructureContours.Cool)) return false;
        var distance = ReadDistancePixel(x, y, kind);
        // Saturated distance also represents a missing class and never supplies evidence.
        return distance < byte.MaxValue && distance <= maxDistance;
    }

    public SemanticStructureEvidence Verify(Mat? labels, Mat? validMask, double scale,
        double offsetX, double offsetY, MapScreenRect viewport, Func<double, double, bool>? isUnknown = null)
    {
        if (labels is null || labels.Empty() || !double.IsFinite(scale) || scale <= 0
            || !double.IsFinite(offsetX) || !double.IsFinite(offsetY))
            return new(false, 0, 0, 0, "semantic-evidence-missing");
        return VerifySamples(SemanticStructureSamples.Create(labels, validMask),
            scale, offsetX, offsetY, viewport, isUnknown);
    }

    public SemanticStructureEvidence VerifyObservation(Vpsg3LiveObservation observation, double scale,
        double offsetX, double offsetY, MapScreenRect viewport, Func<double, double, bool>? isUnknown = null) =>
        VerifySamples(observation.SemanticSamples, scale, offsetX, offsetY, viewport, isUnknown);

    private SemanticStructureEvidence VerifySamples(SemanticStructureSamples samples, double scale,
        double offsetX, double offsetY, MapScreenRect viewport, Func<double, double, bool>? isUnknown)
    {
        if (!samples.HasLabels || !double.IsFinite(scale) || scale <= 0
            || !double.IsFinite(offsetX) || !double.IsFinite(offsetY))
            return new(false, 0, 0, 0, "semantic-evidence-missing");
        Span<int> tested = stackalloc int[2];
        Span<int> hits = stackalloc int[2];
        Span<int> conflicts = stackalloc int[2];
        tested.Clear(); hits.Clear(); conflicts.Clear();
        var supportRadius = Math.Max(2d, 2d / scale);
        for (var cls = 0; cls < 2; cls++)
        {
            var kind = cls == 0 ? SemanticStructureContours.Cool : SemanticStructureContours.Warm;
            foreach (var point in samples.GetPoints(cls))
            {
                var rx = (point.X + viewport.X - offsetX) / scale;
                var ry = (point.Y + viewport.Y - offsetY) / scale;
                if (isUnknown?.Invoke(rx, ry) == true) continue;
                var x = (int)Math.Round(rx); var y = (int)Math.Round(ry);
                tested[cls]++;
                if (IsHit(x, y, kind, supportRadius)) hits[cls]++;
                else if (IsHit(x, y, cls == 0 ? SemanticStructureContours.Warm : SemanticStructureContours.Cool, supportRadius)) conflicts[cls]++;
            }
        }
        var total = tested[0] + tested[1];
        var enough = total >= 24;
        // No amount of matching white geometry can wash out an orange conflict.
        var conflict = false; var supported = true;
        for (var c = 0; c < 2; c++)
        {
            conflict |= conflicts[c] >= 3 && conflicts[c] > tested[c] * .06;
            supported &= tested[c] < 12 || hits[c] >= tested[c] * .45;
        }
        return new(enough && supported && !conflict, total, hits[0] + hits[1], conflicts[0] + conflicts[1],
            conflict ? "semantic-color-conflict" : !enough ? "semantic-evidence-insufficient" : !supported ? "semantic-support-insufficient" : "semantic-supported");
    }
}

/// <summary>Frame-owned samples in the original FindNonZero order. Only sampling is
/// cached: transform-dependent unknown regions and class conflicts are checked anew.</summary>
internal sealed class SemanticStructureSamples(bool hasLabels, Point[] cool, Point[] warm)
{
    internal bool HasLabels { get; } = hasLabels;
    internal ReadOnlySpan<Point> GetPoints(int cls) => cls == 0 ? cool : warm;

    internal static SemanticStructureSamples Create(Mat? labels, Mat? validMask)
    {
        if (labels is null || labels.Empty()) return new(false, [], []);
        using var visible = labels.Clone();
        if (validMask is not null)
        {
            using var invalid = new Mat();
            Cv2.BitwiseNot(validMask, invalid);
            visible.SetTo(0, invalid);
        }
        Point[] Sample(byte kind)
        {
            using var mask = new Mat();
            Cv2.InRange(visible, new Scalar(kind), new Scalar(kind), mask);
            using var points = mask.FindNonZero();
            var count = checked((int)points.Total());
            var selected = new Point[Math.Min(512, count)];
            for (var i = 0; i < selected.Length; i++)
                selected[i] = points.At<Point>(i * count / selected.Length);
            return selected;
        }
        return new(true, Sample(SemanticStructureContours.Cool), Sample(SemanticStructureContours.Warm));
    }
}

public sealed record SemanticStructureEvidence(bool Accepted, int Tested, int Hits, int Conflicts, string Reason);

/// <summary>An independent reader of exact immutable semantic correlation words.
/// Native readers share storage through leases; the managed fallback remains available.</summary>
internal sealed class SemanticCorrelationBits : IDisposable
{
    private readonly IDVBuff.Core.Collections.CompactBitWords? _managed;
    private readonly NativeCompactBitWords? _native;
    private SemanticCorrelationBits(IDVBuff.Core.Collections.CompactBitWords managed) => _managed = managed;
    private SemanticCorrelationBits(NativeCompactBitWords native) => _native = native;
    internal static SemanticCorrelationBits Empty { get; } = new(IDVBuff.Core.Collections.CompactBitWords.Empty);
    internal bool UsesNativeStorage => _native is not null;
    internal NativeCompactBitWords? NativeWords => _native;
    internal IDVBuff.Core.Collections.CompactBitWords? ManagedWords => _managed;
    public int Length => _native?.Length ?? _managed!.Length;
    public long MemoryBytes => _native?.MemoryBytes ?? _managed!.MemoryBytes;
    public ulong this[int index]
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        get => _native is not null ? _native[index] : _managed![index];
    }
    internal static SemanticCorrelationBits Zero(int length, bool native) => native
        ? new(NativeCompactBitWords.Zero(length))
        : new(IDVBuff.Core.Collections.CompactBitWords.Zero(length));
    internal static SemanticCorrelationBits Create(ReadOnlySpan<byte> raster,
        int width, int height, byte threshold, string resource, bool native) => native
        ? new(NativeCompactBitWords.Create(raster, width, height, threshold, resource))
        : new(new IDVBuff.Core.Collections.CompactBitWords(raster, width, height, threshold, resource));
    internal SemanticCorrelationBits BorrowReader() => _native is null ? this : new(_native.Clone());
    public void Dispose() => _native?.Dispose();
}
