using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed partial class IdvmxTemplatePackageService
{
    private static readonly SemaphoreSlim StoreGate = new(1, 1);
    private readonly string _root;
    public IdvmxTemplatePackageService(string? root = null) =>
        _root = Path.GetFullPath(root ?? Path.Combine(AppDataPaths.RootDirectory, "CoCreationTemplates"));

    private sealed record Installed(string Directory, string Sha256);
    private sealed record Pending(Installed Target, string RepositoryRoot);
    private string IndexPath(Guid id) => SafePath(_root, $"index/{id:N}.json");
    private string PendingPath => SafePath(_root, "pending.json");
    private async Task<T> ReadAsync<T>(string path, CancellationToken token) =>
        JsonSerializer.Deserialize<T>(await File.ReadAllBytesAsync(path, token), IdvmxManifest.Json)
            ?? throw new InvalidDataException("模板库索引为空。");

    private static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(value, IdvmxManifest.Json), token);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<MapCoCreationTemplateClass> ReadClassAsync(Installed installed, CancellationToken token)
    {
        var root = SafePath(_root, installed.Directory);
        var path = SafePath(root, "manifest.json");
        if (!string.Equals(await HashAsync(path, token), installed.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("已安装模板的清单损坏，请重新导入原始模板包。");
        var package = await ReadAsync<IdvmxManifest>(path, token);
        var classes = await new MapCoCreationTemplateCatalog(root).LoadClassesAsync(token);
        if (classes.Count != 1 || package.ClassId == Guid.Empty || classes[0].Difficulty != package.Difficulty)
            throw new InvalidDataException("已安装模板地图类无效。");
        foreach (var template in classes[0].Templates) template.PackageRoot = root;
        return classes[0] with { Name = package.Name, PackageRoot = root, Package = package, ManifestSha256 = installed.Sha256 };
    }

    public async Task<IReadOnlyList<MapCoCreationTemplateClass>> LoadClassesAsync(CancellationToken token = default)
    {
        await StoreGate.WaitAsync(token);
        try
        {
            await RecoverCoreAsync(token);
            var index = SafePath(_root, "index");
            if (!Directory.Exists(index)) return [];
            var result = new List<MapCoCreationTemplateClass>();
            foreach (var path in Directory.EnumerateFiles(index, "*.json").Order(StringComparer.Ordinal))
            {
                var item = await ReadClassAsync(await ReadAsync<Installed>(path, token), token);
                if (Path.GetFileNameWithoutExtension(path) != item.Package!.ClassId.ToString("N"))
                    throw new InvalidDataException("模板索引身份不一致。");
                result.Add(item);
            }
            return result;
        }
        finally { StoreGate.Release(); }
    }

    internal async Task<MapImportBatchResult> AddClassAsync(MapRepository repository,
        MapCoCreationTemplateClass selected, CancellationToken token)
    {
        await StoreGate.WaitAsync(token);
        try
        {
            await RecoverCoreAsync(token);
            var current = await ReadClassAsync(await ReadAsync<Installed>(IndexPath(selected.Package!.ClassId), token), token);
            if (current.ManifestSha256 != selected.ManifestSha256)
                throw new InvalidOperationException("模板库已更新，请刷新后重新添加。");
            return await new MapCoCreationTemplateCatalog(current.PackageRoot!).AddClassAsync(repository, current, token);
        }
        finally { StoreGate.Release(); }
    }

    // The pending intent is durable before the atomic map-catalog commit. Replaying
    // that commit is idempotent, including after a crash between the two indexes.
    private async Task RecoverCoreAsync(CancellationToken token)
    {
        if (!File.Exists(PendingPath)) return;
        var pending = await ReadAsync<Pending>(PendingPath, token);
        var item = await ReadClassAsync(pending.Target, token);
        await ValidateAsync(item.PackageRoot!, item.Package!, null, token);
        await new MapRepository(pending.RepositoryRoot).ApplyTemplatePackageAsync(item, token);
        await WriteAtomicAsync(IndexPath(item.Package!.ClassId), pending.Target, token);
        File.Delete(PendingPath);
    }

    public async Task<IdvmxImportResult> ImportAsync(string packagePath, MapRepository repository, CancellationToken token = default)
    {
        await using var inspection = await InspectAsync(packagePath, token);
        await StoreGate.WaitAsync(token);
        try
        {
            await RecoverCoreAsync(token);
            var manifest = inspection.Manifest;
            var index = IndexPath(manifest.ClassId);
            if (File.Exists(index))
            {
                var old = await ReadClassAsync(await ReadAsync<Installed>(index, token), token);
                if (old.Package!.Version > manifest.Version) throw new InvalidDataException("不能导入旧版本模板。");
                if (old.Package.Version == manifest.Version)
                {
                    if (old.ManifestSha256 != inspection.ManifestSha256)
                        throw new InvalidDataException("同一模板版本对应不同内容，请使用新的发布版本。");
                    return new(true, 0, 0, []);
                }
            }
            var relative = $"packages/{manifest.ClassId:N}/{manifest.Version}-{inspection.ManifestSha256}";
            var target = SafePath(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!Directory.Exists(target))
            {
                // The user's data root can be on another volume than %TEMP%.
                // Copy and verify there before publishing a same-volume rename.
                var staging = SafePath(_root, $"packages/{manifest.ClassId:N}/.staging-{Guid.NewGuid():N}");
                Directory.CreateDirectory(staging);
                try
                {
                    foreach (var relativePath in manifest.Files.Select(f => f.Path).Append("manifest.json"))
                    {
                        var destination = SafePath(staging, relativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        await using var input = File.OpenRead(SafePath(inspection.Root, relativePath));
                        await using var output = File.Create(destination);
                        await input.CopyToAsync(output, token);
                    }
                    await ValidateAsync(staging, manifest, null, token);
                    Directory.Move(staging, target);
                }
                finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            }
            else await ValidateAsync(target, manifest, null, token);
            var installed = new Installed(relative, inspection.ManifestSha256);
            var item = await ReadClassAsync(installed, token);
            await WriteAtomicAsync(PendingPath, new Pending(installed, repository.TemplateRepositoryRoot), token);
            var result = await repository.ApplyTemplatePackageAsync(item, token);
            await WriteAtomicAsync(index, installed, token);
            File.Delete(PendingPath);
            return result;
        }
        finally { StoreGate.Release(); }
    }
}
