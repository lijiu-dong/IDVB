using System.Drawing;
using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Features.Maps;

internal static partial class MapOverlayBitmapRenderer
{
    // Player icons must not evict either floor source: retiring a floor also
    // retires its baked layers. Four players previously forced that every frame.
    internal const int MaxPlayerImageCacheEntries = 4;
    private sealed record PlayerImageCacheEntry(SourceImageVersion Version, Bitmap Bitmap);
    private static readonly Dictionary<string, PlayerImageCacheEntry> PlayerImageCache = [];
    private static readonly LinkedList<string> PlayerImageRecency = [];

    // Callers hold ImageCacheLock through drawing, so invalidation cannot close
    // the bitmap or its encoded backing stream while GDI+ is reading it.
    private static Bitmap GetOrLoadPlayerImage(string imagePath)
    {
        var version = ReadSourceImageVersion(imagePath);
        if (PlayerImageCache.TryGetValue(imagePath, out var cached))
        {
            if (version == default || cached.Version == version)
            {
                var node = PlayerImageRecency.Find(imagePath)!;
                PlayerImageRecency.Remove(node);
                PlayerImageRecency.AddLast(node);
                return cached.Bitmap;
            }
            DisposeCachedBitmap(cached.Bitmap);
            PlayerImageCache.Remove(imagePath);
            PlayerImageRecency.Remove(imagePath);
        }
        var bitmap = LoadEncodedSourceImage(imagePath);
        try
        {
            if (PlayerImageCache.Count >= MaxPlayerImageCacheEntries)
            {
                var path = PlayerImageRecency.First!.Value;
                PlayerImageRecency.RemoveFirst();
                DisposeCachedBitmap(PlayerImageCache[path].Bitmap);
                PlayerImageCache.Remove(path);
            }
            PlayerImageCache.Add(imagePath, new(version, bitmap));
            PlayerImageRecency.AddLast(imagePath);
            return bitmap;
        }
        catch { DisposeCachedBitmap(bitmap); throw; }
    }

    private static Bitmap LoadEncodedSourceImage(string imagePath)
    {
        var bytes = IDVBuff.Core.Contracts.Online.RecognitionResources.ReadAllBytes(imagePath);
        MemoryJournal.ObserveManagedArray(bytes, "Overlay.SourceImage.EncodedBytes");
        var request = MemoryJournal.Request("Overlay.SourceImage", nativeModule: "gdiplus.dll");
        var stream = new MemoryStream(bytes, writable: false);
        Bitmap bitmap;
        try { bitmap = new Bitmap(stream); }
        catch { stream.Dispose(); throw; }
        try
        {
            // GDI+ may decode again while the bitmap is alive. Both caches own
            // the stream until DisposeCachedBitmap releases their bitmap first.
            SourceImageStreams.Add(bitmap, stream);
            MemoryJournal.TrackResource(stream, "Overlay.SourceImage.Stream", 0);
            MemoryJournal.TrackResource(bitmap, "Overlay.SourceImage",
                checked((long)bitmap.Width * bitmap.Height * 4), "gdiplus.dll", request);
            return bitmap;
        }
        catch
        {
            try { DisposeCachedBitmap(bitmap); }
            finally { stream.Dispose(); }
            throw;
        }
    }
}
