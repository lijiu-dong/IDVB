namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    /// <summary>
    /// 解析指定地图楼层侧门特征的生成源图像路径。
    /// 严格只使用预制二值轮廓图（prebuilt-{floorKey}.png），彻底删除从攻略地图裁剪灰度图的旧路径。
    /// </summary>
    private (string path, string sourceKind) ResolveSideEntranceFeatureSource(MapRecord record, string floorKey)
    {
        if (HasPrebuiltStructureLine(record, floorKey))
        {
            var prebuiltPath = GetPrebuiltStructureLinePath(record, floorKey);
            if (File.Exists(prebuiltPath)) return (prebuiltPath, "PrebuiltStructureLine");
        }
        // An explicit binding is authoritative, including immutable versioned
        // filenames. Never fall back to an older conventional file after update.
        if (record.Floors.SingleOrDefault(f => f.Key == floorKey)?.PrebuiltStructureLine is null)
        {
            var localPrebuilt = Path.Combine(GetMapDirectory(record.Id), $"prebuilt-{floorKey}.png");
            if (File.Exists(localPrebuilt)) return (localPrebuilt, "PrebuiltStructureLine");
        }
        throw new InvalidOperationException($"地图 {record.DisplayName} 楼层 {floorKey} 缺少预制二值轮廓图 prebuilt-{floorKey}.png。");
    }

    /// <summary>
    /// 在暂存目录（staging）中解析楼层侧门特征的生成源图像路径。
    /// 严格只使用预制二值轮廓图。
    /// </summary>
    private static (string path, string sourceKind) ResolveSideEntranceStagingFeatureSource(
        string stagingDirectory, string floorKey, string? boundFileName)
    {
        var prebuiltPath = GetSafeMapFilePath(stagingDirectory, boundFileName ?? $"prebuilt-{floorKey}.png");
        if (File.Exists(prebuiltPath))
            return (prebuiltPath, "PrebuiltStructureLine");
        throw new InvalidOperationException($"暂存目录缺少预制二值轮廓图 prebuilt-{floorKey}.png。");
    }


}
