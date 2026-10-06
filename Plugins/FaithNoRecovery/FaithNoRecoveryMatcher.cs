using OpenCvSharp;

namespace IDVBuff.Plugins.FaithNoRecovery;

/// <summary>一次命中的结果：相似度 + 模板在帧内的矩形 + 命中的是哪张模板。</summary>
internal readonly record struct FaithHintHit(double Score, Rect Box, double Scale, string Template)
{
    public double CenterX => Box.X + (Box.Width / 2d);

    public double CenterY => Box.Y + (Box.Height / 2d);
}

/// <summary>
/// 「点击ESC，取消操作」文案的多尺度模板匹配。基础缩放按「当前帧宽度 / 1920」推算，
/// 再在其上下各留两档（0.90–1.10），以同时适应窗口化与非 1080p 分辨率。
/// 两张模板取分数较高的一张，背景亮度变化时更稳。
/// </summary>
internal sealed class FaithNoRecoveryMatcher : IDisposable
{
    private static readonly double[] ScaleFactors = [0.90, 0.95, 1.00, 1.05, 1.10];

    private readonly Dictionary<string, Mat> _templates = new(StringComparer.Ordinal);
    private bool _disposed;

    public FaithNoRecoveryMatcher()
    {
        foreach (var name in FaithNoRecoveryTemplates.All)
            _templates[name] = FaithNoRecoveryTemplates.LoadGray(name);
    }

    public bool TryMatch(Mat grayFrame, double threshold, out FaithHintHit hit) =>
        MatchBest(grayFrame, out hit) >= threshold;

    /// <summary>
    /// 取最佳匹配（**不看阈值**）。诊断用：命中不了时也要知道"最像的那一次"是多少分，
    /// 否则日志里只有"无"，分不清是"阈值太高"还是"根本没这句文案"。
    /// 没有任何模板/画面为空时返回 0。
    /// </summary>
    public double MatchBest(Mat grayFrame, out FaithHintHit best)
    {
        best = default;
        if (_disposed || grayFrame.Empty() || _templates.Count == 0)
            return 0d;

        var region = FaithNoRecoveryTemplates.GetSearchRegion(grayFrame.Size());
        if (region.Width < 32 || region.Height < 32
            || region.X + region.Width > grayFrame.Width
            || region.Y + region.Height > grayFrame.Height)
        {
            return 0d;
        }

        using var searchArea = new Mat(grayFrame, region);
        var baseScale = grayFrame.Width / (double)FaithNoRecoveryTemplates.ReferenceWidth;
        var bestScore = double.NegativeInfinity;
        var bestLocation = new Point();
        var bestSize = new Size();
        var bestScale = baseScale;
        var bestTemplate = string.Empty;

        foreach (var (name, template) in _templates)
        {
            if (template.Empty())
                continue;

            foreach (var factor in ScaleFactors)
            {
                var scale = baseScale * factor;
                var width = (int)Math.Round(template.Width * scale);
                var height = (int)Math.Round(template.Height * scale);
                if (width < 8 || height < 8 || width >= searchArea.Width || height >= searchArea.Height)
                    continue;

                using var scaled = new Mat();
                Cv2.Resize(
                    template,
                    scaled,
                    new Size(width, height),
                    0,
                    0,
                    width < template.Width ? InterpolationFlags.Area : InterpolationFlags.Linear);

                using var result = new Mat();
                Cv2.MatchTemplate(searchArea, scaled, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out var maxValue, out _, out var maxLocation);
                if (maxValue > bestScore)
                {
                    bestScore = maxValue;
                    bestLocation = maxLocation;
                    bestSize = new Size(width, height);
                    bestScale = scale;
                    bestTemplate = name;
                }
            }
        }

        if (!double.IsFinite(bestScore) || bestSize.Width == 0)
            return 0d;

        best = new FaithHintHit(
            bestScore,
            new Rect(region.X + bestLocation.X, region.Y + bestLocation.Y, bestSize.Width, bestSize.Height),
            bestScale,
            bestTemplate);
        return bestScore;
    }

    /// <summary>
    /// 只用指定的一张模板做匹配（探针用它做「跨背景泛化」验证：
    /// 拿未满那张的模板去认已满那张，背景完全不同也要认得出）。
    /// </summary>
    public bool TryMatchTemplate(Mat grayFrame, string templateName, double threshold, out FaithHintHit hit)
    {
        hit = default;
        if (_disposed
            || grayFrame.Empty()
            || !_templates.TryGetValue(templateName, out var template)
            || template.Empty())
        {
            return false;
        }

        var region = FaithNoRecoveryTemplates.GetSearchRegion(grayFrame.Size());
        if (region.Width < 32 || region.Height < 32
            || region.X + region.Width > grayFrame.Width
            || region.Y + region.Height > grayFrame.Height)
        {
            return false;
        }

        using var searchArea = new Mat(grayFrame, region);
        var baseScale = grayFrame.Width / (double)FaithNoRecoveryTemplates.ReferenceWidth;
        var bestScore = double.NegativeInfinity;
        var bestLocation = new Point();
        var bestSize = new Size();
        var bestScale = baseScale;

        foreach (var factor in ScaleFactors)
        {
            var scale = baseScale * factor;
            var width = (int)Math.Round(template.Width * scale);
            var height = (int)Math.Round(template.Height * scale);
            if (width < 8 || height < 8 || width >= searchArea.Width || height >= searchArea.Height)
                continue;

            using var scaled = new Mat();
            Cv2.Resize(
                template,
                scaled,
                new Size(width, height),
                0,
                0,
                width < template.Width ? InterpolationFlags.Area : InterpolationFlags.Linear);

            using var result = new Mat();
            Cv2.MatchTemplate(searchArea, scaled, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out var maxValue, out _, out var maxLocation);
            if (maxValue > bestScore)
            {
                bestScore = maxValue;
                bestLocation = maxLocation;
                bestSize = new Size(width, height);
                bestScale = scale;
            }
        }

        if (!double.IsFinite(bestScore) || bestScore < threshold || bestSize.Width == 0)
            return false;

        hit = new FaithHintHit(
            bestScore,
            new Rect(region.X + bestLocation.X, region.Y + bestLocation.Y, bestSize.Width, bestSize.Height),
            bestScale,
            templateName);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var template in _templates.Values)
            template.Dispose();
        _templates.Clear();
    }
}
