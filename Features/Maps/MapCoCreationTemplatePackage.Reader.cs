using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class IdvmxTemplatePackageService
{
    private const long MaxExpanded = 1024L * 1024 * 1024;
    private const long MaxFile = 256L * 1024 * 1024;
    internal static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\')
            || relative.Contains(':') || relative.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ')))
            throw new InvalidDataException("模板资源路径无效。");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模板资源路径越界。");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("模板目录不允许使用目录链接。");
        return path;
    }

    internal static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
    }
    private static bool IsHash(string value) => value is { Length: 64 }
        && value.All(c => char.IsAsciiHexDigit(c));

    public async Task<IdvmxInspection> InspectAsync(string packagePath, CancellationToken token = default)
    {
        if (!Path.GetExtension(packagePath).Equals(".idvmx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 .idvmx 共创模板包。");
        var root = Path.Combine(Path.GetTempPath(), "IDVB-Idvmx", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var zip = ZipFile.OpenRead(packagePath);
            if (zip.Entries.Count is 0 or > 20000) throw new InvalidDataException("模板包文件数量无效。");
            var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                var path = SafePath(root, entry.FullName);
                if (!entries.Add(entry.FullName) || entry.Length < 0 || entry.Length > MaxFile
                    || (total += entry.Length) > MaxExpanded
                    || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                    throw new InvalidDataException("模板包包含重复、链接或过大的文件。");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var source = entry.Open();
                await using var destination = File.Create(path);
                var buffer = new byte[81920];
                long copied = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token)) != 0)
                {
                    if ((copied += count) > entry.Length) throw new InvalidDataException("模板包解压长度无效。");
                    await destination.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (copied != entry.Length) throw new InvalidDataException("模板包文件不完整。");
            }
            var manifestPath = SafePath(root, "manifest.json");
            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 8 * 1024 * 1024)
                throw new InvalidDataException("模板包清单缺失或过大。");
            var manifest = JsonSerializer.Deserialize<IdvmxManifest>(await File.ReadAllBytesAsync(manifestPath, token), IdvmxManifest.Json)
                ?? throw new InvalidDataException("模板包清单为空。");
            await ValidateAsync(root, manifest, entries, token);
            return new(manifest, await HashAsync(manifestPath, token), root);
        }
        catch { Directory.Delete(root, true); throw; }
    }

    internal static async Task ValidateAsync(string root, IdvmxManifest manifest, HashSet<string>? entries, CancellationToken token)
    {
        if (manifest.Format != "idvmx" || manifest.FormatVersion != 1 || manifest.MinimumReader is < 1 or > 1
            || manifest.ClassId == Guid.Empty || manifest.Version <= 0 || string.IsNullOrWhiteSpace(manifest.Name)
            || manifest.Difficulty is not (3 or 4) || manifest.Maps.Count is 0 or > 1000 || manifest.Files.Count is 0 or > 19999
            || manifest.Maps.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != manifest.Maps.Count)
            throw new InvalidDataException("不支持的模板包版本或地图类身份。");
        var files = new Dictionary<string, IdvmxResource>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            var path = SafePath(root, file.Path);
            if (file.Path.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) || !files.TryAdd(file.Path, file)
                || !IsHash(file.Sha256) || string.IsNullOrWhiteSpace(file.Kind)
                || file.Length < 0 || file.Length > MaxFile || (total += file.Length) > MaxExpanded
                || file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && file.Length > 32 * 1024 * 1024
                || !File.Exists(path) || new FileInfo(path).Length != file.Length
                || !string.Equals(await HashAsync(path, token), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"模板资源校验失败：{file.Path}");
        }
        if (entries is not null && !entries.SetEquals(files.Keys.Append("manifest.json")))
            throw new InvalidDataException("模板包存在未登记或缺失的资源。");
        foreach (var file in manifest.Files)
            if (file.Dependencies.Any(dependency => !files.ContainsKey(dependency)))
                throw new InvalidDataException("模板资源依赖缺失。");
        var catalog = new MapCoCreationTemplateCatalog(root);
        var classes = await catalog.LoadClassesAsync(token);
        if (classes.Count != 1 || classes[0].Difficulty != manifest.Difficulty
            || !classes[0].Templates.Select(t => t.Id).SequenceEqual(manifest.Maps.Select(m => m.Id)))
            throw new InvalidDataException("模板包必须且只能包含一个完整地图类。");
        var prepared = await catalog.ReadPreparedClassAsync(classes[0], token);
        foreach (var (template, index) in classes[0].Templates.Select((t, i) => (t, i)))
        {
            MapRepository.ValidatePreparedTemplateAssets(prepared.Maps[index],
                SafePath(root, $"prepared/{manifest.Difficulty}/{template.Id}"));
            var map = manifest.Maps[index];
            if (!IsHash(map.GeometrySha256) || !files.ContainsKey(map.Data)) throw new InvalidDataException("模板地图来源缺失。");
            using var data = JsonDocument.Parse(await File.ReadAllBytesAsync(SafePath(root, map.Data), token));
            foreach (var field in new[] { "doors", "chests", "modules", "structure", "coordinates", "source" })
                if (!data.RootElement.TryGetProperty(field, out _)) throw new InvalidDataException($"模板地图缺少 {field} 数据。");
            foreach (var floor in template.Floors)
            {
                RequireImage(floor.ImageFile, floor.ImageWidth, floor.ImageHeight);
                var stored = prepared.Maps[index].Record.Floors.Single(f => f.Key == floor.FloorKey);
                var prefix = $"prepared/{manifest.Difficulty}/{template.Id}/";
                RequireImage(prefix + stored.RecognitionFileName, stored.RecognitionWidth, stored.RecognitionHeight);
                RequireImage(prefix + stored.OverlayFileName, stored.OverlayWidth, stored.OverlayHeight);
                RequireImage(prefix + stored.ThumbnailFileName, stored.ThumbnailWidth, stored.ThumbnailHeight);
                RequireImage(prefix + stored.PrebuiltStructureLine!.FileName, stored.PrebuiltStructureLine.Width, stored.PrebuiltStructureLine.Height);
                foreach (var (name, hash) in prepared.Maps[index].Files)
                    if (!files.TryGetValue(prefix + name, out var entry) || !entry.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("模板预制资源清单不一致。");
                var algorithm = await new IdvaStructureLineEngine().LoadAsync(SafePath(root, prefix + stored.PrebuiltStructureLine.AlgorithmFileName), token);
                if (!algorithm.Sha256.Equals(stored.PrebuiltStructureLine.AlgorithmSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("模板算法绑定不一致。");
            }
        }
        void RequireImage(string relative, int width, int height)
        {
            if (!files.ContainsKey(relative) || width <= 0 || height <= 0 || (long)width * height > 64_000_000)
                throw new InvalidDataException("模板图片绑定或尺寸无效。");
            using var image = Cv2.ImRead(SafePath(root, relative), ImreadModes.Unchanged);
            if (image.Empty() || image.Width != width || image.Height != height)
                throw new InvalidDataException("模板图片实际尺寸不一致。");
        }
    }
}
