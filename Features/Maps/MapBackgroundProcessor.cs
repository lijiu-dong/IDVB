using OpenCvSharp;
using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Features.Maps;

/// <summary>Options used by the common, non-destructive background pipeline.</summary>
public sealed record MapBackgroundProcessingOptions(bool RemoveBackground);

/// <summary>Result of processing one floor. Both mats are owned by the caller.</summary>
public sealed class MapBackgroundProcessingResult : IDisposable
{
    internal MapBackgroundProcessingResult(Mat recognition, Mat overlay, Mat backgroundMask)
    {
        Recognition = recognition;
        Overlay = overlay;
        BackgroundMask = backgroundMask;
        foreach (var (image, name) in new[] { (recognition, "Background.Recognition"), (overlay, "Background.Overlay"), (backgroundMask, "Background.Mask") })
        {
            var request = MemoryJournal.Request(name, image.Total() * image.ElemSize(), "OpenCvSharpExtern.dll");
            MemoryJournal.TrackResource(image, name, image.Total() * image.ElemSize(), "OpenCvSharpExtern.dll", request);
        }
    }

    public Mat Recognition { get; }
    public Mat Overlay { get; }
    public Mat BackgroundMask { get; }

    public void Dispose()
    {
        Recognition.Dispose();
        Overlay.Dispose();
        BackgroundMask.Dispose();
        MemoryJournal.ReleaseResource(Recognition);
        MemoryJournal.ReleaseResource(Overlay);
        MemoryJournal.ReleaseResource(BackgroundMask);
    }
}

/// <summary>
/// Shared image processing for editor previews, saves, repairs, class rebuilds,
/// and PNG export. The source mat is never modified.
/// </summary>
public static partial class MapBackgroundProcessor
{
    public const int DefaultBackgroundRemovalIntensity = 8;
    public const int MinBackgroundRemovalIntensity = 0;
    public const int MaxBackgroundRemovalIntensity = 64;
    public const int DefaultBrushSizePixels = 64;
    public const int MinBrushSizePixels = 1;
    public const int MaxBrushSizePixels = 1024;

    public static int ClampBrushSize(int value) =>
        Math.Clamp(value, MinBrushSizePixels, MaxBrushSizePixels);

    public static int ClampBackgroundRemovalIntensity(int value) =>
        Math.Clamp(value, MinBackgroundRemovalIntensity, MaxBackgroundRemovalIntensity);

    public static Mat RasterizeMask(
        IReadOnlyList<MapBackgroundLayer>? layers,
        int width,
        int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));

        var mask = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        foreach (var layer in layers ?? [])
        {
            if (layer is null || !layer.IsValid)
                continue;
            var points = layer.Points.Where(point => point.IsValid).ToArray();
            if (points.Length == 0)
                continue;
            var brush = Math.Clamp(layer.BrushSizePixels, MinBrushSizePixels, MaxBrushSizePixels);
            var pixelPoints = points
                .Select(point => new Point(
                    Math.Clamp((int)Math.Round(point.X * (width - 1)), 0, width - 1),
                    Math.Clamp((int)Math.Round(point.Y * (height - 1)), 0, height - 1)))
                .ToArray();
            for (var index = 0; index < pixelPoints.Length; index++)
            {
                DrawBrush(mask, pixelPoints[index], brush, layer.Shape);
                if (index == 0)
                    continue;
                Cv2.Line(
                    mask,
                    pixelPoints[index - 1],
                    pixelPoints[index],
                    Scalar.White,
                    brush,
                    LineTypes.Link8,
                    shift: 0);
            }
        }
        return mask;
    }

    public static MapBackgroundProcessingResult Process(
        Mat source,
        FloorRecognitionProfile profile,
        bool removeBackground,
        int backgroundRemovalIntensity = DefaultBackgroundRemovalIntensity)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(profile);
        if (source.Empty())
            throw new InvalidOperationException("无法解码楼层图像。");

        using var operation = MemoryJournal.BeginOperation("Background.Process", profile.FloorKey);
        Mat? recognition = null, recognitionMask = null, overlay = null;
        try
        {
            if (!removeBackground
                && profile.BackgroundLayers is null or { Count: 0 }
                && profile.FreeCropPoints is null or { Count: 0 })
            {
                // With no mask, color conversion and white-key alpha are
                // pixel-local. Crop first instead of cloning the full source
                // and allocating two full-source black masks for a subfloor.
                var rectangle = UsesWholeSourceImage(profile)
                    ? new Rect(0, 0, source.Width, source.Height)
                    : GetPixelRegion(profile.GetEffectiveRecognitionRegion(), source.Width, source.Height);
                using var region = new Mat(source, rectangle);
                recognition = ToBgra(region);
                UpdateRecognitionDimensions(profile, recognition.Width, recognition.Height);
                recognitionMask = new Mat(recognition.Height, recognition.Width, MatType.CV_8UC1, Scalar.Black);
                overlay = CreateWhiteKeyOverlay(recognition);
                return new MapBackgroundProcessingResult(recognition, overlay, recognitionMask);
            }
            // Process our own conversion in place; source remains unchanged.
            using (var processedFull = ToBgra(source))
            using (var fullMask = RasterizeMask(profile.BackgroundLayers, processedFull.Width, processedFull.Height))
            {
                using var combinedMask = fullMask.Clone();
                ApplyFreeCropMask(combinedMask, profile.FreeCropPoints);
                if (removeBackground)
                {
                    using var automatic = BuildAutomaticMask(
                        processedFull,
                        fullMask,
                        ClampBackgroundRemovalIntensity(backgroundRemovalIntensity));
                    Cv2.BitwiseOr(combinedMask, automatic, combinedMask);
                }
                if (Cv2.CountNonZero(combinedMask) > 0) ClearMaskedPixels(processedFull, combinedMask);

                if (UsesWholeSourceImage(profile))
                    recognition = new Mat(processedFull, new Rect(0, 0, processedFull.Width, processedFull.Height));
                else
                {
                    using var region = new Mat(processedFull, GetPixelRegion(profile.GetEffectiveRecognitionRegion(), processedFull.Width, processedFull.Height));
                    recognition = region.Clone();
                }
                UpdateRecognitionDimensions(profile, recognition.Width, recognition.Height);

                if (UsesWholeSourceImage(profile))
                    recognitionMask = new Mat(combinedMask, new Rect(0, 0, combinedMask.Width, combinedMask.Height));
                else
                {
                    using var region = new Mat(combinedMask, GetPixelRegion(profile.GetEffectiveRecognitionRegion(), combinedMask.Width, combinedMask.Height));
                    recognitionMask = region.Clone();
                }
            }
            // Retire full-size working buffers before generating the overlay.
            overlay = CreateWhiteKeyOverlay(recognition);
            return new MapBackgroundProcessingResult(recognition, overlay, recognitionMask);
        }
        catch
        {
            recognition?.Dispose(); recognitionMask?.Dispose(); overlay?.Dispose();
            if (recognition is not null) MemoryJournal.ReleaseResource(recognition);
            if (recognitionMask is not null) MemoryJournal.ReleaseResource(recognitionMask);
            if (overlay is not null) MemoryJournal.ReleaseResource(overlay);
            throw;
        }
    }

    private static void UpdateRecognitionDimensions(FloorRecognitionProfile profile, int width, int height)
    {
        var previousWidth = profile.RecognitionPixelWidth;
        var previousHeight = profile.RecognitionPixelHeight;
        var previousBounds = profile.ValidMapBounds?.Clone();
        profile.RecognitionPixelWidth = width;
        profile.RecognitionPixelHeight = height;
        if (previousBounds?.IsValid is true && previousWidth > 0 && previousHeight > 0
            && (previousWidth != width || previousHeight != height))
            profile.ValidMapBounds = new MapReferenceBounds
            {
                X = previousBounds.X * width / previousWidth,
                Y = previousBounds.Y * height / previousHeight,
                Width = previousBounds.Width * width / previousWidth,
                Height = previousBounds.Height * height / previousHeight
            };
        else if (profile.ValidMapBounds?.IsValid is not true)
            profile.ValidMapBounds = MapReferenceBounds.FullImage(width, height);
    }

    public static Mat Apply(
        Mat source,
        IReadOnlyList<MapBackgroundLayer>? layers,
        bool removeBackground,
        int backgroundRemovalIntensity = DefaultBackgroundRemovalIntensity)
    {
        var profile = new FloorRecognitionProfile { BackgroundLayers = (layers ?? []).Select(layer => layer.Clone()).ToList() };
        using var result = Process(source, profile, removeBackground, backgroundRemovalIntensity);
        return result.Recognition.Clone();
    }

    public static Mat CreateWhiteKeyOverlay(Mat source)
    {
        var width = source.Width;
        var height = source.Height;
        Mat? result = null;
        try
        {
            for (var y = 0; y < height; y += 64)
            {
                var rectangle = new Rect(0, y, width, Math.Min(64, height - y));
                using var input = new Mat(source, rectangle);
                using var tile = CreateWhiteKeyOverlayTile(input);
                result ??= new Mat(height, width, tile.Type());
                using var destination = new Mat(result, rectangle);
                tile.CopyTo(destination);
            }
            return result ?? throw new InvalidOperationException("无法解码楼层图像。");
        }
        catch { result?.Dispose(); throw; }
    }

    private static Mat CreateWhiteKeyOverlayTile(Mat source)
    {
        using var bgra = ToBgra(source);
        using var bgr = new Mat();
        using var hsv = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        var bgraChannels = Cv2.Split(bgra);
        var hsvChannels = Cv2.Split(hsv);
        try
        {
            using var neutralMask = new Mat();
            using var whiteness = new Mat();
            using var alphaReduction = new Mat();
            using var generatedAlpha = new Mat();
            using var keyedAlpha = new Mat();
            using var finalAlpha = bgraChannels[3].Clone();
            Cv2.InRange(hsvChannels[1], new Scalar(0), new Scalar(25), neutralMask);
            Cv2.Subtract(hsvChannels[2], new Scalar(230), whiteness);
            whiteness.ConvertTo(alphaReduction, MatType.CV_8UC1, 255d / 15d);
            Cv2.Subtract(new Scalar(255), alphaReduction, generatedAlpha);
            Cv2.Min(bgraChannels[3], generatedAlpha, keyedAlpha);
            keyedAlpha.CopyTo(finalAlpha, neutralMask);
            var result = new Mat();
            Cv2.Merge([bgraChannels[0], bgraChannels[1], bgraChannels[2], finalAlpha], result);
            NormalizeTransparentPixels(result);
            return result;
        }
        finally
        {
            foreach (var channel in bgraChannels)
                channel.Dispose();
            foreach (var channel in hsvChannels)
                channel.Dispose();
        }
    }

    /// <summary>
    /// Zeros out color channels (B, G, R) for any pixels where Alpha == 0 to prevent
    /// hidden texture or white background noise from leaking when Alpha is discarded.
    /// </summary>
    public static void NormalizeTransparentPixels(Mat bgra)
    {
        if (bgra is null || bgra.IsDisposed || bgra.Channels() != 4)
            return;

        var width = bgra.Width;
        var height = bgra.Height;
        for (var y = 0; y < height; y += 64)
        {
            using var tile = new Mat(bgra, new Rect(0, y, width, Math.Min(64, height - y)));
            NormalizeTransparentTile(tile);
        }
    }

    private static void NormalizeTransparentTile(Mat bgra)
    {
        var channels = Cv2.Split(bgra);
        try
        {
            using var zeroAlphaMask = new Mat();
            Cv2.Compare(channels[3], 0, zeroAlphaMask, CmpTypes.EQ);
            channels[0].SetTo(Scalar.Black, zeroAlphaMask);
            channels[1].SetTo(Scalar.Black, zeroAlphaMask);
            channels[2].SetTo(Scalar.Black, zeroAlphaMask);
            Cv2.Merge(channels, bgra);
        }
        finally
        {
            foreach (var channel in channels)
                channel.Dispose();
        }
    }

    /// <summary>
    /// Converts an image of 1, 3, or 4 channels into a 3-channel BGR image.
    /// If the input is 4-channel BGRA, pixels are composited onto a solid black background
    /// using their Alpha values (premultiplied over black), ensuring transparent areas (Alpha=0)
    /// become solid black without leaking underlying dirty RGB noise.
    /// </summary>
    public static Mat CompositeToBgr(Mat source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Empty())
            throw new InvalidOperationException("无法转换空图像。");

        if (source.Channels() == 3)
        {
            var bgr = new Mat();
            source.CopyTo(bgr);
            return bgr;
        }

        if (source.Channels() == 1)
        {
            var bgr = new Mat();
            Cv2.CvtColor(source, bgr, ColorConversionCodes.GRAY2BGR);
            return bgr;
        }

        if (source.Channels() == 4)
            return CompositeAlphaToBgr(source);

        throw new InvalidOperationException($"不支持的图像通道数: {source.Channels()}。");
    }

    private static void DrawBrush(Mat mask, Point center, int size, MapBackgroundLayerShape shape)
    {
        if (shape == MapBackgroundLayerShape.Square)
        {
            var half = size / 2;
            Cv2.Rectangle(
                mask,
                new Point(center.X - half, center.Y - half),
                new Point(center.X + (size - half - 1), center.Y + (size - half - 1)),
                Scalar.White,
                -1);
        }
        else
        {
            Cv2.Circle(mask, center, Math.Max(0, (size - 1) / 2), Scalar.White, -1);
        }
    }

    private static void ApplyFreeCropMask(Mat mask, IReadOnlyList<NormalizedPoint>? points)
    {
        var polygon = (points ?? []).Where(point => point.IsValid).ToArray();
        if (polygon.Length < 3)
            return;
        using var kept = new Mat(mask.Rows, mask.Cols, MatType.CV_8UC1, Scalar.Black);
        var pixels = polygon.Select(point => new Point(
            Math.Clamp((int)Math.Round(point.X * (mask.Cols - 1)), 0, mask.Cols - 1),
            Math.Clamp((int)Math.Round(point.Y * (mask.Rows - 1)), 0, mask.Rows - 1))).ToArray();
        Cv2.FillPoly(kept, [pixels], Scalar.White);
        Cv2.BitwiseNot(kept, kept);
        Cv2.BitwiseOr(mask, kept, mask);
    }

    private static Mat BuildAutomaticMask(Mat bgra, Mat manualMask, int tolerance)
    {
        var counts = new Dictionary<int, int>();
        var rows = bgra.Rows;
        var columns = bgra.Cols;
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                if (manualMask.At<byte>(y, x) != 0)
                    continue;
                var pixel = bgra.At<Vec4b>(y, x);
                if (pixel.Item3 == 0)
                    continue;
                var rgb = (pixel.Item2 << 16) | (pixel.Item1 << 8) | pixel.Item0;
                counts[rgb] = counts.TryGetValue(rgb, out var count) ? count + 1 : 1;
            }
        }

        var primary = counts.Count == 0
            ? (int?)null
            : counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First().Key;
        var result = new Mat(rows, columns, MatType.CV_8UC1, Scalar.Black);
        if (primary is null)
            return result;
        var mainR = (primary.Value >> 16) & 0xFF;
        var mainG = (primary.Value >> 8) & 0xFF;
        var mainB = primary.Value & 0xFF;
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                if (manualMask.At<byte>(y, x) != 0)
                    continue;
                var pixel = bgra.At<Vec4b>(y, x);
                if (pixel.Item3 != 0
                    && Math.Abs(pixel.Item2 - mainR) <= tolerance
                    && Math.Abs(pixel.Item1 - mainG) <= tolerance
                    && Math.Abs(pixel.Item0 - mainB) <= tolerance)
                {
                    result.Set(y, x, (byte)255);
                }
            }
        }
        return result;
    }

    private static void ClearMaskedPixels(Mat bgra, Mat mask)
    {
        var channels = Cv2.Split(bgra);
        try
        {
            foreach (var channel in channels)
                channel.SetTo(Scalar.Black, mask);
            Cv2.Merge(channels, bgra);
        }
        finally
        {
            foreach (var channel in channels)
                channel.Dispose();
        }
    }

    private static Mat ToBgra(Mat source)
    {
        var result = new Mat();
        switch (source.Channels())
        {
            case 4:
                source.CopyTo(result);
                NormalizeTransparentPixels(result);
                break;
            case 3:
                Cv2.CvtColor(source, result, ColorConversionCodes.BGR2BGRA);
                break;
            case 1:
                Cv2.CvtColor(source, result, ColorConversionCodes.GRAY2BGRA);
                break;
            default:
                result.Dispose();
                throw new InvalidOperationException("不支持的楼层图像通道数。");
        }
        return result;
    }

    private static bool UsesWholeSourceImage(FloorRecognitionProfile profile)
    {
        var region = profile.RecognitionRegion;
        return region?.IsValid is not true
            || (region.X <= 0.000001d
                && region.Y <= 0.000001d
                && region.X + region.Width >= 0.999999d
                && region.Y + region.Height >= 0.999999d);
    }

    private static Rect GetPixelRegion(NormalizedRectangle region, int width, int height)
    {
        var left = Math.Clamp((int)Math.Floor(region.X * width), 0, Math.Max(0, width - 1));
        var top = Math.Clamp((int)Math.Floor(region.Y * height), 0, Math.Max(0, height - 1));
        var right = Math.Clamp((int)Math.Ceiling((region.X + region.Width) * width), left + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling((region.Y + region.Height) * height), top + 1, height);
        return new Rect(left, top, right - left, bottom - top);
    }
}
