using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation;

namespace IDVBuff.Views;

public sealed class UsageVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Measure against the actual popup space, so narrow windows never gain a horizontal scrollbar.</summary>
public sealed class UsageDialogLayout : Grid
{
    public static readonly DependencyProperty PreferredWidthProperty = DependencyProperty.Register(
        nameof(PreferredWidth), typeof(double), typeof(UsageDialogLayout), new PropertyMetadata(360d,
            (owner, _) => ((UsageDialogLayout)owner).InvalidateMeasure()));
    public double PreferredWidth { get => (double)GetValue(PreferredWidthProperty); set => SetValue(PreferredWidthProperty, value); }
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = Math.Min(PreferredWidth, availableSize.Width);
        var measured = base.MeasureOverride(new(width, availableSize.Height));
        return new(width, measured.Height);
    }
}

public sealed class SponsorshipCardPanel : Panel
{
    private const double Gap = 12;
    private double _cardHeight;
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 720;
        var columns = width >= 600 ? 2 : 1;
        var cardWidth = Math.Max(0, (width - (columns - 1) * Gap) / columns);
        _cardHeight = 0;
        foreach (var child in Children)
        {
            child.Measure(new(cardWidth, double.PositiveInfinity));
            _cardHeight = Math.Max(_cardHeight, child.DesiredSize.Height);
        }
        var rows = (Children.Count + columns - 1) / columns;
        return new(width, rows == 0 ? 0 : rows * _cardHeight + (rows - 1) * Gap);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = finalSize.Width >= 600 ? 2 : 1;
        var cardWidth = Math.Max(0, (finalSize.Width - (columns - 1) * Gap) / columns);
        for (var index = 0; index < Children.Count; index++)
            Children[index].Arrange(new(index % columns * (cardWidth + Gap), index / columns * (_cardHeight + Gap), cardWidth, _cardHeight));
        return finalSize;
    }
}
