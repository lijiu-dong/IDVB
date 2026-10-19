using System.Runtime.CompilerServices;

namespace IDVBuff.Core.Collections;

public sealed partial class NativeEncodedByteField
{
    // The compact encoder owns these directories; this is not an arbitrary
    // binary-file constructor. Validate every offset before a pointer-based
    // pixel kernel can see the immutable snapshot, including its final tile.
    private static void ValidateEncoding(CompactByteField.EncodedStorage storage) =>
        ValidateEncoding(storage.Width, storage.Height, storage.TileColumns, storage.Constant,
            storage.Data, storage.Tiles, storage.CompactTiles, storage.Tiles is not null, storage.CompactTiles is not null);

    internal static void ValidateEncoding(int width, int height, int columns, bool constant,
        ReadOnlySpan<byte> data, ReadOnlySpan<uint> tiles, ReadOnlySpan<byte> compactTiles, bool hasTiles, bool hasCompact)
    {
        if (width <= 0 || height <= 0
            || columns != (width + 7) / 8
            || (hasTiles && hasCompact))
            throw new ArgumentException("Invalid encoded raster dimensions or directory.");
        if (constant)
        {
            if (data.Length != 1 || hasTiles || hasCompact)
                throw new ArgumentException("Invalid constant raster storage.");
            return;
        }
        if (!hasTiles && !hasCompact)
        {
            if (data.Length != checked(width * height))
                throw new ArgumentException("Invalid raw raster storage.");
            return;
        }
        var count = checked(columns * ((height + 7) / 8));
        var compact = compactTiles;
        if (hasCompact ? compact.Length != checked(count * 3 + 1) : tiles.Length != count)
            throw new ArgumentException("Invalid encoded raster directory length.");
        for (var i = 0; i < count; i++)
        {
            int mode, offset;
            if (hasCompact)
            {
                var at = i * 3;
                var descriptor = (uint)(compact[at] | compact[at + 1] << 8 | compact[at + 2] << 16);
                mode = (int)(descriptor >> 20); offset = (int)(descriptor & 0xfffff);
                if (mode == 10) continue; // Complete bounded inline affine encoding.
                if (mode == 9 && offset <= byte.MaxValue) continue;
                if (mode > 8) throw new ArgumentException("Invalid inline raster descriptor.");
            }
            else
            {
                var descriptor = tiles[i];
                mode = (int)(descriptor >> 28); offset = (int)(descriptor & 0xfffffff);
                if (mode > 9) throw new ArgumentException("Invalid raster descriptor mode.");
            }
            var bytes = mode == 9 ? 1 : mode == 8 ? 64 : 5 + 8 * mode;
            if (offset > data.Length - bytes)
                throw new ArgumentException("Encoded tile crosses its payload boundary.");
        }
    }

    /// <summary>A borrowed immutable view. Keep its reader undisposed until all
    /// reads finish. The view roots that reader so an optimizing JIT cannot let
    /// its fallback finalizer release storage before the final pointer access.</summary>
    public readonly ref struct ReadView
    {
        private readonly ReadLease _reader;
        private readonly PixelKernel _pixels;
        internal ReadView(ReadLease reader, PixelKernel pixels) { _reader = reader; _pixels = pixels; }
        public int Width => _pixels.Width;
        public int Height => _pixels.Height;
        public byte At(int x, int y)
        {
            var value = _pixels.At(x, y);
            GC.KeepAlive(_reader);
            return value;
        }
    }

    /// <summary>Borrowed immutable pixel view. The requesting reader must remain
    /// alive for every access. Its complete directory was validated once before
    /// allocation; each public query also validates its raster coordinate.</summary>
    internal readonly unsafe struct PixelKernel
    {
        internal readonly byte* _data;
        internal readonly uint* _tiles;
        internal readonly byte* _compact;
        internal readonly int _width, _height, _columns;
        internal readonly bool _constant, _hasTiles, _hasCompact;
        public int Width => _width;
        public int Height => _height;
        internal PixelKernel(byte* data, uint* tiles, byte* compact, int width, int height,
            int columns, bool constant, bool hasTiles, bool hasCompact)
        {
            _data = data; _tiles = tiles; _compact = compact;
            _width = width; _height = height; _columns = columns;
            _constant = constant; _hasTiles = hasTiles; _hasCompact = hasCompact;
        }

        internal uint Quad(int x, int y)
        {
            if ((uint)x >= _width - 1 || (uint)y >= _height - 1) throw new ArgumentOutOfRangeException();
            if (_constant) return _data[0] * 0x01010101u;
            if (!_hasTiles && !_hasCompact)
            {
                var at = y * _width + x;
                return (uint)(_data[at] | _data[at + 1] << 8 | _data[at + _width] << 16 | _data[at + _width + 1] << 24);
            }
            var px = x & 7; var py = y & 7;
            if (px == 7 || py == 7)
                return (uint)(At(x, y) | At(x + 1, y) << 8 | At(x, y + 1) << 16 | At(x + 1, y + 1) << 24);
            var index = (y >> 3) * _columns + (x >> 3);
            int mode, offset, first, dx, dy;
            if (_hasCompact)
            {
                var descriptor = Unsafe.ReadUnaligned<uint>(_compact + index * 3) & 0xffffff;
                offset = (int)(descriptor & 0xfffff); mode = (int)(descriptor >> 20);
                if (mode == 9) return (byte)offset * 0x01010101u;
                if (mode == 10)
                {
                    dx = (offset << 18) >> 26; dy = (offset << 12) >> 26;
                    first = (byte)offset + dx * px + dy * py;
                    return PackAffine(first, dx, dy);
                }
            }
            else
            {
                var descriptor = _tiles[index];
                offset = (int)(descriptor & 0xfffffff); mode = (int)(descriptor >> 28);
                if (mode == 9) return _data[offset] * 0x01010101u;
            }
            var pixel = py * 8 + px;
            if (mode == 8)
            {
                var at = offset + pixel;
                return (uint)(_data[at] | _data[at + 1] << 8 | _data[at + 8] << 16 | _data[at + 9] << 24);
            }
            dx = (sbyte)_data[offset + 1]; dy = (sbyte)_data[offset + 2];
            first = _data[offset] + dx * px + dy * py + (short)(_data[offset + 3] | _data[offset + 4] << 8);
            if (mode == 0) return PackAffine(first, dx, dy);
            var bits = _data + offset + 5;
            return (uint)((byte)(first + Residual(bits, pixel, mode))
                | (byte)(first + dx + Residual(bits, pixel + 1, mode)) << 8
                | (byte)(first + dy + Residual(bits, pixel + 8, mode)) << 16
                | (byte)(first + dx + dy + Residual(bits, pixel + 9, mode)) << 24);
        }
        private static uint PackAffine(int first, int dx, int dy) =>
            (uint)((byte)first | (byte)(first + dx) << 8 | (byte)(first + dy) << 16 | (byte)(first + dx + dy) << 24);
        private static int Residual(byte* bits, int pixel, int mode)
        {
            var bit = pixel * mode; var at = bit >> 3; var packed = (int)bits[at];
            if ((bit & 7) + mode > 8) packed |= bits[at + 1] << 8;
            return (packed >> (bit & 7)) & ((1 << mode) - 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte At(int x, int y)
        {
            if ((uint)x >= _width || (uint)y >= _height) throw new ArgumentOutOfRangeException();
            if (_constant) return _data[0];
            if (!_hasTiles && !_hasCompact) return _data[y * _width + x];
            var index = (y >> 3) * _columns + (x >> 3);
            int offset, mode;
            if (_hasCompact)
            {
                // The directory includes its own final padding byte. No read can
                // cross an allocation even for an image's last partial tile.
                var descriptor = Unsafe.ReadUnaligned<uint>(_compact + index * 3) & 0xffffff;
                offset = (int)(descriptor & 0xfffff); mode = (int)(descriptor >> 20);
                if (mode == 9) return (byte)offset;
                if (mode == 10) return (byte)((byte)offset + ((offset << 18) >> 26) * (x & 7) + ((offset << 12) >> 26) * (y & 7));
            }
            else
            {
                var descriptor = _tiles[index];
                offset = (int)(descriptor & 0xfffffff); mode = (int)(descriptor >> 28);
                if (mode == 9) return _data[offset];
            }
            var pixel = (y & 7) * 8 + (x & 7);
            if (mode == 8) return _data[offset + pixel];
            var minimum = (short)(_data[offset + 3] | _data[offset + 4] << 8);
            var residual = 0;
            if (mode > 0)
            {
                var bit = pixel * mode; var at = offset + 5 + (bit >> 3); var packed = (int)_data[at];
                if ((bit & 7) + mode > 8) packed |= _data[at + 1] << 8;
                residual = (packed >> (bit & 7)) & ((1 << mode) - 1);
            }
            return (byte)(_data[offset] + (sbyte)_data[offset + 1] * (x & 7)
                + (sbyte)_data[offset + 2] * (y & 7) + minimum + residual);
        }
    }
}
