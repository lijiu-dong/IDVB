using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace IDVBuff.Features.Maps;

internal static partial class MapOverlayBitmapRenderer
{
    // Fade the complete vector layer once, including text halos and overlapping
    // shapes. The raster, player markers and floor labels remain independent.
    private static void DrawRouteContent(Graphics destination, MapOverlayRenderMap map,
        float dpiScale, Action<Graphics> draw)
    {
        var opacity = float.IsFinite(map.RouteOpacity) ? Math.Clamp(map.RouteOpacity, 0f, 1f) : 1f;
        if (opacity <= 0f) return;
        if (opacity >= 1f)
        {
            draw(destination);
            return;
        }
        if (map.Annotations is not { Count: > 0 } && map.Anchors.Count == 0) return;

        using var layer = new Bitmap(Math.Max(1, (int)Math.Round(map.Width)),
            Math.Max(1, (int)Math.Round(map.Height)), PixelFormat.Format32bppPArgb);
        layer.SetResolution(dpiScale * DefaultDpi, dpiScale * DefaultDpi);
        using (var graphics = Graphics.FromImage(layer))
        {
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = destination.CompositingQuality;
            graphics.InterpolationMode = destination.InterpolationMode;
            graphics.SmoothingMode = destination.SmoothingMode;
            graphics.PixelOffsetMode = destination.PixelOffsetMode;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            draw(graphics);
        }
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
        var state = destination.Save();
        try
        {
            destination.InterpolationMode = InterpolationMode.NearestNeighbor;
            destination.PixelOffsetMode = PixelOffsetMode.Half;
            destination.DrawImage(layer, new Rectangle(0, 0, layer.Width, layer.Height),
                0, 0, layer.Width, layer.Height, GraphicsUnit.Pixel, attributes);
        }
        finally { destination.Restore(state); }
    }
}
