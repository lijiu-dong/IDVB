using System.Security.Cryptography;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCoCreationTemplateCatalog
{
    // A self-contained IDVM carries the authority for templates absent from this
    // installation. Validate those exact assets instead of requiring a catalog.
    private static async Task ValidatePortableFixedStructureAsync(MapDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.SourceVisualSha256) || draft.Floors.Count == 0
            || string.IsNullOrWhiteSpace(draft.PrebuiltStructureAlgorithmPath))
            throw new InvalidDataException("共创地图包缺少完整的固定轮廓资源。");
        var algorithm = await new IdvaStructureLineEngine().LoadAsync(draft.PrebuiltStructureAlgorithmPath);
        foreach (var floor in draft.Floors)
        {
            if (draft.Recognition.GetFloor(floor.Key) is not { FreeCropPoints.Count: 0 })
                throw new InvalidDataException("固定轮廓不支持自由裁剪。");
            var asset = floor.PrebuiltStructureLine;
            if (asset?.IsComplete is not true || !draft.PrebuiltStructureLinePaths.TryGetValue(floor.Key, out var path)
                || !File.Exists(path) || new FileInfo(path).Length != asset.FileLength
                || algorithm.AlgorithmId != asset.AlgorithmId || algorithm.Sha256 != asset.AlgorithmSha256
                || algorithm.SchemaVersion != asset.AlgorithmSchemaVersion)
                throw new InvalidDataException("共创地图包的固定轮廓或算法绑定无效。");
            await using var stream = File.OpenRead(path);
            if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream)), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("共创地图包的固定轮廓摘要不匹配。");
            using var image = Cv2.ImRead(path, ImreadModes.Unchanged);
            if (image.Empty() || image.Width != asset.Width || image.Height != asset.Height)
                throw new InvalidDataException("共创地图包的固定轮廓尺寸不匹配。");
        }
    }
}

public sealed partial class MapRepository
{
    private async Task<bool> RebindPortableFixedFloorAsync(MapRecord map, FloorDefinition floor)
    {
        var asset = floor.PrebuiltStructureLine;
        if (asset?.IsComplete is not true)
            throw new InvalidDataException("共创固定轮廓登记缺失，请重新导入原始 IDVM。");
        // Recognition has just been refreshed, so the public getter's source-hash
        // gate cannot be used until this verified rebind is complete.
        var linePath = GetSafeMapFilePath(GetMapDirectory(map.Id), asset.FileName);
        var algorithmPath = GetSafeMapFilePath(GetMapDirectory(map.Id), asset.AlgorithmFileName);
        if (!string.Equals(floor.ImageSha256, map.SourceVisualSha256, StringComparison.OrdinalIgnoreCase)
            || floor.RecognitionWidth != asset.Width || floor.RecognitionHeight != asset.Height
            || !File.Exists(linePath) || new FileInfo(linePath).Length != asset.FileLength || !File.Exists(algorithmPath))
            throw new InvalidDataException("共创固定轮廓资源缺失或几何不一致，请重新导入原始 IDVM。");
        if (!string.Equals(await ComputeFileSha256Async(linePath, default), asset.Sha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(await ComputeFileSha256Async(algorithmPath, default), asset.AlgorithmSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("共创固定轮廓资源已损坏，请重新导入原始 IDVM。");
        var changed = asset.SourceSha256 != floor.RecognitionSha256;
        asset.SourceSha256 = floor.RecognitionSha256;
        return changed;
    }

    private void BindStoredPortableFixedStructure(MapDraft draft, MapRecord stored)
    {
        MapCoCreationTemplateCatalog.ValidateFixedGeometry(stored, draft.Floors, draft.Recognition);
        draft.PrebuiltStructureLinePaths.Clear();
        foreach (var floor in draft.Floors)
        {
            var canonical = stored.Floors.Single(item => item.Key == floor.Key);
            floor.PrebuiltStructureLine = canonical.PrebuiltStructureLine?.Clone()
                ?? throw new InvalidDataException("已导入共创地图缺少固定轮廓。");
            draft.PrebuiltStructureLinePaths[floor.Key] = GetPrebuiltStructureLinePath(stored, floor.Key);
            draft.PrebuiltStructureAlgorithmPath = GetPrebuiltStructureAlgorithmPath(stored, floor.Key);
        }
    }
}
