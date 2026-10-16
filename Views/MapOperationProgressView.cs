using System.Globalization;
using IDVBuff.Features.Maps;
using IDVBuff.Appearance;
using IDVBuff.Presentation.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace IDVBuff.Views;

internal sealed class MapOperationProgressView : StackPanel, IProgress<MapOperationUpdate>, IDisposable
{
    private readonly TextBlock _operation;
    private readonly TextBlock _percentage;
    private readonly ProgressBar _bar;
    private readonly Image _icon;
    private readonly object _gate = new();
    private MapOperationUpdate _latest;
    private bool _queued;
    private bool _closed;

    public MapOperationProgressView(string operation)
    {
        MinWidth = 360;
        Spacing = 16;
        _latest = new(0, operation);
        var heading = new Grid { ColumnSpacing = 14 };
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        _icon = new Image { Width = 40, Height = 40 };
        MemoryImageBinding.Attach(_icon, new Uri(Path.Combine(Path.GetDirectoryName(typeof(MapOperationProgressView).Assembly.Location)!,
            "Assets", "Icons", "IDVB_icon_square_master.png")), 80, "UI.MapOperation.Icon");
        heading.Children.Add(_icon);
        _operation = new TextBlock { Text = operation, FontSize = 17,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_operation, 1);
        heading.Children.Add(_operation);
        Children.Add(heading);
        var progress = new Grid { ColumnSpacing = 12 };
        progress.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        progress.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0,
            IsIndeterminate = false, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_bar, "地图操作进度");
        progress.Children.Add(_bar);
        _percentage = new TextBlock { Text = "0.0%", MinWidth = 58,
            HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(_percentage, 1);
        progress.Children.Add(_percentage);
        Children.Add(progress);
        Loaded += (_, _) =>
        {
            var resources = ThemeService.For(this);
            _operation.Foreground = resources[ThemeToken.Text];
            _percentage.Foreground = resources[ThemeToken.TextSecondary];
            _bar.Foreground = resources[ThemeToken.Accent];
            _bar.Background = resources[ThemeToken.ControlBorder];
        };
    }

    public void Report(MapOperationUpdate value)
    {
        lock (_gate)
        {
            if (_closed || !double.IsFinite(value.Fraction)) return;
            // Ignore delayed callbacks from a previous stage, including their label.
            if (value.Fraction < _latest.Fraction) return;
            _latest = value with { Fraction = Math.Clamp(value.Fraction, 0, 1) };
            if (_queued) return;
            _queued = true;
        }
        DispatcherQueue.TryEnqueue(RenderLatest);
    }

    private void RenderLatest()
    {
        lock (_gate)
        {
            _queued = false;
            if (_closed) return;
            _operation.Text = _latest.Operation;
            _bar.Value = _latest.Fraction * 100;
            _percentage.Text = _bar.Value.ToString("F1", CultureInfo.InvariantCulture) + "%";
        }
    }

    public void Complete()
    {
        Report(new(1, "操作完成"));
        RenderLatest();
    }

    public void Dispose()
    {
        lock (_gate) _closed = true;
        MemoryImageBinding.Release(_icon);
    }
}
