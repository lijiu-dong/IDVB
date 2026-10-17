using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    private static bool TryWriteOwnedUnmaskedRegionAssets(string sourcePath, string destinationPath,
        FloorRecognitionProfile profile, string? overlayPath, bool removeBackground, bool writeRecognition,
        string? thumbnailPath = null)
    {
        if (removeBackground || profile.BackgroundLayers is { Count: > 0 }
            || profile.FreeCropPoints is { Count: > 0 } || !MapRecognitionRegionDecoder.IsEightBitPng(sourcePath))
            return false;
        var region = UsesWholeSourceImage(profile)
            ? new NormalizedRectangle { Width = 1d, Height = 1d }
            : profile.GetEffectiveRecognitionRegion();
        MapBackgroundProcessor.WriteOwnedUnmaskedRegion(MapRecognitionRegionDecoder.Decode(sourcePath, region),
            profile,
            pixels =>
            {
                if (writeRecognition && !Cv2.ImWrite(destinationPath, pixels))
                    throw new InvalidOperationException("无法保存地图识别区域。");
                if (writeRecognition && thumbnailPath is not null)
                    WriteBorrowedRecognitionThumbnail(pixels, thumbnailPath);
            },
            overlayPath is null ? null : pixels =>
            {
                if (!Cv2.ImWrite(overlayPath, pixels))
                    throw new InvalidOperationException("无法保存透明地图图层。");
            });
        return true;
    }
}
