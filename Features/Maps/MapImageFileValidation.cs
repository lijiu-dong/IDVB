using System.Buffers.Binary;
using System.IO.Compression;
using IDVBuff.Core.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static partial class MapImageFileValidation
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    internal static (int Width, int Height) ValidateImageFile(string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var operation = MemoryJournal.BeginOperation("Image.ValidateFile", path);
        using var input = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[8];
        if (input.Read(signature) == 8 && signature.SequenceEqual(PngSignature))
            return ValidatePng(input, cancellation);
        // Preserve existing JPEG rejection behavior. WIC can silently fill
        // missing pixels, so it is not a validation substitute.
        var encodedRequest = MemoryJournal.Request("Image.ValidationEncodedInput", new FileInfo(path).Length);
        var encoded = File.ReadAllBytes(path);
        MemoryJournal.ObserveManagedArray(encoded, "Image.ValidationEncodedInput", encodedRequest);
        var request = MemoryJournal.Request("Image.ValidationRaster", nativeModule: "OpenCvSharpExtern.dll");
        Mat? image = null;
        try
        {
            image = Cv2.ImDecode(encoded, ImreadModes.Unchanged);
            MemoryJournal.TrackResource(image, "Image.ValidationRaster", image.Total() * image.ElemSize(),
                "OpenCvSharpExtern.dll", request);
            if (image.Empty()) throw new InvalidDataException("地图图片无法完整解码。");
            return (image.Width, image.Height);
        }
        finally
        {
            if (image is not null) { image.Dispose(); MemoryJournal.ReleaseResource(image); }
        }
    }

    private static (int Width, int Height) ValidatePng(FileStream input, CancellationToken cancellation)
    {
        const string resource = "Image.ValidationPngChunkBuffer";
        var request = MemoryJournal.Request(resource, 65536);
        var buffer = new byte[65536];
        MemoryJournal.ObserveManagedArray(buffer, resource, request);
        using var owner = MemoryJournal.Own(resource, buffer.Length, requestId: request);
        Span<byte> header = stackalloc byte[8];
        Span<byte> expectedCrc = stackalloc byte[4];
        int width = 0, height = 0, depth = 0, channels = 0, color = 0, interlace = 0, paletteEntries = 0;
        bool sawData = false, endedData = false;
        long dataBytes = 0;
        uint trailer = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            input.ReadExactly(header);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            if (length > int.MaxValue || length > input.Length - input.Position - 4)
                throw new InvalidDataException("PNG 数据块被截断。");
            if (width == 0 && type != 0x49484452) throw new InvalidDataException("PNG 缺少图片头。");
            var crc = UpdateCrc(uint.MaxValue, header[4..]);
            var remaining = length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, (uint)buffer.Length);
                input.ReadExactly(buffer.AsSpan(0, count));
                crc = UpdateCrc(crc, buffer.AsSpan(0, count));
                if (type == 0x49484452)
                {
                    if (width != 0 || length != 13) throw new InvalidDataException("PNG 图片头无效。");
                    width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(buffer));
                    height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(4)));
                    depth = buffer[8]; color = buffer[9]; interlace = buffer[12];
                    channels = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
                    var validDepth = color is 0 ? depth is 1 or 2 or 4 or 8 or 16
                        : color is 3 ? depth is 1 or 2 or 4 or 8 : depth is 8 or 16;
                    if (width <= 0 || height <= 0 || channels == 0 || !validDepth || buffer[10] != 0
                        || buffer[11] != 0 || interlace > 1) throw new InvalidDataException("PNG 图片头无效。");
                }
                if (type == 0x49444154)
                {
                    if (count >= 4) trailer = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(count - 4, 4));
                    else for (var i = 0; i < count; i++) trailer = (trailer << 8) | buffer[i];
                }
                remaining -= (uint)count;
            }
            input.ReadExactly(expectedCrc);
            if ((crc ^ uint.MaxValue) != BinaryPrimitives.ReadUInt32BigEndian(expectedCrc))
                throw new InvalidDataException("PNG 数据块校验失败。");
            if (type == 0x504c5445)
            {
                if (sawData || length == 0 || length > 768 || length % 3 != 0)
                    throw new InvalidDataException("PNG 调色板无效。");
                paletteEntries = (int)length / 3;
            }
            if (type == 0x49444154)
            {
                if (endedData || color == 3 && paletteEntries == 0) throw new InvalidDataException("PNG 像素块无效。");
                sawData = true; dataBytes += length;
            }
            else if (sawData) endedData = true;
            if (type == 0x49454e44)
            {
                if (length != 0 || !sawData || dataBytes < 6) throw new InvalidDataException("PNG 缺少完整像素数据。");
                break;
            }
            if ((header[4] & 32) == 0 && type is not (0x49484452 or 0x504c5445 or 0x49444154))
                throw new InvalidDataException("PNG 包含不支持的关键数据块。");
        }
        // Two scanlines only. Validate all filters, palette indices, pixel
        // bytes and the Adler checksum, including all seven Adam7 passes.
        var maxRow = checked((int)(((long)width * channels * depth + 7) / 8));
        var rowRequest = MemoryJournal.Request("Image.ValidationPngRows", checked(2L * maxRow));
        var row = new byte[maxRow]; var previous = new byte[maxRow];
        MemoryJournal.ObserveManagedArray(row, "Image.ValidationPngRow", rowRequest);
        MemoryJournal.ObserveManagedArray(previous, "Image.ValidationPngPreviousRow");
        using var rowsOwner = MemoryJournal.Own("Image.ValidationPngRows", 2L * maxRow, requestId: rowRequest);
        input.Position = 8;
        using var idat = new PngDataStream(input);
        using var pixels = new ZLibStream(idat, CompressionMode.Decompress, leaveOpen: true);
        uint sum1 = 1, sum2 = 0;
        ReadOnlySpan<int> startsX = [0, 4, 0, 2, 0, 1, 0], startsY = [0, 0, 4, 0, 2, 0, 1];
        ReadOnlySpan<int> stepsX = [8, 8, 4, 4, 2, 2, 1], stepsY = [8, 8, 8, 4, 4, 2, 2];
        for (var pass = 0; pass < (interlace == 0 ? 1 : 7); pass++)
        {
            var passWidth = interlace == 0 ? width : (int)Math.Max(0, ((long)width - startsX[pass] + stepsX[pass] - 1) / stepsX[pass]);
            var passHeight = interlace == 0 ? height : (int)Math.Max(0, ((long)height - startsY[pass] + stepsY[pass] - 1) / stepsY[pass]);
            if (passWidth == 0 || passHeight == 0) continue;
            var rowBytes = checked((int)(((long)passWidth * channels * depth + 7) / 8));
            Array.Clear(previous);
            for (var y = 0; y < passHeight; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                var filter = pixels.ReadByte();
                if (filter is < 0 or > 4) throw new InvalidDataException("PNG 像素行无效。");
                sum1 = (sum1 + (uint)filter) % 65521; sum2 = (sum2 + sum1) % 65521;
                pixels.ReadExactly(row.AsSpan(0, rowBytes));
                UpdateAdler(ref sum1, ref sum2, row.AsSpan(0, rowBytes));
                if (color == 3)
                {
                    for (var i = 0; i < rowBytes; i++)
                    {
                        var a = i == 0 ? 0 : row[i - 1]; var b = previous[i]; var c = i == 0 ? 0 : previous[i - 1];
                        var prediction = filter switch { 1 => a, 2 => b, 3 => (a + b) / 2, 4 => Paeth(a, b, c), _ => 0 };
                        row[i] = unchecked((byte)(row[i] + prediction));
                    }
                    for (var x = 0; x < passWidth; x++)
                        if (((row[x * depth / 8] >> (8 - depth - x * depth % 8)) & ((1 << depth) - 1)) >= paletteEntries)
                            throw new InvalidDataException("PNG 调色板像素无效。");
                    (row, previous) = (previous, row);
                }
            }
        }
        if (pixels.ReadByte() != -1 || (sum2 << 16 | sum1) != trailer)
            throw new InvalidDataException("PNG 像素数据长度或校验和无效。");
        return (width, height);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c; var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return crc;
    }
    private static uint[] BuildCrcTable()
    {
        var result = new uint[256];
        for (uint i = 0; i < result.Length; i++) { var value = i; for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1; result[i] = value; }
        return result;
    }
    private sealed class PngDataStream(FileStream input) : Stream
    {
        private long _remaining;
        private bool _end;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0) return 0;
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
