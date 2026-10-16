using IDVBuff.Core.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    // Borrow the normalized recognition pixels before the persistence writer
    // transforms them into the overlay. The thumbnail must not modify or own
    // this input; its owner closes it after both persistence callbacks finish.
    private static void WriteBorrowedRecognitionThumbnail(Mat pixels, string destinationPath)
    {
        using var operation = MemoryJournal.BeginOperation("Thumbnail.GenerateBorrowed", destinationPath);
        using var borrowing = MemoryJournal.Own("Thumbnail.BorrowedRecognition", 0, "OpenCvSharpExtern.dll");
        Mat? bgr = null, thumbnail = null;
        try
        {
            bgr = MapBackgroundProcessor.CompositeToBgr(pixels);
            TrackThumbnailRaster(bgr, "CompositeBgr");
            var width = Math.Min(400, bgr.Width);
            var height = Math.Max(1, (int)Math.Round(bgr.Height * (width / (double)bgr.Width)));
            thumbnail = new Mat();
            Cv2.Resize(bgr, thumbnail, new Size(width, height), 0, 0, InterpolationFlags.Area);
            TrackThumbnailRaster(thumbnail, "Resized");
            ReleaseThumbnailRaster(ref bgr);
            if (!Cv2.ImWrite(destinationPath, thumbnail, [new ImageEncodingParam(ImwriteFlags.JpegQuality, 82)]))
                throw new InvalidOperationException($"Image cannot be written: '{destinationPath}'.");
        }
        finally { ReleaseThumbnailRaster(ref thumbnail); ReleaseThumbnailRaster(ref bgr); }
    }
}
