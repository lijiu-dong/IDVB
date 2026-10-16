using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace IDVBuff.Features.Maps;

internal static partial class MapImageFileValidation
{
    private static readonly Vector256<sbyte> Weights32 = Vector256.Create(
        (sbyte)32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17,
        16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1);
    private static readonly Vector128<short> WeightsHigh16 = Vector128.Create((short)16, 15, 14, 13, 12, 11, 10, 9);
    private static readonly Vector128<short> WeightsLow16 = Vector128.Create((short)8, 7, 6, 5, 4, 3, 2, 1);

    // Each block adds n * sum1 plus a descending weighted sum of its bytes.
    // All arithmetic is exact integer arithmetic within the scalar 5552-byte
    // overflow bound. No pixel buffer or external native dependency is added.
    internal static void UpdateAdler(ref uint sum1, ref uint sum2, ReadOnlySpan<byte> bytes)
    {
        for (var offset = 0; offset < bytes.Length;)
        {
            var end = Math.Min(bytes.Length, offset + 5552);
            if (Avx2.IsSupported)
            {
                var start = offset;
                var total = Vector256<ulong>.Zero;
                var prefix = Vector256<ulong>.Zero;
                var weighted = Vector256<int>.Zero;
                ref var first = ref MemoryMarshal.GetReference(bytes);
                for (; offset <= end - 32; offset += 32)
                {
                    // The loop bound proves the complete unaligned load lies
                    // within this span. Keep vector reductions outside the loop.
                    var block = Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref first, offset));
                    prefix = Avx2.Add(prefix, total);
                    total = Avx2.Add(total, Avx2.SumAbsoluteDifferences(block, Vector256<byte>.Zero).AsUInt64());
                    var pairs = Avx2.MultiplyAddAdjacent(block, Weights32);
                    weighted = Avx2.Add(weighted, Avx2.MultiplyAddAdjacent(pairs, Vector256.Create((short)1)));
                }
                weighted = Avx2.HorizontalAdd(weighted, weighted);
                weighted = Avx2.HorizontalAdd(weighted, weighted);
                var prefixSum = prefix.GetElement(0) + prefix.GetElement(1) + prefix.GetElement(2) + prefix.GetElement(3);
                sum2 += (uint)((ulong)(offset - start) * sum1 + 32 * prefixSum
                    + (uint)(weighted.GetElement(0) + weighted.GetElement(4)));
                sum1 += (uint)(total.GetElement(0) + total.GetElement(1) + total.GetElement(2) + total.GetElement(3));
            }
            if (Sse2.IsSupported)
            {
                var start = offset;
                var total = Vector128<ulong>.Zero;
                var prefix = Vector128<ulong>.Zero;
                var weighted = Vector128<int>.Zero;
                ref var first = ref MemoryMarshal.GetReference(bytes);
                for (; offset <= end - 16; offset += 16)
                {
                    var block = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref first, offset));
                    prefix = Sse2.Add(prefix, total);
                    total = Sse2.Add(total, Sse2.SumAbsoluteDifferences(block, Vector128<byte>.Zero).AsUInt64());
                    var high = Sse2.MultiplyAddAdjacent(Sse2.UnpackLow(block, Vector128<byte>.Zero).AsInt16(), WeightsHigh16);
                    var low = Sse2.MultiplyAddAdjacent(Sse2.UnpackHigh(block, Vector128<byte>.Zero).AsInt16(), WeightsLow16);
                    weighted = Sse2.Add(weighted, Sse2.Add(high, low));
                }
                var prefixSum = prefix.GetElement(0) + prefix.GetElement(1);
                sum2 += (uint)((ulong)(offset - start) * sum1 + 16 * prefixSum
                    + (uint)(weighted.GetElement(0) + weighted.GetElement(1) + weighted.GetElement(2) + weighted.GetElement(3)));
                sum1 += (uint)(total.GetElement(0) + total.GetElement(1));
            }
            for (; offset < end; offset++) { sum1 += bytes[offset]; sum2 += sum1; }
            sum1 %= 65521; sum2 %= 65521;
        }
    }
}
