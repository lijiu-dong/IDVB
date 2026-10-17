using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace IDVBuff.Features.Maps;
internal static partial class MapOverlayBitmapRenderer
{
    // A GDI+ clone of a Bitmap wrapping external DIB pixels can retain that
    // backing storage. Compatibility callers own this result independently
    // of cache eviction, so copy locked premultiplied bytes into owned pixels.
    private static Bitmap CopyOwnedRaster(Bitmap source)
    {
        var width = source.Width;
        var height = source.Height;
        var copy = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        try
        {
            copy.SetResolution(source.HorizontalResolution, source.VerticalResolution);
            var rectangle = new Rectangle(0, 0, width, height);
            var input = source.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var output = copy.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    var rowBytes = checked(width * 4);
                    for (var y = 0; y < height; y++)
                        CopyRasterBytes(output.Scan0 + checked(y * output.Stride), input.Scan0 + checked(y * input.Stride), (IntPtr)rowBytes);
                }
                finally { copy.UnlockBits(output); }
            }
            finally { source.UnlockBits(input); }
            return copy;
        }
        catch { copy.Dispose(); throw; }
    }

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyRasterBytes(IntPtr destination, IntPtr source, IntPtr length);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Bitmap, MapOverlayNativeWindow.SharedDib> SharedMapLayers = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Bitmap, MemoryStream> SourceImageStreams = new();
    internal static bool TryGetSharedMapLayer(Bitmap bitmap, out MapOverlayNativeWindow.SharedDib? layer) =>
        SharedMapLayers.TryGetValue(bitmap, out layer);
    // Native presentation synchronously copies into a retained DIB. The callback
    // must neither mutate nor retain this cache-owned bitmap; hold the cache lock
    // through that copy instead of creating another full-raster clone.
    internal static void WithMapLayer(MapOverlayRenderMap map, uint dpi,
        bool showGateMarkers, bool showAuxiliaryAnchors, bool showTextAnnotations,
        bool showBoxAnnotations, bool showLineAnnotations, float mapOpacity,
        Action<Bitmap> consume, Rectangle? sourceRegion = null)
    {
        ArgumentNullException.ThrowIfNull(consume);
        using var operation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Overlay.MapLayer.Present", map.ImagePath);
        var width = MapOverlayRealtimeTransformPlanner.RoundToPixel(map.Width);
        var height = MapOverlayRealtimeTransformPlanner.RoundToPixel(map.Height);
        lock (ImageCacheLock)
        {
            var bitmap = GetOrBuildMapLayer(map with { Left = 0f, Top = 0f, Width = width, Height = height },
                width, height, ScaleFor(dpi), mapOpacity, showGateMarkers, showAuxiliaryAnchors,
                showTextAnnotations, showBoxAnnotations, showLineAnnotations, sourceRegion);
            consume(bitmap);
        }
    }

    // Same contract as WithMapLayer, but any cached window of this layer that
    // contains `coverage` is presented instead of rebuilding `sourceRegion`.
    // The callback receives the window actually used for its raster placement.
    internal static void WithMapLayerCovering(MapOverlayRenderMap map, uint dpi,
        bool showGateMarkers, bool showAuxiliaryAnchors, bool showTextAnnotations,
        bool showBoxAnnotations, bool showLineAnnotations, float mapOpacity,
        Rectangle coverage, Rectangle sourceRegion, Action<Bitmap, Rectangle> consume)
    {
        ArgumentNullException.ThrowIfNull(consume);
        using var operation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Overlay.MapLayer.Present", map.ImagePath);
        var width = MapOverlayRealtimeTransformPlanner.RoundToPixel(map.Width);
        var height = MapOverlayRealtimeTransformPlanner.RoundToPixel(map.Height);
        lock (ImageCacheLock)
        {
            var bitmap = GetOrBuildMapLayer(map with { Left = 0f, Top = 0f, Width = width, Height = height },
                width, height, ScaleFor(dpi), mapOpacity, showGateMarkers, showAuxiliaryAnchors,
                showTextAnnotations, showBoxAnnotations, showLineAnnotations, sourceRegion, coverage,
                out var usedRegion);
            consume(bitmap, usedRegion);
        }
    }


    private static Bitmap CreateCachedBitmap(int width, int height, string resource)
    {
        var bytes = checked((long)width * height * 4);
        if (resource == "Overlay.BakedMapLayer")
        {
            var layer = new MapOverlayNativeWindow.SharedDib(width, height, resource);
            try
            {
                SharedMapLayers.Add(layer.Bitmap, layer);
                return layer.Bitmap;
            }
            catch { layer.Dispose(); throw; }
        }
        var request = IDVBuff.Core.Diagnostics.MemoryJournal.Request(resource, bytes, "gdiplus.dll");
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        IDVBuff.Core.Diagnostics.MemoryJournal.TrackResource(bitmap, resource, bytes, "gdiplus.dll", request);
        return bitmap;
    }

    private static void DisposeCachedBitmap(Bitmap bitmap)
    {
        if (SharedMapLayers.TryGetValue(bitmap, out var layer))
        {
            SharedMapLayers.Remove(bitmap);
            layer.Dispose();
            return;
        }
        bitmap.Dispose();
        if (SourceImageStreams.TryGetValue(bitmap, out var stream))
        {
            SourceImageStreams.Remove(bitmap);
            stream.Dispose();
        }
        // Complete physical cleanup even if the diagnostic writer has failed.
        IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(bitmap);
        if (stream is not null) IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(stream);
    }

    internal static Bitmap GetOrLoadScaledMapImage(
        string imagePath,
        int width,
        int height,
        uint dpi)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        // A scale can drift by fractions of a pixel on every alignment. Keep
        // only the latest raster for each source image/DPI instead of retaining
        // one full-size bitmap for every observed dimension.
        var key = $"{Path.GetFullPath(imagePath)}|dpi={dpi}";
        lock (ImageCacheLock)
        {
            EnsureImageCacheSourceVersionCore(imagePath);
            if (ScaledImageCache.TryGetValue(key, out var cached))
            {
                if (cached.Width == width && cached.Height == height)
                    return cached.Bitmap;
                DisposeCachedBitmap(cached.Bitmap);
                ScaledImageCache.Remove(key);
            }

            var source = GetOrLoadMapImage(imagePath);
            var scaled = CreateCachedBitmap(width, height, "Overlay.ScaledSource");
            scaled.SetResolution(
                dpi == 0 ? DefaultDpi : dpi,
                dpi == 0 ? DefaultDpi : dpi);
            using (var graphics = Graphics.FromImage(scaled))
            {
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            }
            TrimScaledImageCacheIfNecessary(MaxLayerCacheEntries - 1);
            ScaledImageCache[key] = new ScaledImageCacheEntry(
                width,
                height,
                scaled);
            return scaled;
        }
    }

    private static SizeF MeasureWrapped(Graphics graphics, string text, Font font, float width)
    {
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.None,
            FormatFlags = StringFormatFlags.LineLimit
        };
        return graphics.MeasureString(text, font, new SizeF(width, 10000f), format);
    }

    private static float MeasureUnwrappedWidth(Graphics graphics, string text, Font font) =>
        Math.Max(1f, graphics.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width);

    private static Font CreateFont(float pixelSize, FontStyle style) =>
        new("Segoe UI", Math.Max(1f, pixelSize), style, GraphicsUnit.Pixel);

    private static float ScaleFor(uint dpi) => Math.Max(1f, (dpi == 0 ? DefaultDpi : dpi) / DefaultDpi);

    private static int ScaleAlpha(int sourceAlpha, int opacityByte) =>
        Math.Clamp(sourceAlpha * opacityByte / 255, 0, 255);

    private static GraphicsPath CreateRoundedRectangle(RectangleF bounds, float radius)
    {
        var diameter = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new RectangleF(bounds.Left, bounds.Top, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static Color AnchorColor(string key) => key switch
    {
        "main-entrance" => Color.FromArgb(255, 38, 133, 255),
        "side-entrance" => Color.FromArgb(255, 63, 207, 123),
        "second-floor-primary" => Color.FromArgb(255, 132, 94, 247),
        _ => Color.FromArgb(255, 236, 150, 61)
    };

    internal static Color AnnotationColor(int colorIndex) => colorIndex switch
    {
        0 => Color.FromArgb(255, 255, 59, 48),
        1 => Color.FromArgb(255, 255, 149, 0),
        2 => Color.FromArgb(255, 255, 204, 0),
        3 => Color.FromArgb(255, 52, 199, 89),
        4 => Color.FromArgb(255, 50, 173, 230),
        5 => Color.FromArgb(255, 0, 122, 255),
        6 => Color.FromArgb(255, 175, 82, 222),
        7 => Color.FromArgb(255, 255, 45, 85),
        _ => Color.FromArgb(255, 242, 242, 242)
    };

    internal static Color AnnotationColor(string? colorHex, int fallbackIndex)
    {
        if (!MapAnnotationColor.TryNormalize(colorHex, out var normalized))
            return AnnotationColor(fallbackIndex);
        return Color.FromArgb(
            255,
            Convert.ToInt32(normalized.Substring(1, 2), 16),
            Convert.ToInt32(normalized.Substring(3, 2), 16),
            Convert.ToInt32(normalized.Substring(5, 2), 16));
    }

    internal static Color StatusColor(MapOverlayStatusLevel level) => level switch
    {
        MapOverlayStatusLevel.Success => Color.FromArgb(255, 91, 220, 138),
        MapOverlayStatusLevel.Warning => Color.FromArgb(255, 255, 184, 77),
        MapOverlayStatusLevel.Failure => Color.FromArgb(255, 255, 105, 97),
        MapOverlayStatusLevel.Scanning => Color.FromArgb(255, 91, 176, 255),
        MapOverlayStatusLevel.ManualSelection => Color.FromArgb(255, 115, 193, 255),
        _ => Color.FromArgb(255, 225, 225, 225)
    };
}
