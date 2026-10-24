namespace IDVBuff.Features.Maps;

public sealed record MapCoCreationTemplateClass(string Name, int Difficulty,
    IReadOnlyList<MapCoCreationTemplate> Templates)
{
    public IReadOnlyList<MapCoCreationTemplateVariantGroup> VariantGroups { get; init; } = [];
    internal string? PackageRoot { get; init; }
    internal IdvmxManifest? Package { get; init; }
    internal string? ManifestSha256 { get; init; }
}

public sealed class MapCoCreationTemplateVariantGroup
{
    public int Difficulty { get; set; }
    public int PaletteSlot { get; set; }
    // Stable source identities survive user-facing sequence/title changes.
    public List<string> TemplateIds { get; set; } = [];
}

public sealed partial class MapCoCreationTemplateCatalog
{
    public async Task<IReadOnlyList<MapCoCreationTemplateClass>> LoadClassesAsync(CancellationToken cancellationToken = default)
    {
        if (_installedStore) return await new IdvmxTemplatePackageService(_root).LoadClassesAsync(cancellationToken);
        var catalog = await LoadDocumentAsync(cancellationToken);
        return catalog.Templates.GroupBy(template => template.Difficulty).OrderBy(group => group.Key)
            .Select(group => new MapCoCreationTemplateClass(ClassName(group.Key), group.Key, group.ToArray())
            {
                VariantGroups = catalog.VariantGroups.Where(variant => variant.Difficulty == group.Key).ToArray()
            }).ToArray();
    }

    public async Task<MapImportBatchResult> AddClassAsync(MapRepository repository,
        MapCoCreationTemplateClass templateClass, CancellationToken cancellationToken = default)
    {
        if (_installedStore)
        {
            if (templateClass.PackageRoot is null) throw new InvalidDataException("模板包来源缺失。");
            return await new IdvmxTemplatePackageService(_root).AddClassAsync(repository, templateClass, cancellationToken);
        }
        var manifest = await ReadPreparedClassAsync(templateClass, cancellationToken);
        if (templateClass.Package is { } package)
        {
            var instance = Guid.NewGuid();
            foreach (var map in manifest.Maps)
                map.Record.TemplateOrigin = new(package.ClassId, instance, map.TemplateId, package.Version,
                    templateClass.ManifestSha256!, package.Maps.Single(m => m.Id == map.TemplateId).GeometrySha256);
        }
        return await MapOperationProgress.RunAsync(.2, 1, () => repository.AddPreparedTemplateClassAsync(templateClass.Name, manifest.Maps,
            templateClass.VariantGroups, Path.Combine(_root, "prepared", templateClass.Difficulty.ToString()), cancellationToken));
    }

    internal async Task<MapCoCreationPreparedClass> ReadPreparedClassAsync(MapCoCreationTemplateClass templateClass, CancellationToken cancellationToken)
    {
        if (templateClass.Templates.Count == 0 || templateClass.Difficulty is not (3 or 4)
            || templateClass.Templates.Any(template => template.Difficulty != templateClass.Difficulty))
            throw new InvalidDataException("共创模板地图类无效。");
        ValidateVariantGroups(templateClass.Templates, templateClass.VariantGroups);
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
        return manifest;
    }

    private static void ValidateVariantGroups(IReadOnlyList<MapCoCreationTemplate> templates,
        IReadOnlyList<MapCoCreationTemplateVariantGroup> groups)
    {
        var byId = templates.ToDictionary(template => template.Id, StringComparer.Ordinal);
        var members = new HashSet<string>(StringComparer.Ordinal);
        var slots = new HashSet<(int Difficulty, int Slot)>();
        foreach (var group in groups)
        {
            if (group is null || group.TemplateIds is null || group.Difficulty is not (3 or 4)
                || group.PaletteSlot is < 0 or >= MapVariantGroup.PaletteSize
                || !slots.Add((group.Difficulty, group.PaletteSlot)) || group.TemplateIds.Count < 2)
                throw new InvalidDataException("共创模板变体组合的难度、颜色槽或成员数量无效。");
            foreach (var id in group.TemplateIds)
                if (string.IsNullOrWhiteSpace(id) || !byId.TryGetValue(id, out var template)
                    || template.Difficulty != group.Difficulty || !members.Add(id))
                    throw new InvalidDataException("共创模板变体组合包含缺失、重复或跨难度地图。");
        }
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
