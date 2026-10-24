using System.Security.Cryptography;
using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Features.Maps;

// Derived, disposable disk data. No source Mat, MapRecord or completed Task is
// retained here. Every successful read creates independent resource ownership.
internal static class PreparedFloorArtifactCache
{
    internal static T? Read<T>(string path, byte[] key, long maximumBytes, Func<BinaryReader, T> read) where T : class, IDisposable
    {
        T? result = null;
        try
        {
            if (!File.Exists(path)) return null;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            if (file.Length < 68 || file.Length > maximumBytes) return null;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var stream = new HashStream(file, hash);
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            if (reader.ReadInt32() != 0x31465049) return null;
            Span<byte> storedKey = stackalloc byte[32]; stream.ReadExactly(storedKey);
            if (!storedKey.SequenceEqual(key)) return null;
            result = read(reader);
            if (file.Position != file.Length - 32) throw new InvalidDataException("Prepared floor cache has trailing or missing data.");
            Span<byte> digest = stackalloc byte[32]; file.ReadExactly(digest);
            if (!CryptographicOperations.FixedTimeEquals(digest, hash.GetHashAndReset()))
                throw new InvalidDataException("Prepared floor cache checksum differs.");
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            MemoryJournal.Current?.Record("prepared-floor-cache-hit", new { path, bytes = file.Length });
            return result;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            result?.Dispose();
            MemoryJournal.Current?.Record("prepared-floor-cache-miss", new { path, reason = error.Message });
            return null;
        }
    }

    internal static void Write(string path, byte[] key, Action<BinaryWriter> write)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var stream = new HashStream(file, hash);
                using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
                writer.Write(0x31465049); stream.Write(key); write(writer); writer.Flush();
                file.Write(hash.GetHashAndReset()); file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            MemoryJournal.Current?.Record("prepared-floor-cache-write-failed", new { path, reason = error.Message });
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed class HashStream(Stream source, IncrementalHash hash) : Stream
    {
        public override bool CanRead => source.CanRead;
        public override bool CanWrite => source.CanWrite;
        public override bool CanSeek => source.CanSeek;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => throw new NotSupportedException(); }
        public override int Read(Span<byte> buffer)
        {
            var count = source.Read(buffer); hash.AppendData(buffer[..count]); return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) { source.Write(buffer); hash.AppendData(buffer); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Flush() => source.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
