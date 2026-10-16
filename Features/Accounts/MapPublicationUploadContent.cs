using System.Net;
using IDVBuff.Features.Maps;

namespace IDVBuff.Features.Accounts;

internal sealed class MapPublicationUploadContent(Stream source) : HttpContent
{
    private readonly Action<double, string> _report = MapOperationProgress.Capture();

    protected override bool TryComputeLength(out long length) { length = source.Length; return true; }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
    {
        var buffer = new byte[81920];
        long sent = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token)) != 0)
        {
            await stream.WriteAsync(buffer.AsMemory(0, count), token);
            sent += count;
            _report(.95 * sent / Math.Max(1d, source.Length), "正在上传地图……");
        }
        _report(.95, "正在等待发布结果……");
    }
}
