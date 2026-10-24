namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> MetadataRepairGates
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly record struct AssetMetadata(string FileName, string Hash, int Width, int Height, long Length, long Written)
    {
        internal static AssetMetadata Image(FloorDefinition floor) => new(floor.ImageFileName, floor.ImageSha256,
            floor.ImageWidth, floor.ImageHeight, floor.ImageFileLength, floor.ImageLastWriteUtcTicks);
        internal static AssetMetadata Thumbnail(FloorDefinition floor) => new(floor.ThumbnailFileName, floor.ThumbnailSha256,
            floor.ThumbnailWidth, floor.ThumbnailHeight, floor.ThumbnailFileLength, floor.ThumbnailLastWriteUtcTicks);
    }
    private readonly record struct RepairMetadataInput(AssetMetadata Image, AssetMetadata Thumbnail,
        string RecognitionFile, string RecognitionHash);

    private bool ImageMetadataIsCurrent(MapCatalogSnapshot snapshot, CancellationToken token)
    {
        foreach (var map in snapshot.Maps)
        foreach (var floor in map.Floors)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(floor.ImageFileName)) continue;
            var source = GetSafeMapFilePath(GetMapDirectory(map.Id), floor.ImageFileName);
            if (!File.Exists(source)) continue; // Integrity diagnostics reports missing sources.
            if (string.IsNullOrWhiteSpace(floor.ImageSha256) || floor.ImageWidth <= 0 || floor.ImageHeight <= 0
                || !HasMatchingFileStamp(source, floor.ImageFileLength, floor.ImageLastWriteUtcTicks)
                || string.IsNullOrWhiteSpace(floor.ThumbnailFileName) || floor.ThumbnailWidth <= 0 || floor.ThumbnailHeight <= 0
                || !HasMatchingFileStamp(GetOriginalFloorThumbnailPath(map, floor.Key), floor.ThumbnailFileLength, floor.ThumbnailLastWriteUtcTicks))
                return false;
        }
        return true;
    }
}
