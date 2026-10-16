#if IDVB_DEVELOPMENT_BUILD
using System.Diagnostics;
using IDVBuff.Features.Maps;
using IDVBuff.Lifecycle;
using OpenCvSharp;

namespace IDVBuff.Cli;

internal sealed partial class RealCliHost
{
    private CliCommandResult CheckBackgroundCapture(string input, Stopwatch stopwatch)
    {
        if (!BackgroundTestRuntime.Enabled || !BackgroundTestRuntime.TryGetWindow(out var window))
            throw new InvalidOperationException("此验证只允许已绑定 OG 的独立测试桌面。");
        var service = new DwrGameWindowCaptureService();
        try
        {
            if (!service.TryCaptureClient(out var full, out var reason) || full is null)
                throw new InvalidOperationException(reason);
            using (full)
            {
                Cv2.MeanStdDev(full.Image, out Scalar mean, out Scalar deviation);
                var evidence = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "artifacts", "memory", "background-capture-pixels"));
                Directory.CreateDirectory(evidence);
                var originalFrame = Path.Combine(evidence, $"{Environment.ProcessId}-full.png");
                if (!Cv2.ImWrite(originalFrame, full.Image)) throw new IOException("无法保存后台原始捕获帧。");
                if (mean.Val0 + mean.Val1 + mean.Val2 <= 0 || deviation.Val0 + deviation.Val1 + deviation.Val2 <= 0)
                    throw new InvalidOperationException($"后台捕获返回空白或纯色像素，原始帧：{originalFrame}");
                var viewport = new NormalizedRectangle { X = .15, Y = .12, Width = .71, Height = .73 };
                if (!service.TryCaptureViewport(viewport, out var cropped, out reason) || cropped is null)
                    throw new InvalidOperationException(reason);
                using (cropped)
                {
                    var rectangle = cropped.ViewportBounds;
                    using var expected = new Mat(full.Image, new Rect(
                        (int)(rectangle.X - full.ClientBounds.X), (int)(rectangle.Y - full.ClientBounds.Y),
                        (int)rectangle.Width, (int)rectangle.Height));
                    var maxDifference = Cv2.Norm(expected, cropped.Image, NormTypes.INF);
                    if (maxDifference != 0 || full.WindowHandle != window || cropped.WindowHandle != window
                        || full.CaptureBackend != "og-printclient" || cropped.CaptureBackend != "og-printclient")
                        throw new InvalidOperationException("OG 全窗口与专用视口捕获不一致。");
                    var result = new { window = window.ToInt64(), BackgroundTestRuntime.DesktopName,
                        full.ClientBounds, viewport = rectangle, maxDifference, mean, deviation, originalFrame,
                        backend = cropped.CaptureBackend, scope = "Two fresh stable-scene renders; verifies ROI coordinates and ownership, not interactive-desktop pixel equivalence." };
                    return CliCommandResult.Ok(input, "后台全窗口与视口像素逐字节一致。", stopwatch.Elapsed.TotalMilliseconds, result);
                }
            }
        }
        finally { service.Reset(); }
    }
}
#endif
