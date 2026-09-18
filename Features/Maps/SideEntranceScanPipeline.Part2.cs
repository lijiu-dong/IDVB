using OpenCvSharp;
using IDVBuff.Pipeline;
using System.Runtime.InteropServices;

namespace IDVBuff.Features.Maps;

/// <summary>
/// 侧门专属扫描管线：基于二值结构线几何先验与稀疏边缘点检验的快速评价实现。
/// 彻底废除旧版滑窗灰度模板匹配，以门锚点推演平移残差与尺度网格，实现 &lt;1ms 级极速评价。
/// </summary>
public sealed partial class SideEntranceScanPipeline
{
    private static SideEntranceScanCandidate? EvaluateStructuralCandidate(
        MapRecord map,
        string floorKey,
        Mat template,
        IReadOnlyList<Point> sparsePoints,
        Mat validMask,
        double gx,
        double gy)
    {
        if (template.Empty() || sparsePoints.Count == 0)
            return null;

        var profile = MapFloorRules.GetFloorProfile(map, floorKey);
        var anchor = MapScanFloorRules.GetScanFeatureAnchor(map, floorKey);
        if (profile is null || anchor?.Bounds?.IsValid is not true
            || profile.RecognitionPixelWidth <= 0 || profile.RecognitionPixelHeight <= 0)
        {
            return null;
        }

        var anchorCenterX = (anchor.Bounds.X + anchor.Bounds.Width / 2d)
            * profile.RecognitionPixelWidth;
        var anchorCenterY = (anchor.Bounds.Y + anchor.Bounds.Height / 2d)
            * profile.RecognitionPixelHeight;

        var featureCenterX = profile.SideEntranceFeatureCenterX;
        var featureCenterY = profile.SideEntranceFeatureCenterY;
        if (!double.IsFinite(featureCenterX) || featureCenterX <= 0d
            || !double.IsFinite(featureCenterY) || featureCenterY <= 0d)
        {
            featureCenterX = anchorCenterX;
            featureCenterY = anchorCenterY;
        }

        var deltaAnchorRefX = anchorCenterX - featureCenterX;
        var deltaAnchorRefY = anchorCenterY - featureCenterY;

        var tplWidth = template.Width;
        var tplHeight = template.Height;

        var bestScore = double.NegativeInfinity;
        var bestScale = 1.0d;
        var bestDeltaX = 0d;
        var bestDeltaY = 0d;

        var minScale = SideEntranceScanRules.MinimumScale;
        var maxScale = SideEntranceScanRules.MaximumScale;
        var coarseStep = SideEntranceScanRules.CoarseScaleStep;

        // 仅保留实机门附近的边缘点，排除远端（如大门附近）无关点
        var maxExtentX = Math.Max(32d, (tplWidth / 2d) * maxScale);
        var maxExtentY = Math.Max(32d, (tplHeight / 2d) * maxScale);
        var localPoints = new List<(double rx, double ry)>(sparsePoints.Count);
        for (var i = 0; i < sparsePoints.Count; i++)
        {
            var rx = sparsePoints[i].X - gx;
            var ry = sparsePoints[i].Y - gy;
            if (Math.Abs(rx) <= maxExtentX && Math.Abs(ry) <= maxExtentY)
            {
                localPoints.Add((rx, ry));
            }
        }

        var localCount = localPoints.Count;
        if (localCount == 0)
            return null;

        var nPoints = localCount;
        var relX = new double[nPoints];
        var relY = new double[nPoints];
        for (var i = 0; i < nPoints; i++)
        {
            relX[i] = localPoints[i].rx;
            relY[i] = localPoints[i].ry;
        }

        // 多级膨胀构建距离衰减核（Cone Filter）：
        // 5x5 膨胀 (±2px) 基础捕获层，3x3 膨胀 (±1px) 梯度层，原始模板 (0px) 峰值层
        using var k3 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        using var dilated3 = new Mat();
        Cv2.Dilate(template, dilated3, k3);

        using var dilated5 = new Mat();
        Cv2.Dilate(dilated3, dilated5, k3);

        var step = (int)template.Step();
        var rawBytes = new byte[tplHeight * step];
        var d3Bytes = new byte[tplHeight * step];
        var d5Bytes = new byte[tplHeight * step];
        Marshal.Copy(template.Data, rawBytes, 0, rawBytes.Length);
        Marshal.Copy(dilated3.Data, d3Bytes, 0, d3Bytes.Length);
        Marshal.Copy(dilated5.Data, d5Bytes, 0, d5Bytes.Length);

        // 以 1.0d 为中心对齐网格，避免粗网格因浮点累积误差跨越 1.000 基准
        var scaleGrid = new List<double>(25);
        for (var s = 1.0d; s <= maxScale; s *= 1d + coarseStep)
            scaleGrid.Add(s);
        for (var s = 1.0d / (1d + coarseStep); s >= minScale; s /= 1d + coarseStep)
            scaleGrid.Add(s);
        scaleGrid.Sort();

        // 阶段一：粗尺度与整数残差搜索（覆盖门检测中心 [-3, 3] 像素内的所有整数偏移）
        for (var scaleIdx = 0; scaleIdx < scaleGrid.Count; scaleIdx++)
        {
            var scale = scaleGrid[scaleIdx];
            var invS = 1.0d / scale;
            var baseTx = (tplWidth / 2d) + deltaAnchorRefX;
            var baseTy = (tplHeight / 2d) + deltaAnchorRefY;

            for (var dx = -3; dx <= 3; dx++)
            {
                var shiftTx = baseTx - (dx * invS);
                for (var dy = -3; dy <= 3; dy++)
                {
                    var shiftTy = baseTy - (dy * invS);

                    var hit5 = 0;
                    var hit3 = 0;
                    var hit1 = 0;
                    var testedPoints = 0;

                    for (var i = 0; i < nPoints; i++)
                    {
                        var tx = shiftTx + (relX[i] * invS);
                        var ty = shiftTy + (relY[i] * invS);

                        var ix = (int)Math.Round(tx);
                        var iy = (int)Math.Round(ty);

                        if (ix >= 0 && ix < tplWidth && iy >= 0 && iy < tplHeight)
                        {
                            testedPoints++;
                            var offset = iy * step + ix;
                            if (d5Bytes[offset] > 128)
                            {
                                hit5++;
                                if (d3Bytes[offset] > 128)
                                {
                                    hit3++;
                                    if (rawBytes[offset] > 128)
                                        hit1++;
                                }
                            }
                        }
                    }

                    var supportFactor = Math.Min(1.0d, testedPoints / 18.0d);
                    var weightedHits = (hit5 * 0.4d) + (hit3 * 0.3d) + (hit1 * 0.3d);
                    var hitRatio = testedPoints > 0 ? weightedHits / testedPoints : 0d;
                    var score = hitRatio * supportFactor;

                    var isBetter = score > bestScore + 1e-4d ||
                        (Math.Abs(score - bestScore) <= 1e-4d &&
                         Math.Abs(scale - 1.0d) < Math.Abs(bestScale - 1.0d));

                    if (isBetter)
                    {
                        bestScore = score;
                        bestScale = scale;
                        bestDeltaX = dx;
                        bestDeltaY = dy;
                    }
                }
            }
        }

        // 阶段二：峰值附近的细化搜索
        if (bestScore > 0.3d)
        {
            var refineSteps = SideEntranceScanRules.RefineStepsPerSide;
            var fineStep = coarseStep / (refineSteps + 1d);
            var fineJitters = new (double dx, double dy)[]
            {
                (bestDeltaX, bestDeltaY),
                (bestDeltaX - 1, bestDeltaY), (bestDeltaX + 1, bestDeltaY),
                (bestDeltaX, bestDeltaY - 1), (bestDeltaX, bestDeltaY + 1),
                (bestDeltaX - 1, bestDeltaY - 1), (bestDeltaX + 1, bestDeltaY - 1),
                (bestDeltaX - 1, bestDeltaY + 1), (bestDeltaX + 1, bestDeltaY + 1)
            };

            for (var stepIdx = -refineSteps; stepIdx <= refineSteps; stepIdx++)
            {
                if (stepIdx == 0) continue;
                var scale = bestScale * (1d + stepIdx * fineStep);
                if (scale < minScale || scale > maxScale)
                    continue;

                var invS = 1.0d / scale;
                var baseTx = (tplWidth / 2d) + deltaAnchorRefX;
                var baseTy = (tplHeight / 2d) + deltaAnchorRefY;

                for (var j = 0; j < fineJitters.Length; j++)
                {
                    var (dx, dy) = fineJitters[j];
                    var shiftTx = baseTx - (dx * invS);
                    var shiftTy = baseTy - (dy * invS);

                    var hit5 = 0;
                    var hit3 = 0;
                    var hit1 = 0;
                    var testedPoints = 0;

                    for (var i = 0; i < nPoints; i++)
                    {
                        var tx = shiftTx + (relX[i] * invS);
                        var ty = shiftTy + (relY[i] * invS);

                        var ix = (int)Math.Round(tx);
                        var iy = (int)Math.Round(ty);

                        if (ix >= 0 && ix < tplWidth && iy >= 0 && iy < tplHeight)
                        {
                            testedPoints++;
                            var offset = iy * step + ix;
                            if (d5Bytes[offset] > 128)
                            {
                                hit5++;
                                if (d3Bytes[offset] > 128)
                                {
                                    hit3++;
                                    if (rawBytes[offset] > 128)
                                        hit1++;
                                }
                            }
                        }
                    }

                    var supportFactor = Math.Min(1.0d, testedPoints / 18.0d);
                    var weightedHits = (hit5 * 0.4d) + (hit3 * 0.3d) + (hit1 * 0.3d);
                    var hitRatio = testedPoints > 0 ? weightedHits / testedPoints : 0d;
                    var score = hitRatio * supportFactor;

                    var isBetter = score > bestScore + 1e-4d ||
                        (Math.Abs(score - bestScore) <= 1e-4d &&
                         Math.Abs(scale - 1.0d) < Math.Abs(bestScale - 1.0d));

                    if (isBetter)
                    {
                        bestScore = score;
                        bestScale = scale;
                        bestDeltaX = dx;
                        bestDeltaY = dy;
                    }
                }
            }
        }

        if (bestScore <= 0d || !double.IsFinite(bestScore))
            return null;

        var featureOriginX = featureCenterX - (tplWidth / 2d);
        var featureOriginY = featureCenterY - (tplHeight / 2d);
        var matchLocX = (gx + bestDeltaX) - ((anchorCenterX - featureOriginX) * bestScale);
        var matchLocY = (gy + bestDeltaY) - ((anchorCenterY - featureOriginY) * bestScale);
        var matchWidth = (int)Math.Round(tplWidth * bestScale);
        var matchHeight = (int)Math.Round(tplHeight * bestScale);

        return new SideEntranceScanCandidate
        {
            Map = map,
            FloorKey = floorKey,
            MatchScore = bestScore,
            MatchScale = bestScale,
            MatchLocation = new MapScreenRect(matchLocX, matchLocY, matchWidth, matchHeight),
            GateSpatialResidualPixels = Math.Sqrt(bestDeltaX * bestDeltaX + bestDeltaY * bestDeltaY),
            Disposition = SideEntranceCandidateDisposition.NeedsVerification
        };
    }
}
