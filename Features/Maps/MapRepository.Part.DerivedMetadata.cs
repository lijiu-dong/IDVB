namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    private static bool MatchesStoredDerivedMetadata(
        string path,
        string expectedSha256,
        int expectedWidth,
        int expectedHeight,
        long expectedFileLength,
        long expectedLastWriteUtcTicks,
        string expectedSourceSha256,
        bool requiresFile,
        out FloorImageMetadata? refreshedMetadata)
    {
        refreshedMetadata = null;
        if (string.IsNullOrWhiteSpace(expectedSha256)
            || string.IsNullOrWhiteSpace(expectedSourceSha256)
            || expectedWidth <= 0
            || expectedHeight <= 0)
            return false;
        if (requiresFile && !File.Exists(path))
            return false;
        if (!File.Exists(path))
            return string.Equals(path, expectedSourceSha256, StringComparison.OrdinalIgnoreCase);

        var info = new FileInfo(path);
        if (expectedFileLength > 0
            && expectedLastWriteUtcTicks > 0
            && info.Length == expectedFileLength
            && info.LastWriteTimeUtc.Ticks == expectedLastWriteUtcTicks)
            return true;

        var actual = ReadImageMetadataAsync(path).GetAwaiter().GetResult();
        var matches = string.Equals(actual.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase)
            && actual.Width == expectedWidth
            && actual.Height == expectedHeight;
        if (matches) refreshedMetadata = actual;
        return matches;
    }

}
