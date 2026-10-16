using System.Numerics;
using IDVBuff.Core.Diagnostics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace IDVBuff.Views;

/// <summary>
/// A layered, diffused light volume emitted from the upper-right command area.
/// The blurred shells and independently shaped rays deliberately avoid the look
/// of a single translucent polygon.
/// </summary>
internal sealed partial class ScanModeBloom : UserControl
{
    private CanvasControl? _canvas;
    private CanvasRenderTarget? _lightTexture;
    private ScanModeBloomTextureCache? _textureCache;
    private ScanModeBloomTextureCache.Lease? _textureLease;
    private ColorMatrixEffect? _tintEffect;
    private int _drawCount, _textureCount, _tintCount;
    private bool _retired;
    private Color _accentColor = ScanModeSelector.GetAccentColor(
        Features.Maps.ScanPerformanceMode.Balanced);

    public ScanModeBloom()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => AttachCanvas();
        SizeChanged += (_, _) => { ReleaseTexture(); _canvas?.Invalidate(); };
        Unloaded += (_, _) => DetachCanvas();
    }

    private void AttachCanvas()
    {
        if (_retired || _canvas is not null) return;
        var request = MemoryJournal.Request("UI.Home.BloomCanvas", nativeModule: "Microsoft.Graphics.Canvas.dll");
        var canvas = new CanvasControl { IsHitTestVisible = false };
        canvas.Draw += DrawBloom;
        canvas.CreateResources += CanvasCreateResources;
        _canvas = canvas;
        Content = canvas;
        MemoryJournal.TrackResource(canvas, "UI.Home.BloomCanvas", null, "Microsoft.Graphics.Canvas.dll", request);
        MemoryJournal.ObserveManagedObject(canvas, "UI.Home.BloomCanvas");
    }

    private void CanvasCreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        ReleaseTexture();
        if (args.Reason == Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesReason.NewDevice)
            _textureCache?.Invalidate();
    }

    private void DetachCanvas()
    {
        ReleaseTexture();
        if (_canvas is not { } canvas) return;
        canvas.Draw -= DrawBloom;
        canvas.CreateResources -= CanvasCreateResources;
        // Win2D controls retain native/managed event cycles unless explicitly
        // detached. Recreate on a later Loaded instead of retaining old surfaces.
        canvas.RemoveFromVisualTree();
        Content = null;
        _canvas = null;
        MemoryJournal.ReleaseResource(canvas);
        MemoryJournal.Current?.Record("bloom-retired", new { draws = _drawCount,
            textures = _textureCount, tintEffects = _tintCount,
            meaning = "Native wrappers disposed/detached; not proof of driver cache or OS page release" });
    }

    internal void Retire() { _retired = true; DetachCanvas(); }

    public Color AccentColor
    {
        get => _accentColor;
        set
        {
            if (_accentColor.Equals(value))
                return;
            _accentColor = value;
            _canvas?.Invalidate();
        }
    }

    internal void UseTextureCache(ScanModeBloomTextureCache cache)
    {
        if (ReferenceEquals(_textureCache, cache)) return;
        ReleaseTexture();
        _textureCache = cache;
        _canvas?.Invalidate();
    }

    private void DrawBloom(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_retired || !IsLoaded) return;
        var width = (float)ActualWidth;
        var height = (float)ActualHeight;
        if (width <= 1 || height <= 1)
            return;
        _drawCount++;

        // Rasterize geometry, diffusion and feathering only for a new size/device.
        // Accent animation then samples this texture rather than rebuilding blur
        // graphs on the UI thread for every Rendering callback.
        if (_lightTexture is null || _lightTexture.Dpi != sender.Dpi)
        {
            ReleaseTexture();
            CanvasRenderTarget CreateTexture()
            {
                var request = MemoryJournal.Request("UI.Home.BloomTexture", nativeModule: "Microsoft.Graphics.Canvas.dll");
                var texture = new CanvasRenderTarget(sender, width, height, sender.Dpi);
                try
                {
                    using var drawing = texture.CreateDrawingSession();
                    drawing.Clear(Color.FromArgb(0, 0, 0, 0));
                    DrawLightTexture(sender, drawing, width, height);
                    _textureCount++;
                    MemoryJournal.TrackResource(texture, "UI.Home.BloomTexture", null, "Microsoft.Graphics.Canvas.dll", request);
                    MemoryJournal.Current?.Record("canvas-texture-created", new { resource = "UI.Home.BloomTexture", width, height, sender.Dpi,
                        owner = _textureCache is null ? "page" : "main-window single current raster",
                        meaning = "native drawing target dimensions; resident payload not directly measured" });
                    return texture;
                }
                catch { texture.Dispose(); throw; }
            }
            if (_textureCache is null) _lightTexture = CreateTexture();
            else
            {
                _textureLease = _textureCache.Acquire(sender, width, height, CreateTexture);
                _lightTexture = _textureLease.Texture;
            }
        }
        if (_tintEffect is null)
        {
            var request = MemoryJournal.Request("UI.Home.BloomTintEffect", nativeModule: "Microsoft.Graphics.Canvas.dll");
            var tinted = new ColorMatrixEffect();
            try
            {
                tinted.Source = _lightTexture;
                MemoryJournal.TrackResource(tinted, "UI.Home.BloomTintEffect", null, "Microsoft.Graphics.Canvas.dll", request);
                _tintEffect = tinted;
                _tintCount++;
            }
            catch
            {
                tinted.Dispose();
                throw;
            }
        }
        // Red encodes accent energy; green encodes the white highlights.
        // Mixing them retains the original white core/rays for every accent.
        _tintEffect.ColorMatrix = new Matrix5x4
        {
            M11 = _accentColor.R / 255f,
            M12 = _accentColor.G / 255f,
            M13 = _accentColor.B / 255f,
            M21 = 1 - _accentColor.R / 255f,
            M22 = 1 - _accentColor.G / 255f,
            M23 = 1 - _accentColor.B / 255f,
            M44 = 1
        };
        args.DrawingSession.DrawImage(_tintEffect);
    }

    private void ReleaseTexture()
    {
        var tinted = _tintEffect;
        _tintEffect = null;
        if (tinted is not null)
        {
            tinted.Dispose();
            MemoryJournal.ReleaseResource(tinted);
        }
        var texture = _lightTexture;
        _lightTexture = null;
        var lease = _textureLease;
        _textureLease = null;
        if (lease is not null) { lease.Dispose(); return; }
        if (texture is null) return;
        texture.Dispose();
        MemoryJournal.ReleaseResource(texture);
    }

    private void DrawLightTexture(CanvasControl sender, CanvasDrawingSession target, float width, float height)
    {

        var source = new Vector2(width - 82f, 28f);
        using var lightVolume = new CanvasCommandList(sender);
        using (var drawing = lightVolume.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            DrawAtmosphericShell(drawing, sender, source, width, height);
            DrawRay(drawing, sender, source,
                new Vector2(width * .10f, height * .24f),
                new Vector2(width * .57f, height * .68f), .11f);
            DrawRay(drawing, sender, source,
                new Vector2(width * .30f, height * .13f),
                new Vector2(width * .72f, height * .61f), .16f);
            DrawRay(drawing, sender, source,
                new Vector2(width * .52f, height * .09f),
                new Vector2(width * .87f, height * .51f), .21f);
        }

        using var broadDiffusion = new GaussianBlurEffect
        {
            Source = lightVolume,
            BlurAmount = 34f,
            BorderMode = EffectBorderMode.Soft,
            Optimization = EffectOptimization.Balanced
        };
        using var nearDiffusion = new GaussianBlurEffect
        {
            Source = lightVolume,
            BlurAmount = 13f,
            BorderMode = EffectBorderMode.Soft,
            Optimization = EffectOptimization.Quality
        };

        using var featherMask = CreateFeatherMask(sender, width, height);
        using var broadFeathered = new AlphaMaskEffect
        {
            Source = broadDiffusion,
            AlphaMask = featherMask
        };
        using var nearFeathered = new AlphaMaskEffect
        {
            Source = nearDiffusion,
            AlphaMask = featherMask
        };
        using var coreFeathered = new AlphaMaskEffect
        {
            Source = lightVolume,
            AlphaMask = featherMask
        };

        // The mask reaches zero before the render target ends. Blur therefore
        // has room to dissipate instead of exposing the CanvasControl rectangle.
        target.DrawImage(broadFeathered, 0, 0);
        target.DrawImage(nearFeathered, 0, 0);
        target.DrawImage(coreFeathered, 0, 0);
    }

    private static CanvasCommandList CreateFeatherMask(
        ICanvasResourceCreator resourceCreator, float width, float height)
    {
        var mask = new CanvasCommandList(resourceCreator);
        using var drawing = mask.CreateDrawingSession();
        drawing.Clear(Color.FromArgb(0, 0, 0, 0));
        using var feather = new CanvasLinearGradientBrush(resourceCreator,
        [
            new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(0, 255, 255, 255) },
            new CanvasGradientStop { Position = .09f, Color = Color.FromArgb(255, 255, 255, 255) },
            new CanvasGradientStop { Position = .66f, Color = Color.FromArgb(255, 255, 255, 255) },
            new CanvasGradientStop { Position = .90f, Color = Color.FromArgb(0, 255, 255, 255) },
            new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(0, 255, 255, 255) }
        ]);
        feather.StartPoint = Vector2.Zero;
        feather.EndPoint = new Vector2(0, height);
        drawing.FillRectangle(0, 0, width, height, feather);
        return mask;
    }

    private void DrawAtmosphericShell(CanvasDrawingSession drawing,
        ICanvasResourceCreator resourceCreator, Vector2 source, float width, float height)
    {
        using var outer = new CanvasRadialGradientBrush(
            resourceCreator,
            WithAlpha(Microsoft.UI.Colors.Red, 62),
            WithAlpha(Microsoft.UI.Colors.Red, 0));
        outer.Center = source;
        outer.RadiusX = width * .76f;
        outer.RadiusY = height * .69f;
        drawing.FillRectangle(0, 0, width, height, outer);

        using var core = new CanvasRadialGradientBrush(
            resourceCreator,
            WithAlpha(MixWithWhite(Microsoft.UI.Colors.Red, .22f), 105),
            WithAlpha(Microsoft.UI.Colors.Red, 0));
        core.Center = source;
        core.RadiusX = width * .38f;
        core.RadiusY = height * .43f;
        drawing.FillRectangle(0, 0, width, height, core);
    }

    private void DrawRay(CanvasDrawingSession drawing,
        ICanvasResourceCreator resourceCreator, Vector2 source,
        Vector2 upperEnd, Vector2 lowerEnd, float opacity)
    {
        using var geometry = CanvasGeometry.CreatePolygon(resourceCreator,
        [
            source + new Vector2(-10, 0),
            upperEnd,
            lowerEnd,
            source + new Vector2(20, 8)
        ]);
        using var brush = new CanvasLinearGradientBrush(
            resourceCreator,
            WithAlpha(MixWithWhite(Microsoft.UI.Colors.Red, .28f), (byte)Math.Round(255 * opacity)),
            WithAlpha(Microsoft.UI.Colors.Red, 0));
        brush.StartPoint = source;
        brush.EndPoint = (upperEnd + lowerEnd) / 2;
        drawing.FillGeometry(geometry, brush);
    }

    private static Color MixWithWhite(Color color, float amount) => Color.FromArgb(
        255,
        (byte)Math.Round(color.R + (255 - color.R) * amount),
        (byte)Math.Round(color.G + (255 - color.G) * amount),
        (byte)Math.Round(color.B + (255 - color.B) * amount));

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);
}
