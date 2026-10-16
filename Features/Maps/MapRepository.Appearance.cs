using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    private static readonly object AppearanceGate = new();

    public Task SetClassAppearanceAsync(string className, MapAppearancePalette palette) => Task.Run(async () =>
    {
        var normalized = palette.Normalize();
        await Gate.WaitAsync();
        try
        {
            var catalog = await ReadCatalogAsync();
            var maps = catalog.Maps.Where(map => string.Equals(map.Class, className,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (maps.Length == 0 || maps.Any(map => !map.IsCoCreationTemplate))
                throw new InvalidOperationException("请选择共创模板地图类。");
            var completedFloors = 0;
            var totalFloors = maps.Sum(map => map.Floors.Count);
            foreach (var map in maps)
            {
                // Generate every display asset before the single atomic catalog commit.
                // Failure leaves the previous palette and all recognition assets intact.
                map.AppearancePalette = normalized == new MapAppearancePalette() ? null : normalized;
                foreach (var floor in map.Floors)
                {
                    _ = GetFloorOverlayPath(map, floor.Key);
                    _ = GetFloorThumbnailPath(map, floor.Key);
                    MapOperationProgress.Report(.95 * ++completedFloors / Math.Max(1, totalFloors), "正在更新配色……");
                }
                map.ContentVersion++;
                map.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await WriteCatalogAsync(catalog);
        }
        finally { Gate.Release(); }
    });

    public string GetDraftAppearanceImagePath(MapDraft draft, string floorKey)
    {
        var source = draft.FloorPaths[floorKey];
        return GetAppearancePath(source, draft.IsCoCreationTemplate ? draft.AppearancePalette : null,
            draft.Floors, draft.Recognition, draft.PrebuiltStructureLinePaths, croppedFloor: null, thumbnail: false);
    }

    public string GetFloorAppearanceImagePath(MapRecord map, string floorKey) =>
        GetAppearancePath(GetFloorImagePath(map, floorKey), map.IsCoCreationTemplate ? map.AppearancePalette : null,
            map.Floors, map.Recognition, map.Floors.Where(floor => floor.PrebuiltStructureLine?.IsComplete is true)
                .ToDictionary(floor => floor.Key, floor => GetPrebuiltStructureLinePath(map, floor.Key)), null, false);

    private string GetAppearancePath(MapRecord map, string floorKey, bool thumbnail)
    {
        return GetAppearancePath(GetOriginalFloorOverlayPath(map, floorKey),
            map.IsCoCreationTemplate ? map.AppearancePalette : null, map.Floors, map.Recognition,
            map.Floors.Where(floor => floor.PrebuiltStructureLine?.IsComplete is true)
                .ToDictionary(floor => floor.Key, floor => GetPrebuiltStructureLinePath(map, floor.Key)),
            floorKey, thumbnail);
    }

    private string GetAppearancePath(string sourcePath, MapAppearancePalette? palette,
        IReadOnlyList<FloorDefinition> floors, MapRecognitionProfile recognition,
        IReadOnlyDictionary<string, string> structurePaths, string? croppedFloor, bool thumbnail)
    {
        if (palette is null || palette.Normalize() == new MapAppearancePalette()) return sourcePath;
        palette = palette.Normalize();
        var selected = floors.Where(floor => croppedFloor is null || floor.Key == croppedFloor).ToArray();
        static string Stamp(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("地图配色资源缺失。", path);
            return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        // Pixels are unchanged by the buffer ownership correction. Retain the
        // existing cache identity so upgrades do not regenerate all artwork.
        var name = ComputeAppearanceCacheKey(Stamp(sourcePath), palette, croppedFloor, thumbnail,
            selected.Select(floor =>
            {
                if (!structurePaths.TryGetValue(floor.Key, out var path))
                    throw new InvalidDataException("地图缺少固定三值轮廓。");
                return (Stamp(path), recognition.GetFloor(floor.Key));
            }));
        var directory = Path.Combine(_rootDirectory, ".appearance");
        var destination = Path.Combine(directory, name + ".png");
        lock (AppearanceGate)
        {
            if (File.Exists(destination)) return destination;
            using var operation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Appearance.RenderFile", sourcePath);
            using var sourceOwner = new AppearanceRaster(DecodeImage(sourcePath), "Appearance.Source/" + sourcePath);
            var source = sourceOwner.Image;
            using var semanticOwner = new AppearanceRaster(new Mat(source.Size(), MatType.CV_8UC1, Scalar.Black), "Appearance.LabelCanvas/" + sourcePath);
            var semantic = semanticOwner.Image;
            foreach (var floor in selected)
            {
                using var tileOwner = new AppearanceRaster(DecodeImage(structurePaths[floor.Key]), "Appearance.SemanticTile/" + floor.Key);
                var tile = tileOwner.Image;
                var profile = recognition.GetFloor(floor.Key)
                    ?? throw new InvalidDataException("地图楼层缺少配色区域。");
                var region = croppedFloor is null
                    ? GetPixelRegion(profile.GetEffectiveRecognitionRegion(), source.Width, source.Height)
                    : new Rect(0, 0, source.Width, source.Height);
                if (tile.Type() != MatType.CV_8UC3 || tile.Size() != region.Size)
                    throw new InvalidDataException("地图配色需要尺寸一致的三值轮廓。");
                using var labelsOwner = new AppearanceRaster(SemanticStructureContours.Decode(tile), "Appearance.LabelTile/" + floor.Key);
                using var target = new Mat(semantic, region);
                labelsOwner.Image.CopyTo(target);
            }
            MapAppearanceRenderer.RenderOwnedPixels(source, semantic, palette);
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, name + $"-{Guid.NewGuid():N}.png");
            try
            {
                if (thumbnail)
                {
                    using var resized = new AppearanceRaster(MapAppearanceRenderer.CreateThumbnail(source), "Appearance.Thumbnail/" + sourcePath);
                    if (!Cv2.ImWrite(temporary, resized.Image)) throw new IOException("无法保存配色缩略图。");
                }
                else if (!Cv2.ImWrite(temporary, source)) throw new IOException("无法保存地图配色。");
                File.Move(temporary, destination, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return destination;
    }

    private sealed class AppearanceRaster : IDisposable
    {
        internal Mat Image { get; }
        internal AppearanceRaster(Mat image, string resource)
        {
            Image = image;
            try
            {
                var request = IDVBuff.Core.Diagnostics.MemoryJournal.Request(resource, nativeModule: "OpenCvSharpExtern.dll");
                IDVBuff.Core.Diagnostics.MemoryJournal.TrackResource(image, resource,
                    checked(image.Total() * image.ElemSize()), "OpenCvSharpExtern.dll", request);
            }
            catch { image.Dispose(); throw; }
        }
        public void Dispose()
        {
            Image.Dispose();
            IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(Image);
        }
    }
}
