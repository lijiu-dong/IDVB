using System.Runtime.CompilerServices;
using IDVBuff.Core.Collections;

namespace IDVBuff.Core.Imaging;

public sealed unsafe partial class NativeSemanticField
{
    // Borrowed validated pixels; caller retains its independent lease for the full batch.
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    internal static uint ReadEncodedQuad(in NativeEncodedByteField.PixelKernel storage, int x, int y)
    {
        if ((uint)x >= storage._width - 1 || (uint)y >= storage._height - 1) throw new ArgumentOutOfRangeException();
        if (storage._constant) return storage._data[0] * 0x01010101u;
        if (!storage._hasTiles && !storage._hasCompact)
        {
            var at = y * storage._width + x;
            return (uint)(storage._data[at] | storage._data[at + 1] << 8 | storage._data[at + storage._width] << 16 | storage._data[at + storage._width + 1] << 24);
        }
        var px = x & 7; var py = y & 7;
        if (px == 7 || py == 7)
            return (uint)(Read(in storage, x, y) | Read(in storage, x + 1, y) << 8 | Read(in storage, x, y + 1) << 16 | Read(in storage, x + 1, y + 1) << 24);
        var index = (y >> 3) * storage._columns + (x >> 3);
        int mode, offset, first, dx, dy;
        if (storage._hasCompact)
        {
            var descriptor = Unsafe.ReadUnaligned<uint>(storage._compact + index * 3) & 0xffffff;
            offset = (int)(descriptor & 0xfffff); mode = (int)(descriptor >> 20);
            if (mode == 9) return (byte)offset * 0x01010101u;
            if (mode == 10)
            {
                dx = (offset << 18) >> 26; dy = (offset << 12) >> 26;
                first = (byte)offset + dx * px + dy * py;
                return PackDistanceQuad(first, dx, dy);
            }
        }
        else
        {
            var descriptor = storage._tiles[index];
            offset = (int)(descriptor & 0xfffffff); mode = (int)(descriptor >> 28);
            if (mode == 9) return storage._data[offset] * 0x01010101u;
        }
        var pixel = py * 8 + px;
        if (mode == 8)
        {
            var at = offset + pixel;
            return (uint)(storage._data[at] | storage._data[at + 1] << 8 | storage._data[at + 8] << 16 | storage._data[at + 9] << 24);
        }
        dx = (sbyte)storage._data[offset + 1]; dy = (sbyte)storage._data[offset + 2];
        first = storage._data[offset] + dx * px + dy * py + (short)(storage._data[offset + 3] | storage._data[offset + 4] << 8);
        if (mode == 0) return PackDistanceQuad(first, dx, dy);
        var bits = storage._data + offset + 5;
        return (uint)((byte)(first + ReadDistanceResidual(bits, pixel, mode))
            | (byte)(first + dx + ReadDistanceResidual(bits, pixel + 1, mode)) << 8
            | (byte)(first + dy + ReadDistanceResidual(bits, pixel + 8, mode)) << 16
            | (byte)(first + dx + dy + ReadDistanceResidual(bits, pixel + 9, mode)) << 24);
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    private static uint PackDistanceQuad(int first, int dx, int dy) =>
        (uint)((byte)first | (byte)(first + dx) << 8 | (byte)(first + dy) << 16 | (byte)(first + dx + dy) << 24);
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    private static int ReadDistanceResidual(byte* bits, int pixel, int mode)
    {
        var bit = pixel * mode; var at = bit >> 3; var packed = (int)bits[at];
        if ((bit & 7) + mode > 8) packed |= bits[at + 1] << 8;
        return (packed >> (bit & 7)) & ((1 << mode) - 1);
    }

}
