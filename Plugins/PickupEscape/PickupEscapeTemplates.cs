using OpenCvSharp;

namespace IDVBuff.Plugins.PickupEscape;

/// <summary>
/// 「可拾取」面板的识别模板。两张 PNG 与「背包辅助」用的是同一批 1920×1080 游戏截图裁剪：
///   pickup_all   —— 面板下方的「拾取全部」文案（刻意不带左边的按键框，改键后照样认得出）；
///   quick_replace —— 面板第一行右侧的「快捷替换」按钮。
/// 只要认出其中任意一张，就说明「可拾取」面板已经打开了。
/// </summary>
internal static class PickupEscapeTemplates
{
    public const string PickupAll = "pickup_all";
    public const string QuickReplace = "quick_replace";

    public const int ReferenceWidth = 1920;
    public const int ReferenceHeight = 1080;

    /// <summary>模板在 1920×1080 参考帧中的位置，仅用于日志与参考。</summary>
    private static readonly Dictionary<string, Rect> ReferenceBoxes = new(StringComparer.Ordinal)
    {
        [PickupAll] = new(1432, 564, 146, 52),
        [QuickReplace] = new(1418, 342, 144, 52)
    };

    /// <summary>搜索区域（相对客户区归一化），把匹配范围限制在面板实际出现的区域内。</summary>
    private static readonly Dictionary<string, (double X1, double Y1, double X2, double Y2)> SearchRegions =
        new(StringComparer.Ordinal)
        {
            [PickupAll] = (0.60, 0.24, 0.95, 0.66),
            [QuickReplace] = (0.60, 0.24, 0.95, 0.66)
        };

    public static IReadOnlyCollection<string> All { get; } = [PickupAll, QuickReplace];

    public static Rect GetReferenceBox(string name) => ReferenceBoxes[name];

    public static Rect GetSearchRegion(string name, Size frameSize)
    {
        var region = SearchRegions[name];
        var x1 = (int)Math.Round(region.X1 * frameSize.Width);
        var y1 = (int)Math.Round(region.Y1 * frameSize.Height);
        var x2 = (int)Math.Round(region.X2 * frameSize.Width);
        var y2 = (int)Math.Round(region.Y2 * frameSize.Height);
        x1 = Math.Clamp(x1, 0, Math.Max(0, frameSize.Width - 1));
        y1 = Math.Clamp(y1, 0, Math.Max(0, frameSize.Height - 1));
        x2 = Math.Clamp(x2, x1 + 1, frameSize.Width);
        y2 = Math.Clamp(y2, y1 + 1, frameSize.Height);
        return new Rect(x1, y1, x2 - x1, y2 - y1);
    }

    public static Mat LoadGray(string name)
    {
        var suffix = $".{name}.png";
        var assembly = typeof(PickupEscapeTemplates).Assembly;
        var resourceName = Array.Find(
            assembly.GetManifestResourceNames(),
            candidate => candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            throw new InvalidOperationException($"找不到嵌入的识别模板：{name}");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"无法读取嵌入的识别模板：{resourceName}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var image = Cv2.ImDecode(buffer.ToArray(), ImreadModes.Grayscale);
        if (image.Empty())
        {
            image.Dispose();
            throw new InvalidOperationException($"识别模板解码失败：{name}");
        }

        return image;
    }
}
