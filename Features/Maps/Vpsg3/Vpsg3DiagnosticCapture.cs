using OpenCvSharp;
using System.Diagnostics;
using IDVBuff.Core.Imaging;

namespace IDVBuff.Features.Maps;

/// <summary>
/// VPSG 3.0 诊断捕获服务。
/// 直接复用对齐阶段已提取的实时结构线（observation.ObservedEdges），
/// 仅在诊断模式开启时按需生成参考预制线的仿射投影贴合重叠图。
/// </summary>
internal readonly record struct Vpsg3DiagnosticTiming(double InputWriteMs, double SemanticWriteMs,
    double OverlayMs, double FitnessWriteMs, double EnqueueMs = 0d, bool Deferred = false);

internal static class Vpsg3DiagnosticCapture
{
    /// <summary>
    /// 若诊断模式处于激活状态，输出结构配准图（直接复用）与贴合重叠图（按需生成）。
    /// </summary>
    internal static Vpsg3DiagnosticTiming CaptureIfActive(
        int? attemptId,
        Vpsg3LiveObservation observation,
        string? referencePath,
        Vpsg3BootstrapResult result,
        string? tag = null, Vpsg3PreparedFloor? preparedFloor = null)
    {
        if (!MapDiagnosticModeCapture.IsAlignmentCaptureActive || observation is null || result is null)
            return default;

        var started = Stopwatch.GetTimestamp();
        try
        {
            CaptureSnapshot? snapshot = null;
            var deferred = MapDiagnosticModeCapture.QueueAlignmentCapture(attemptId, (directory, id) =>
                snapshot = CaptureSnapshot.Create(directory, id, tag, observation, referencePath, result, preparedFloor));
            if (deferred is null) return default;
            // Background stage timings are not alignment-thread timings. Only a
            // full queue executes them here, preserving the existing overflow policy.
            var timing = deferred.Value ? default : snapshot!.Timing;
            return timing with { EnqueueMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Deferred = deferred.Value };
        }
        catch
        {
            // 诊断捕获决不能影响对齐结果或抛出未捕获异常
        }
        return default;
    }

    /// <summary>
    /// 按需生成贴合重叠图：将参考预制线投影至视口空间，
    /// 绿色=实时结构线，红色=投影参考线，黄色=贴合重合部分。
    /// </summary>
    internal static Mat? CreateFittedOverlay(
        Vpsg3LiveObservation observation,
        string? referencePath,
        Vpsg3BootstrapResult result, Vpsg3PreparedFloor? preparedFloor = null)
    {
        if (observation.ObservedEdges.Empty())
            return null;
        try
        {
            using var reference = !string.IsNullOrEmpty(referencePath) && File.Exists(referencePath)
                ? ReadReferenceEdges(referencePath, preparedFloor) : null;
            return CreateFittedOverlay(observation.ObservedEdges, observation.ViewportBounds, reference, result);
        }
        catch { return null; }
    }

    private static Mat? CreateFittedOverlay(Mat observedEdges, MapScreenRect bounds,
        Mat? referenceEdges, Vpsg3BootstrapResult result)
    {
        if (observedEdges.Empty()) return null;
        var size = observedEdges.Size();

        if (referenceEdges is null)
        {
            var missingVisual = new Mat(size, MatType.CV_8UC3, Scalar.Black);
            missingVisual.SetTo(new Scalar(0, 170, 0), observedEdges);
            Cv2.PutText(
                missingVisual,
                $"VPSG3: Reference line missing ({result.FallbackReason})",
                new Point(10, 25),
                HersheyFonts.HersheySimplex,
                0.6,
                Scalar.OrangeRed,
                2);
            return missingVisual;
        }

        try
        {
            if (referenceEdges.Empty())
                return null;

            if (result.ScaleResult.Success)
            {
                var scale = result.Scale;
                var tx = result.OffsetX - bounds.X;
                var ty = result.OffsetY - bounds.Y;

                using var matrix = new Mat(2, 3, MatType.CV_64FC1);
                matrix.Set(0, 0, scale);
                matrix.Set(0, 1, 0d);
                matrix.Set(0, 2, tx);
                matrix.Set(1, 0, 0d);
                matrix.Set(1, 1, scale);
                matrix.Set(1, 2, ty);

                using var projected = new Mat();
                Cv2.WarpAffine(
                    referenceEdges,
                    projected,
                    matrix,
                    size,
                    InterpolationFlags.Area,
                    BorderTypes.Constant,
                    Scalar.Black);
                Cv2.Threshold(projected, projected, 127d, 255d, ThresholdTypes.Binary);

                var visual = new Mat(size, MatType.CV_8UC3, Scalar.Black);
                visual.SetTo(new Scalar(0, 170, 0), observedEdges); // 实时边缘：绿
                visual.SetTo(new Scalar(0, 0, 220), projected);                // 投影参考：红
                using var overlap = new Mat();
                Cv2.BitwiseAnd(observedEdges, projected, overlap);
                visual.SetTo(new Scalar(0, 255, 255), overlap);                 // 重叠区域：黄

                var statusText = result.IsAccepted
                    ? $"VPSG3 Accepted: s={result.Scale:F4} m={result.ApertureMargin:F3} c={result.Confidence:P0}"
                    : $"VPSG3 {(result.ScaleResult.Success ? "Rejected" : "ScaleFailed")}: {result.FallbackReason}";

                Cv2.PutText(
                    visual,
                    statusText,
                    new Point(10, 25),
                    HersheyFonts.HersheySimplex,
                    0.6,
                    result.IsAccepted ? Scalar.LimeGreen : Scalar.OrangeRed,
                    2);

                return visual;
            }
            else
            {
                var visual = new Mat(size, MatType.CV_8UC3, Scalar.Black);
                visual.SetTo(new Scalar(0, 170, 0), observedEdges);
                Cv2.PutText(
                    visual,
                    $"VPSG3 Scale Failed: {result.ScaleResult.RejectReason}",
                    new Point(10, 25),
                    HersheyFonts.HersheySimplex,
                    0.6,
                    Scalar.OrangeRed,
                    2);
                return visual;
            }
        }
        catch
        {
            return null;
        }
    }

    private static Mat ReadReferenceEdges(string referencePath, Vpsg3PreparedFloor? preparedFloor)
    {
        // The caller holds the same immutable floor revision as the solver.
        // Retain its precision buffer independently for this synchronous read;
        // retirement may detach cache ownership but cannot free these pixels.
        using var precision = preparedFloor?.RentPrecisionDistance();
        if (precision is not null)
            return NativeDistanceMetrics.ZeroMask(precision.Span,
                preparedFloor!.ReferenceWidth, preparedFloor.ReferenceHeight);
        using var referenceImage = Cv2.ImRead(referencePath, ImreadModes.Unchanged);
        return SemanticStructureContours.Geometry(referenceImage);
    }

    private sealed class CaptureSnapshot(string structurePath, string semanticPath, string fitnessPath,
        Mat edges, Mat? labels, MapScreenRect bounds, Vpsg3BootstrapResult result,
        Vpsg3PreparedFloor.PrecisionDistanceLease? precision, int referenceWidth, int referenceHeight,
        Mat? referenceEdges, IDisposable protection) : MapDiagnosticModeCapture.PendingWrite
    {
        internal override int ImageCount => labels is null ? 2 : 3;
        internal Vpsg3DiagnosticTiming Timing { get; private set; }

        internal static CaptureSnapshot Create(string directory, int id, string? tag,
            Vpsg3LiveObservation observation, string? referencePath, Vpsg3BootstrapResult result,
            Vpsg3PreparedFloor? floor)
        {
            Mat? edges = null, labels = null, reference = null;
            Vpsg3PreparedFloor.PrecisionDistanceLease? precision = null;
            IDisposable? protection = null;
            var transferred = false;
            try
            {
                protection = AppDataPaths.ProtectCachePath(directory);
                edges = observation.ObservedEdges.Clone();
                labels = observation.SemanticLabels?.Clone();
                if (!string.IsNullOrEmpty(referencePath) && File.Exists(referencePath))
                {
                    // Retain the exact immutable precision revision independently
                    // of the floor/registry. No frame or floor owner escapes here.
                    precision = floor?.RentPrecisionDistance();
                    if (precision is null) reference = ReadReferenceEdges(referencePath, null);
                }
                var suffix = string.IsNullOrWhiteSpace(tag) ? string.Empty : $"_{tag}";
                var semanticTag = string.IsNullOrWhiteSpace(tag) ? "semantic" : tag + "_semantic";
                var snapshot = new CaptureSnapshot(
                    Path.Combine(directory, "结构配准", $"结构配准 {id}{suffix}.png"),
                    Path.Combine(directory, "结构配准", $"结构配准 {id}_{semanticTag}.png"),
                    Path.Combine(directory, "贴合度", $"贴合度 {id}{suffix}.png"),
                    edges, labels, observation.ViewportBounds, result, precision,
                    floor?.ReferenceWidth ?? 0, floor?.ReferenceHeight ?? 0, reference, protection);
                transferred = true;
                return snapshot;
            }
            finally
            {
                if (!transferred)
                {
                    edges?.Dispose(); labels?.Dispose(); reference?.Dispose();
                    precision?.Dispose(); protection?.Dispose();
                }
            }
        }

        internal override void Write()
        {
            var started = Stopwatch.GetTimestamp();
            MapDiagnosticModeCapture.TryWrite(structurePath, edges);
            var inputMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            started = Stopwatch.GetTimestamp();
            if (labels is not null)
            {
                using var semantic = SemanticStructureContours.Encode(labels);
                MapDiagnosticModeCapture.TryWrite(semanticPath, semantic);
            }
            var semanticMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            started = Stopwatch.GetTimestamp();
            using var reference = precision is not null
                ? NativeDistanceMetrics.ZeroMask(precision.Span, referenceWidth, referenceHeight) : null;
            using var overlay = CreateFittedOverlay(edges, bounds, reference ?? referenceEdges, result);
            var overlayMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            started = Stopwatch.GetTimestamp();
            if (overlay is not null) MapDiagnosticModeCapture.TryWrite(fitnessPath, overlay);
            Timing = new(inputMs, semanticMs, overlayMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        public override void Dispose()
        {
            try { edges.Dispose(); labels?.Dispose(); referenceEdges?.Dispose(); }
            finally { precision?.Dispose(); protection.Dispose(); }
        }
    }
}
