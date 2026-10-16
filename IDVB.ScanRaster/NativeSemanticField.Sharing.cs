using IDVBuff.Core.Collections;

namespace IDVBuff.Core.Imaging;

public sealed unsafe partial class NativeSemanticField
{
    private readonly object _identity;
    private readonly NativeSemanticField? _basis, _patch;
    private readonly int _left, _top;

    private NativeSemanticField(NativeSemanticField basis, NativeSemanticField patch, int left, int top, object? identity)
    {
        _basis=basis;_patch=patch;_left=left;_top=top;_identity=identity ?? new object();
        Width=basis.Width;Height=basis.Height;MemoryBytes=checked(basis.MemoryBytes+patch.MemoryBytes);
    }
    public bool HasSameConstantPixelsAs(byte value,int width,int height) =>
        Width==width && Height==height && _constant && _value==value;
    public long SharedBytesFrom(NativeSemanticField other) =>
        ReferenceEquals(_identity,other._identity) || (_basis is not null && ReferenceEquals(_basis._identity,other._identity))
            ? other.MemoryBytes : 0;

    public static NativeSemanticField Create(ReadOnlySpan<byte> pixels,int width,int height,string resource,
        NativeSemanticField? reuse=null)
    {
        if(width<=0 || height<=0 || pixels.Length!=checked(width*height))
            throw new ArgumentException("Invalid semantic raster dimensions.");
        if(reuse is null || reuse.Width!=width || reuse.Height!=height)
            return CreateUnshared(pixels,width,height,resource);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref reuse._disposed)!=0,reuse);
        if(pixels.IndexOfAnyExcept(pixels[0])<0)
            return reuse.HasSameConstantPixelsAs(pixels[0],width,height) ? reuse.Clone() : new(width,height,pixels[0]);
        int left=width,top=height,right=-1,bottom=-1;
        for(var y=0;y<height;y++)for(var x=0;x<width;x++)
        {
            if(pixels[y*width+x]==reuse.AtCompact(x,y))continue;
            left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);
            if((long)(right-left+1)*(bottom-top+1)>pixels.Length/4)
                return CreateUnshared(pixels,width,height,resource);
        }
        if(right<0)return reuse.Clone();
        if(reuse._basis is not null)return CreateUnshared(pixels,width,height,resource);
        var patch=CreateWindow(pixels,width,height,left,top,right-left+1,bottom-top+1,resource+".Patch");
        var transferred=false;
        try
        {
            if(patch.MemoryBytes>=reuse.MemoryBytes)return CreateUnshared(pixels,width,height,resource);
            var basis=reuse.Clone();
            try
            {
                var result=new NativeSemanticField(basis,patch,left,top,null);
                transferred=true;
                return result;
            }
            catch { basis.Dispose(); throw; }
        }
        finally { if(!transferred)patch.Dispose(); }
    }
    private static NativeSemanticField CreateWindow(ReadOnlySpan<byte> pixels,int sourceWidth,int sourceHeight,
        int left,int top,int width,int height,string resource)
    {
        if(sourceWidth<=0 || sourceHeight<=0 || pixels.Length!=checked(sourceWidth*sourceHeight)
            || left<0 || top<0 || width<=0 || height<=0 || left>sourceWidth-width || top>sourceHeight-height)
            throw new ArgumentException("Invalid semantic raster window.");
        var value=pixels[top*sourceWidth+left];var constant=true;
        for(var y=0;y<height && constant;y++)
            constant=pixels.Slice((top+y)*sourceWidth+left,width).IndexOfAnyExcept(value)<0;
        if(constant)return new(width,height,value);
        using var owner=NativeEncodedByteField.EncodeWindow(pixels,sourceWidth,sourceHeight,left,top,width,height,resource);
        var reader=owner.TryRent()!;
        try { return new(reader); }
        catch { reader.Dispose(); throw; }
    }
    private NativeSemanticField ClonePatch()
    {
        var basis=_basis!.Clone();NativeSemanticField? patch=null;
        try { patch=_patch!.Clone();return new(basis,patch,_left,_top,_identity); }
        catch { basis.Dispose();patch?.Dispose();throw; }
    }
    private uint ReadSharedQuad(int x,int y)
    {
        var patch=_patch!;
        if(x>=_left && y>=_top && x+1<_left+patch.Width && y+1<_top+patch.Height)
            return patch.ReadQuad(x-_left,y-_top);
        if(x+1<_left || y+1<_top || x>=_left+patch.Width || y>=_top+patch.Height)
            return _basis!.ReadQuad(x,y);
        return (uint)(AtCompact(x,y) | AtCompact(x+1,y)<<8 | AtCompact(x,y+1)<<16 | AtCompact(x+1,y+1)<<24);
    }

}
