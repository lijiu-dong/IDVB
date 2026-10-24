namespace IDVBuff.Features.Maps;

public sealed partial class IdvmPackageService
{
    private static async Task WriteNavigationAsync(string staging, string root, MetadataDto metadata,
        MapRecord map, CancellationToken cancellationToken)
    {
        // The writer consumes one topology at a time. Loading through Navigation
        // pins every processed graph in selectedMaps until the whole export ends.
        var topology = map.LoadNavigationForDraft();
        if (topology is null) return;
        using var operation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Idvm.ExportNavigation", map.Id.ToString());
        using var ownership = IDVBuff.Core.Diagnostics.MemoryJournal.Own("Idvm.ExportNavigation/" + map.Id);
        IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedObject(topology, "Idvm.ExportNavigation/" + map.Id);
        MapNavigationRules.Validate(topology, metadata.Floors.Select(f => f.Key));
        foreach (var floor in topology.Floors)
        {
            var binding = map.Floors.Single(f => f.Key == floor.FloorKey);
            if (binding.ImageSha256 != floor.ImageSha256 || binding.ImageWidth != floor.ImageWidth
                || binding.ImageHeight != floor.ImageHeight)
                throw new InvalidDataException("地图原图与保存的导航结构不一致。");
        }
        // Portable export may use the repository's preserved whole image from
        // before downsampling. This is an explicit repository-owned transform.
        await MapNavigationRules.RebindGeneratedImagesAsync(topology,
            metadata.Floors.ToDictionary(f => f.Key, f => ToPhysicalPath(staging, f.Image)), cancellationToken);
        await ValidateNavigationFloorBindingsAsync(staging, metadata, topology, cancellationToken);
        metadata.Navigation = $"{root}/data/navigation.json";
        var path = ToPhysicalPath(staging, metadata.Navigation);
        await WriteJsonAsync(path, topology, cancellationToken);
        if (new FileInfo(path).Length > MaxJsonBytes)
            throw new InvalidDataException("导航数据超过 IDVM JSON 大小限制。");
    }

    private static async Task<Func<MapNavigationTopology>?> ReadNavigationAsync(string root,
        ManifestDto manifest, ManifestMapDto map, MetadataDto metadata, CancellationToken cancellationToken)
    {
        if (metadata.Navigation is null)
        {
            if (File.Exists(ToPhysicalPath(root, $"{map.Root}/data/navigation.json")))
                throw new InvalidDataException("导航数据文件缺少 metadata 引用。");
            return null;
        }
        if (manifest.FormatVersion is not ("1.5" or "1.6") || !manifest.Capabilities.NavigationTopology
            || metadata.Navigation != $"{map.Root}/data/navigation.json")
            throw new InvalidDataException("导航数据必须由 IDVM 1.5 声明并位于所属地图目录。");
        using var operation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Idvm.InspectNavigation", map.MapId.ToString());
        using var ownership = IDVBuff.Core.Diagnostics.MemoryJournal.Own("Idvm.InspectNavigation/" + map.MapId);
        var path = ToPhysicalPath(root, metadata.Navigation);
        var bytes = await ReadLimitedBytesAsync(path, MaxJsonBytes, cancellationToken);
        var topology = MapNavigationRules.Read(bytes);
        IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedObject(topology, "Idvm.InspectNavigation/" + map.MapId);
        MapNavigationRules.Validate(topology, metadata.Floors.Select(f => f.Key));
        await ValidateNavigationFloorBindingsAsync(root, metadata, topology, cancellationToken);
        return CreateNavigationLoader(path, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
    }

    // Keep only path/hash in this closure, never the inspected graph or bytes.
    private static Func<MapNavigationTopology> CreateNavigationLoader(string path, string hash) => () =>
    {
        using var input = File.OpenRead(path);
        if (input.Length > MaxJsonBytes) throw new InvalidDataException("导航数据超过 IDVM JSON 大小限制。");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) != hash)
            throw new InvalidDataException("已检查的导航数据发生变化，请重新选择数据包。");
        return MapNavigationRules.Read(bytes);
    };

    private static async Task ValidateNavigationFloorBindingsAsync(string root, MetadataDto metadata,
        MapNavigationTopology topology, CancellationToken cancellationToken)
    {
        var images = metadata.Floors.ToDictionary(f => f.Key, f => ToPhysicalPath(root, f.Image), StringComparer.Ordinal);
        await MapNavigationRules.ValidateImagesAsync(topology, images, cancellationToken);
        foreach (var floor in topology.Floors)
        {
            var image = metadata.Floors.Single(f => f.Key == floor.FloorKey);
            if (image.ImageWidth != floor.ImageWidth || image.ImageHeight != floor.ImageHeight)
                throw new InvalidDataException($"楼层 {floor.FloorKey} 的导航图片尺寸不一致。");
        }
    }
}
