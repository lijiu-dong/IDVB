using System.Drawing;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Allocation-free classification for the retained map layer hot path.
/// Translation preserves the baked bitmap; only a physical pixel-size change
/// requires a new layer upload.
/// </summary>
internal static class MapOverlayRealtimeTransformPlanner
{
    internal static bool RequiresBitmapRebuild(
        float currentWidth,
        float currentHeight,
        float nextWidth,
        float nextHeight) =>
        RoundToPixel(currentWidth) != RoundToPixel(nextWidth)
        || RoundToPixel(currentHeight) != RoundToPixel(nextHeight);

    internal static int RoundToPixel(float value) =>
        Math.Max(1, (int)Math.Round(value));

    internal static Rectangle VisibleRasterRegion(MapScreenRect bounds, MapScreenRect clip)
    {
        if (!bounds.IsValid || !clip.IsValid) return Rectangle.Empty;
        var width = Math.Max(1, (int)Math.Round(bounds.Width));
        var height = Math.Max(1, (int)Math.Round(bounds.Height));
        var x = Math.Round(bounds.X); var y = Math.Round(bounds.Y);
        var left = (int)Math.Clamp(Math.Floor(clip.X) - x, 0, width);
        var top = (int)Math.Clamp(Math.Floor(clip.Y) - y, 0, height);
        var right = (int)Math.Clamp(Math.Ceiling(clip.X + clip.Width) - x, 0, width);
        var bottom = (int)Math.Clamp(Math.Ceiling(clip.Y + clip.Height) - y, 0, height);
        return right <= left || bottom <= top ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right, bottom);
    }

    // Retain a margin for ordinary translation, without retaining pixels far
    // outside the game's clipping region. This never changes sampling scale.
    internal static Rectangle RetainedRasterRegion(Rectangle visible, int width, int height, int margin = 128)
    {
        if (visible.IsEmpty) return Rectangle.Empty;
        return Rectangle.FromLTRB(Math.Max(0, visible.Left - margin), Math.Max(0, visible.Top - margin),
            (int)Math.Min(width, (long)visible.Right + margin), (int)Math.Min(height, (long)visible.Bottom + margin));
    }

    internal static MapScreenRect RasterPlacement(MapScreenRect bounds, Rectangle source) =>
        // Round the original origin first: adding an odd crop offset before
        // midpoint-to-even rounding would move a .5 origin by one pixel.
        new(Math.Round(bounds.X) + source.X, Math.Round(bounds.Y) + source.Y, source.Width, source.Height);
}
