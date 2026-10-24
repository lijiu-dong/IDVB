using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

public sealed class IdvmxManifest
{
    public string Format { get; set; } = "idvmx";
    public int FormatVersion { get; set; } = 1;
    public int MinimumReader { get; set; } = 1;
    public Guid ClassId { get; set; }
    public int Version { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Difficulty { get; set; }
    public List<IdvmxMap> Maps { get; set; } = [];
    public List<IdvmxResource> Files { get; set; } = [];
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 64
    };
}

public sealed class IdvmxMap
{
    public string Id { get; set; } = string.Empty;
    public string GeometrySha256 { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
}

public sealed class IdvmxResource
{
    public string Path { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public long Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public List<string> Dependencies { get; set; } = [];
}

/// <summary>Local provenance. User drawings remain independent of the template base.</summary>
public sealed record MapTemplateOrigin(Guid ClassId, Guid InstanceId, string MapId,
    int Version, string ManifestSha256, string GeometrySha256);

public sealed partial class MapRecord
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MapTemplateOrigin? TemplateOrigin { get; set; }
}

public sealed partial class MapDraft
{
    public MapTemplateOrigin? TemplateOrigin { get; set; }
}

internal sealed class MapTemplateInstance
{
    public Guid Id { get; set; }
    public Guid ClassId { get; set; }
    public string LocalClass { get; set; } = string.Empty;
    // Retain identities after deletion, including deletion of every map in a class.
    public HashSet<string> KnownMapIds { get; set; } = new(StringComparer.Ordinal);
    public List<MapCoCreationTemplateVariantGroup> VariantBaselines { get; set; } = [];
}

public sealed record IdvmxImportResult(bool AlreadyInstalled, int UpdatedMaps, int AddedMaps,
    IReadOnlyList<string> SkippedMaps);

public sealed class IdvmxInspection : IAsyncDisposable
{
    public IdvmxManifest Manifest { get; }
    public string ManifestSha256 { get; }
    internal string Root { get; }
    internal IdvmxInspection(IdvmxManifest manifest, string hash, string root) =>
        (Manifest, ManifestSha256, Root) = (manifest, hash, root);
    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
        return ValueTask.CompletedTask;
    }
}
