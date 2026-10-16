namespace IDVBuff.Features.Maps;

public sealed record MapCoCreationTemplateClass(string Name, int Difficulty,
    IReadOnlyList<MapCoCreationTemplate> Templates);

public sealed partial class MapCoCreationTemplateCatalog
{
    public async Task<IReadOnlyList<MapCoCreationTemplateClass>> LoadClassesAsync(CancellationToken cancellationToken = default)
    {
        var templates = await LoadAsync(cancellationToken);
        return templates.GroupBy(template => template.Difficulty).OrderBy(group => group.Key)
            .Select(group => new MapCoCreationTemplateClass(ClassName(group.Key), group.Key, group.ToArray())).ToArray();
    }

    public async Task<MapImportBatchResult> AddClassAsync(MapRepository repository,
        MapCoCreationTemplateClass templateClass, CancellationToken cancellationToken = default)
    {
        if (templateClass.Templates.Count == 0 || templateClass.Difficulty is not (3 or 4)
            || templateClass.Name != ClassName(templateClass.Difficulty)
            || templateClass.Templates.Any(template => template.Difficulty != templateClass.Difficulty))
            throw new InvalidDataException("共创模板地图类无效。");
        var manifestPath = ResolvePath($"prepared/{templateClass.Difficulty}/manifest.json");
        if (!File.Exists(manifestPath))
            throw new InvalidDataException("缺少共创模板预制资源，请重新安装模板资源。");
        var manifest = System.Text.Json.JsonSerializer.Deserialize<MapCoCreationPreparedClass>(
            await File.ReadAllBytesAsync(manifestPath, cancellationToken), JsonOptions)
            ?? throw new InvalidDataException("共创模板预制资源为空。");
        var catalogHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            await File.ReadAllBytesAsync(Path.Combine(_root, "catalog.json"), cancellationToken)));
        if (manifest.SchemaVersion != 1 || manifest.Difficulty != templateClass.Difficulty
            || !string.Equals(manifest.CatalogSha256, catalogHash, StringComparison.OrdinalIgnoreCase)
            || !manifest.Maps.Select(map => map.TemplateId).SequenceEqual(templateClass.Templates.Select(template => template.Id)))
            throw new InvalidDataException("共创模板预制资源与目录不一致，请重新安装模板资源。");
        for (var index = 0; index < manifest.Maps.Count; index++)
        {
            MapOperationProgress.Report(.2 * index / manifest.Maps.Count, "正在解析共创模板……");
            var template = templateClass.Templates[index];
            Validate(template);
            var record = manifest.Maps[index].Record;
            if (record.Title != template.Title || record.Floors.Count != template.Floors.Count
                || record.SubscriptionId is not null || record.Navigation is not null)
                throw new InvalidDataException("共创模板预制地图元数据无效。");
            foreach (var floor in template.Floors)
            {
                var stored = record.Floors.SingleOrDefault(item => item.Key == floor.FloorKey);
                var profile = record.Recognition.GetFloor(floor.FloorKey);
                if (stored is null || profile is null || stored.ImageSha256 != floor.ImageSha256
                    || stored.ImageWidth != floor.ImageWidth || stored.ImageHeight != floor.ImageHeight
                    || !SameRectangle(profile.RecognitionRegion, floor.RecognitionRegion)
                    || floor.Anchors.Any(anchor => !SameRectangle(profile.FindAnchor(anchor.Key)?.Bounds, anchor.Bounds)))
                    throw new InvalidDataException("共创模板预制楼层与原图或门标注不一致。");
            }
            record.Navigation = await LoadNavigationAsync(template, cancellationToken);
        }
        return await MapOperationProgress.RunAsync(.2, 1, () => repository.AddPreparedTemplateClassAsync(templateClass.Name, manifest.Maps,
            Path.GetDirectoryName(manifestPath)!, cancellationToken));
    }

    private static string ClassName(int difficulty) => $"共创地图 · {(difficulty == 3 ? "困难" : "噩梦")}";

    // Canonical normalization clamps width to 1-X. At the image boundary
    // that subtraction can differ from the catalog by a floating-point bit.
    private static bool SameRectangle(NormalizedRectangle? left, NormalizedRectangle right) => left is not null
        && Math.Abs(left.X - right.X) <= 1e-12 && Math.Abs(left.Y - right.Y) <= 1e-12
        && Math.Abs(left.Width - right.Width) <= 1e-12 && Math.Abs(left.Height - right.Height) <= 1e-12;
}

internal sealed class MapCoCreationPreparedClass
{
    public int SchemaVersion { get; set; }
    public int Difficulty { get; set; }
    public string CatalogSha256 { get; set; } = string.Empty;
    public List<MapCoCreationPreparedMap> Maps { get; set; } = [];
}

internal sealed class MapCoCreationPreparedMap
{
    public string TemplateId { get; set; } = string.Empty;
    public MapRecord Record { get; set; } = new();
    public Dictionary<string, string> Files { get; set; } = [];
}
