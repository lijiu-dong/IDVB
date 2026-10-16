using System.Security.Cryptography;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCoCreationTemplateCatalog
{
    private readonly object _fixedStructureGate = new();
    private Task<Dictionary<string, FixedTemplateStructure>>? _fixedStructures;

    internal async Task<FixedTemplateStructure?> GetFixedStructureAsync(string? sourceHash)
    {
        if (_installedStore || string.IsNullOrWhiteSpace(sourceHash) || !File.Exists(Path.Combine(_root, "catalog.json")))
            return null;
        Task<Dictionary<string, FixedTemplateStructure>> load;
        lock (_fixedStructureGate)
            load = _fixedStructures ??= LoadFixedStructuresAsync();
        return (await load).GetValueOrDefault(sourceHash);
    }

    private async Task<Dictionary<string, FixedTemplateStructure>> LoadFixedStructuresAsync()
    {
        using var memory = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Template.LoadFixedStructures");
        var result = new Dictionary<string, FixedTemplateStructure>(StringComparer.OrdinalIgnoreCase);
        var catalogHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(_root, "catalog.json"))));
        foreach (var group in (await LoadAsync()).GroupBy(template => template.Difficulty))
        {
            var manifestPath = ResolvePath($"prepared/{group.Key}/manifest.json");
            var manifest = JsonSerializer.Deserialize<MapCoCreationPreparedClass>(await File.ReadAllBytesAsync(manifestPath), JsonOptions)
                ?? throw new InvalidDataException("共创模板预制资源为空。");
            if (manifest.SchemaVersion != 1 || manifest.Difficulty != group.Key
                || !string.Equals(manifest.CatalogSha256, catalogHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("共创模板固定轮廓与目录不一致。");
            IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedObject(manifest.Maps, "Template.PreparedRecords");
            foreach (var template in group)
            {
                var prepared = manifest.Maps.Single(map => map.TemplateId == template.Id);
                if (prepared.Record.Floors.Count != template.Floors.Count
                    || prepared.Record.Floors.Any(floor => floor.ImageSha256 != template.Floors[0].ImageSha256))
                    throw new InvalidDataException("共创模板固定轮廓来源无效。");
                var fixedStructure = new FixedTemplateStructure(prepared.Record.Floors.Select(floor =>
                    FixedTemplateFloor.Create(floor, prepared.Record.Recognition.GetFloor(floor.Key))).ToArray(), prepared.Files,
                    ResolvePath($"prepared/{group.Key}/{template.Id}"));
                if (!result.TryAdd(template.Floors[0].ImageSha256, fixedStructure))
                {
                    // Different level IDs can share byte-identical artwork.
                    // They may share this lookup only when every floor's crop
                    // and fixed binary geometry are identical as well.
                    var existing = result[template.Floors[0].ImageSha256];
                    existing.ValidateGeometry(prepared.Record.Floors, prepared.Record.Recognition);
                    if (prepared.Record.Floors.Any(floor =>
                        existing.Floors.Single(item => item.Key == floor.Key).PrebuiltStructureLine?.Sha256
                            != floor.PrebuiltStructureLine?.Sha256))
                        throw new InvalidDataException("相同模板原图存在不一致的固定轮廓。");
                }
            }
        }
        IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedObject(result, "Template.FixedGeometryCache");
        return result;
    }

    internal async Task BindFixedStructureAsync(MapDraft draft)
    {
        var template = await GetFixedStructureAsync(draft.SourceVisualSha256);
        if (template is null)
        {
            if (draft.IsCoCreationTemplate)
                await ValidatePortableFixedStructureAsync(draft);
            return;
        }
        template.ValidateGeometry(draft.Floors, draft.Recognition);
        draft.IsCoCreationTemplate = true;
        draft.PrebuiltStructureLinePaths.Clear();
        foreach (var floor in draft.Floors)
        {
            var asset = template.Floors.Single(item => item.Key == floor.Key).PrebuiltStructureLine
                ?? throw new InvalidDataException("模板楼层缺少固定二值轮廓。");
            floor.PrebuiltStructureLine = asset.Clone();
            draft.PrebuiltStructureLinePaths[floor.Key] = await template.VerifyFileAsync(asset.FileName, asset.Sha256);
            draft.PrebuiltStructureAlgorithmPath = await template.VerifyFileAsync(asset.AlgorithmFileName, asset.AlgorithmSha256);
        }
    }

    // This cache validates/rebinds fixed geometry; it must not own the prepared
    // map's editable annotations, navigation, anchors or background strokes.
    internal sealed record FixedTemplateFloor(string Key, string ImageSha256,
        PrebuiltStructureLineAsset? PrebuiltStructureLine, int? OrientationDegrees, NormalizedRectangle? RecognitionRegion)
    {
        internal static FixedTemplateFloor Create(FloorDefinition floor, FloorRecognitionProfile? profile) => new(
            floor.Key, floor.ImageSha256, floor.PrebuiltStructureLine?.Clone(),
            profile?.OrientationDegrees, profile?.RecognitionRegion?.Clone());
    }

    internal sealed record FixedTemplateStructure(IReadOnlyList<FixedTemplateFloor> Floors,
        IReadOnlyDictionary<string, string> Files, string Directory)
    {
        internal async Task<string> VerifyFileAsync(string name, string expectedHash)
        {
            if (Path.GetFileName(name) != name || !Files.TryGetValue(name, out var manifestHash)
                || !string.Equals(manifestHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("模板固定轮廓资源绑定无效。");
            var path = Path.Combine(Directory, name);
            await using var stream = File.OpenRead(path);
            if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream)), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("模板固定轮廓校验失败，请重新安装模板资源。");
            return path;
        }

        internal void ValidateGeometry(IReadOnlyList<FloorDefinition> floors, MapRecognitionProfile recognition)
        {
            if (floors.Count != Floors.Count || !floors.Select(f => f.Key).ToHashSet(StringComparer.Ordinal)
                .SetEquals(Floors.Select(f => f.Key)))
                throw new InvalidOperationException("共创模板的固定轮廓不支持修改楼层。");
            foreach (var floor in Floors)
                ValidateFixedFloor(recognition.GetFloor(floor.Key), floor.OrientationDegrees, floor.RecognitionRegion);
        }
    }

    internal static void ValidateFixedGeometry(MapRecord record, IReadOnlyList<FloorDefinition> floors, MapRecognitionProfile recognition)
    {
        if (floors.Count != record.Floors.Count || !floors.Select(f => f.Key).ToHashSet(StringComparer.Ordinal)
            .SetEquals(record.Floors.Select(f => f.Key)))
            throw new InvalidOperationException("共创模板的固定轮廓不支持修改楼层。");
        foreach (var floor in floors)
        {
            var expected = record.Recognition.GetFloor(floor.Key);
            ValidateFixedFloor(recognition.GetFloor(floor.Key), expected?.OrientationDegrees, expected?.RecognitionRegion);
        }
    }

    private static void ValidateFixedFloor(FloorRecognitionProfile? actual, int? orientation, NormalizedRectangle? region)
    {
        if (orientation is null || actual is null || actual.OrientationDegrees != orientation
            || actual.FreeCropPoints.Count != 0
            || (region is null ? actual.RecognitionRegion is not null : !SameRectangle(actual.RecognitionRegion, region)))
            throw new InvalidOperationException("共创模板的固定轮廓不支持修改裁剪区域或旋转。");
    }
}
