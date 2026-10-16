using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

public sealed partial class IdvmPackageService
{
    private sealed class ManifestDto
    {
        public string Format { get; set; } = string.Empty;
        public string FormatVersion { get; set; } = string.Empty;
        public string PackageType { get; set; } = string.Empty;
        public Guid PackageId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string MinimumReader { get; set; } = string.Empty;
        public string? Compression { get; set; }
        public List<string>? SupportedPlatforms { get; set; }
        public List<ManifestClassDto> Classes { get; set; } = [];
        public List<ManifestMapDto> Maps { get; set; } = [];
        public List<ManifestVariantGroupDto> VariantGroups { get; set; } = [];
        public List<ManifestFileDto> Files { get; set; } = [];
        public CapabilitiesDto Capabilities { get; set; } = new();
    }

    private sealed class ManifestClassDto
    {
        public Guid ClassId { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<Guid> MapIds { get; set; } = [];
        public ManifestClassPropertiesDto Properties { get; set; } = new();
    }

    private sealed class ManifestClassPropertiesDto
    {
        public bool RemoveBackground { get; set; }
        public string? ScanFloorKey { get; set; }
        public bool? ContainsVectorRoutes { get; set; }
    }

    private sealed class ManifestVariantGroupDto
    {
        public Guid GroupId { get; set; }
        public Guid ClassId { get; set; }
        public int PaletteSlot { get; set; }
        public List<Guid> MapIds { get; set; } = [];
    }

    private sealed class ManifestMapDto
    {
        public Guid MapId { get; set; }
        public int MapVersion { get; set; }
        public string Name { get; set; } = string.Empty;
        public Guid ClassId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string Root { get; set; } = string.Empty;
        public List<ManifestFloorDto> Floors { get; set; } = [];
    }

    private sealed class ManifestFloorDto
    {
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public string Image { get; set; } = string.Empty;
        public List<string> MarkerKeys { get; set; } = [];
    }

    private sealed class CapabilitiesDto
    {
        public bool MultiClass { get; set; } = true;
        public bool MultiFloor { get; set; } = true;
        public bool RecognitionAnchors { get; set; } = true;
        public bool DerivedCache { get; set; }
        public bool BackgroundLayers { get; set; } = true;
        public bool ClassBackgroundRemoval { get; set; } = true;
        public bool VariantGroups { get; set; } = true;
        public bool FloorMarkerKeys { get; set; }
        public bool MapTags { get; set; }
        public bool PrebuiltStructureLines { get; set; }
        public bool? ContainsVectorRoutes { get; set; }
        public bool NavigationTopology { get; set; }
    }

    private sealed class MetadataDto
    {
        public int SchemaVersion { get; set; } = 1;
        public MetadataMapDto Map { get; set; } = new();
        public List<MetadataFloorDto> Floors { get; set; } = [];
        public RecognitionSettingsDto Recognition { get; set; } = new();
        public List<MetadataTagDto> Tags { get; set; } = [];
        public string? Navigation { get; set; }
    }

    private sealed class MetadataTagDto
    {
        public Guid GroupId { get; set; }
        public string GroupName { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    private sealed class MetadataMapDto
    {
        public MapAppearancePalette? AppearancePalette { get; set; }
        public bool IsCoCreationTemplate { get; set; }
        public Guid Id { get; set; }
        public Guid ClassId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public Guid? SourceProjectId { get; set; }
        public long? SourceProjectRevision { get; set; }
        public string? SourceVisualSha256 { get; set; }
        public string? SourceStructureSha256 { get; set; }
        public string CoordinateSystem { get; set; } = string.Empty;
    }

    private sealed class MetadataFloorDto
    {
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public string Image { get; set; } = string.Empty;
        public List<string> MarkerKeys { get; set; } = [];
        public string? RecognitionImage { get; set; }
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }
        public int OrientationDegrees { get; set; }
        public RectangleDto? RecognitionRegion { get; set; }
        public List<PointDto> FreeCropPoints { get; set; } = [];
        public RectangleDto ValidMapBounds { get; set; } = new() { Width = 1d, Height = 1d };
        /// <summary>侧门特征图元数据（可选，旧版 IDVM 包不含此字段）。</summary>
        public SideEntranceFeatureDto? SideEntranceFeature { get; set; }
        public PrebuiltStructureLineDto? PrebuiltStructureLine { get; set; }
    }

    /// <summary>侧门特征图的可移植元数据，写入 metadata.json。</summary>
    private sealed class SideEntranceFeatureDto
    {
        /// <summary>特征图在 IDVM 包内的逻辑路径（{root}/data/floor-n-side-entrance-feature.png）。</summary>
        public string File { get; set; } = string.Empty;
        /// <summary>实际中心点 X（识别图像素，边界挤压后）。</summary>
        public double CenterX { get; set; }
        /// <summary>实际中心点 Y（识别图像素，边界挤压后）。</summary>
        public double CenterY { get; set; }
        /// <summary>实际裁剪半径（像素）。</summary>
        public int Radius { get; set; }
    }

    private sealed class RecognitionSettingsDto
    {
        public int SchemaVersion { get; set; } = 1;
        public WholeImageDto WholeImage { get; set; } = new();
    }

    private sealed class WholeImageDto
    {
        public bool Enabled { get; set; }
        public double Weight { get; set; } = 0.15d;
        public double AnnotatedReferencePenalty { get; set; } = 0.55d;
        public bool ReferenceMayContainAnnotations { get; set; } = true;
    }

    private sealed class GatesDto
    {
        public int SchemaVersion { get; set; } = 1;
        public List<GateDto> Gates { get; set; } = [];
    }

    private sealed class GateDto
    {
        public string Id { get; set; } = string.Empty;
        public string FloorKey { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public RectangleDto Bounds { get; set; } = new();
        public double DirectionDegrees { get; set; }
        public bool Enabled { get; set; } = true;
        public double Confidence { get; set; } = 1d;
    }

    private sealed class AnchorsDto
    {
    public int SchemaVersion { get; set; } = 4;
        public Dictionary<string, AnchorFloorDto> Floors { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class AnchorFloorDto
    {
        public List<AnchorDto> Anchors { get; set; } = [];
        public List<RectangleDto> WholeImageIgnoreRegions { get; set; } = [];
        public List<AnnotationDto> Annotations { get; set; } = [];
        // Nullable so schema 4 can distinguish a required field that is
        // missing from the legacy schema 1–3 shape.
        public List<BackgroundLayerDto>? BackgroundLayers { get; set; }
    }

    private sealed class BackgroundLayerDto
    {
        public Guid Id { get; set; }
        public string Semantic { get; set; } = string.Empty;
        public string Shape { get; set; } = string.Empty;
        public int BrushSizePixels { get; set; }
        public List<PointDto> Points { get; set; } = [];
    }

    private sealed class AnchorDto
    {
        public Guid Id { get; set; }
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public double Weight { get; set; }
        public RectangleDto? Bounds { get; set; }
        public bool BuiltIn { get; set; }
        public string? GateId { get; set; }
    }
}
