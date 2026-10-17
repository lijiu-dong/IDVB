using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    // Preserve the exact appearance-v2 byte stream. Cache hits used to create
    // a profile JSON string, a combined string, and a full UTF-8 copy, including
    // every freehand crop point and annotation. Hash that same stream directly.
    internal static string ComputeAppearanceCacheKey(string sourceStamp, MapAppearancePalette palette,
        string? croppedFloor, bool thumbnail,
        IEnumerable<(string Stamp, FloorRecognitionProfile? Profile)> floors)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new AppearanceHashStream(hash);
        using var text = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
        text.Write("appearance-v2|");
        text.Write(sourceStamp);
        text.Flush();
        JsonSerializer.Serialize(stream, palette);
        text.Write(croppedFloor);
        text.Write(thumbnail);
        foreach (var (stamp, profile) in floors)
        {
            text.Write(stamp);
            text.Flush();
            JsonSerializer.Serialize(stream, profile);
        }
        text.Flush();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class AppearanceHashStream(IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => hash.AppendData(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
