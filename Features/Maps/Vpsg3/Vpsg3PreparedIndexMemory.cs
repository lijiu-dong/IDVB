namespace IDVBuff.Features.Maps;

public sealed partial class Vpsg3PreparedFloor
{
    private readonly bool _deriveGeometryK3;
    private readonly bool _deriveGeometryK5;
    private IDisposable? _derivedGeometryK3Ownership;
    private IDisposable? _derivedGeometryK5Ownership;
    private long DerivedGeometryK3Bytes => _deriveGeometryK3 && _dilatedBitsetK3 is { Length: > 0 } words
        ? words.LongLength * sizeof(ulong) + 24 : 0;
    private long DerivedGeometryK5Bytes => _deriveGeometryK5 && _dilatedBitsetK5 is { Length: > 0 } words
        ? words.LongLength * sizeof(ulong) + 24 : 0;

    // A second 3x3 dilation of the exact geometry K3 is exactly K5. Operate
    // on words, preserving neighboring-row and neighboring-word carries;
    // readers still borrow the same continuous native-solver input format.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private ulong[]? GetGeometryK5()
    {
        var current = _dilatedBitsetK5;
        if (!_deriveGeometryK5 || current is null || current.Length > 0) return current;
        return BuildGeometryK5();
    }

    private ulong[]? BuildGeometryK5()
    {
        lock (_precisionGate)
        {
            if (IsDisposed) return null;
            if (_dilatedBitsetK5 is { Length: > 0 } ready) return ready;
            var k3 = GetGeometryK3() ?? throw new InvalidOperationException("本楼层缺少 K3 几何位图。");
            var stride = WordsPerRow;
            var height = ReferenceHeight;
            var count = checked(stride * height);
            if (k3.Length != count) throw new InvalidOperationException("本楼层 K3 位图尺寸不一致。");
            var request = IDVBuff.Core.Diagnostics.MemoryJournal.Request($"VPSG3.DerivedK5/{CacheKey}", (long)count * sizeof(ulong));
            var words = new ulong[count];
            var padding = ReferenceWidth & 63;
            var lastMask = padding == 0 ? ulong.MaxValue : (1UL << padding) - 1;
            for (var y = 0; y < height; y++)
            {
                var row = y * stride;
                // Reusing the current row at a clipped border is an exact
                // idempotent OR, avoiding a branch for every source word.
                var top = y > 0 ? row - stride : row;
                var bottom = y + 1 < height ? row + stride : row;
                var previous = 0UL;
                var center = k3[row] | k3[top] | k3[bottom];
                for (var column = 0; column < stride; column++)
                {
                    var next = column + 1 < stride
                        ? k3[row + column + 1] | k3[top + column + 1] | k3[bottom + column + 1]
                        : 0UL;
                    words[row + column] = center | (center << 1) | (center >> 1)
                        | (previous >> 63) | (next << 63);
                    previous = center; center = next;
                }
                words[row + stride - 1] &= lastMask;
            }
            IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedArray(words, "VPSG3.DerivedK5", request);
            _derivedGeometryK5Ownership = IDVBuff.Core.Diagnostics.MemoryJournal.Own($"VPSG3.DerivedK5/{CacheKey}",
                words.LongLength * sizeof(ulong), requestId: request);
            _dilatedBitsetK5 = words;
            return words;
        }
    }

    // RGB geometry is the union of the two semantic classes. Their immutable
    // Chebyshev <=1 bitsets are exactly the original 3x3 dilation, including
    // clipped image borders and zero padding in the final word of each row.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private ulong[]? GetGeometryK3()
    {
        var current = _dilatedBitsetK3;
        if (!_deriveGeometryK3 || current is null || current.Length > 0) return current;
        return BuildGeometryK3();
    }

    private ulong[]? BuildGeometryK3()
    {
        lock (_precisionGate)
        {
            if (IsDisposed) return null;
            if (_dilatedBitsetK3 is { Length: > 0 } ready) return ready;
            var semantic = SemanticIndex ?? throw new InvalidOperationException("RGB 楼层缺少已准备的语义索引。");
            var count = checked(WordsPerRow * ReferenceHeight);
            var warm = semantic.BitsK3(SemanticStructureContours.Warm);
            var cool = semantic.BitsK3(SemanticStructureContours.Cool);
            if (warm.Length != count || cool.Length != count)
                throw new InvalidOperationException("语义位图尺寸与本楼层几何位图不一致。");
            var request = IDVBuff.Core.Diagnostics.MemoryJournal.Request($"VPSG3.DerivedK3/{CacheKey}", (long)count * sizeof(ulong));
            var words = new ulong[count];
            for (var i = 0; i < count; i++) words[i] = warm[i] | cool[i];
            IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedArray(words, "VPSG3.DerivedK3", request);
            _derivedGeometryK3Ownership = IDVBuff.Core.Diagnostics.MemoryJournal.Own($"VPSG3.DerivedK3/{CacheKey}",
                words.LongLength * sizeof(ulong), requestId: request);
            _dilatedBitsetK3 = words;
            return words;
        }
    }

    internal void ReleaseDerivedGeometryK3IfIdle()
    {
        if ((!_deriveGeometryK3 && !_deriveGeometryK5) || System.Threading.Volatile.Read(ref _refCount) > 1) return;
        lock (_precisionGate)
        {
            if (IsDisposed || System.Threading.Volatile.Read(ref _refCount) > 1) return;
            if (_deriveGeometryK3)
            {
                _dilatedBitsetK3 = [];
                _derivedGeometryK3Ownership?.Dispose();
                _derivedGeometryK3Ownership = null;
            }
            if (_deriveGeometryK5)
            {
                _dilatedBitsetK5 = [];
                _derivedGeometryK5Ownership?.Dispose();
                _derivedGeometryK5Ownership = null;
            }
        }
    }

    // One inverse threshold buffer replaces threshold + inversion. Preserve
    // OpenCV's native output allocation: writing into a differently aligned managed
    // array changed the last float bit on the original-input equivalence check.
    internal static float[] CreatePrecisionDistance(OpenCvSharp.Mat edgeImage)
    {
        using var operation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("VPSG3.Precision.Build");
        using var inverse = new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.Threshold(edgeImage, inverse, 128, 255, OpenCvSharp.ThresholdTypes.BinaryInv);
        using var output = new OpenCvSharp.Mat();
        var request = IDVBuff.Core.Diagnostics.MemoryJournal.Request("VPSG3.Precision.NativeOutput",
            checked(edgeImage.Total() * sizeof(float)), "OpenCvSharpExtern.dll");
        OpenCvSharp.Cv2.DistanceTransform(inverse, output, OpenCvSharp.DistanceTypes.L2,
            OpenCvSharp.DistanceTransformMasks.Precise);
        using var ownership = IDVBuff.Core.Diagnostics.MemoryJournal.Own("VPSG3.Precision.NativeOutput",
            checked(output.Total() * sizeof(float)), "OpenCvSharpExtern.dll", request);
        var buffer = new float[checked(edgeImage.Width * edgeImage.Height)];
        System.Runtime.InteropServices.Marshal.Copy(output.Data, buffer, 0, buffer.Length);
        return buffer;
    }

    private static PrecisionDistanceBuffer CreateNativePrecisionDistance(OpenCvSharp.Mat edges, string resource)
    {
        using var operation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("VPSG3.Precision.Build");
        using var inverse = new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.Threshold(edges, inverse, 128, 255, OpenCvSharp.ThresholdTypes.BinaryInv);
        var output = new NativeDistanceMat();
        try
        {
            var request = IDVBuff.Core.Diagnostics.MemoryJournal.Request(resource,
                checked(edges.Total() * sizeof(float)), "OpenCvSharpExtern.dll");
            OpenCvSharp.Cv2.DistanceTransform(inverse, output, OpenCvSharp.DistanceTypes.L2,
                OpenCvSharp.DistanceTransformMasks.Precise);
            output.AccountOwnedPixels();
            return new PrecisionDistanceBuffer(output, resource, request);
        }
        catch { output.Dispose(); throw; }
    }

    // Readers retain the exact native distance-transform allocation. Retirement
    // detaches cache ownership immediately; the last reader releases its pixels.
    // Legacy callers supplying float[] keep the same managed storage contract.
    internal sealed class PrecisionDistanceBuffer : IDisposable
    {
        private readonly OpenCvSharp.Mat? _native;
        private readonly float[]? _managed;
        private readonly IDisposable? _ownership;
        private int _references = 1;
        internal long MemoryBytes { get; }

        internal PrecisionDistanceBuffer(float[] values, string resource)
        {
            _managed = values;
            MemoryBytes = values.LongLength * sizeof(float) + 24L;
            _ownership = IDVBuff.Core.Diagnostics.MemoryJournal.Own(resource, values.LongLength * sizeof(float));
        }

        internal PrecisionDistanceBuffer(OpenCvSharp.Mat values, string resource, long request)
        {
            if (!values.IsContinuous() || values.Type() != OpenCvSharp.MatType.CV_32FC1)
                throw new InvalidOperationException("Precision distance output must be continuous float pixels.");
            _native = values;
            MemoryBytes = checked(values.Total() * sizeof(float));
            _ownership = IDVBuff.Core.Diagnostics.MemoryJournal.Own(resource, MemoryBytes,
                "OpenCvSharpExtern.dll", request);
        }

        internal ReadOnlySpan<float> Span => _managed is not null ? _managed :
            System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(
                ref _native!.At<float>(0, 0), checked((int)_native.Total()));

        internal PrecisionDistanceLease Rent()
        {
            // The floor gate prevents renting a retired buffer. A reader can
            // release concurrently; the attached cache root still keeps it live.
            var lease = new PrecisionDistanceLease(this);
            Interlocked.Increment(ref _references);
            return lease;
        }

        public void Dispose()
        {
            if (Interlocked.Decrement(ref _references) != 0) return;
            try { _native?.Dispose(); }
            finally { _ownership?.Dispose(); }
        }
    }

    internal sealed class PrecisionDistanceLease : IDisposable
    {
        private PrecisionDistanceBuffer? _buffer;
        internal PrecisionDistanceLease(PrecisionDistanceBuffer buffer) => _buffer = buffer;
        internal ReadOnlySpan<float> Span => _buffer is { } buffer ? buffer.Span :
            throw new ObjectDisposedException(nameof(PrecisionDistanceLease));
        public void Dispose() => Interlocked.Exchange(ref _buffer, null)?.Dispose();
    }

    private readonly IDisposable? _baseOwnership;
    private SemanticStructureIndex? _semanticIndex;
    private IDisposable? _semanticOwnership;

    public SemanticStructureIndex? SemanticIndex
    {
        get => _semanticIndex;
        init
        {
            _semanticIndex = value;
            if (value is not null)
                _semanticOwnership = IDVBuff.Core.Diagnostics.MemoryJournal.Own($"VPSG3.Semantic/{CacheKey}", value.MemoryBytes);
        }
    }
}
