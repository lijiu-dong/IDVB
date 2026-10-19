using System.Security.Cryptography;
using System.Text.Json;
using IDVBuff.UpdateCore;
namespace IDVBuff.Features.Maps;
public sealed record MapCoCreationOwnedPublication(MapSubscriptionRecord Publication, IReadOnlyList<MapRecord> Maps);
public sealed record MapCoCreationOwnedClass(MapSubscriptionRecord Publication, string Name, IReadOnlyList<MapRecord> Maps);
public sealed class MapCoCreationTemplateAnchor
{
    public string Key { get; set; } = string.Empty;
    public NormalizedRectangle Bounds { get; set; } = new();
}
public sealed class MapCoCreationTemplateFloor
{
    public string FloorKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public int WorldFloor { get; set; }
    public string ImageFile { get; set; } = string.Empty;
    public string ImageSha256 { get; set; } = string.Empty;
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public NormalizedRectangle RecognitionRegion { get; set; } = new();
    public List<MapCoCreationTemplateAnchor> Anchors { get; set; } = [];
}
public sealed class MapCoCreationTemplate
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal string? PackageRoot { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int Difficulty { get; set; }
    public string SourceLevelId { get; set; } = string.Empty;
    public string PreviewFile { get; set; } = string.Empty;
    public List<MapCoCreationTemplateFloor> Floors { get; set; } = [];
    public string DifficultyName => Difficulty == 3 ? "困难" : "噩梦";
}
public sealed partial class MapCoCreationTemplateCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string _root;
    private readonly bool _installedStore;
    public MapCoCreationTemplateCatalog(string? root = null) =>
        (_root, _installedStore) = (Path.GetFullPath(root ?? Path.Combine(AppDataPaths.RootDirectory, "CoCreationTemplates")), root is null);
    public MapCoCreationTemplateCatalog(string root, bool installedStore) =>
        (_root, _installedStore) = (Path.GetFullPath(root), installedStore);
    public async Task<IReadOnlyList<MapCoCreationTemplate>> LoadAsync(CancellationToken cancellationToken = default)
        => _installedStore ? (await LoadClassesAsync(cancellationToken)).SelectMany(c => c.Templates).ToArray()
            : (await LoadDocumentAsync(cancellationToken)).Templates;

    private async Task<CatalogDocument> LoadDocumentAsync(CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(_root, "catalog.json"), cancellationToken);
        var catalog = JsonSerializer.Deserialize<CatalogDocument>(bytes, JsonOptions)
            ?? throw new InvalidDataException("共创模板目录为空。");
        if (catalog.SchemaVersion != 2 || catalog.Layout != "horizontal" || catalog.VariantGroups is null
            || catalog.Templates.Select(t => t.Id).Distinct().Count() != catalog.Templates.Count)
            throw new InvalidDataException("共创模板目录版本或标识无效。");
        foreach (var template in catalog.Templates) Validate(template);
        ValidateVariantGroups(catalog.Templates, catalog.VariantGroups);
        return catalog;
    }
    private void Validate(MapCoCreationTemplate template)
    {
        if (template.Difficulty is not (3 or 4) || string.IsNullOrWhiteSpace(template.Title)
            || template.Floors.Count < 2 || template.Floors.Select(f => f.FloorKey).Distinct().Count() != template.Floors.Count)
            throw new InvalidDataException("共创模板楼层元数据无效。");
        ResolvePath(template.PreviewFile);
        var ordered = template.Floors.OrderBy(f => f.SortOrder).ToArray();
        if (ordered[0].FloorKey != "1f" || ordered[1].FloorKey != "2f"
            || ordered.Select(f => f.SortOrder).Distinct().Count() != ordered.Length)
            throw new InvalidDataException("共创模板楼层顺序无效。");
        foreach (var floor in ordered)
        {
            ResolvePath(floor.ImageFile);
            if (string.IsNullOrWhiteSpace(floor.DisplayName) || floor.ImageWidth <= 0 || floor.ImageHeight <= 0
                || !IsInside(floor.RecognitionRegion) || floor.ImageFile != ordered[0].ImageFile
                || floor.ImageSha256 != ordered[0].ImageSha256 || floor.ImageWidth != ordered[0].ImageWidth
                || floor.ImageHeight != ordered[0].ImageHeight || floor.Anchors.Any(a => !IsInside(a.Bounds))
                || floor.Anchors.Select(a => a.Key).Distinct().Count() != floor.Anchors.Count)
                throw new InvalidDataException("共创模板原图或裁剪区域无效。");
        }
        if (!ordered[0].Anchors.Any(a => a.Key == "main-entrance")
            || !ordered[0].Anchors.Any(a => a.Key == "side-entrance")
            || !ordered[1].Anchors.Any(a => a.Key == "second-floor-primary"))
            throw new InvalidDataException("共创模板缺少门特征。");
    }
    // Prepared recognition images are full-resolution crops of the individual floor.
    // The legacy 480px preview is a two-floor overview and must not be enlarged as a cover.
    public string GetPreviewPath(MapCoCreationTemplate template) =>
        _installedStore && template.PackageRoot is { } root ? new MapCoCreationTemplateCatalog(root).GetPreviewPath(template)
            : ResolvePath($"prepared/{template.Difficulty}/{template.Id}/floor-1-recognition.png");
    public async Task<MapDraft> CreateDraftAsync(MapCoCreationTemplate template, CancellationToken cancellationToken = default)
    {
        if (_installedStore && template.PackageRoot is { } root)
            return await new MapCoCreationTemplateCatalog(root).CreateDraftAsync(template, cancellationToken);
        Validate(template);
        var ordered = template.Floors.OrderBy(f => f.SortOrder).ToArray();
        var imagePath = ResolvePath(ordered[0].ImageFile);
        var bytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), ordered[0].ImageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模板图片校验失败，请重新安装模板资源。");
        var draft = new MapDraft
        {
            Title = template.Title, Class = $"共创地图 · {template.DifficultyName}",
            IsCoCreationTemplate = true,
            FloorOnePath = imagePath, FloorTwoPath = imagePath,
            FloorPaths = ordered.ToDictionary(f => f.FloorKey, _ => imagePath),
            Floors = ordered.Select(f => new FloorDefinition { Key = f.FloorKey, DisplayName = f.DisplayName,
                SortOrder = f.SortOrder, ImageWidth = f.ImageWidth, ImageHeight = f.ImageHeight }).ToList(),
            Source = "co-creation-template", SourceVisualSha256 = ordered[0].ImageSha256
        };
        draft.Recognition.Floors = ordered.ToDictionary(f => f.FloorKey, f => new FloorRecognitionProfile
        {
            FloorKey = f.FloorKey, Floor = f.FloorKey == "1f" ? MapFloor.First : MapFloor.Second,
            RecognitionRegion = f.RecognitionRegion.Clone()
        });
        draft.Recognition.NormalizeForFloors(draft.Floors);
        foreach (var floor in ordered)
        foreach (var anchor in floor.Anchors)
        {
            var target = draft.Recognition.GetFloor(floor.FloorKey)!.FindAnchor(anchor.Key)
                ?? throw new InvalidDataException("共创模板门特征类型无效。");
            target.Bounds = anchor.Bounds.Clone();
        }
        draft.Navigation = await LoadNavigationAsync(template, cancellationToken);
        await BindFixedStructureAsync(draft);
        return draft;
    }
    private string ResolvePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || relative.Contains('\\') || relative.Split('/').Any(segment => segment is ".." or "." or ""))
            throw new InvalidDataException("共创模板资源路径无效。");
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("共创模板资源超出模板目录。");
        return full;
    }
    private static bool IsInside(NormalizedRectangle rectangle) => rectangle.IsValid
        && double.IsFinite(rectangle.X) && double.IsFinite(rectangle.Y)
        && double.IsFinite(rectangle.Width) && double.IsFinite(rectangle.Height)
        && rectangle.X >= 0 && rectangle.Y >= 0 && rectangle.X + rectangle.Width <= 1.000001
        && rectangle.Y + rectangle.Height <= 1.000001;
    private sealed class CatalogDocument
    {
        public int SchemaVersion { get; set; }
        public string Layout { get; set; } = string.Empty;
        public List<MapCoCreationTemplate> Templates { get; set; } = [];
        public List<MapCoCreationTemplateVariantGroup> VariantGroups { get; set; } = [];
    }
}
