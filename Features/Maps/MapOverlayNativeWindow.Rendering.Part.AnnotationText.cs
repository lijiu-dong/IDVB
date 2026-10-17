using System.Drawing;

namespace IDVBuff.Features.Maps;

internal static partial class MapOverlayBitmapRenderer
{
    private static void DrawAnnotationLabel(Graphics graphics, string text, Font font, Brush brush,
        RectangleF bounds, StringFormat format, bool marker)
    {
        if (marker)
        {
            using var halo = new SolidBrush(Color.FromArgb(255, 10, 15, 22));
            var offset = Math.Max(1f, font.Size * .07f);
            foreach (var x in new[] { -offset, 0, offset })
            foreach (var y in new[] { -offset, 0, offset })
                if (x != 0 || y != 0) graphics.DrawString(text, font, halo,
                    new RectangleF(bounds.X + x, bounds.Y + y, bounds.Width, bounds.Height), format);
        }
        graphics.DrawString(text, font, brush, bounds, format);
    }

    private static Font CreateAnnotationFont(string? familyName, float pixelSize, FontStyle style)
    {
        if (string.IsNullOrWhiteSpace(familyName))
            return CreateFont(pixelSize, style);
        try
        {
            return new Font(familyName, pixelSize, style, GraphicsUnit.Pixel);
        }
        catch (ArgumentException)
        {
            return CreateFont(pixelSize, style);
        }
    }
}
