namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private const string MapViewportSourcePreset = "preset-toml";
    private const string MapViewportSourceManual = "manual-calibration";
    private const string MapViewportSourceLayout = "game-layout";
    private const string MapViewportSourceWholeClient = "whole-client";

    private sealed record MapViewportLogKey(
        string Source, int ClientWidth, int ClientHeight,
        int RegionX, int RegionY, int RegionWidth, int RegionHeight, string LogTarget);

    private MapViewportLogKey? _lastMapViewportLog;

    // The viewport is resolved for every capture; log only when its source,
    // client geometry or crop changes, or when a new output/scan log opens
    // (the first resolution can precede both), so every log shows the rule.
    private void RecordMapViewportResolution(
        MapScreenRect client,
        IntPtr windowHandle,
        NormalizedRectangle region,
        string source)
    {
        var pixels = DwrGameWindowCaptureService.GetViewportBounds(client, region);
        var clientWidth = (int)Math.Round(client.Width);
        var clientHeight = (int)Math.Round(client.Height);
        var regionX = (int)Math.Round(pixels.X - client.X);
        var regionY = (int)Math.Round(pixels.Y - client.Y);
        if (IDVBuff.Diagnostics.OutputLog.CurrentLogPath is not null)
        {
            IDVBuff.Diagnostics.OutputLog.UpdateCaptureContext(
                clientWidth, clientHeight, (int)pixels.Width, (int)pixels.Height, windowHandle,
                regionX, regionY, source);
        }

        var key = new MapViewportLogKey(source, clientWidth, clientHeight,
            regionX, regionY, (int)pixels.Width, (int)pixels.Height,
            $"{IDVBuff.Diagnostics.OutputLog.CurrentLogPath}|{_logCollector.CurrentSessionPath}");
        if (Interlocked.Exchange(ref _lastMapViewportLog, key) == key)
            return;
        _logCollector.Append(
            MapLogCategory.ViewportCapture,
            MapLogLevel.Info,
            $"地图识别区域：{DescribeMapViewportSource(source)} · 客户区 {clientWidth}×{clientHeight}"
                + $" · 区域 x={regionX} y={regionY} {key.RegionWidth}×{key.RegionHeight}",
            details: new()
            {
                ["source"] = source,
                ["clientWidth"] = clientWidth,
                ["clientHeight"] = clientHeight,
                ["regionX"] = regionX,
                ["regionY"] = regionY,
                ["regionWidth"] = key.RegionWidth,
                ["regionHeight"] = key.RegionHeight,
                ["regionNormalized"] = $"X={region.X:F4},Y={region.Y:F4},W={region.Width:F4},H={region.Height:F4}"
            });
    }

    private static string DescribeMapViewportSource(string source) => source switch
    {
        MapViewportSourcePreset => "预设 viewport.toml",
        MapViewportSourceManual => "手动校准",
        MapViewportSourceLayout => "按游戏界面布局自动推算",
        _ => "整个客户区（未取得窗口尺寸）"
    };
}
