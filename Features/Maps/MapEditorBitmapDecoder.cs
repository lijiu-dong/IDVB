using System.Runtime.InteropServices;
using IDVBuff.Core.Diagnostics;
using Windows.Graphics.Imaging;

namespace IDVBuff.Features.Maps;

// Decode directly into one closeable pixel owner. Every WIC interface is
// released before returning; no managed full-image copy or URI decode cache.
internal static class MapEditorBitmapDecoder
{
    internal static SoftwareBitmap Decode(string path, int maximumWidth, CancellationToken cancellation)
        => DecodeCore(path, null, maximumWidth, cancellation, "Editor.DecodedPixels");

    internal static SoftwareBitmap DecodeVerifiedBytes(byte[] bytes, int maximumWidth,
        CancellationToken cancellation, string resource)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0) throw new InvalidDataException("图片数据为空。");
        return DecodeCore(null, bytes, maximumWidth, cancellation, resource);
    }

    private static SoftwareBitmap DecodeCore(string? path, byte[]? bytes, int maximumWidth,
        CancellationToken cancellation, string resource)
    {
        if (maximumWidth <= 0) throw new ArgumentOutOfRangeException(nameof(maximumWidth));
        cancellation.ThrowIfCancellationRequested();
        using var operation = MemoryJournal.BeginOperation("Editor.Decode", path);
        var initialized = CoInitializeEx(0, 0);
        if (initialized < 0 && initialized != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(initialized);
        nint factory = 0, decoder = 0, frame = 0, scaler = 0, converter = 0, stream = 0;
        GCHandle pinned = default;
        SoftwareBitmap? pixels = null;
        try
        {
            var clsid = new Guid("cacaf262-9370-4615-a13b-9f5539da4c0a");
            var iid = new Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
            Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid, 0, 1, in iid, out factory));
            if (bytes is null)
                Marshal.ThrowExceptionForHR(Method<CreateDecoder>(factory, 3)(factory, path!, 0, 0x80000000, 0, out decoder));
            else
            {
                pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                Marshal.ThrowExceptionForHR(Method<CreateObject>(factory, 14)(factory, out stream));
                Marshal.ThrowExceptionForHR(Method<InitializeMemoryStream>(stream, 16)(stream,
                    pinned.AddrOfPinnedObject(), checked((uint)bytes.Length)));
                Marshal.ThrowExceptionForHR(Method<CreateStreamDecoder>(factory, 4)(factory, stream, 0, 0, out decoder));
            }
            Marshal.ThrowExceptionForHR(Method<GetFrame>(decoder, 13)(decoder, 0, out frame));
            Marshal.ThrowExceptionForHR(Method<GetSize>(frame, 3)(frame, out var originalWidth, out var originalHeight));
            if (originalWidth == 0 || originalHeight == 0 || originalWidth > int.MaxValue || originalHeight > int.MaxValue)
                throw new InvalidDataException("图片尺寸无效。");
            var width = Math.Min((int)originalWidth, maximumWidth);
            var height = Math.Max(1, checked((int)Math.Round((double)originalHeight * width / originalWidth,
                MidpointRounding.AwayFromZero)));
            var input = frame;
            if (width != originalWidth || height != originalHeight)
            {
                Marshal.ThrowExceptionForHR(Method<CreateObject>(factory, 11)(factory, out scaler));
                // The editor's PNG URI path and the verified payment stream
                // path are checked against their actual WinUI raster output.
                var interpolation = bytes is not null || width >= originalWidth / 2 && height >= originalHeight / 2 ? 1u : 3u;
                MemoryJournal.Current?.Record("image-scaling-request", new { resource, originalWidth, originalHeight,
                    width, height, interpolation, verifiedMemoryInput = bytes is not null });
                Marshal.ThrowExceptionForHR(Method<InitializeScaler>(scaler, 8)(scaler, frame, (uint)width, (uint)height, interpolation));
                input = scaler;
            }
            Marshal.ThrowExceptionForHR(Method<CreateObject>(factory, 10)(factory, out converter));
            var pbgra = new Guid("6fddc324-4e03-4bfe-b185-3d77768dc910");
            Marshal.ThrowExceptionForHR(Method<InitializeConverter>(converter, 8)(converter, input, in pbgra, 0, 0, 0, 0));
            cancellation.ThrowIfCancellationRequested();
            var pixelBytes = checked((long)width * height * 4);
            var request = MemoryJournal.Request(resource, pixelBytes, "WindowsCodecs.dll");
            pixels = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
            MemoryJournal.TrackResource(pixels, resource, pixelBytes, "WindowsCodecs.dll", request);
            using (var buffer = pixels.LockBuffer(BitmapBufferAccessMode.Write))
            using (var reference = buffer.CreateReference())
            {
                var inspectable = WinRT.MarshalInspectable<Windows.Foundation.IMemoryBufferReference>.FromManaged(reference);
                nint access = 0;
                try
                {
                    var accessId = new Guid("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d");
                    Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in accessId, out access));
                    Marshal.ThrowExceptionForHR(Method<GetBuffer>(access, 3)(access, out var pointer, out var capacity));
                    var plane = buffer.GetPlaneDescription(0);
                    var required = checked((long)plane.StartIndex + (long)plane.Stride * (height - 1) + width * 4L);
                    if (plane.Stride < width * 4 || required > capacity) throw new InvalidDataException("图片缓冲尺寸无效。");
                    Marshal.ThrowExceptionForHR(Method<CopyPixels>(converter, 7)(converter, 0, checked((uint)plane.Stride),
                        checked(capacity - (uint)plane.StartIndex), pointer + plane.StartIndex));
                }
                finally
                {
                    if (access != 0) Marshal.Release(access);
                    WinRT.MarshalInspectable<Windows.Foundation.IMemoryBufferReference>.DisposeAbi(inspectable);
                }
            }
            cancellation.ThrowIfCancellationRequested();
            var result = pixels;
            pixels = null;
            return result;
        }
        finally
        {
            if (pixels is not null) Release(pixels);
            foreach (var pointer in new[] { converter, scaler, frame, decoder, stream, factory })
                if (pointer != 0) Marshal.Release(pointer);
            if (pinned.IsAllocated) pinned.Free();
            if (initialized >= 0) CoUninitialize();
        }
    }

    internal static void Release(SoftwareBitmap bitmap)
    {
        bitmap.Dispose();
        MemoryJournal.ReleaseResource(bitmap);
    }

    private static T Method<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint apartment);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int CreateDecoder(nint self, [MarshalAs(UnmanagedType.LPWStr)] string path, nint vendor, uint access, uint metadata, out nint decoder);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateStreamDecoder(nint self, nint stream, nint vendor, uint metadata, out nint decoder);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InitializeMemoryStream(nint self, nint bytes, uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateObject(nint self, out nint instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetFrame(nint self, uint index, out nint frame);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetSize(nint self, out uint width, out uint height);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InitializeScaler(nint self, nint input, uint width, uint height, uint interpolation);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InitializeConverter(nint self, nint input, in Guid format, uint dither, nint palette, double alpha, uint paletteType);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CopyPixels(nint self, nint rectangle, uint stride, uint capacity, nint pixels);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBuffer(nint self, out nint pixels, out uint capacity);
}
