using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace IDVBuff.Features.Maps;

public sealed partial class MapControlPanelWindow
{
    private const double EntryDurationMs = 160d;
    private const double ExitDurationMs = 140d;
    private const float TransitionSlideOffsetDip = -10f;
    private static readonly TimeSpan TransitionTickInterval = TimeSpan.FromMilliseconds(16);

    private DispatcherQueueTimer? _transitionTimer;
    private int _transitionDirection;
    private double _transitionProgress;
    private long _transitionLastTimestamp;

    private void BeginEnterTransition()
    {
        if (_disposed || _window is null)
            return;

        var startProgress = !_isVisible && _transitionDirection == 0
            ? 0d
            : Math.Clamp(_transitionProgress, 0d, 1d);
        _isVisible = true;
        _transitionProgress = startProgress;

        var hwnd = WindowNative.GetWindowHandle(_window);
        GameInputPreservingWindow.SetClickThrough(hwnd, false);
        if (_window.Content is UIElement rootElement)
            rootElement.IsHitTestVisible = true;

        ApplyTransitionVisuals(_transitionProgress);
        GameInputPreservingWindow.Show(hwnd);
        RegisterCaptureProtection();

        if (_transitionProgress >= 1d)
        {
            _transitionProgress = 1d;
            _transitionDirection = 0;
            _transitionTimer?.Stop();
            ApplyTransitionVisuals(1d);
            return;
        }

        _transitionDirection = 1;
        _transitionLastTimestamp = Stopwatch.GetTimestamp();
        EnsureTransitionTimerStarted();
    }

    private void BeginExitTransition()
    {
        _isVisible = false;
        if (_disposed || _window is null)
        {
            StopAndResetTransition();
            return;
        }

        if (_classComboBox.IsDropDownOpen)
            _classComboBox.IsDropDownOpen = false;

        var hwnd = WindowNative.GetWindowHandle(_window);
        GameInputPreservingWindow.SetClickThrough(hwnd, true);
        if (_window.Content is UIElement rootElement)
            rootElement.IsHitTestVisible = false;

        if (!GameInputPreservingWindow.IsVisible(hwnd) || _transitionProgress <= 0.001d)
        {
            CompleteExitTransition();
            return;
        }

        _transitionDirection = -1;
        _transitionLastTimestamp = Stopwatch.GetTimestamp();
        EnsureTransitionTimerStarted();
    }

    private void EnsureTransitionTimerStarted()
    {
        if (_disposed || _window is null)
            return;

        if (_transitionTimer is null)
        {
            var dispatcher = _window.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
            if (dispatcher is null)
            {
                if (_transitionDirection > 0)
                {
                    _transitionProgress = 1d;
                    _transitionDirection = 0;
                    ApplyTransitionVisuals(1d);
                }
                else if (_transitionDirection < 0)
                {
                    CompleteExitTransition();
                }
                return;
            }

            _transitionTimer = dispatcher.CreateTimer();
            _transitionTimer.Interval = TransitionTickInterval;
            _transitionTimer.IsRepeating = true;
            _transitionTimer.Tick += OnTransitionTimerTick;
        }

        if (!_transitionTimer.IsRunning)
            _transitionTimer.Start();
    }

    private void OnTransitionTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_disposed || _window is null || _transitionDirection == 0)
        {
            sender.Stop();
            _transitionDirection = 0;
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsedMs = (now - _transitionLastTimestamp) * 1000d / Stopwatch.Frequency;
        _transitionLastTimestamp = now;
        elapsedMs = Math.Clamp(elapsedMs, 1d, 64d);

        if (_transitionDirection > 0)
        {
            _transitionProgress = Math.Min(1d, _transitionProgress + (elapsedMs / EntryDurationMs));
            ApplyTransitionVisuals(_transitionProgress);
            if (_transitionProgress >= 1d)
            {
                _transitionProgress = 1d;
                _transitionDirection = 0;
                sender.Stop();
            }
        }
        else
        {
            _transitionProgress = Math.Max(0d, _transitionProgress - (elapsedMs / ExitDurationMs));
            ApplyTransitionVisuals(_transitionProgress);
            if (_transitionProgress <= 0d)
                CompleteExitTransition();
        }
    }

    private void CompleteExitTransition()
    {
        _transitionTimer?.Stop();
        _transitionDirection = 0;
        _transitionProgress = 0d;
        ApplyTransitionVisuals(0d);

        if (!_isVisible && _window is not null)
        {
            var hwnd = WindowNative.GetWindowHandle(_window);
            GameInputPreservingWindow.SetClickThrough(hwnd, false);
            GameInputPreservingWindow.Hide(hwnd);
        }
    }

    private void ApplyTransitionVisuals(double progress)
    {
        var clamped = Math.Clamp(progress, 0d, 1d);
        var inverse = 1d - clamped;
        var eased = 1d - (inverse * inverse * inverse);

        if (_window is not null)
        {
            var alpha = (byte)Math.Clamp((int)Math.Round(1d + (eased * 254d)), 1, 255);
            GameInputPreservingWindow.SetOpacity(WindowNative.GetWindowHandle(_window), alpha);
        }

        var offsetY = _classComboBox.IsDropDownOpen ? 0f : (float)((1d - eased) * TransitionSlideOffsetDip);
        var translation = Vector3.UnitY * offsetY;

        if (_content is not null)
        {
            _content.Opacity = eased;
            _content.Translation = translation;
        }

        if (_rootBorder is not null)
        {
            _rootBorder.Opacity = eased;
            _rootBorder.Translation = translation;
        }
    }

    private void StopAndResetTransition()
    {
        _transitionTimer?.Stop();
        _transitionDirection = 0;
        _transitionProgress = 0d;
        if (_content is not null)
        {
            _content.Opacity = 1d;
            _content.Translation = Vector3.Zero;
        }
        if (_rootBorder is not null)
        {
            _rootBorder.Opacity = 1d;
            _rootBorder.Translation = Vector3.Zero;
        }
    }

    private void DisposeTransition()
    {
        if (_transitionTimer is not null)
        {
            _transitionTimer.Stop();
            _transitionTimer.Tick -= OnTransitionTimerTick;
            _transitionTimer = null;
        }
        _transitionDirection = 0;
        _transitionProgress = 0d;
    }

    private void ClearVariantButtons()
    {
        foreach (var child in _variantButtons.Children)
        {
            if (child is Button button)
                button.Click -= VariantButton_Click;
        }
        _variantButtons.Children.Clear();
        _renderedVariantOptions = Array.Empty<MapVariantOption>();
    }
}
