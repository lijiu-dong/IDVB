using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCoCreationTemplateCatalog
{
    private HashSet<string>? _templateSourceHashes;

    // Earlier template imports lost their Source label on SaveAsync. Recover
    // their origin from the original-artwork hash, independent of class naming.
    public bool IsTemplateDraft(MapDraft draft)
    {
        if (draft.IsCoCreationTemplate || draft.Source == "co-creation-template") return true;
        if (string.IsNullOrWhiteSpace(draft.SourceVisualSha256)) return false;
        if (_templateSourceHashes is null)
        {
            var catalogPath = Path.Combine(_root, "catalog.json");
            if (!File.Exists(catalogPath)) return false;
            var catalog = JsonSerializer.Deserialize<CatalogDocument>(File.ReadAllBytes(catalogPath), JsonOptions)
                ?? throw new InvalidDataException("共创模板目录为空。");
            _templateSourceHashes = catalog.Templates.SelectMany(template => template.Floors)
                .Select(floor => floor.ImageSha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        return _templateSourceHashes.Contains(draft.SourceVisualSha256);
    }
}
