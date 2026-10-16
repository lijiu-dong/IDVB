using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IDVBuff.Core.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Core.Imaging;

/// <summary>Reads supported prebuilt PNG scanlines directly into categorical
/// pixels. Other PNG encodings remain with the caller's existing decoder.</summary>
public static class PrebuiltContourLabels
{
    private static readonly byte[] Signature = CreateSignature();
    private static readonly uint[] CrcTable = CreateCrcTable();

    /// <summary>The caller owns and must dispose/release a successful label Mat.
    /// False means unsupported encoding, never a partially accepted image.</summary>
    // Cold preparation uses this pixel loop only once per floor. Measured
    // tier-zero execution was slower than the original native decoder, so
    // optimize this bounded kernel from its first call, without changing UI JIT.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool TryRead(string path, [NotNullWhen(true)] out Mat? labels, out bool semantic,
        string resource = "ScanLabels.DirectPng")
    {
        labels = null;
        semantic = false;
        using var operation = MemoryJournal.BeginOperation("Scan.DecodePngLabels", path);
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[33];
        if (file.Read(header[..8]) != 8 || !header[..8].SequenceEqual(Signature)) return false;
        file.ReadExactly(header[8..]);
        if (BinaryPrimitives.ReadUInt32BigEndian(header[8..]) != 13
            || BinaryPrimitives.ReadUInt32BigEndian(header[12..]) != 0x49484452)
            throw new InvalidDataException("预制轮廓 PNG 图片头无效。");
        // Preserve the unchanged decoder's handling of palette, 16-bit,
        // alpha and Adam7 inputs. A rejected palette never falls back here.
        if (header[24] != 8 || header[25] is not (0 or 2)
            || header[26] != 0 || header[27] != 0 || header[28] != 0) return false;
        var width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[16..]));
        var height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[20..]));
        if (width <= 0 || height <= 0) throw new InvalidDataException("预制轮廓尺寸无效。");
        var channels = header[25] == 2 ? 3 : 1;
        var trailer = ValidateChunks(file);
        if (trailer is null) return false; // tRNS changes the unchanged decoder's channel count.
        var rowLength = checked(width * channels);
        var row = new byte[rowLength];
        var previous = new byte[rowLength];
        var rowResource = resource + ".Rows";
        MemoryJournal.ObserveManagedArray(row, rowResource);
        MemoryJournal.ObserveManagedArray(previous, rowResource);
        using var rows = MemoryJournal.Own(rowResource, 2L * rowLength);
        file.Position = 8;
        using var idat = new PngDataStream(file);
        using var pixels = new ZLibStream(idat, CompressionMode.Decompress);
        var request = MemoryJournal.Request(resource, checked((long)width * height), "OpenCvSharpExtern.dll");
        Mat? result = null;
        try
        {
            result = new Mat(height, width, MatType.CV_8UC1);
            MemoryJournal.TrackResource(result, resource, checked((long)width * height), "OpenCvSharpExtern.dll", request);
            uint sum1 = 1, sum2 = 0;
            for (var y = 0; y < height; y++)
            {
                var filter = pixels.ReadByte();
                if (filter is < 0 or > 4) throw new InvalidDataException("预制轮廓像素行无效。");
                sum1 = (sum1 + (uint)filter) % 65521;
                sum2 = (sum2 + sum1) % 65521;
                pixels.ReadExactly(row);
                PngRasterChecksum.UpdateAdler(ref sum1, ref sum2, row);
                Unfilter(row, previous, channels, filter);
                var output = MemoryMarshal.CreateSpan(ref result.At<byte>(y, 0), width);
                if (channels == 1)
                {
                    // Original CV_8UC1 contract uses Threshold(128, Binary).
                    for (var x = 0; x < width; x++) output[x] = row[x] > 128 ? (byte)255 : (byte)0;
                }
                else
                {
                    for (int x = 0, i = 0; x < width; x++, i += 3)
                    {
                        byte r = row[i], g = row[i + 1], b = row[i + 2];
                        if ((r | g | b) == 0) output[x] = 0;
                        else if (r == 255 && g == 255 && b == 255) output[x] = 255;
                        else if (r == 255 && g == 140 && b == 0) output[x] = 127;
                        else throw new InvalidDataException("预制语义轮廓含非黑/白/橙像素，拒绝灰度降级。");
                    }
                }
                (row, previous) = (previous, row);
            }
            if (pixels.ReadByte() != -1 || (sum2 << 16 | sum1) != trailer.Value)
                throw new InvalidDataException("预制轮廓像素长度或校验和无效。");
            labels = result;
            semantic = channels == 3;
            return true;
        }
        catch
        {
            if (result is not null) { result.Dispose(); MemoryJournal.ReleaseResource(result); }
            throw;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Unfilter(Span<byte> row, ReadOnlySpan<byte> previous, int channels, int filter)
    {
        if (filter == 2)
        {
            var i = 0;
            for (; i <= row.Length - Vector<byte>.Count; i += Vector<byte>.Count)
                (new Vector<byte>(row[i..]) + new Vector<byte>(previous[i..])).CopyTo(row[i..]);
            for (; i < row.Length; i++) row[i] = unchecked((byte)(row[i] + previous[i]));
        }
        else if (filter != 0)
        {
            for (var i = 0; i < row.Length; i++)
            {
                var a = i >= channels ? row[i - channels] : 0;
                var b = previous[i];
                var c = i >= channels ? previous[i - channels] : 0;
                var prediction = filter == 1 ? a : filter == 3 ? (a + b) / 2 : Paeth(a, b, c);
                row[i] = unchecked((byte)(row[i] + prediction));
            }
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // Check every chunk before consuming pixels. The second pass streams IDAT;
    // neither pass retains the encoded file or a full RGB raster.
    private static uint? ValidateChunks(FileStream file)
    {
        file.Position = 8;
        Span<byte> header = stackalloc byte[8];
        Span<byte> expected = stackalloc byte[4];
        var buffer = new byte[65536];
        MemoryJournal.ObserveManagedArray(buffer, "ScanLabels.PngCrcBuffer");
        using var owner = MemoryJournal.Own("ScanLabels.PngCrcBuffer", buffer.Length);
        bool ihdr = false, data = false, endedData = false;
        long dataBytes = 0;
        uint trailer = 0;
        while (true)
        {
            file.ReadExactly(header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            if (length > int.MaxValue || length > file.Length - file.Position - 4)
                throw new InvalidDataException("预制轮廓 PNG 数据块被截断。");
            if (!ihdr)
            {
                if (type != 0x49484452 || length != 13) throw new InvalidDataException("预制轮廓缺少 PNG 图片头。");
                ihdr = true;
            }
            else if (type == 0x49484452) throw new InvalidDataException("预制轮廓 PNG 图片头重复。");
            if (type == 0x74524e53) return null;
            var crc = UpdateCrc(uint.MaxValue, header[4..]);
            var remaining = length;
            while (remaining != 0)
            {
                var count = (int)Math.Min(remaining, (uint)buffer.Length);
                file.ReadExactly(buffer.AsSpan(0, count));
                crc = UpdateCrc(crc, buffer.AsSpan(0, count));
                if (type == 0x49444154)
                {
                    if (count >= 4) trailer = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(count - 4, 4));
                    else for (var i = 0; i < count; i++) trailer = (trailer << 8) | buffer[i];
                }
                remaining -= (uint)count;
            }
            file.ReadExactly(expected);
            if ((crc ^ uint.MaxValue) != BinaryPrimitives.ReadUInt32BigEndian(expected))
                throw new InvalidDataException("预制轮廓 PNG 数据块校验失败。");
            if (type == 0x504c5445 && (data || length == 0 || length > 768 || length % 3 != 0))
                throw new InvalidDataException("预制轮廓 PNG 调色板无效。");
            if (type == 0x49444154)
            {
                if (endedData) throw new InvalidDataException("预制轮廓 PNG 像素块不连续。");
                data = true; dataBytes += length;
            }
            else if (data) endedData = true;
            if (type == 0x49454e44)
            {
                if (length != 0 || !data || dataBytes < 6) throw new InvalidDataException("预制轮廓 PNG 像素数据不完整。");
                return trailer;
            }
            if ((header[4] & 32) == 0 && type is not (0x49484452 or 0x49444154 or 0x504c5445))
                throw new InvalidDataException("预制轮廓 PNG 含未知关键数据块。");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
            table[i] = crc;
        }
        MemoryJournal.ObserveManagedArray(table, "ScanLabels.PngCrcTable");
        return table;
    }

    private static byte[] CreateSignature()
    {
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        MemoryJournal.ObserveManagedArray(signature, "ScanLabels.PngSignature");
        return signature;
    }

    private sealed class PngDataStream(FileStream input) : Stream
    {
        private long _remaining;
        private bool _end;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return 0;
            Span<byte> header = stackalloc byte[8];
            while (!_end && _remaining == 0)
            {
                input.ReadExactly(header);
                var length = BinaryPrimitives.ReadUInt32BigEndian(header);
                var type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
                if (type == 0x49444154) { _remaining = length; if (_remaining == 0) input.Position += 4; }
                else { input.Position += length + 4L; if (type == 0x49454e44) _end = true; }
            }
            if (_end) return 0;
            var count = input.Read(buffer[..(int)Math.Min(_remaining, buffer.Length)]);
            if (count == 0) throw new EndOfStreamException();
            _remaining -= count;
            if (_remaining == 0) input.Position += 4;
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
