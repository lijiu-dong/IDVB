// Types split into:
//   MapStructureRegistrationModels.Enums.cs  – MapStructureRejectionReason, MapStructureEvidenceDisposition, MapStructureRejectionReasonExtensions
//   MapStructureRegistrationModels.Tuning.cs – MapStructureRegistrationTuning
//   MapStructureRegistrationModels.Request.cs – MapStructureRegistrationRequest
//   MapStructureRegistrationModels.Result.cs  – MapStructureCandidate, MapStructureRegistrationResult
//   MapStructureRegistrationModels.Detail.cs  – MapStructureConfidenceBreakdown, MapStructureConfidenceCalculator
using OpenCvSharp;
using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class MapStructureFeatures : IDisposable
{
    private readonly object _visibleAwareCacheSync = new();
    private readonly Dictionary<int, Mat> _visibleAwareStructureCache = new();
    private Mat _nuisanceMask;
    private Mat _repeatedRegionMask;
    private bool _deferredZeroMasks;
    private bool _disposed;
    public MapStructureFeatures(
        Mat nuisanceMask,
        Mat structureMask,
        Mat edges,
        Mat? referenceDistanceMap = null,
        Mat? clippedReferenceDistanceMap = null,
        double? clippedDistancePixels = null,
        Mat? normalizedGray = null,
        IReadOnlyList<Mat>? edgePyramid = null,
        KeyPoint[]? keyPoints = null,
        Mat? descriptors = null,
        Mat? repeatedRegionMask = null,
        PreprocessTiming? diagnosticTiming = null,
        Mat? rawVisibleMask = null, Mat? semanticLabels = null, SemanticStructureIndex? semanticIndex = null,
        bool deferZeroMasks = false)
    {
        if (deferZeroMasks && (!nuisanceMask.Empty()
            || repeatedRegionMask is null || !repeatedRegionMask.Empty()))
            throw new ArgumentException("Deferred zero masks require two empty markers.", nameof(deferZeroMasks));
        _nuisanceMask = nuisanceMask;
        _deferredZeroMasks = deferZeroMasks;
        StructureMask = structureMask;
        Edges = edges;
        ReferenceDistanceMap = referenceDistanceMap;
        ClippedReferenceDistanceMap = clippedReferenceDistanceMap;
        ClippedDistancePixels = clippedDistancePixels;
        NormalizedGray = normalizedGray ?? new Mat();
        EdgePyramid = edgePyramid ?? [];
        KeyPoints = keyPoints ?? [];
        Descriptors = descriptors ?? new Mat();
        _repeatedRegionMask = repeatedRegionMask ?? Mat.Zeros(
            edges.Size(),
            MatType.CV_8UC1).ToMat();
        DiagnosticTiming = diagnosticTiming;
        RawVisibleMask = rawVisibleMask;
        SemanticLabels = semanticLabels;
        SemanticIndex = semanticIndex;
        if (MemoryJournal.Current is { } journal)
        {
            var request = MemoryJournal.Request("Structure.FeatureOwner", nativeModule: "OpenCvSharpExtern.dll");
            MemoryJournal.TrackResource(this, "Structure.FeatureOwner", null, "OpenCvSharpExtern.dll", request);
            var buffers = new[] { _nuisanceMask, StructureMask, Edges, ReferenceDistanceMap, ClippedReferenceDistanceMap,
                NormalizedGray, Descriptors, _repeatedRegionMask, RawVisibleMask, SemanticLabels }.Concat(EdgePyramid)
                .Where(mat => mat is not null && !mat.Empty()).Select(mat => new
                {
                    address = mat!.Data.ToInt64(), bytes = checked(mat.Total() * mat.ElemSize()),
                    width = mat.Width, height = mat.Height, type = mat.Type().ToString()
                }).GroupBy(buffer => buffer.address).Select(group => group.First()).ToArray();
            journal.Record("native-feature-buffers", new { request, buffers, payloadBytes = buffers.Sum(buffer => buffer.bytes),
                meaning = "owned raster buffers already created by preprocessing; excludes native allocator overhead and is not process residency" });
        }
    }

    public Mat? SemanticLabels { get; }
    public SemanticStructureIndex? SemanticIndex { get; }
    public Mat NuisanceMask
    {
        get { if (Volatile.Read(ref _deferredZeroMasks)) MaterializeZeroMasks(); return _nuisanceMask; }
    }
    public Mat StructureMask { get; }
    public Mat Edges { get; }
    public Mat? ReferenceDistanceMap { get; private set; }
    public Mat? ClippedReferenceDistanceMap { get; private set; }
    public double? ClippedDistancePixels { get; private set; }
    public Mat NormalizedGray { get; }
    public IReadOnlyList<Mat> EdgePyramid { get; }
    public KeyPoint[] KeyPoints { get; }
    public Mat Descriptors { get; }
    public Mat RepeatedRegionMask
    {
        get { if (Volatile.Read(ref _deferredZeroMasks)) MaterializeZeroMasks(); return _repeatedRegionMask; }
    }
    public PreprocessTiming? DiagnosticTiming { get; }
    public Mat? RawVisibleMask { get; }

    // Immutable prebuilt references have no nuisance or repeated regions.
    // Most consumers need only edges/labels; compatibility copies and feature
    // consumers still receive the exact full-size zero masks on first access.
    private void MaterializeZeroMasks()
    {
        lock (_visibleAwareCacheSync)
        {
            if (!_deferredZeroMasks) return;
            ObjectDisposedException.ThrowIf(_disposed, this);
            var request = MemoryJournal.Request("Structure.Reference.ZeroMasks", Edges.Total(), "OpenCvSharpExtern.dll");
            var zeros = new Mat(Edges.Size(), MatType.CV_8UC1, Scalar.Black);
            Mat repeated;
            try { repeated = new Mat(zeros, new Rect(0, 0, zeros.Width, zeros.Height)); }
            catch { zeros.Dispose(); throw; }
            _nuisanceMask.Dispose();
            _repeatedRegionMask.Dispose();
            _nuisanceMask = zeros;
            _repeatedRegionMask = repeated;
            MemoryJournal.TrackResource(zeros, "Structure.Reference.ZeroMasks", zeros.Total(), "OpenCvSharpExtern.dll", request);
            Volatile.Write(ref _deferredZeroMasks, false);
        }
    }

    internal Mat GetOrCreateUnitStructureMask(int factor)
    {
        lock (_visibleAwareCacheSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1);
            if (_visibleAwareStructureCache.TryGetValue(factor, out var cached)) return cached;
            using var operation = MemoryJournal.BeginOperation("Structure.Reference.UnitMask", $"factor={factor}");
            var size = new Size(Math.Max(1, StructureMask.Width / factor), Math.Max(1, StructureMask.Height / factor));
            var resource = $"Structure.Reference.UnitMask/{factor}";
            var request = MemoryJournal.Request(resource, checked((long)size.Width * size.Height * sizeof(float)), "OpenCvSharpExtern.dll");
            var result = new Mat();
            try
            {
                if (factor == 1) StructureMask.ConvertTo(result, MatType.CV_32FC1, 1d / 255d);
                else
                {
                    var scratchRequest = MemoryJournal.Request("Structure.Reference.UnitMaskScratch",
                        checked(StructureMask.Total() * sizeof(float)), "OpenCvSharpExtern.dll");
                    var unit = new Mat();
                    try
                    {
                        StructureMask.ConvertTo(unit, MatType.CV_32FC1, 1d / 255d);
                        MemoryJournal.TrackResource(unit, "Structure.Reference.UnitMaskScratch",
                            checked(unit.Total() * sizeof(float)), "OpenCvSharpExtern.dll", scratchRequest);
                        Cv2.Resize(unit, result, size, 0d, 0d, InterpolationFlags.Area);
                    }
                    finally { unit.Dispose(); MemoryJournal.ReleaseResource(unit); }
                }
                MemoryJournal.TrackResource(result, resource, checked(result.Total() * sizeof(float)), "OpenCvSharpExtern.dll", request);
                _visibleAwareStructureCache.Add(factor, result);
                return result;
            }
            catch { result.Dispose(); MemoryJournal.ReleaseResource(result); throw; }
        }
    }

    /// <summary>按需创建匹配用的腐蚀掩码。调用者负责释放。</summary>
    public Mat? CreateSafeVisibleMask(int erodePixels = 1)
    {
        if (RawVisibleMask is null || RawVisibleMask.Empty())
            return null;
        var safe = new Mat();
        var kernel = Cv2.GetStructuringElement(
            MorphShapes.Rect,
            new Size(1 + erodePixels * 2, 1 + erodePixels * 2));
        Cv2.Erode(RawVisibleMask, safe, kernel);
        return safe;
    }

    public Mat GetOrCreateReferenceDistanceMap()
    {
        if (ReferenceDistanceMap is { } existing && !existing.Empty())
            return existing;
        using var inverse = new Mat();
        Cv2.BitwiseNot(Edges, inverse);
        var distance = new NativeDistanceMat();
        var request = MemoryJournal.Request("Structure.Reference.Distance", checked(Edges.Total() * sizeof(float)), "OpenCvSharpExtern.dll");
        try
        {
            Cv2.DistanceTransform(inverse, distance, DistanceTypes.L2, DistanceTransformMasks.Precise);
            distance.AccountOwnedPixels();
        }
        catch { distance.Dispose(); throw; }
        MemoryJournal.TrackResource(distance, "Structure.Reference.Distance", checked(distance.Total() * distance.ElemSize()), "OpenCvSharpExtern.dll", request);
        ReferenceDistanceMap = distance;
        return distance;
    }

    /// <summary>
    /// 返回按 <paramref name="clipPixels"/> 裁剪的参考距离图。返回的 Mat 由本
    /// 对象持有，调用方不得释放；且用**不同** clip 再次调用会释放并重建它，
    /// 使先前返回的引用失效。结构配准全程只用 tuning.DistanceClipPixels 这一个
    /// clip（Normalize 已把它钳在 [3,50]），据此可以在整轮搜索中安全持有引用。
    /// </summary>
    public Mat GetOrCreateClippedReferenceDistanceMap(double clipPixels)
    {
        lock (_visibleAwareCacheSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return GetOrCreateClippedReferenceDistanceMapCore(clipPixels);
        }
    }

    private Mat GetOrCreateClippedReferenceDistanceMapCore(double clipPixels)
    {
        if (ClippedReferenceDistanceMap is { } existing
            && !existing.Empty()
            && ClippedDistancePixels is { } existingClip
            && Math.Abs(existingClip - clipPixels) < 0.0001d)
        {
            return existing;
        }
        if (_compactClippedDistance is not null
            && ClippedDistancePixels is { } packedClip
            && Math.Abs(packedClip - clipPixels) < 0.0001d)
        {
            using var operation = MemoryJournal.BeginOperation("Structure.Reference.ClippedDistance.Restore");
            ClippedReferenceDistanceMap = _compactClippedDistance.ReadOwned(
                new Rect(0, 0, _compactClippedDistance.Width, _compactClippedDistance.Height),
                "Structure.Reference.ClippedDistance");
            return ClippedReferenceDistanceMap;
        }
        _compactClippedDistance?.Dispose();
        _compactClippedDistance = null;
        ClippedReferenceDistanceMap?.Dispose();
        if (ClippedReferenceDistanceMap is not null) MemoryJournal.ReleaseResource(ClippedReferenceDistanceMap);
        ClippedReferenceDistanceMap = null;
        ClippedDistancePixels = null;
        var request = MemoryJournal.Request("Structure.Reference.ClippedDistance", checked(Edges.Total() * sizeof(float)), "OpenCvSharpExtern.dll");
        // A rental's scoring consumers request only the clipped field. Build
        // that owned field directly, rather than retaining an unused full-size
        // unclipped field as well. Explicit unclipped/compatibility consumers
        // still get their independent field and this path clones it unchanged.
        var distance = new NativeDistanceMat();
        try
        {
            if (ReferenceDistanceMap is { } full && !full.Empty()) full.CopyTo(distance);
            if (distance.Empty())
            {
                using var inverse = new Mat();
                Cv2.BitwiseNot(Edges, inverse);
                Cv2.DistanceTransform(inverse, distance, DistanceTypes.L2, DistanceTransformMasks.Precise);
            }
            Cv2.Min(distance, clipPixels, distance);
            distance.AccountOwnedPixels();
        }
        catch { distance.Dispose(); throw; }
        MemoryJournal.TrackResource(distance, "Structure.Reference.ClippedDistance", checked(distance.Total() * distance.ElemSize()), "OpenCvSharpExtern.dll", request);
        ClippedReferenceDistanceMap = distance;
        ClippedDistancePixels = clipPixels;
        return distance;
    }

    public MapStructureFeatures Clone()
    {
        lock (_visibleAwareCacheSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var owned = new List<Mat>();
            Mat Copy(Mat mat) { var copy=mat.Clone(); try { owned.Add(copy); return copy; } catch { copy.Dispose(); throw; } }
            Mat? CopyOptional(Mat? mat) => mat is null ? null : Copy(mat);
            var semantic = SemanticIndex?.BorrowReader();
            try
            {
                return new(Copy(NuisanceMask), Copy(StructureMask), Copy(Edges),
                    CopyOptional(ReferenceDistanceMap), CopyOptional(ClippedReferenceDistanceMap), ClippedDistancePixels,
                    Copy(NormalizedGray), EdgePyramid.Select(Copy).ToArray(), KeyPoints.ToArray(),
                    Copy(Descriptors), Copy(RepeatedRegionMask), rawVisibleMask: CopyOptional(RawVisibleMask),
                    semanticLabels: CopyOptional(SemanticLabels), semanticIndex: semantic);
            }
            catch { foreach(var mat in owned) mat.Dispose(); semantic?.Dispose(); throw; }
        }
    }

    public void Dispose()
    {
        lock (_visibleAwareCacheSync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var cached in _visibleAwareStructureCache.Values)
            { cached.Dispose(); MemoryJournal.ReleaseResource(cached); }
            _visibleAwareStructureCache.Clear();
            foreach (var cached in _semanticDistances.Values) cached.Dispose();
            _semanticDistances.Clear();
            _compactClippedDistance?.Dispose();
            _compactClippedDistance = null;
            _nuisanceMask.Dispose();
            _repeatedRegionMask.Dispose();
            MemoryJournal.ReleaseResource(_nuisanceMask);
        }
        StructureMask.Dispose();
        Edges.Dispose();
        ReferenceDistanceMap?.Dispose();
        ClippedReferenceDistanceMap?.Dispose();
        if (ReferenceDistanceMap is not null) MemoryJournal.ReleaseResource(ReferenceDistanceMap);
        if (ClippedReferenceDistanceMap is not null) MemoryJournal.ReleaseResource(ClippedReferenceDistanceMap);
        NormalizedGray.Dispose();
        foreach (var level in EdgePyramid)
            level.Dispose();
        Descriptors.Dispose();
        RawVisibleMask?.Dispose();
        SemanticLabels?.Dispose();
        SemanticIndex?.Dispose();
        MemoryJournal.ReleaseResource(this);
    }
}
/*
 * 文件职责：MapStructureRegistrationModels。
 * 所属模块：Features/Maps，主要负责地图结构特征注册、候选评估与验证。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
