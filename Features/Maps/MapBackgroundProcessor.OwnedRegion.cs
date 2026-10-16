using IDVBuff.Core.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public static partial class MapBackgroundProcessor
{
    // Persistence consumes one exclusive decoded buffer. A synchronous writer
    // borrows it only for that callback: recognition is written before white-key
    // processing changes its pixels, then the same storage becomes the overlay.
    // The persistence consumer needs no background mask or simultaneous images.
    internal static void WriteOwnedUnmaskedRegion(Mat pixels, FloorRecognitionProfile profile,
        Action<Mat> writeRecognition, Action<Mat>? writeOverlay)
    {
        using var operation = MemoryJournal.BeginOperation("Background.WriteOwnedRegion", profile.FloorKey);
        try
        {
            if (pixels.Empty() || pixels.Type() != MatType.CV_8UC4
                || profile.BackgroundLayers is { Count: > 0 } || profile.FreeCropPoints is { Count: > 0 })
                throw new InvalidOperationException("区域像素不满足独立处理条件。");
            NormalizeTransparentPixels(pixels);
            UpdateRecognitionDimensions(profile, pixels.Width, pixels.Height);
            MemoryJournal.Current?.Record("background-write-stage", new { stage = "recognition",
                payloadBytes = pixels.Total() * pixels.ElemSize(), independentMask = false });
            writeRecognition(pixels);
            if (writeOverlay is not null)
            {
                var width = pixels.Width;
                var height = pixels.Height;
                for (var y = 0; y < height; y += 64)
                {
                    using var input = new Mat(pixels, new Rect(0, y, width, Math.Min(64, height - y)));
                    using var tile = CreateWhiteKeyOverlayTile(input);
                    tile.CopyTo(input);
                }
                MemoryJournal.Current?.Record("background-write-stage", new { stage = "overlay",
                    payloadBytes = pixels.Total() * pixels.ElemSize(), reusedRecognitionStorage = true });
                writeOverlay(pixels);
            }
        }
        finally
        {
            pixels.Dispose();
            MemoryJournal.ReleaseResource(pixels);
        }
    }

    // Always consumes a newly decoded, exclusively owned BGRA region, including
    // on failure. Existing callers of Process still keep their source unchanged.
    // Masked/free-crop processing requires source coordinates and cannot use it.
    internal static MapBackgroundProcessingResult ProcessOwnedUnmaskedRegion(Mat pixels, FloorRecognitionProfile profile)
    {
        using var operation = MemoryJournal.BeginOperation("Background.ProcessOwnedRegion", profile.FloorKey);
        Mat? mask = null, overlay = null;
        try
        {
            if (pixels.Empty() || pixels.Type() != MatType.CV_8UC4
                || profile.BackgroundLayers is { Count: > 0 } || profile.FreeCropPoints is { Count: > 0 })
                throw new InvalidOperationException("区域像素不满足独立处理条件。");
            NormalizeTransparentPixels(pixels);
            UpdateRecognitionDimensions(profile, pixels.Width, pixels.Height);
            mask = new Mat(pixels.Height, pixels.Width, MatType.CV_8UC1, Scalar.Black);
            overlay = CreateWhiteKeyOverlay(pixels);
            // Ownership transfer, not a heap free: the result keeps these same
            // decoded pixels alive and closes them after all PNG writes finish.
            MemoryJournal.ReleaseResource(pixels);
            MemoryJournal.Current?.Record("recognition-region-ownership-transfer", new
                { from = "Recognition.DecodedRegion", to = "Background.Recognition", pixelCopy = false });
            return new MapBackgroundProcessingResult(pixels, overlay, mask);
        }
        catch
        {
            pixels.Dispose(); mask?.Dispose(); overlay?.Dispose();
            MemoryJournal.ReleaseResource(pixels);
            if (mask is not null) MemoryJournal.ReleaseResource(mask);
            if (overlay is not null) MemoryJournal.ReleaseResource(overlay);
            throw;
        }
    }

}
