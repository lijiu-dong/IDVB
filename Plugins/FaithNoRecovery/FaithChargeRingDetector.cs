using OpenCvSharp;

namespace IDVBuff.Plugins.FaithNoRecovery;

/// <summary>一次蓄力环检测的结果：亮扇区数量 + 最亮的那个扇区高出背景多少灰度。</summary>
internal readonly record struct FaithRingObservation(int BrightSectors, double BestContrast)
{
    public bool IsPresent(int minimumSectors) => BrightSectors >= Math.Max(1, minimumSectors);
}

/// <summary>
/// 准心下方那个「蓄力环」的几何检测。
///
/// 环是一段下半圆（1920×1080 下圆心 ≈ (960,543.5)、半径 40–52px），
/// 蓄力过程里它会被逐段点亮：未满时只有右侧几段是亮的、其余是暗色轨道，蓄满时整段都白。
/// 因此**不能**靠「白色」判断在不在场（用户明确说过未蓄满时不是白的），
/// 而是把下半圆切成 24 个扇区，逐扇区比较「环带平均灰度 − 环外对照带平均灰度」：
///   · 实测未蓄满的截图：核心 10 个扇区里有 5 个对比度 ≈ 93–99；
///   · 实测蓄满的截图：10 个都有，对比度 ≈ 166–183；
///   · 把同一几何挪到画面其它位置（无环）：最多 1 个扇区超过 20。
/// 于是「亮扇区数 ≥ 2、单扇区对比度 &gt; 40」就是很干净的判据。
///
/// 0° 与 180° 两个扇区被排除：那里是准心自己的左右横杠，跟蓄力无关。
/// 半径按客户区高度缩放（HUD 随画面高度缩放），圆心横向固定在画面正中。
/// </summary>
internal sealed class FaithChargeRingDetector : IDisposable
{
    /// <summary>圆心纵向位置（1920×1080 实测 543.5）。</summary>
    private const double CenterYRatio = 543.5 / 1080d;

    private const double InnerRadiusRatio = 40d / 1080d;
    private const double OuterRadiusRatio = 52d / 1080d;
    private const double ControlGapRatio = 12d / 1080d;
    private const double ControlWidthRatio = 14d / 1080d;

    private const int SectorCount = 24;

    /// <summary>参与判定的扇区范围：略过 0°（右）与 180°（左）两个准心横杠所在的扇区。</summary>
    private const int FirstCoreSector = 1;
    private const int LastCoreSector = 10;

    private readonly List<Mat> _ringMasks = [];
    private readonly List<Mat> _controlMasks = [];
    private Size _maskSize;
    private bool _disposed;

    /// <summary>当前是否拿到了可用的扇区掩膜（画面尺寸异常时为 false）。</summary>
    public bool HasMasks => _ringMasks.Count > 0;

    public bool TryDetect(Mat grayFrame, int contrastThreshold, out FaithRingObservation observation)
    {
        observation = default;
        if (_disposed || grayFrame.Empty() || grayFrame.Channels() != 1)
            return false;

        EnsureMasks(grayFrame.Size());
        if (_ringMasks.Count == 0)
            return false;

        var bright = 0;
        var best = double.NegativeInfinity;
        for (var index = 0; index < _ringMasks.Count; index++)
        {
            var ringMean = Cv2.Mean(grayFrame, _ringMasks[index]).Val0;
            var controlMean = Cv2.Mean(grayFrame, _controlMasks[index]).Val0;
            var contrast = ringMean - controlMean;
            if (contrast > best)
                best = contrast;
            if (contrast > contrastThreshold)
                bright++;
        }

        observation = new FaithRingObservation(bright, double.IsFinite(best) ? best : 0d);
        return true;
    }

    private void EnsureMasks(Size size)
    {
        if (_maskSize == size && _ringMasks.Count > 0)
            return;

        ResetMasks();
        if (size.Width < 64 || size.Height < 64)
            return;

        _maskSize = size;
        var center = new Point(size.Width * 0.5, size.Height * CenterYRatio);
        var radiusInner = size.Height * InnerRadiusRatio;
        var radiusOuter = size.Height * OuterRadiusRatio;
        var radiusMiddle = (radiusInner + radiusOuter) / 2d;
        var thickness = Math.Max(3, (int)Math.Round(radiusOuter - radiusInner));

        var controlInner = radiusOuter + (size.Height * ControlGapRatio);
        var controlOuter = controlInner + (size.Height * ControlWidthRatio);
        var controlMiddle = (controlInner + controlOuter) / 2d;
        var controlThickness = Math.Max(3, (int)Math.Round(controlOuter - controlInner));

        for (var sector = FirstCoreSector; sector <= LastCoreSector; sector++)
        {
            var start = sector * 360d / SectorCount;
            var end = (sector + 1) * 360d / SectorCount;
            _ringMasks.Add(DrawArc(size, center, radiusMiddle, thickness, start, end));
            _controlMasks.Add(DrawArc(size, center, controlMiddle, controlThickness, start, end));
        }
    }

    private static Mat DrawArc(Size size, Point center, double radius, int thickness, double start, double end)
    {
        var mask = Mat.Zeros(size, MatType.CV_8UC1).ToMat();
        var axes = new Size(Math.Max(1, (int)Math.Round(radius)), Math.Max(1, (int)Math.Round(radius)));
        // OpenCV 的椭圆参数化是 (cx + rx·cos t, cy + ry·sin t)（y 向下），
        // 因此 t ∈ [0°,180°] 正好是下半圆，与实测的环位置一致。
        Cv2.Ellipse(mask, center, axes, 0, start, end, Scalar.All(255), thickness, LineTypes.Link8);
        return mask;
    }

    private void ResetMasks()
    {
        foreach (var mask in _ringMasks)
            mask.Dispose();
        foreach (var mask in _controlMasks)
            mask.Dispose();
        _ringMasks.Clear();
        _controlMasks.Clear();
        _maskSize = default;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ResetMasks();
    }
}
