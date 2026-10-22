using OpenCvSharp;
using System.Security.Cryptography;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    // Measurement metadata only. A floor switch does not change the authored
    // gate icons, and must not decode its full RGB reference repeatedly.
    private readonly record struct GateSizeKey(Guid MapId, string Floor, string Content, string Path,
        long Length, long Written, MapScreenRect Main, MapScreenRect Side, long GateLength, long GateWritten);
    private readonly Dictionary<GateSizeKey, (double Width, double Height)> _referenceGateSizes = [];

    private (double Width, double Height) GetReferenceGateIconSize(MapRecord map, string floorKey, string recognitionImagePath,
        MapScreenRect mainRefBounds, MapScreenRect sideRefBounds)
    {
        // Measure actual gate icon size in the reference image so that
        // EstimateAxisScale uses comparable objects on both sides of the
        // ratio (screen-side: template-matched tight box; reference-side:
        // template-matched tight box) instead of comparing a tight box to
        // a user-drawn loose anchor rectangle.
        double iconWidth = 0d;
        double iconHeight = 0d;
        try
        {
            var info = new FileInfo(recognitionImagePath);
            IDVBuff.Core.Contracts.Online.RecognitionResources.Demand();
#if IDVB_PRIVATE_ONLINE_SERVICES
            var gateLength = 0L;
            var gateWritten = 0L;
            var recognitionContent = IDVBuff.Core.Contracts.Online.RecognitionResources.Version;
#else
            var gateInfo = new FileInfo(GateTemplateDetector.ResolveGateAssetPath());
            var gateLength = gateInfo.Length;
            var gateWritten = gateInfo.LastWriteTimeUtc.Ticks;
            var recognitionContent = "local";
#endif
            var key = new GateSizeKey(map.Id, floorKey, MapFeatureCacheRules.ComputeContentFingerprint(map), recognitionImagePath,
                info.Length, info.LastWriteTimeUtc.Ticks, mainRefBounds, sideRefBounds, gateLength, gateWritten);
            var diskKey = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { algorithm = "gate-size-roi-v1", recognitionContent, key })));
            if (_referenceGateSizes.TryGetValue(key, out var remembered))
            {
                (iconWidth, iconHeight) = remembered;
                IDVBuff.Core.Diagnostics.MemoryJournal.Current?.Record("reference-gate-size", new { mapId = map.Id, floorKey, cached = true });
            }
            else if (_repository.TryReadReferenceGateSize(diskKey, out remembered))
            {
                (iconWidth, iconHeight) = remembered;
                RememberReferenceGateSize(key, remembered);
                IDVBuff.Core.Diagnostics.MemoryJournal.Current?.Record("reference-gate-size", new { mapId = map.Id, floorKey, cached = true, source = "validated-disk-metadata" });
            }
            else
            {
                IDVBuff.Core.Diagnostics.MemoryJournal.Current?.Record("reference-gate-size", new { mapId = map.Id, floorKey, cached = false });
                using var reference = Cv2.ImRead(recognitionImagePath, ImreadModes.Unchanged);
                if (!reference.Empty())
                {
                    var mainCenter = new Point2d(
                        mainRefBounds.CenterX,
                        mainRefBounds.CenterY);
                    var sideCenter = new Point2d(
                        sideRefBounds.CenterX,
                        sideRefBounds.CenterY);
                    var mainSize = GateTemplateDetector.EstimateReferenceGateIconSize(
                        reference,
                        mainCenter);
                    var sideSize = GateTemplateDetector.EstimateReferenceGateIconSize(
                        reference,
                        sideCenter);
                    if (mainSize is { } mainSz && sideSize is { } sideSz)
                    {
                        // Average the two measurements — they should be very close.
                        iconWidth = (mainSz.Width + sideSz.Width) / 2d;
                        iconHeight = (mainSz.Height + sideSz.Height) / 2d;
                    }
                    else if (mainSize is { } mSz)
                    {
                        iconWidth = mSz.Width;
                        iconHeight = mSz.Height;
                    }
                    else if (sideSize is { } sSz)
                    {
                        iconWidth = sSz.Width;
                        iconHeight = sSz.Height;
                    }
                    // Includes an honest no-match result for this exact source and
                    // gate-template stamp. Missing/corrupt sources are never cached.
                    RememberReferenceGateSize(key, (iconWidth, iconHeight));
                    _repository.StoreReferenceGateSize(diskKey, (iconWidth, iconHeight));
                }
            }
        }
        catch
        {
            // Reference image missing or corrupt — fall back to anchor bounds.
        }

        return (iconWidth, iconHeight);
    }

    private void RememberReferenceGateSize(GateSizeKey key, (double Width, double Height) size)
    {
        if (_referenceGateSizes.Count >= 1024) _referenceGateSizes.Clear();
        _referenceGateSizes[key] = size;
    }
}
