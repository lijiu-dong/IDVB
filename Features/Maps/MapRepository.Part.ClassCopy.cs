namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    public Task<string> CopyClassAsync(string className) => Task.Run(async () =>
    {
        await Gate.WaitAsync();
        var createdDirectories = new List<string>();
        var committed = false;
        try
        {
            MapOperationProgress.Report(0, "正在读取地图类……");
            var catalog = await ReadCatalogAsync();
            var sourceName = catalog.Classes.SingleOrDefault(name => string.Equals(
                name, className, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("找不到要复制的地图类。");
            var namingRoot = sourceName;
            var suffix = System.Text.RegularExpressions.Regex.Match(sourceName, @"^(.*) - 复制(?: [2-9][0-9]*| 1[0-9]+)?$");
            if (suffix.Success && catalog.Classes.Contains(suffix.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
                namingRoot = suffix.Groups[1].Value;
            var copyName = namingRoot + " - 复制";
            for (var number = 2; catalog.Classes.Contains(copyName, StringComparer.OrdinalIgnoreCase); number++)
                copyName = $"{namingRoot} - 复制 {number}";

            var sourceMaps = catalog.Maps.Where(map => string.Equals(
                map.Class, sourceName, StringComparison.OrdinalIgnoreCase)).ToArray();
            var idMapping = sourceMaps.ToDictionary(map => map.Id, _ => Guid.NewGuid());
            var copies = sourceMaps.Select(map =>
            {
                var copy = map.Clone();
                copy.Id = idMapping[map.Id];
                copy.Class = copyName;
                return copy;
            }).ToArray();
            var files = new List<(string Source, string Target, long Length)>();
            void PlanDirectory(string source, string target)
            {
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("地图资源目录不能是目录链接。");
                Directory.CreateDirectory(target);
                createdDirectories.Add(target);
                foreach (var entry in Directory.EnumerateFileSystemEntries(source))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("地图资源不能包含链接。");
                    var destination = Path.Combine(target, Path.GetFileName(entry));
                    if ((attributes & FileAttributes.Directory) != 0)
                        PlanDirectory(entry, destination);
                    else
                        files.Add((entry, destination, new FileInfo(entry).Length));
                }
            }
            foreach (var map in sourceMaps)
            {
                PlanDirectory(GetMapDirectory(map.Id), GetMapDirectory(idMapping[map.Id]));
                var originalsRoot = Path.Combine(_rootDirectory, ".downsample-originals");
                var originals = GetDownsampleOriginalDirectory(originalsRoot, map.Id);
                if (Directory.Exists(originals))
                    PlanDirectory(originals, GetDownsampleOriginalDirectory(originalsRoot, idMapping[map.Id]));
            }

            var totalBytes = files.Sum(file => file.Length);
            long copiedBytes = 0;
            var buffer = new byte[128 * 1024];
            IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedArray(buffer, "Repository.CopyClass.TransferBuffer");
            foreach (var file in files)
            {
                // The shared transfer buffer already batches each file's I/O.
                // A second 128KiB FileStream buffer allocates a large object for
                // every non-empty tail block, including each small image.
                await using (var input = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 1, useAsync: true))
                await using (var output = new FileStream(file.Target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 1, useAsync: true))
                {
                    int count;
                    while ((count = await input.ReadAsync(buffer)) != 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, count));
                        copiedBytes += count;
                        MapOperationProgress.Report(.05 + .9 * copiedBytes / Math.Max(1, totalBytes), "正在复制地图资源……");
                    }
                }
                File.SetLastWriteTimeUtc(file.Target, File.GetLastWriteTimeUtc(file.Source));
            }

            catalog.Classes.Add(copyName);
            catalog.ClassProperties[copyName] = GetClassProperties(catalog, sourceName).Clone();
            catalog.Maps.AddRange(copies);
            foreach (var instance in catalog.TemplateInstances.Where(i => i.LocalClass == sourceName).ToArray())
            {
                var copyInstance = new MapTemplateInstance { Id = Guid.NewGuid(), ClassId = instance.ClassId,
                    LocalClass = copyName, KnownMapIds = new(instance.KnownMapIds, StringComparer.Ordinal),
                    VariantBaselines = CloneTemplateVariants(instance.VariantBaselines) };
                catalog.TemplateInstances.Add(copyInstance);
                foreach (var copy in copies.Where(m => m.TemplateOrigin?.InstanceId == instance.Id))
                    copy.TemplateOrigin = copy.TemplateOrigin! with { InstanceId = copyInstance.Id };
            }
            catalog.VariantGroups.AddRange(catalog.VariantGroups
                .Where(group => string.Equals(group.Class, sourceName, StringComparison.OrdinalIgnoreCase))
                .Select(group => new MapVariantGroup
                {
                    Id = Guid.NewGuid(), Class = copyName, PaletteSlot = group.PaletteSlot,
                    MapIds = group.MapIds.Select(id => idMapping[id]).ToList()
                }).ToArray());
            MapOperationProgress.Report(.97, "正在保存地图类……");
            await WriteCatalogAsync(catalog);
            committed = true;
            MapOperationProgress.Report(1, "地图类复制完成");
            return copyName;
        }
        finally
        {
            try
            {
                if (!committed)
                    foreach (var directory in createdDirectories.AsEnumerable().Reverse())
                        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            finally { Gate.Release(); }
        }
    });
}
