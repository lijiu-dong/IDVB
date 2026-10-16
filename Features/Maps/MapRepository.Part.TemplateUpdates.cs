using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    internal string TemplateRepositoryRoot => Path.GetFullPath(_rootDirectory);

    internal async Task<IdvmxImportResult> ApplyTemplatePackageAsync(MapCoCreationTemplateClass templateClass, CancellationToken token)
    {
        var package = templateClass.Package ?? throw new InvalidDataException("模板身份缺失。");
        var source = new MapCoCreationTemplateCatalog(templateClass.PackageRoot!);
        var prepared = await source.ReadPreparedClassAsync(templateClass, token);
        await Gate.WaitAsync(token);
        try
        {
            var catalog = await ReadCatalogAsync();
            if (catalog.TemplateImports.GetValueOrDefault(package.ClassId) == templateClass.ManifestSha256)
                return new(false, 0, 0, []);
            var revision = GetCatalogRevision();
            var skipped = new List<string>();
            // Historical imports did not retain the stable level identity. Bind
            // only exact, unique artwork/geometry matches, never names or order.
            foreach (var old in catalog.Maps.Where(m => m.TemplateOrigin is null && m.SubscriptionId is null))
            {
                var matches = prepared.Maps.Where(t => SameTemplateGeometry(old, t.Record)).ToArray();
                if (matches.Length != 1) continue;
                var instance = catalog.TemplateInstances.SingleOrDefault(i => i.ClassId == package.ClassId && i.LocalClass == old.Class);
                if (instance is null)
                {
                    instance = new() { Id = Guid.NewGuid(), ClassId = package.ClassId, LocalClass = old.Class };
                    // Unknown historical deletions must not be resurrected.
                    instance.KnownMapIds.UnionWith(package.Maps.Select(m => m.Id));
                    catalog.TemplateInstances.Add(instance);
                }
                old.TemplateOrigin = new(package.ClassId, instance.Id, matches[0].TemplateId, 0,
                    "legacy", package.Maps.Single(m => m.Id == matches[0].TemplateId).GeometrySha256);
            }
            int updated = 0, added = 0;
            foreach (var instance in catalog.TemplateInstances.Where(i => i.ClassId == package.ClassId).ToArray())
            {
                var current = catalog.Maps.Where(m => m.TemplateOrigin?.InstanceId == instance.Id).ToArray();
                // Renaming does not change identity; deleting a whole class retires the instance.
                if (current.Length > 0) instance.LocalClass = current[0].Class;
                if (!catalog.Classes.Contains(instance.LocalClass, StringComparer.OrdinalIgnoreCase)) continue;
                foreach (var template in prepared.Maps)
                {
                    token.ThrowIfCancellationRequested();
                    var definition = package.Maps.Single(m => m.Id == template.TemplateId);
                    var targets = current.Where(m => m.TemplateOrigin!.MapId == template.TemplateId).ToArray();
                    if (targets.Length == 0 && instance.KnownMapIds.Contains(template.TemplateId)) continue;
                    if (targets.Length == 0)
                    {
                        var map = template.Record.Clone();
                        map.Id = Guid.NewGuid();
                        map.Class = instance.LocalClass;
                        map.SequenceNumber = catalog.Maps.Where(m => m.Class == map.Class).Select(m => m.SequenceNumber).DefaultIfEmpty(0).Max() + 1;
                        map.CreatedAt = map.UpdatedAt = DateTimeOffset.UtcNow;
                        map.AcquisitionKind = MapAcquisitionKind.ImportedPackage;
                        map.TemplateOrigin = Origin();
                        await CopyTemplateAssetsAsync(map, template, templateClass, token);
                        catalog.Maps.Add(map);
                        instance.KnownMapIds.Add(template.TemplateId);
                        added++;
                    }
                    foreach (var old in targets)
                    {
                        if (old.TemplateOrigin!.Version == package.Version && old.TemplateOrigin.ManifestSha256 == templateClass.ManifestSha256) continue;
                        if (old.TemplateOrigin.GeometrySha256 != definition.GeometrySha256 || !SameTemplateGeometry(old, template.Record))
                        { skipped.Add($"{old.Class} / {old.DisplayName}：楼层或坐标几何不兼容，保留原版"); continue; }
                        var map = template.Record.Clone();
                        map.Id = old.Id;
                        map.Class = old.Class;
                        map.SequenceNumber = old.SequenceNumber;
                        map.Title = old.Title;
                        map.Tags = new(old.Tags);
                        map.AppearancePalette = old.AppearancePalette;
                        map.AcquisitionKind = old.AcquisitionKind;
                        map.CreatedAt = old.CreatedAt;
                        map.UpdatedAt = DateTimeOffset.UtcNow;
                        map.ContentVersion = old.ContentVersion + 1;
                        map.TemplateOrigin = Origin();
                        map.Recognition.WholeImage = old.Recognition.WholeImage.Clone();
                        foreach (var floor in map.Floors)
                        {
                            var personal = old.Recognition.GetFloor(floor.Key)!.Clone();
                            var profile = map.Recognition.GetFloor(floor.Key)!;
                            profile.Annotations = personal.Annotations;
                            profile.BackgroundLayers = personal.BackgroundLayers;
                            profile.WholeImageIgnoreRegions = personal.WholeImageIgnoreRegions;
                        }
                        // Editor-created connections are user data too. Keep them
                        // only when the exact referenced nodes still exist.
                        if (map.Navigation is { } navigation && old.Navigation is { } previous)
                        {
                            var nodes = navigation.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
                            var custom = previous.FloorTransitions.Where(t => t.Id.StartsWith("editor-", StringComparison.Ordinal)).ToArray();
                            if (custom.Any(t => !nodes.Contains(t.FromNodeId) || !nodes.Contains(t.ToNodeId)))
                            { skipped.Add($"{old.Class} / {old.DisplayName}：用户连接无法映射，保留原版"); continue; }
                            foreach (var transition in custom)
                                if (!navigation.FloorTransitions.Any(t => t.Id == transition.Id)) navigation.FloorTransitions.Add(transition.Clone());
                        }
                        await CopyTemplateAssetsAsync(map, template, templateClass, token);
                        catalog.Maps[catalog.Maps.IndexOf(old)] = map;
                        updated++;
                    }
                    MapTemplateOrigin Origin() => new(package.ClassId, instance.Id, template.TemplateId, package.Version,
                        templateClass.ManifestSha256!, definition.GeometrySha256);
                }
                MergeTemplateVariants(catalog, instance, templateClass.VariantGroups);
            }
            token.ThrowIfCancellationRequested();
            if (revision != GetCatalogRevision()) throw new IOException("地图目录在模板更新期间发生变化，请重新导入。");
            catalog.TemplateImports[package.ClassId] = templateClass.ManifestSha256!;
            await WriteCatalogAsync(catalog);
            return new(false, updated, added, skipped);
        }
        finally { Gate.Release(); }
    }

    private static bool SameTemplateGeometry(MapRecord old, MapRecord next) =>
        string.Equals(old.SourceVisualSha256, next.SourceVisualSha256, StringComparison.OrdinalIgnoreCase)
        && old.Floors.Count == next.Floors.Count && next.Floors.All(f => old.Floors.SingleOrDefault(o => o.Key == f.Key) is { } prior
            && prior.ImageSha256 == f.ImageSha256 && prior.ImageWidth == f.ImageWidth && prior.ImageHeight == f.ImageHeight
            && old.Recognition.GetFloor(f.Key) is { } a && next.Recognition.GetFloor(f.Key) is { } b
            && a.OrientationDegrees == b.OrientationDegrees
            && SameTemplateRegion(a.RecognitionRegion, b.RecognitionRegion)
            && a.FreeCropPoints.Count == 0 && b.FreeCropPoints.Count == 0);

    private static bool SameTemplateRegion(NormalizedRectangle? a, NormalizedRectangle? b) => a is null ? b is null
        : b is not null && Math.Abs(a.X - b.X) < 1e-12 && Math.Abs(a.Y - b.Y) < 1e-12
            && Math.Abs(a.Width - b.Width) < 1e-12 && Math.Abs(a.Height - b.Height) < 1e-12;

    private static List<MapCoCreationTemplateVariantGroup> CloneTemplateVariants(IReadOnlyList<MapCoCreationTemplateVariantGroup> groups) =>
        groups.Select(g => new MapCoCreationTemplateVariantGroup { Difficulty = g.Difficulty,
            PaletteSlot = g.PaletteSlot, TemplateIds = [.. g.TemplateIds] }).ToList();

    private static void MergeTemplateVariants(MapCatalogDocument catalog, MapTemplateInstance instance,
        IReadOnlyList<MapCoCreationTemplateVariantGroup> incoming)
    {
        var maps = catalog.Maps.Where(m => m.TemplateOrigin?.InstanceId == instance.Id).ToArray();
        Guid[] LocalIds(IEnumerable<string> sourceIds) => maps.Where(m => sourceIds.Contains(m.TemplateOrigin!.MapId)).Select(m => m.Id).ToArray();
        foreach (var group in incoming)
        {
            var previous = instance.VariantBaselines.SingleOrDefault(g => g.PaletteSlot == group.PaletteSlot);
            var actual = catalog.VariantGroups.SingleOrDefault(g => g.Class == instance.LocalClass && g.PaletteSlot == group.PaletteSlot);
            var expected = previous is null ? [] : LocalIds(previous.TemplateIds);
            // Unbinding/rebinding is a user setting. Upgrade only the untouched
            // template grouping; deleting a member is accounted for by LocalIds.
            if (actual is not null && (previous is null || !actual.MapIds.ToHashSet().SetEquals(expected))
                || actual is null && expected.Length >= 2) continue;
            var desired = LocalIds(group.TemplateIds);
            if (catalog.VariantGroups.Any(g => g != actual && g.MapIds.Intersect(desired).Any())) continue;
            if (desired.Length < 2)
            {
                if (actual is not null) catalog.VariantGroups.Remove(actual);
            }
            else if (actual is not null) actual.MapIds = desired.ToList();
            else catalog.VariantGroups.Add(new() { Id = Guid.NewGuid(), Class = instance.LocalClass,
                PaletteSlot = group.PaletteSlot, MapIds = desired.ToList() });
        }
        instance.VariantBaselines = CloneTemplateVariants(incoming);
    }

    private async Task CopyTemplateAssetsAsync(MapRecord map, MapCoCreationPreparedMap template,
        MapCoCreationTemplateClass templateClass, CancellationToken token)
    {
        var directory = GetMapDirectory(map.Id);
        Directory.CreateDirectory(directory);
        var names = template.Files.Keys.ToDictionary(n => n, n => $"idvmx-{templateClass.Package!.Version}-{templateClass.ManifestSha256![..12]}-{n}");
        foreach (var (name, hash) in template.Files)
        {
            var source = IdvmxTemplatePackageService.SafePath(templateClass.PackageRoot!, $"prepared/{templateClass.Difficulty}/{template.TemplateId}/{name}");
            var target = GetSafeMapFilePath(directory, names[name]);
            if (File.Exists(target))
            {
                if (!string.Equals(await ComputeFileSha256Async(target, token), hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("模板目标资源已损坏。");
                continue;
            }
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var input = File.OpenRead(source))
                await using (var output = File.Create(temporary)) await input.CopyToAsync(output, token);
                if (!string.Equals(await ComputeFileSha256Async(temporary, token), hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("模板复制校验失败。");
                File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        map.IsCoCreationTemplate = true;
        foreach (var floor in map.Floors)
        {
            floor.ImageFileName = names[floor.ImageFileName];
            floor.RecognitionFileName = names[floor.RecognitionFileName];
            floor.OverlayFileName = names[floor.OverlayFileName];
            floor.ThumbnailFileName = names[floor.ThumbnailFileName];
            floor.PrebuiltStructureLine!.FileName = names[floor.PrebuiltStructureLine.FileName];
            floor.PrebuiltStructureLine.AlgorithmFileName = names[floor.PrebuiltStructureLine.AlgorithmFileName];
            var profile = map.Recognition.GetFloor(floor.Key)!;
            if (!string.IsNullOrEmpty(profile.SideEntranceFeatureFileName)) profile.SideEntranceFeatureFileName = names[profile.SideEntranceFeatureFileName];
        }
        map.FloorOneFileName = map.Floors.OrderBy(f => f.SortOrder).First().ImageFileName;
        map.FloorTwoFileName = map.Floors.OrderBy(f => f.SortOrder).Skip(1).First().ImageFileName;
        BindPreparedTemplateAssets(map, template.Files.ToDictionary(p => names[p.Key], p => p.Value), directory);
        ValidateFloorDefinitions(map);
        ValidateFloorBindingsFast(map);
    }
}
