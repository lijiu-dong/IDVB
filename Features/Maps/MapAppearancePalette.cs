using System.Globalization;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>Display-only colors. Null/default preserves the original artwork byte for byte.</summary>
public sealed record MapAppearancePalette
{
    public string Room { get; init; } = "#59493C";
    public string Corridor { get; init; } = "#3C404E";
    public string RoomOutline { get; init; } = "#D8DEE9";
    public string CorridorOutline { get; init; } = "#D8DEE9";

    public MapAppearancePalette Normalize() => new()
    {
        Room = NormalizeColor(Room), Corridor = NormalizeColor(Corridor),
        RoomOutline = NormalizeColor(RoomOutline), CorridorOutline = NormalizeColor(CorridorOutline)
    };

    private static string NormalizeColor(string value) => value is { Length: 7 } && value[0] == '#'
        && uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
        ? value.ToUpperInvariant() : throw new InvalidDataException("配色必须为 #RRGGBB。");

    internal static byte[] Bgr(string hex) => [Convert.ToByte(hex[5..7], 16),
        Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[1..3], 16)];
}

public sealed partial class MapRecord
{
    public MapAppearancePalette? AppearancePalette { get; set; }
}

public sealed partial class MapDraft
{
    public MapAppearancePalette? AppearancePalette { get; set; }
}

public static class MapAppearanceRenderer
{
    public static Mat CreateThumbnail(Mat source)
    {
        // Match the original thumbnail pipeline: transparency is represented by
        // black in previews only. Composite before resizing to retain edge alpha.
        using var bgr = MapBackgroundProcessor.CompositeToBgr(source);
        var width = Math.Min(400, bgr.Width);
        var thumbnail = new Mat();
        Cv2.Resize(bgr, thumbnail, new Size(width,
            Math.Max(1, (int)Math.Round(bgr.Height * (double)width / bgr.Width))),
            0, 0, InterpolationFlags.Area);
        return thumbnail;
    }

    // Classify only the canonical baked warm/cool artwork, never a recolored output.
    // Outlines use the immutable semantic PNG, not brightness or nearest-color guesses.
    public static Mat Render(Mat source, Mat semantic, MapAppearancePalette palette)
    {
        if (source.Type() != MatType.CV_8UC3 && source.Type() != MatType.CV_8UC4)
            throw new InvalidDataException("地图显示图片必须为 RGB 或 RGBA。");
        if (semantic.Size() != source.Size()) throw new InvalidDataException("地图配色轮廓尺寸不一致。");
        using var labels = SemanticStructureContours.Decode(semantic);
        var output = source.Clone();
        try { RenderOwnedPixels(output, labels, palette); return output; }
        catch { output.Dispose(); throw; }
    }

    // This image is exclusively owned by the repository. The public Render
    // contract above still preserves caller input; this path needs no clone.
    internal static void RenderOwnedPixels(Mat output, Mat labels, MapAppearancePalette palette)
    {
        palette = palette.Normalize();
        var channels = output.Channels();
        if (output.Type() != MatType.CV_8UC3 && output.Type() != MatType.CV_8UC4
            || labels.Type() != MatType.CV_8UC1 || labels.Size() != output.Size()
            || !output.IsContinuous() || !labels.IsContinuous())
            throw new InvalidDataException("地图配色需要尺寸一致的连续 RGB/RGBA 图片及语义标签。");
        var pixels = MemoryMarshal.CreateSpan(ref output.At<byte>(0, 0), checked(output.Rows * output.Cols * channels));
        var kinds = MemoryMarshal.CreateReadOnlySpan(ref labels.At<byte>(0, 0), checked(labels.Rows * labels.Cols));
        var defaults = new MapAppearancePalette();
        var room = MapAppearancePalette.Bgr(palette.Room);
        var corridor = MapAppearancePalette.Bgr(palette.Corridor);
        var roomEdge = MapAppearancePalette.Bgr(palette.RoomOutline);
        var corridorEdge = MapAppearancePalette.Bgr(palette.CorridorOutline);
        for (var index = 0; index < kinds.Length; index++)
        {
            var offset = index * channels;
            if (channels == 4 && pixels[offset + 3] == 0) continue;
            var b = pixels[offset]; var g = pixels[offset + 1]; var r = pixels[offset + 2];
            if (b == 233 && g == 222 && r == 216 && kinds[index] != 0)
            {
                var edge = kinds[index] == SemanticStructureContours.Warm ? roomEdge : corridorEdge;
                edge.AsSpan().CopyTo(pixels.Slice(offset, 3));
            }
            else if (r < 180 && b < 180 && palette.Room != defaults.Room
                && r - g is >= 8 and <= 24 && g - b is >= 7 and <= 22)
                Tint(pixels, offset, b, g, r, room, 60, 73, 89);
            else if (r < 180 && b < 180 && palette.Corridor != defaults.Corridor
                && b - g is >= 8 and <= 20 && g - r is >= 0 and <= 10)
                Tint(pixels, offset, b, g, r, corridor, 78, 64, 60);
        }
        static void Tint(Span<byte> pixels, int offset, byte b, byte g, byte r, byte[] color, int baseB, int baseG, int baseR)
        {
            // Retain the source's texture and stair detail around its base tint.
            pixels[offset] = (byte)Math.Clamp(b + color[0] - baseB, 0, 255);
            pixels[offset + 1] = (byte)Math.Clamp(g + color[1] - baseG, 0, 255);
            pixels[offset + 2] = (byte)Math.Clamp(r + color[2] - baseR, 0, 255);
        }
    }
}
