using OpenCvSharp;

namespace IDVBuff.Plugins.FaithNoRecovery;

/// <summary>
/// 蓄力期间右侧那句「点击ESC，取消操作」的识别模板。
///
/// 两张 PNG 都是 1920×1080 游戏截图在 (1668,700) 处裁下的 228×41 文字条：
///   faith_esc_hint_a —— 蓄力未满那张（背景是暗色书架）
///   faith_esc_hint_b —— 蓄力已满那张（背景偏暖、更亮）
/// 文字本身完全一样，留着两张是为了让匹配对背景亮度变化更宽容（取两张里的较高分）。
/// 蓄力环不用模板，走几何检测（见 <see cref="FaithChargeRingDetector"/>）。
/// </summary>
internal static class FaithNoRecoveryTemplates
{
    public const string EscHintPartial = "faith_esc_hint_a";
    public const string EscHintFull = "faith_esc_hint_b";

    public const int ReferenceWidth = 1920;
    public const int ReferenceHeight = 1080;

    /// <summary>模板在 1920×1080 参考帧中的位置（仅用于日志与探针断言）。</summary>
    public static readonly Rect ReferenceBox = new(1668, 700, 228, 41);

    /// <summary>
    /// 搜索区域（相对客户区归一化）：文案固定在右下角，
    /// 但不同分辨率/窗口化下会整体平移，因此留出比模板大得多的范围。
    /// </summary>
    private static readonly (double X1, double Y1, double X2, double Y2) SearchRegion =
        (0.70, 0.52, 1.00, 0.84);

    public static IReadOnlyCollection<string> All { get; } =
        [EscHintPartial, EscHintFull];

    public static Rect GetSearchRegion(Size frameSize)
    {
        var x1 = (int)Math.Round(SearchRegion.X1 * frameSize.Width);
        var y1 = (int)Math.Round(SearchRegion.Y1 * frameSize.Height);
        var x2 = (int)Math.Round(SearchRegion.X2 * frameSize.Width);
        var y2 = (int)Math.Round(SearchRegion.Y2 * frameSize.Height);
        x1 = Math.Clamp(x1, 0, Math.Max(0, frameSize.Width - 1));
        y1 = Math.Clamp(y1, 0, Math.Max(0, frameSize.Height - 1));
        x2 = Math.Clamp(x2, x1 + 1, frameSize.Width);
        y2 = Math.Clamp(y2, y1 + 1, frameSize.Height);
        return new Rect(x1, y1, x2 - x1, y2 - y1);
    }

    public static Mat LoadGray(string name)
    {
        var suffix = $".{name}.png";
        var assembly = typeof(FaithNoRecoveryTemplates).Assembly;
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
