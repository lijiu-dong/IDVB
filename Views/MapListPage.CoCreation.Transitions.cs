using IDVBuff.Appearance;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Numerics;
using Windows.UI.ViewManagement;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private static bool CoCreationAnimationsEnabled => new UISettings().AnimationsEnabled;

    // XAML owns element opacity. Animating its backing Visual directly is overwritten
    // by subsequent XAML render/property updates (including pointer visual states).
    private static Storyboard CoCreationFade(UIElement target, double from, double to, int milliseconds, int delay = 0)
    {
        var fade = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(milliseconds),
            BeginTime = TimeSpan.FromMilliseconds(delay), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(fade, target);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        return storyboard;
    }

    private static void AnimateCoCreationCard(FrameworkElement card, int index)
    {
        var started = false;
        Storyboard? entrance = null;
        var transform = new TranslateTransform();
        card.RenderTransform = transform;
        card.Opacity = CoCreationAnimationsEnabled ? 0 : 1;
        card.Loaded += (_, _) =>
        {
            if (started) return;
            started = true;
            if (!CoCreationAnimationsEnabled) { card.Opacity = 1; return; }
            var delay = Math.Min(index, 7) * 45;
            entrance = CoCreationFade(card, 0, 1, 300, delay);
            var move = new DoubleAnimation { From = 12, To = 0, BeginTime = TimeSpan.FromMilliseconds(delay),
                Duration = TimeSpan.FromMilliseconds(300), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(move, transform);
            Storyboard.SetTargetProperty(move, "Y");
            entrance.Children.Add(move);
            entrance.Completed += (_, _) => { entrance.Stop(); card.Opacity = 1; transform.Y = 0; };
            entrance.Begin();
        };
        card.Unloaded += (_, _) => { entrance?.Stop(); card.Opacity = 1; transform.Y = 0; };
    }

    private Border CreateCoCreationGlowingBar(double height, double? width = null, double radius = 5)
    {
        var background = new SolidColorBrush();
        var bar = new Border { Height = height, Width = width ?? double.NaN, CornerRadius = new CornerRadius(radius),
            HorizontalAlignment = width is null ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            Background = background, IsHitTestVisible = false };
        SpriteVisual? glow = null;
        CompositionColorBrush? glowBrush = null;
        CompositionRoundedRectangleGeometry? geometry = null;
        void Theme(ThemeSnapshot snapshot)
        {
            var baseColor = ThemeResources.ToColor(snapshot[ThemeToken.TextMuted]);
            baseColor.A = snapshot.IsHighContrast ? (byte)64 : (byte)24;
            background.Color = baseColor;
            if (glowBrush is not null)
            {
                var light = ThemeResources.ToColor(snapshot[ThemeToken.Text]);
                light.A = snapshot.IsHighContrast ? (byte)0 : (byte)30;
                glowBrush.Color = light;
            }
        }
        FluentTheme.Observe(bar, Theme);
        Theme(FluentTheme.Snapshot(bar));
        void Resize()
        {
            if (glow is null || geometry is null) return;
            glow.Size = new Vector2((float)bar.ActualWidth, (float)bar.ActualHeight);
            geometry.Size = glow.Size;
        }
        bar.Loaded += (_, _) =>
        {
            if (glow is null)
            {
                var compositor = ElementCompositionPreview.GetElementVisual(bar).Compositor;
                glow = compositor.CreateSpriteVisual();
                glowBrush = compositor.CreateColorBrush();
                glow.Brush = glowBrush;
                geometry = compositor.CreateRoundedRectangleGeometry();
                geometry.CornerRadius = new Vector2((float)radius);
                glow.Clip = compositor.CreateGeometricClip(geometry);
                ElementCompositionPreview.SetElementChildVisual(bar, glow);
            }
            Theme(FluentTheme.Snapshot(bar));
            Resize();
            if (!CoCreationAnimationsEnabled) { glow.Opacity = .3f; return; }
            // Only an independent light layer breathes; the base and card remain steady.
            using var pulse = glow.Compositor.CreateScalarKeyFrameAnimation();
            pulse.InsertKeyFrame(0, .15f); pulse.InsertKeyFrame(.5f, .75f); pulse.InsertKeyFrame(1, .15f);
            pulse.Duration = TimeSpan.FromMilliseconds(2200);
            pulse.IterationBehavior = AnimationIterationBehavior.Forever;
            glow.StartAnimation("Opacity", pulse);
        };
        bar.SizeChanged += (_, _) => Resize();
        bar.Unloaded += (_, _) =>
        {
            if (glow is null) return;
            glow.StopAnimation("Opacity");
            ElementCompositionPreview.SetElementChildVisual(bar, null);
            var clip = glow.Clip;
            glow.Clip = null;
            glow.Brush = null;
            clip?.Dispose(); geometry?.Dispose(); glowBrush?.Dispose(); glow.Dispose();
            glow = null; geometry = null; glowBrush = null;
        };
        return bar;
    }

    private static void RevealCoCreationElement(FrameworkElement element, FrameworkElement skeleton)
    {
        if (skeleton.Visibility == Visibility.Collapsed) return;
        if (!CoCreationAnimationsEnabled || !element.IsLoaded)
        {
            element.Opacity = 1; skeleton.Visibility = Visibility.Collapsed; return;
        }
        var incoming = CoCreationFade(element, 0, 1, 240);
        var outgoing = CoCreationFade(skeleton, 1, 0, 240);
        incoming.Completed += (_, _) => { incoming.Stop(); element.Opacity = 1; };
        outgoing.Completed += (_, _) => { outgoing.Stop(); skeleton.Visibility = Visibility.Collapsed; };
        void Unloaded(object sender, RoutedEventArgs args)
        {
            incoming.Stop(); outgoing.Stop(); element.Opacity = 1;
            skeleton.Visibility = Visibility.Collapsed; element.Unloaded -= Unloaded;
        }
        element.Unloaded += Unloaded;
        outgoing.Completed += (_, _) => element.Unloaded -= Unloaded;
        incoming.Begin(); outgoing.Begin();
    }
}
