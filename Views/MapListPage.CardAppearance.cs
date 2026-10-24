using IDVBuff.Presentation.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private void UpdateSelectedCardVisuals()
    {
        var theme = FluentTheme.Snapshot(this);
        foreach (var (id, card) in _cardBorders)
        {
            var selected = _selectedMapIds.Contains(id);
            var group = _variantGroups.FirstOrDefault(candidate => candidate.MapIds.Contains(id));
            var colors = IDVBuff.Appearance.MapCardPalette.Resolve(theme, group?.PaletteSlot, selected);
            var fill = ThemeResources.ToColor(colors.Fill);
            var stroke = ThemeResources.ToColor(colors.Border);
            if (card.Background is SolidColorBrush background) { if (background.Color != fill) background.Color = fill; }
            else card.Background = new SolidColorBrush(fill);
            if (card.BorderBrush is SolidColorBrush border) { if (border.Color != stroke) border.Color = stroke; }
            else card.BorderBrush = new SolidColorBrush(stroke);
            if (card.Child is Grid content)
                foreach (var label in content.Children.OfType<TextBlock>())
                {
                    if (label.Tag is "variant-group")
                    {
                        // Selection deliberately overrides the category colors;
                        // keep membership visible after binding and on reload.
                        label.Text = group is null ? string.Empty : $"变体组合 {group.PaletteSlot + 1}";
                        label.Visibility = group is null ? Visibility.Collapsed : Visibility.Visible;
                    }
                    var foreground = ThemeResources.ToColor(Grid.GetRow(label) == 1 ? colors.Text : colors.SecondaryText);
                    if (label.Foreground is SolidColorBrush brush) { if (brush.Color != foreground) brush.Color = foreground; }
                    else label.Foreground = new SolidColorBrush(foreground);
                }
        }

        if (_editButton is not null)
            _editButton.IsEnabled = HasSelection;
        if (_deleteButton is not null)
            _deleteButton.IsEnabled = HasSelection;
        if (_variantButton is not null)
            _variantButton.IsEnabled = _selectedMapIds.Count >= 2;
    }

}
