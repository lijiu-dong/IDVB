using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    private static readonly JsonSerializerOptions CatalogWriteOptions = CreateCatalogWriteOptions();

    private static JsonSerializerOptions CreateCatalogWriteOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            if (type.Type != typeof(MapRecord)) return;
            var navigation = type.Properties.Single(property => property.Name == nameof(MapRecord.Navigation));
            // Suppressing serialization alone would still invoke the getter
            // and load every topology before evaluating ShouldSerialize.
            navigation.Get = _ => null;
            navigation.ShouldSerialize = (_, _) => false;
        });
        return new JsonSerializerOptions(SerializerOptions) { TypeInfoResolver = resolver };
    }

    private void BindNavigationStorage(MapRecord record)
    {
        if (record.LoadedNavigation is not null || record.NavigationFileName is null)
            return;
        var fileName = record.NavigationFileName;
        var path = NavigationPath(record.Id, fileName);
        record.DeferNavigation(fileName, () => ReadNavigation(path, fileName));
    }

    private string NavigationPath(Guid mapId, string fileName)
    {
        const string prefix = "navigation-";
        const string extension = ".json";
        if (fileName.Length != prefix.Length + 64 + extension.Length
            || !fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(extension, StringComparison.Ordinal)
            || fileName.AsSpan(prefix.Length, 64).ContainsAnyExcept("0123456789abcdef"))
            throw new InvalidDataException("地图路线文件引用无效。");
        return GetSafeMapFilePath(GetMapDirectory(mapId), fileName);
    }

    private static MapNavigationTopology ReadNavigation(string path, string fileName)
    {
        var bytes = File.ReadAllBytes(path);
        if (!string.Equals(NavigationFileName(bytes), fileName, StringComparison.Ordinal))
            throw new InvalidDataException("地图路线文件校验失败。");
        return JsonSerializer.Deserialize<MapNavigationTopology>(bytes, SerializerOptions)
            ?? throw new InvalidDataException("地图路线文件为空。");
    }

    private static string NavigationFileName(byte[] bytes) =>
        $"navigation-{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}.json";

    private void PreserveNavigationSnapshots(Guid mapId, string stagingDirectory)
    {
        var directory = GetMapDirectory(mapId);
        if (!Directory.Exists(directory)) return;
        // Saving artwork replaces the whole asset directory. Keep immutable
        // route versions usable by an already detached match/editor snapshot,
        // just as route-only commits retain their previous payloads.
        foreach (var path in Directory.EnumerateFiles(directory, "navigation-*.json"))
        {
            var fileName = Path.GetFileName(path);
            _ = NavigationPath(mapId, fileName);
            File.Copy(path, Path.Combine(stagingDirectory, fileName));
        }
    }

    private async Task PersistNavigationAsync(MapRecord record)
    {
        if (record.LoadedNavigation is not { } topology)
        {
            if (record.NavigationFileName is { } reference)
            {
                var path = NavigationPath(record.Id, reference);
                if (!File.Exists(path))
                    throw new FileNotFoundException("地图路线文件缺失，地图目录未提交。", path);
                // A class copy has a new map ID and its own copied assets.
                BindNavigationStorage(record);
            }
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(topology, SerializerOptions);
        var fileName = NavigationFileName(bytes);
        var destination = NavigationPath(record.Id, fileName);
        Directory.CreateDirectory(GetMapDirectory(record.Id));
        if (File.Exists(destination))
        {
            if (!string.Equals(await ComputeFileSha256Async(destination),
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("已有地图路线文件校验失败，地图目录未提交。");
        }
        else
        {
            // Immutable payload first, atomic index replacement last. A failed
            // migration leaves the old catalog readable and can be retried.
            var pending = destination + $".pending-{Guid.NewGuid():N}";
            await File.WriteAllBytesAsync(pending, bytes);
            File.Move(pending, destination);
        }
        record.DeferNavigation(fileName, () => ReadNavigation(destination, fileName));
    }
}
