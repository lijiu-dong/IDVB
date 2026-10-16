using System.Buffers.Binary;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    // Only two measured dimensions, never reference pixels. The caller hashes
    // the algorithm version, map/floor/content, source/template stamps and anchors.
    internal bool TryReadReferenceGateSize(string key, out (double Width, double Height) size)
    {
        size = default;
        try
        {
            var path = ReferenceGateSizePath(key);
            if (!File.Exists(path) || new FileInfo(path).Length != 20) return false;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length != 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x31534749) return false;
            size = (BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(4)), BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(12)));
            return IsValidGateSize(size);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal void StoreReferenceGateSize(string key, (double Width, double Height) size)
    {
        if (!IsValidGateSize(size)) return;
        var path = ReferenceGateSizePath(key);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x31534749);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(4), size.Width);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(12), size.Height);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Derived metadata is optional. Keep the freshly measured dimensions
            // and recognition behavior when the cache cannot be written.
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private string ReferenceGateSizePath(string key)
    {
        if (key.Length != 64 || key.Any(character => !char.IsAsciiHexDigit(character)))
            throw new ArgumentException("门图标尺寸缓存键必须为 SHA-256。", nameof(key));
        return Path.Combine(_rootDirectory, ".reference-gate-sizes", key + ".bin");
    }

    private static bool IsValidGateSize((double Width, double Height) size) =>
        double.IsFinite(size.Width) && double.IsFinite(size.Height)
        && (size == (0d, 0d) || size.Width > 0 && size.Height > 0 && size.Width <= 4096 && size.Height <= 4096);
}
