using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace IDVBuff.Views;

internal sealed class GentleSponsorshipMotion : IDisposable
{
    private FrameworkElement? _target;
    private UISettings? _settings = new();
    private AccessibilitySettings? _accessibility = new();
    private readonly List<(UIElement Element, long Token)> _ancestors = [];
    private Window? _window;
    private XamlRoot? _root;
    private bool _active;
    private bool _running;
    private int _revision;
    private bool _disposed;

    internal GentleSponsorshipMotion(FrameworkElement target)
    {
        _target = target;
        target.Loaded += Loaded;
        target.Unloaded += Unloaded;
        target.SizeChanged += SizeChanged;
        if (target.IsLoaded) Loaded(target, new RoutedEventArgs());
    }

    private bool Allowed => !_disposed && _target is { IsLoaded: true } target && _active && _root?.IsHostVisible == true
        && _settings?.AnimationsEnabled == true && _accessibility?.HighContrast == false
        && _ancestors.All(item => item.Element.Visibility == Visibility.Visible)
        && target.Visibility == Visibility.Visible;

    private void Loaded(object sender, RoutedEventArgs args)
    {
        if (_disposed || _target is not { } target) return;
        if (_window is not null) return;
        _window = IDVBuff.Presentation.Theming.ThemeService.WindowFor(target)
            ?? (Application.Current as App)?.MainWindow;
        if (_window is null) return;
        _active = IDVBuff.Lifecycle.StartupFocusSnapshot.Capture().ForegroundWindow
            == WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _window.Activated += Activated;
        _window.AppWindow.Changed += WindowChanged;
        _root = target.XamlRoot;
        if (_root is not null) _root.Changed += RootChanged;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            _settings!.AnimationsEnabledChanged += SettingsChanged;
        IDVBuff.Presentation.Theming.ThemeService.For(target).Changed += ThemeChanged;
        for (DependencyObject? node = target; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement element)
                _ancestors.Add((element, element.RegisterPropertyChangedCallback(UIElement.VisibilityProperty,
                    (_, _) => Update())));
        Update();
    }

    private void Unloaded(object sender, RoutedEventArgs args)
    {
        if (_window is not null)
        {
            _window.Activated -= Activated;
            _window.AppWindow.Changed -= WindowChanged;
        }
        if (_root is not null) _root.Changed -= RootChanged;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && _settings is { } settings)
            settings.AnimationsEnabledChanged -= SettingsChanged;
        if (_target is { } target)
            IDVBuff.Presentation.Theming.ThemeService.For(target).Changed -= ThemeChanged;
        foreach (var (element, token) in _ancestors)
            element.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, token);
        _ancestors.Clear();
        _window = null; _root = null; _active = false;
        Update();
    }

    private void SizeChanged(object sender, SizeChangedEventArgs args) => Update();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_target is not { } target) return;
        target.Loaded -= Loaded;
        target.Unloaded -= Unloaded;
        target.SizeChanged -= SizeChanged;
        Unloaded(target, new RoutedEventArgs());
        ++_revision; // An in-flight pulse must not restart a retired visual.
        // Subscription removal ends callbacks; clearing owned WinRT references
        // also ends this object's ownership even if its wrapper awaits GC.
        _target = null;
        _settings = null;
        _accessibility = null;
    }

    private void Activated(object sender, WindowActivatedEventArgs args)
    { _active = args.WindowActivationState != WindowActivationState.Deactivated; Update(); }
    private void WindowChanged(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args) => Update();
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Update();
    private void SettingsChanged(UISettings sender, object args) => _target?.DispatcherQueue.TryEnqueue(() =>
    {
        if (!_disposed) Update();
    });
    private void ThemeChanged(IDVBuff.Appearance.ThemeSnapshot snapshot) => Update();

    private void Update()
    {
        if (_target is not { } target) return;
        var visual = ElementCompositionPreview.GetElementVisual(target);
        visual.CenterPoint = new Vector3((float)target.ActualWidth / 2, (float)target.ActualHeight / 2, 0);
        if (Allowed == _running) return;
        ++_revision;
        _running = Allowed;
        visual.StopAnimation("Scale");
        visual.Scale = Vector3.One;
        if (_running) StartBreathing(visual);
    }

    private static void StartBreathing(Visual visual)
    {
        using var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0, Vector3.One);
        animation.InsertKeyFrame(.45f, new Vector3(1.07f, 1.07f, 1));
        animation.InsertKeyFrame(1, Vector3.One);
        animation.Duration = TimeSpan.FromSeconds(3.4);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("Scale", animation);
    }

    internal async void Pulse()
    {
        if (_disposed || !Allowed || _target is not { } target) return;
        var revision = ++_revision;
        var visual = ElementCompositionPreview.GetElementVisual(target);
        using var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0, Vector3.One);
        animation.InsertKeyFrame(.35f, new Vector3(1.23f, 1.23f, 1));
        animation.InsertKeyFrame(.7f, new Vector3(.97f, .97f, 1));
        animation.InsertKeyFrame(1, Vector3.One);
        animation.Duration = TimeSpan.FromMilliseconds(420);
        visual.StartAnimation("Scale", animation);
        await Task.Delay(430);
        if (revision == _revision && Allowed) StartBreathing(visual);
    }
}
