namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    private static async Task ImportFixedTemplateStructureAsync(
        string staging, FloorDefinition floor, string key, MapDraft draft)
    {
        var asset = floor.PrebuiltStructureLine
            ?? throw new InvalidDataException("共创模板缺少固定预制轮廓。");
        if (!asset.IsComplete || !string.Equals(floor.ImageSha256, draft.SourceVisualSha256, StringComparison.OrdinalIgnoreCase)
            || floor.RecognitionWidth != asset.Width || floor.RecognitionHeight != asset.Height)
            throw new InvalidOperationException("共创模板的原图或尺寸发生变化，不能绑定固定轮廓。");
        // Background painting affects display/recognition bytes, never the
        // template's structure. Rebind the source hash without re-executing IDVA.
        asset.SourceSha256 = floor.RecognitionSha256;
        await ReplaceFileAsync(draft.PrebuiltStructureLinePaths[key], Path.Combine(staging, asset.FileName), default);
        await ReplaceFileAsync(draft.PrebuiltStructureAlgorithmPath!, Path.Combine(staging, asset.AlgorithmFileName), default);
    }

    private async Task<bool> RestoreFixedTemplateFloorAsync(
        MapRecord map, FloorDefinition floor, MapCoCreationTemplateCatalog.FixedTemplateStructure template)
    {
        var canonical = template.Floors.Single(item => item.Key == floor.Key);
        var asset = canonical.PrebuiltStructureLine?.Clone()
            ?? throw new InvalidDataException("模板楼层缺少固定预制轮廓。");
        if (!asset.IsComplete || !string.Equals(floor.ImageSha256, canonical.ImageSha256, StringComparison.OrdinalIgnoreCase)
            || floor.RecognitionWidth != asset.Width || floor.RecognitionHeight != asset.Height)
            throw new InvalidOperationException("共创模板的原图或尺寸发生变化，不能恢复固定轮廓。");
        asset.SourceSha256 = floor.RecognitionSha256;
        var changed = !map.IsCoCreationTemplate
            || System.Text.Json.JsonSerializer.Serialize(floor.PrebuiltStructureLine)
                != System.Text.Json.JsonSerializer.Serialize(asset);
        foreach (var (name, hash) in new[] { (asset.FileName, asset.Sha256), (asset.AlgorithmFileName, asset.AlgorithmSha256) })
        {
            var source = await template.VerifyFileAsync(name, hash);
            var destination = GetSafeMapFilePath(GetMapDirectory(map.Id), name);
            if (!File.Exists(destination) || !string.Equals(await ComputeFileSha256Async(destination, default), hash, StringComparison.OrdinalIgnoreCase))
            {
                await ReplaceFileAsync(source, destination, default);
                changed = true;
            }
        }
        floor.PrebuiltStructureLine = asset;
        map.IsCoCreationTemplate = true;
        if (changed)
            MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                $"共创模板固定轮廓已更新 · map={map.DisplayName} · floor={floor.Key}",
                details: new() { ["mapId"] = map.Id, ["floor"] = floor.Key,
                    ["algorithmId"] = asset.AlgorithmId, ["prebuiltSha256"] = asset.Sha256 });
        return changed;
    }
}
