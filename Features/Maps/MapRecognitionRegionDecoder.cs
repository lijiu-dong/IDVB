using System;
using System.IO;
using System.Runtime.InteropServices;
using IDVBuff.Core.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

// Straight BGRA, original pixel scale, one independently owned requested region.
// Decoder/cache interfaces close before the returned Mat enters processing.
internal static class MapRecognitionRegionDecoder
{
    internal static bool IsEightBitPng(string path)
    {
        Span<byte> header = stackalloc byte[29];
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        using var file = File.OpenRead(path);
        return file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
            && header[..8].SequenceEqual(signature)
            && header[12] == 'I' && header[13] == 'H' && header[14] == 'D' && header[15] == 'R'
            && header[24] == 8;
    }

    internal static Mat Decode(string path, NormalizedRectangle region)
    {
        using var operation = MemoryJournal.BeginOperation("Recognition.DecodeRegion", path);
        var initialized = CoInitializeEx(0, 0);
        if (initialized < 0 && initialized != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(initialized);
        nint factory = 0, decoder = 0, frame = 0, converter = 0;
        Mat? pixels = null;
        IDisposable? interfaces = null;
        try
        {
            var clsid = new Guid("cacaf262-9370-4615-a13b-9f5539da4c0a");
            var iid = new Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
            Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid, 0, 1, in iid, out factory));
            interfaces = MemoryJournal.Own("Recognition.WicRegion.Interfaces", nativeModule: "WindowsCodecs.dll");
            Marshal.ThrowExceptionForHR(Method<CreateDecoder>(factory, 3)(factory, path, 0, 0x80000000, 0, out decoder));
            Marshal.ThrowExceptionForHR(Method<GetFrame>(decoder, 13)(decoder, 0, out frame));
            Marshal.ThrowExceptionForHR(Method<GetSize>(frame, 3)(frame, out var width, out var height));
            if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
                throw new InvalidDataException("图片尺寸无效。");
            var left = Math.Clamp((int)Math.Floor(region.X * width), 0, (int)width - 1);
            var top = Math.Clamp((int)Math.Floor(region.Y * height), 0, (int)height - 1);
            var right = Math.Clamp((int)Math.Ceiling((region.X + region.Width) * width), left + 1, (int)width);
            var bottom = Math.Clamp((int)Math.Ceiling((region.Y + region.Height) * height), top + 1, (int)height);
            var rectangle = new PixelRect { X = left, Y = top, Width = right - left, Height = bottom - top };
            Marshal.ThrowExceptionForHR(Method<CreateObject>(factory, 10)(factory, out converter));
            var bgra = new Guid("6fddc324-4e03-4bfe-b185-3d77768dc90f");
            Marshal.ThrowExceptionForHR(Method<InitializeConverter>(converter, 8)(converter, frame, in bgra, 0, 0, 0, 0));
            var bytes = checked((long)rectangle.Width * rectangle.Height * 4);
            var request = MemoryJournal.Request("Recognition.DecodedRegion", bytes, "OpenCvSharpExtern.dll");
            pixels = new Mat(rectangle.Height, rectangle.Width, MatType.CV_8UC4);
            MemoryJournal.TrackResource(pixels, "Recognition.DecodedRegion", bytes, "OpenCvSharpExtern.dll", request);
            Marshal.ThrowExceptionForHR(Method<CopyPixels>(converter, 7)(converter, in rectangle,
                checked((uint)pixels.Step()), checked((uint)bytes), pixels.Data));
            MemoryJournal.Current?.Record("recognition-region-decoded", new { originalWidth = width, originalHeight = height, rectangle.X, rectangle.Y,
                rectangle.Width, rectangle.Height, bytes, pixelScale = 1, codec = "WindowsCodecs.dll",
                pixelOwner = "OpenCvSharpExtern.dll", meaning = "Requested region payload; decoder internal cache size unknown" });
            var result = pixels;
            pixels = null;
            return result;
        }
        finally
        {
            if (pixels is not null) { pixels.Dispose(); MemoryJournal.ReleaseResource(pixels); }
            foreach (var pointer in new[] { converter, frame, decoder, factory })
                if (pointer != 0) Marshal.Release(pointer);
            interfaces?.Dispose();
            if (initialized >= 0) CoUninitialize();
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct PixelRect { public int X, Y, Width, Height; }
    private static T Method<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint apartment);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int CreateDecoder(nint self, [MarshalAs(UnmanagedType.LPWStr)] string path, nint vendor, uint access, uint metadata, out nint decoder);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetFrame(nint self, uint index, out nint frame);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetSize(nint self, out uint width, out uint height);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateObject(nint self, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InitializeConverter(nint self, nint source, in Guid format, uint dither, nint palette, double threshold, uint paletteType);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CopyPixels(nint self, in PixelRect rectangle, uint stride, uint capacity, nint pixels);
}
