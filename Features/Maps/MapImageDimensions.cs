using System.Runtime.InteropServices;
using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Features.Maps;

// Dimension queries must not decode the source pixels. Image.FromFile eagerly
// expands PNGs, temporarily owning a full image in addition to WinUI's surface.
// WIC GetSize reads the first frame metadata without CopyPixels/GetThumbnail.
internal static class MapImageDimensions
{
    internal static (int Width, int Height) Read(string path)
    {
        using var operation = MemoryJournal.BeginOperation("Image.Metadata", path);
        var initialized = CoInitializeEx(0, 0); // MTA on workers; keep an existing STA unchanged.
        if (initialized < 0 && initialized != unchecked((int)0x80010106))
            Marshal.ThrowExceptionForHR(initialized);
        nint factory = 0, decoder = 0, frame = 0;
        IDisposable? ownership = null;
        try
        {
            var factoryClass = new Guid("cacaf262-9370-4615-a13b-9f5539da4c0a");
            var factoryInterface = new Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
            Marshal.ThrowExceptionForHR(CoCreateInstance(in factoryClass, 0, 1, in factoryInterface, out factory));
            ownership = MemoryJournal.Own("Image.Metadata/" + path, nativeModule: "WindowsCodecs.dll");
            // Windows SDK wincodec.h: factory CreateDecoderFromFilename slot 3,
            // decoder GetFrame slot 13, frame's inherited GetSize slot 3.
            Marshal.ThrowExceptionForHR(Method<CreateDecoder>(factory, 3)(factory, path, 0,
                0x80000000, 0 /* WICDecodeMetadataCacheOnDemand */, out decoder));
            Marshal.ThrowExceptionForHR(Method<GetFrame>(decoder, 13)(decoder, 0, out frame));
            Marshal.ThrowExceptionForHR(Method<GetSize>(frame, 3)(frame, out var width, out var height));
            if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
                throw new InvalidDataException($"图片尺寸无效：{path}");
            MemoryJournal.Current?.Record("image-metadata", new { path, width, height,
                nativeApiModule = "WindowsCodecs.dll", pixelCopyRequested = false });
            return ((int)width, (int)height);
        }
        finally
        {
            if (frame != 0) Marshal.Release(frame);
            if (decoder != 0) Marshal.Release(decoder);
            if (factory != 0) Marshal.Release(factory);
            ownership?.Dispose();
            if (initialized >= 0) CoUninitialize();
        }
    }

    private static T Method<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint apartment);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int CreateDecoder(nint self, [MarshalAs(UnmanagedType.LPWStr)] string path,
        nint vendor, uint access, uint metadataOptions, out nint decoder);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFrame(nint self, uint index, out nint frame);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSize(nint self, out uint width, out uint height);
}
