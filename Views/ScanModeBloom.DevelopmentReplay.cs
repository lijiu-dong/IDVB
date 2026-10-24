#if IDVB_DEVELOPMENT_BUILD
using System.Numerics;
using System.Security.Cryptography;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using IDVBuff.Core.Diagnostics;

namespace IDVBuff.Views;

internal sealed partial class ScanModeBloom
{
    internal readonly record struct DevelopmentBloomLifetime(int draws, int textures, int tintEffects,
        bool textureAlive, bool tintAlive);
    internal DevelopmentBloomLifetime ReadDevelopmentLifetime() =>
        new(_drawCount, _textureCount, _tintCount, _lightTexture is not null, _tintEffect is not null);
    internal CanvasRenderTarget? ReadDevelopmentTexture() => _lightTexture;

    internal async Task<object> ReplayDevelopmentPixelsAsync(CancellationToken cancellation)
    {
        await WaitDevelopmentDrawAsync(cancellation);
        var original = AccentColor;
        var reused = _tintEffect ?? throw new InvalidOperationException("首页光效效果尚未创建。");
        var texture = _lightTexture ?? throw new InvalidOperationException("首页光效纹理尚未创建。");
        using var neutral = new DevelopmentPixelTarget(_canvas!, "UI.Home.BloomReplay.FreshNeutral",
            (float)ActualWidth, (float)ActualHeight);
        using (var drawing = neutral.Target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            // Production geometry uses the DIP layout size, which can be
            // fractional after a real resize. Bitmap Size can round to pixels.
            DrawLightTexture(_canvas!, drawing, (float)ActualWidth, (float)ActualHeight);
        }
        using var expected = new DevelopmentPixelTarget(_canvas!, "UI.Home.BloomReplay.ReferenceTarget");
        using var actual = new DevelopmentPixelTarget(_canvas!, "UI.Home.BloomReplay.CurrentTarget");
        var checks = new List<object>();
        try
        {
            foreach (var color in new[] { Microsoft.UI.Colors.Red, Microsoft.UI.Colors.Blue,
                Microsoft.UI.Colors.Purple, Microsoft.UI.Colors.White, Microsoft.UI.Colors.Black })
            {
                AccentColor = color;
                await WaitDevelopmentDrawAsync(cancellation);
                if (!ReferenceEquals(reused, _tintEffect) || !ReferenceEquals(texture, _lightTexture))
                    throw new InvalidOperationException("同尺寸颜色刷新重新创建了光效资源。");
                // Render the former per-draw graph against the exact production
                // texture, and the actual cached graph after its real Draw callback.
                using var former = new ColorMatrixEffect { Source = neutral.Target,
                    ColorMatrix = new Matrix5x4 { M11 = color.R / 255f, M12 = color.G / 255f,
                        M13 = color.B / 255f, M21 = 1 - color.R / 255f, M22 = 1 - color.G / 255f,
                        M23 = 1 - color.B / 255f, M44 = 1 } };
                foreach (var output in new[] { expected, actual })
                {
                    using var drawing = output.Target.CreateDrawingSession();
                    drawing.Clear(Color.FromArgb(0, 0, 0, 0));
                    drawing.Transform = Matrix3x2.CreateScale(256f / (float)texture.Size.Width, 160f / (float)texture.Size.Height);
                    drawing.DrawImage(ReferenceEquals(output, expected) ? former : reused);
                }
                var oldRequest = MemoryJournal.Request("UI.Home.BloomReplay.ReferencePixels", nativeModule: "Microsoft.Graphics.Canvas.dll");
                var oldPixels = expected.Target.GetPixelBytes();
                MemoryJournal.ObserveManagedArray(oldPixels, "UI.Home.BloomReplay.ReferencePixels", oldRequest);
                var newRequest = MemoryJournal.Request("UI.Home.BloomReplay.CurrentPixels", nativeModule: "Microsoft.Graphics.Canvas.dll");
                var newPixels = actual.Target.GetPixelBytes();
                MemoryJournal.ObserveManagedArray(newPixels, "UI.Home.BloomReplay.CurrentPixels", newRequest);
                if (!oldPixels.AsSpan().SequenceEqual(newPixels) || !oldPixels.Any(value => value != 0))
                    throw new InvalidOperationException("真实光效像素与旧渲染路径不一致或为空。");
                checks.Add(new { color, bytes = oldPixels.Length, sha256 = Convert.ToHexString(SHA256.HashData(newPixels)) });
            }
            return new { checks, lifetime = ReadDevelopmentLifetime(), identical = true };
        }
        finally { AccentColor = original; }
    }

    private sealed class DevelopmentPixelTarget : IDisposable
    {
        internal CanvasRenderTarget Target { get; }
        internal DevelopmentPixelTarget(Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl canvas, string resource,
            float width = 256, float height = 160)
        {
            var request = MemoryJournal.Request(resource, nativeModule: "Microsoft.Graphics.Canvas.dll");
            Target = new(canvas, width, height, canvas.Dpi);
            try { MemoryJournal.TrackResource(Target, resource, null, "Microsoft.Graphics.Canvas.dll", request); }
            catch { Target.Dispose(); throw; }
        }
        public void Dispose()
        {
            Target.Dispose();
            MemoryJournal.ReleaseResource(Target);
        }
    }

    private async Task WaitDevelopmentDrawAsync(CancellationToken cancellation)
    {
        var before = _drawCount;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Rendered(object? sender, object args) { if (_drawCount > before) completion.TrySetResult(); }
        CompositionTarget.Rendering += Rendered;
        try
        {
            _canvas?.Invalidate();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation);
        }
        finally { CompositionTarget.Rendering -= Rendered; }
    }
}
#endif
