
namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    public Task<MapDraft?> CreateDraftAsync(Guid id) => Task.Run(() => CreateDraftCoreAsync(id));

    private async Task<MapDraft?> CreateDraftCoreAsync(Guid id, bool gateAlreadyHeld = false)
    {
        if (!gateAlreadyHeld)
            await Gate.WaitAsync();
        try
        {
            var catalog = await ReadMapCatalogAsync(id);
            var record = catalog.Maps.SingleOrDefault(map => map.Id == id);
            if (record is null)
                return null;
            // The list path uses file stamps to stay responsive. Editing a
            // specific map is the point where we perform the full content
            // verification before exposing its assets to the editor.
            await VerifyFloorImageBindingsAsync(record);

            record.ClassProperties = GetClassProperties(catalog, record.Class);
            return CreateRoutePlanningDraft(record);
        }
        finally
        {
            if (!gateAlreadyHeld)
                Gate.Release();
        }
    }

    // A detached class snapshot already owns normalized metadata. Route setup
    // needs no image decoding and must not reread the entire catalog per map.
    // Generation validates images; SavePlannedRouteAsync rechecks live version
    // and class before the atomic commit.
    internal MapDraft CreateRoutePlanningDraft(MapRecord record)
    {

            var floorPaths = record.Floors.ToDictionary(
                floor => floor.Key,
                floor => GetEditableFloorImagePath(record, floor.Key),
                StringComparer.Ordinal);
            var floorPreviewPaths = floorPaths.ToDictionary(
                pair => pair.Key,
                pair =>
                {
                    var previewPath = GetFloorRecognitionPath(record, pair.Key);
                    return File.Exists(previewPath) ? previewPath : pair.Value;
                },
                StringComparer.Ordinal);

            return new MapDraft
            {
                Id = record.Id,
                FloorOnePath = GetFloorOnePath(record),
                FloorTwoPath = GetFloorTwoPath(record),
                FloorPaths = floorPaths,
                FloorPreviewPaths = floorPreviewPaths,
                FloorRecognitionSourcePaths = string.Equals(record.Source, "survey", StringComparison.Ordinal)
                    ? record.Floors.ToDictionary(
                        floor => floor.Key,
                        floor => GetFloorRecognitionPath(record, floor.Key),
                        StringComparer.Ordinal)
                    : [],
                Floors = record.Floors.Select(f => new FloorDefinition
                {
                    Key = f.Key,
                    DisplayName = f.DisplayName,
                    SortOrder = f.SortOrder,
                    MarkerKeys = MapFloorMarkerRules.Normalize(f.MarkerKeys).ToList(),
                    PrebuiltStructureLine = f.PrebuiltStructureLine?.Clone()
                }).ToList(),
                Class = record.Class,
                ClassProperties = record.ClassProperties.Clone(),
                Title = record.Title,
                ContentVersion = record.ContentVersion,
                Source = record.Source,
                IsCoCreationTemplate = record.IsCoCreationTemplate,
                AppearancePalette = record.AppearancePalette,
                AcquisitionKind = record.AcquisitionKind,
                SubscriptionId = record.SubscriptionId,
                SubscriptionPublisherHandle = record.SubscriptionPublisherHandle,
                SubscriptionPublisherIsOfficial = record.SubscriptionPublisherIsOfficial,
                SubscriptionPublisherIsBuilder = record.SubscriptionPublisherIsBuilder,
                SubscriptionPublisherKeyId = record.SubscriptionPublisherKeyId,
                SubscriptionVersion = record.SubscriptionVersion,
                SourceProjectId = record.SourceProjectId,
                SourceProjectRevision = record.SourceProjectRevision,
                SourceVisualSha256 = record.SourceVisualSha256,
                SourceStructureSha256 = record.SourceStructureSha256,
                PortableGates = record.PortableGates.Select(gate => gate.Clone()).ToList(),
                Navigation = record.LoadNavigationForDraft(),
                Tags = new Dictionary<Guid, string>(record.Tags),
                Recognition = record.Recognition.Clone(),
                PrebuiltStructureLinePaths = record.Floors
                    .Where(floor => floor.PrebuiltStructureLine?.IsComplete is true)
                    .ToDictionary(
                        floor => floor.Key,
                        floor => GetPrebuiltStructureLinePath(record, floor.Key),
                        StringComparer.Ordinal),
                PrebuiltStructureAlgorithmPath = record.Floors
                    .FirstOrDefault(floor => floor.PrebuiltStructureLine?.IsComplete is true)
                    is { } algorithmFloor
                        ? GetPrebuiltStructureAlgorithmPath(record, algorithmFloor.Key)
                        : null
            };
    }

}
