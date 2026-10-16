namespace IDVBuff.Features.Maps;

public sealed partial class MapRecord
{
    public MapRecord Clone() => CloneCore(includeEditableGeometry: true);

    internal MapRecord CloneRecognitionMetadata() => CloneCore(includeEditableGeometry: false);

    private MapRecord CloneCore(bool includeEditableGeometry)
    {
        NormalizeRecognition();
        var clone = new MapRecord
        {
            IsListProjection = IsListProjection || !includeEditableGeometry,
            Id = Id,
            SequenceNumber = SequenceNumber,
            FloorOneFileName = FloorOneFileName,
            FloorTwoFileName = FloorTwoFileName,
            Recognition = Recognition.Clone(includeEditableGeometry),
            Source = Source,
            IsCoCreationTemplate = IsCoCreationTemplate,
            TemplateOrigin = TemplateOrigin,
            AppearancePalette = AppearancePalette,
            AcquisitionKind = AcquisitionKind,
            SubscriptionId = SubscriptionId,
            SubscriptionPublisherHandle = SubscriptionPublisherHandle,
            SubscriptionPublisherIsOfficial = SubscriptionPublisherIsOfficial,
            SubscriptionPublisherIsBuilder = SubscriptionPublisherIsBuilder,
            SubscriptionPublisherKeyId = SubscriptionPublisherKeyId,
            SubscriptionVersion = SubscriptionVersion,
            SourceProjectId = SourceProjectId,
            SourceProjectRevision = SourceProjectRevision,
            SourceVisualSha256 = SourceVisualSha256,
            SourceStructureSha256 = SourceStructureSha256,
            Title = Title,
            ContentVersion = ContentVersion,
            PortableGates = PortableGates.Select(gate => gate.Clone()).ToList(),
            Tags = new Dictionary<Guid, string>(Tags),
            MainEntrance = MainEntrance?.Clone(),
            SideEntrance = SideEntrance?.Clone(),
            Class = Class,
            ClassProperties = ClassProperties.Clone(),
            Floors = Floors.Select(f => new FloorDefinition
            {
                Key = f.Key,
                DisplayName = f.DisplayName,
                SortOrder = f.SortOrder,
                MarkerKeys = MapFloorMarkerRules.Normalize(f.MarkerKeys).ToList(),
                ImageFileName = f.ImageFileName,
                ImageSha256 = f.ImageSha256,
                ImageWidth = f.ImageWidth,
                ImageHeight = f.ImageHeight,
                ImageFileLength = f.ImageFileLength,
                ImageLastWriteUtcTicks = f.ImageLastWriteUtcTicks,
                RecognitionFileName = f.RecognitionFileName,
                RecognitionSha256 = f.RecognitionSha256,
                RecognitionSourceSha256 = f.RecognitionSourceSha256,
                RecognitionWidth = f.RecognitionWidth,
                RecognitionHeight = f.RecognitionHeight,
                RecognitionFileLength = f.RecognitionFileLength,
                RecognitionLastWriteUtcTicks = f.RecognitionLastWriteUtcTicks,
                OverlayFileName = f.OverlayFileName,
                OverlaySha256 = f.OverlaySha256,
                OverlaySourceSha256 = f.OverlaySourceSha256,
                OverlayWidth = f.OverlayWidth,
                OverlayHeight = f.OverlayHeight,
                OverlayFileLength = f.OverlayFileLength,
                OverlayLastWriteUtcTicks = f.OverlayLastWriteUtcTicks,
                ThumbnailFileName = f.ThumbnailFileName,
                ThumbnailSha256 = f.ThumbnailSha256,
                ThumbnailWidth = f.ThumbnailWidth,
                ThumbnailHeight = f.ThumbnailHeight,
                ThumbnailFileLength = f.ThumbnailFileLength,
                ThumbnailLastWriteUtcTicks = f.ThumbnailLastWriteUtcTicks,
                PrebuiltStructureLine = f.PrebuiltStructureLine?.Clone()
            }).ToList(),
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt
        };
        CloneNavigationTo(clone);
        return clone;
    }
}
