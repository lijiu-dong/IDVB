namespace IDVBuff.Features.Maps;

internal static partial class MapOverlayBitmapRenderer
{
    // Main map and persistent minimap can use different floor sources.
    internal const int MaxSourceCacheEntries = 2;
    private readonly record struct SourceImageVersion(long Length, long WriteTicks);
    private static readonly Dictionary<string, SourceImageVersion> SourceImageVersions = [];
    private static readonly LinkedList<string> SourceImageRecency = [];

    private static SourceImageVersion ReadSourceImageVersion(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? new(file.Length, file.LastWriteTimeUtc.Ticks) : default;
    }

    internal static void PrepareImageCacheForRescan()
    {
        lock (ImageCacheLock)
        {
            foreach (var path in ImageCache.Keys.ToArray())
                EnsureImageCacheSourceVersionCore(path, discardMissing: true, touchRecency: false);
        }
    }

    private static bool EnsureImageCacheSourceVersionCore(string path, bool discardMissing = false, bool touchRecency = true)
    {
        if (!ImageCache.TryGetValue(path, out var source)) return false;
        var current = ReadSourceImageVersion(path);
        // A rendered source owns encoded bytes, not a file lock. It may finish
        // presenting after deletion; a new rescan explicitly retires that source.
        if (!discardMissing && current == default) return false;
        if (SourceImageVersions.TryGetValue(path, out var version)
            && current != default && version == current)
        {
            if (touchRecency && SourceImageRecency.Find(path) is { } node)
            { SourceImageRecency.Remove(node); SourceImageRecency.AddLast(node); }
            return false;
        }
        DisposeCachedBitmap(source);
        ImageCache.Remove(path);
        SourceImageVersions.Remove(path);
        SourceImageRecency.Remove(path);
        InvalidateLayerCachesCore();
        return true;
    }

    // Called under ImageCacheLock before publishing a newly decoded source.
    // Derived caches cannot retain pixels from a source removed from the budget.
    private static void TrimSourceImageCacheCore(int maximum)
    {
        var trimmed = false;
        while (ImageCache.Count > maximum)
        {
            var path = SourceImageRecency.First!.Value;
            SourceImageRecency.RemoveFirst();
            DisposeCachedBitmap(ImageCache[path]);
            ImageCache.Remove(path);
            SourceImageVersions.Remove(path);
            trimmed = true;
        }
        if (trimmed) InvalidateLayerCachesCore();
    }
}
