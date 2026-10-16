using System;
using System.Runtime.InteropServices;
using IDVBuff.Core.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>Exact clipped floats, sharing bit-identical tiles and omitting tiles uniformly equal to the
/// clipping value. Native storage is private to this process and explicitly
/// freed; no quantization, decompression cache, or alternate distance kernel.</summary>
internal sealed class ClippedFloatPlane : IDisposable
{
    private const int TileSize = 16;
    private readonly Mat _offsets;
    private readonly NativeDistanceMat _payload = new();
    private readonly float _constant;
    public int Width { get; }
    public int Height { get; }
    public bool IsDisposed => _offsets.IsDisposed;
    public long MemoryBytes => _offsets.Total() * sizeof(int) + _payload.Total() * sizeof(float);

    public ClippedFloatPlane(Mat source, float constant)
    {
        if (source.Empty() || source.Type() != MatType.CV_32FC1 || !source.IsContinuous()
            || !float.IsFinite(constant) || constant <= 0)
            throw new ArgumentException("Clipped distance requires continuous float pixels and a positive finite clip.");
        _constant = constant;
        Width = source.Width;
        Height = source.Height;
        var columns = (Width + TileSize - 1) / TileSize;
        var rows = (Height + TileSize - 1) / TileSize;
        _offsets = Allocate(rows, columns, MatType.CV_32SC1, "Structure.SemanticScale.TileOffsets");
        try
        {
            var index = MemoryMarshal.CreateSpan(ref _offsets.At<int>(0, 0), checked(columns * rows));
            var pixels = MemoryMarshal.CreateReadOnlySpan(ref source.At<float>(0, 0), checked(Width * Height));
            var count = 0;

            // Hash directory contains only numbers; it never retains the input.
            // At most 32768 unique blocks, load <= 1/2, native bytes freed here.
            var maximumUnique = Math.Min(columns * rows, 32768);
            var capacity = 1;
            while (capacity < maximumUnique * 2) capacity *= 2;
            using var metadata = new FloatTileDirectory(capacity);
            var directory = MemoryMarshal.CreateSpan(ref metadata.Storage.At<int>(0, 0), capacity * 2);
            count = ClippedFloatTileEncoder.BuildIndex(pixels, index, directory, Width, Height, constant);
            if (count == 0) return;
            var request = MemoryJournal.Request("Structure.SemanticScale.TilePixels",
                (long)count * sizeof(float), "OpenCvSharpExtern.dll");
            _payload.Create(1, count, MatType.CV_32FC1);
            _payload.AccountOwnedPixels();
            MemoryJournal.TrackResource(_payload, "Structure.SemanticScale.TilePixels",
                (long)count * sizeof(float), "OpenCvSharpExtern.dll", request);
            var target = MemoryMarshal.CreateSpan(ref _payload.At<float>(0, 0), count);
            ClippedFloatTileEncoder.CopyPayload(pixels, index, target, Width, Height);
        }
        catch { Dispose(); throw; }
    }

    private sealed class FloatTileDirectory : IDisposable
    {
        public Mat Storage { get; }
        public FloatTileDirectory(int capacity)
        {
            Storage = Allocate(1, checked(capacity * 2), MatType.CV_32SC1,
                "Structure.SemanticScale.TileDedupScratch");
            try { Storage.SetTo(Scalar.All(0)); }
            catch { Release(Storage); throw; }
        }
        public void Dispose() => Release(Storage);
    }
    public Size Size() => new(Width, Height);

    // The requested rectangle remains the original search domain. A cached
    // surrounding rectangle may supply pixels, but never expands the search.
    public Mat Read(Rect region)
        => ReadOwned(region, "Structure.SemanticScale.Region");

    internal Mat ReadOwned(Rect region, string resource)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (region.Width <= 0 || region.Height <= 0 || region.Left < 0 || region.Top < 0
            || region.Right > Width || region.Bottom > Height)
            throw new ArgumentOutOfRangeException(nameof(region));
        var result = Allocate(region.Height, region.Width, MatType.CV_32FC1,
            resource);
        try
        {
            result.SetTo(new Scalar(_constant));
            var columns = (Width + TileSize - 1) / TileSize;
            var rows = (Height + TileSize - 1) / TileSize;
            var index = MemoryMarshal.CreateReadOnlySpan(ref _offsets.At<int>(0, 0), checked(columns * rows));
            var pixels = _payload.Empty() ? ReadOnlySpan<float>.Empty
                : MemoryMarshal.CreateReadOnlySpan(ref _payload.At<float>(0, 0), checked((int)_payload.Total()));
            var target = MemoryMarshal.CreateSpan(ref result.At<float>(0, 0), checked(region.Width * region.Height));
            for (var ty = region.Top / TileSize; ty <= (region.Bottom - 1) / TileSize; ty++)
            for (var tx = region.Left / TileSize; tx <= (region.Right - 1) / TileSize; tx++)
            {
                var offset = index[ty * columns + tx];
                if (offset < 0) continue;
                var x = tx * TileSize;
                var y = ty * TileSize;
                var width = Math.Min(TileSize, Width - x);
                var left = Math.Max(x, region.Left);
                var right = Math.Min(x + width, region.Right);
                var top = Math.Max(y, region.Top);
                var bottom = Math.Min(y + TileSize, region.Bottom);
                for (var row = top; row < bottom; row++)
                    pixels.Slice(offset + (row - y) * width + left - x, right - left)
                        .CopyTo(target.Slice((row - region.Top) * region.Width + left - region.Left, right - left));
            }
            return result;
        }
        catch { Release(result); throw; }
    }

    internal static Mat Allocate(int rows, int columns, MatType type, string resource)
    {
        if (type != MatType.CV_32FC1 && type != MatType.CV_32SC1)
            throw new ArgumentException("Distance storage requires one 32-bit channel.", nameof(type));
        var bytes = checked((long)rows * columns * sizeof(float));
        var request = MemoryJournal.Request(resource, bytes, "OpenCvSharpExtern.dll");
        var mat = new NativeDistanceMat(rows, columns, type);
        MemoryJournal.TrackResource(mat, resource, bytes, "OpenCvSharpExtern.dll", request);
        return mat;
    }

    internal static void Release(Mat mat)
    {
        mat.Dispose();
        MemoryJournal.ReleaseResource(mat);
    }

    public void Dispose()
    {
        Release(_offsets);
        Release(_payload);
    }
}

// These factories own independent native allocations. ROI headers keep the
// ordinary Mat type and borrow their parent's lifetime without another charge.
// OpenCvSharp removes the notification in its normal native disposal path.
internal sealed class NativeDistanceMat : Mat
{
    internal long OwnedPixelBytes => AllocatedMemorySize;
    internal NativeDistanceMat() { }
    internal NativeDistanceMat(int rows, int columns, MatType type) : base(rows, columns, type)
        => AccountOwnedPixels();
    internal NativeDistanceMat(Size size, MatType type, Scalar value) : base(size, type, value)
        => AccountOwnedPixels();
    internal void AccountOwnedPixels() => NotifyMemoryPressure(checked(Total() * ElemSize()));
}


