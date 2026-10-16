namespace IDVBuff.Features.Maps;

public static class MapMarkerTool
{
    public const string Color = "#FFF1A8";
    public static IReadOnlyList<string> Symbols { get; } = Array.AsReadOnly(new[] { "★", "⇅", "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" });
    public static bool IsValidLabel(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 8) return false;
        if (text is "★" or "⇅" or "↑" or "↓") return true;
        var transfer = text[0] is '⇅' or '↑' or '↓';
        var digits = transfer && text.StartsWith(text[0] + " ★ ", StringComparison.Ordinal) ? text[4..]
            : text.StartsWith("★ ", StringComparison.Ordinal) || transfer && text.StartsWith(text[0] + " ", StringComparison.Ordinal) ? text[2..] : text;
        return digits.Length is >= 1 and <= 4 && digits.All(c => c is >= '0' and <= '9');
    }

    public static string RoomLabel(bool connector, bool highValue, int number, string transferSymbol = "⇅") =>
        (connector ? transferSymbol + " " : "") + (highValue ? "★ " : "") + number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static MapAnnotation Create(string label, NormalizedPoint center, double imageWidth, double imageHeight)
    {
        if (!IsValidLabel(label) || !center.IsValid || !double.IsFinite(imageWidth) || !double.IsFinite(imageHeight)
            || imageWidth <= 0 || imageHeight <= 0)
            throw new ArgumentException("标记图案或位置无效。");
        var height = Math.Clamp(Math.Max(22, imageWidth * .018) / imageHeight, .01, .08);
        var width = Math.Clamp(height * imageHeight / imageWidth * Math.Max(1.15, label.Length * .72), .01, .18);
        return new() { Type = MapAnnotationType.Text, IsMarker = true, Text = label, ColorHex = Color,
            FontFamily = "Segoe UI Symbol", IsBold = true,
            Bounds = new() { X = Math.Clamp(center.X - width / 2, 0, 1 - width),
                Y = Math.Clamp(center.Y - height / 2, 0, 1 - height), Width = width, Height = height } };
    }
}
