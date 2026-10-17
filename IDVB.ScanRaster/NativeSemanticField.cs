using System.Runtime.CompilerServices;
using IDVBuff.Core.Collections;

namespace IDVBuff.Core.Imaging;

/// <summary>Isolated native semantic pixel reader. One scope owns one lease;
/// finish reads before disposing that scope. Other cloned scopes may retire.
/// Storage is validated and owned by Core; decoding runs beside the existing
/// semantic raster kernel with the same project optimization settings.</summary>
public sealed unsafe partial class NativeSemanticField : IDisposable
{
    private NativeEncodedByteField.ReadLease? _reader;
    private readonly NativeEncodedByteField.PixelKernel _pixels;
    private readonly bool _constant;
    private readonly byte _value;
    private int _disposed;
    public int Width { get; }
    public int Height { get; }
    public long MemoryBytes { get; }

    private NativeSemanticField(int width, int height, byte value, object? identity = null)
    { Width=width; Height=height; _value=value; _constant=true; MemoryBytes=1; _identity=identity ?? new object(); }
    private NativeSemanticField(NativeEncodedByteField.ReadLease reader, object? identity = null)
    {
        _pixels=reader.BorrowPixelKernel(); Width=_pixels._width; Height=_pixels._height;
        MemoryBytes=reader.PayloadBytes; _reader=reader; _identity=identity ?? new object();
    }
    public static NativeSemanticField Filled(int width, int height, byte value)
    {
        if(width<=0 || height<=0)throw new ArgumentException("Invalid semantic raster dimensions.");
        _=checked(width*height);
        return new(width,height,value);
    }
    private static NativeSemanticField CreateUnshared(ReadOnlySpan<byte> pixels, int width, int height, string resource)
    {
        if(width<=0 || height<=0 || pixels.Length!=checked(width*height))
            throw new ArgumentException("Invalid semantic raster dimensions.");
        // A missing class is one immediate value, with no native allocation.
        if(pixels.IndexOfAnyExcept(pixels[0])<0) return new(width,height,pixels[0]);
        using var owner=NativeEncodedByteField.Encode(pixels,width,height,resource,preferCompactDirectory:true);
        var reader=owner.TryRent()!;
        try { return new(reader); }
        catch { reader.Dispose(); throw; }
    }
    public NativeSemanticField Clone()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed)!=0,this);
        if(_basis is not null)return ClonePatch();
        if(_constant)return new(Width,Height,_value,_identity);
        var reader=(Volatile.Read(ref _reader) ?? throw new ObjectDisposedException(nameof(NativeSemanticField))).Clone();
        try { return new(reader,_identity); }
        catch { reader.Dispose(); throw; }
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        try { Interlocked.Exchange(ref _reader,null)?.Dispose(); }
        finally { try { _basis?.Dispose(); } finally { _patch?.Dispose(); } }
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    public byte AtCompact(int x,int y)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed)!=0,this);
        if((uint)x>=Width || (uint)y>=Height)throw new ArgumentOutOfRangeException();
        if(_basis is not null)
            return (uint)(x-_left)<_patch!.Width && (uint)(y-_top)<_patch.Height
                ? _patch.AtCompact(x-_left,y-_top) : _basis.AtCompact(x,y);
        if(_constant)return _value;
        var reader=_reader ?? throw new ObjectDisposedException(nameof(NativeSemanticField));
        var value=Read(in _pixels,x,y);
        GC.KeepAlive(reader);
        return value;
    }
    public uint ReadQuad(int x,int y)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed)!=0,this);
        if((uint)x>=Width-1 || (uint)y>=Height-1)throw new ArgumentOutOfRangeException();
        if(_basis is not null)return ReadSharedQuad(x,y);
        if(_constant)return _value*0x01010101u;
        return (_reader ?? throw new ObjectDisposedException(nameof(NativeSemanticField))).ReadQuad(x,y);
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    private static byte Read(in NativeEncodedByteField.PixelKernel storage,int x,int y)
    {
        var data=storage._data;
        if(storage._constant)return data[0];
        if(!storage._hasTiles && !storage._hasCompact)return data[y*storage._width+x];
        var index=(y>>3)*storage._columns+(x>>3);
        int offset,mode;
        if(storage._hasCompact)
        {
            var descriptor=Unsafe.ReadUnaligned<uint>(storage._compact+index*3)&0xffffff;
            offset=(int)(descriptor&0xfffff);mode=(int)(descriptor>>20);
            if(mode==9)return (byte)offset;
            if(mode==10)return (byte)((byte)offset+((offset<<18)>>26)*(x&7)+((offset<<12)>>26)*(y&7));
        }
        else
        {
            var descriptor=storage._tiles[index];offset=(int)(descriptor&0xfffffff);mode=(int)(descriptor>>28);
            if(mode==9)return data[offset];
        }
        var pixel=(y&7)*8+(x&7);
        if(mode==8)return data[offset+pixel];
        var minimum=(short)(data[offset+3]|data[offset+4]<<8);var residual=0;
        if(mode>0)
        {
            var bit=pixel*mode;var at=offset+5+(bit>>3);var packed=(int)data[at];
            if((bit&7)+mode>8)packed|=data[at+1]<<8;
            residual=(packed>>(bit&7))&((1<<mode)-1);
        }
        return (byte)(data[offset]+(sbyte)data[offset+1]*(x&7)+(sbyte)data[offset+2]*(y&7)+minimum+residual);
    }
}
