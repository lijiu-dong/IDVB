using System.Security.Cryptography;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    // Bundled templates are already processed by the production save pipeline.
    // Install their assets without decoding/re-encoding each floor or committing
    // the entire catalog once per map. The existing journal covers crash recovery.
    internal async Task<MapImportBatchResult> AddPreparedTemplateClassAsync(string className,
        IReadOnlyList<MapCoCreationPreparedMap> templates,
        IReadOnlyList<MapCoCreationTemplateVariantGroup> variantGroups, string preparedRoot, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        var createdDirectories = new List<string>();
        string? journalPath = null;
        var committed = false;
        try
        {
            var catalog = await ReadCatalogAsync();
            var localClass = BuildUniqueImportedClassName(className, catalog.Classes);
            var maps = templates.Select((template, index) =>
            {
                var record = template.Record.Clone();
                record.Id = Guid.NewGuid();
                record.IsCoCreationTemplate = true;
                record.Class = localClass;
                record.SequenceNumber = index + 1;
                record.AcquisitionKind = MapAcquisitionKind.ImportedPackage;
                record.CreatedAt = record.UpdatedAt = DateTimeOffset.UtcNow;
                return record;
            }).ToArray();
            var localIds = templates.Select((template, index) => (template.TemplateId, maps[index].Id))
                .ToDictionary(item => item.TemplateId, item => item.Id, StringComparer.Ordinal);
            var groups = variantGroups.Select(group => new MapVariantGroup
            {
                Id = Guid.NewGuid(), Class = localClass, PaletteSlot = group.PaletteSlot,
                MapIds = group.TemplateIds.Select(id => localIds[id]).ToList()
            }).ToArray();
            var journal = new IdvmImportJournal
            {
                ProcessId = Environment.ProcessId,
                CreatedClasses = [localClass],
                ImportedMapIds = maps.Select(map => map.Id).ToList(),
                ImportedVariantGroupIds = groups.Select(group => group.Id).ToList()
            };
            journalPath = Path.Combine(_rootDirectory, $".idvm-import-{Guid.NewGuid():N}.json");
            WriteImportJournal(journalPath, journal);
            for (var index = 0; index < maps.Length; index++)
            {
                MapOperationProgress.Report(.95 * index / maps.Length, "正在写入共创模板……");
                token.ThrowIfCancellationRequested();
                var template = templates[index];
                // A template id is a directory name, never a relative path.
                var sourceDirectory = GetSafeMapFilePath(preparedRoot, template.TemplateId);
                var targetDirectory = GetMapDirectory(maps[index].Id);
                if (Directory.Exists(targetDirectory)) throw new IOException("模板目标目录已存在。");
                Directory.CreateDirectory(targetDirectory);
                createdDirectories.Add(targetDirectory);
                foreach (var (fileName, expectedHash) in template.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var sourcePath = GetSafeMapFilePath(sourceDirectory, fileName);
                    var targetPath = GetSafeMapFilePath(targetDirectory, fileName);
                    await using (var source = File.OpenRead(sourcePath))
                    await using (var destination = File.Create(targetPath))
                        await source.CopyToAsync(destination, token);
                    await using var copied = File.OpenRead(targetPath);
                    var actual = Convert.ToHexString(await SHA256.HashDataAsync(copied, token));
                    if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("共创模板预制图片校验失败，请重新安装模板资源。");
                }
                BindPreparedTemplateAssets(maps[index], template.Files, targetDirectory);
                ValidateFloorDefinitions(maps[index]);
                ValidateFloorBindingsFast(maps[index]);
            }
            token.ThrowIfCancellationRequested();
            catalog.Classes.Add(localClass);
            catalog.ClassProperties[localClass] = new MapClassProperties();
            catalog.Maps.AddRange(maps);
            if (maps[0].TemplateOrigin is { } origin)
                catalog.TemplateInstances.Add(new() { Id = origin.InstanceId, ClassId = origin.ClassId,
                    LocalClass = localClass, KnownMapIds = templates.Select(t => t.TemplateId).ToHashSet(StringComparer.Ordinal),
                    VariantBaselines = CloneTemplateVariants(variantGroups) });
            catalog.VariantGroups.AddRange(groups);
            MapOperationProgress.Report(.98, "正在保存模板索引……");
            await WriteCatalogAsync(catalog);
            committed = true;
            journal.Completed = true;
            WriteImportJournal(journalPath, journal);
            File.Delete(journalPath);
            return new MapImportBatchResult([localClass], maps.Select(map => map.Clone()).ToArray(),
                groups.Select(group => group.Clone()).ToArray());
        }
        catch
        {
            // Before the atomic catalog commit no user data refers to these new
            // directories. After it, leave the journal for normal recovery.
            if (!committed)
            {
                foreach (var directory in createdDirectories)
                    Directory.Delete(directory, recursive: true);
                if (journalPath is not null && File.Exists(journalPath)) File.Delete(journalPath);
            }
            throw;
        }
        finally { Gate.Release(); }
    }

    internal static void ValidatePreparedTemplateAssets(MapCoCreationPreparedMap template, string directory)
    {
        var map = template.Record.Clone();
        if (map.Floors.Count == 0 || !string.Equals(map.SourceVisualSha256, map.Floors[0].ImageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模板来源原图绑定无效。");
        BindPreparedTemplateAssets(map, template.Files, directory);
        ValidateFloorDefinitions(map);
    }

    private static void BindPreparedTemplateAssets(MapRecord map, IReadOnlyDictionary<string, string> files, string directory)
    {
        (long length, long ticks) Stamp(string name, string hash)
        {
            if (!files.TryGetValue(name, out var expected)
                || !string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("共创模板预制资源绑定无效。");
            var info = new FileInfo(GetSafeMapFilePath(directory, name));
            return (info.Length, info.LastWriteTimeUtc.Ticks);
        }
        foreach (var floor in map.Floors)
        {
            if (!string.Equals(floor.RecognitionSourceSha256, floor.ImageSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(floor.OverlaySourceSha256, floor.RecognitionSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("共创模板预制资源来源无效。");
            (floor.ImageFileLength, floor.ImageLastWriteUtcTicks) = Stamp(floor.ImageFileName, floor.ImageSha256);
            (floor.RecognitionFileLength, floor.RecognitionLastWriteUtcTicks) = Stamp(floor.RecognitionFileName, floor.RecognitionSha256);
            (floor.OverlayFileLength, floor.OverlayLastWriteUtcTicks) = Stamp(floor.OverlayFileName, floor.OverlaySha256);
            (floor.ThumbnailFileLength, floor.ThumbnailLastWriteUtcTicks) = Stamp(floor.ThumbnailFileName, floor.ThumbnailSha256);
            var line = floor.PrebuiltStructureLine;
            if (line?.IsComplete is not true
                || line.Width != floor.RecognitionWidth || line.Height != floor.RecognitionHeight
                || !string.Equals(line.SourceSha256, floor.RecognitionSha256, StringComparison.OrdinalIgnoreCase)
                || Stamp(line.FileName, line.Sha256).length != line.FileLength)
                throw new InvalidDataException("共创模板缺少有效的固定二值轮廓。");
            Stamp(line.AlgorithmFileName, line.AlgorithmSha256);
        }
    }
}
