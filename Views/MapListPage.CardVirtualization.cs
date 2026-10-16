using IDVBuff.Core.Diagnostics;
using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.CompilerServices;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private MapCardRowFactory? _mapCardRowFactory;
    private ItemsRepeater? _mapCardsRepeater;
    private TextBlock? _mapCardsEmpty;
    private readonly ConditionalWeakTable<Image, BitmapImage> _mapPreviewBitmaps = new();
    private readonly HashSet<Image> _activeMapPreviewImages = [];

    private static MapRecord[][] MapCardRows(IReadOnlyList<MapRecord> maps) => maps.Chunk(3).ToArray();

    private ItemsRepeater CreateVirtualMapCards(IReadOnlyList<MapRecord> maps)
    {
        _mapCardRowFactory ??= new(this);
        return new ItemsRepeater
        {
            Margin = new Thickness(7, 12, 7, 12),
            Layout = new StackLayout(),
            VerticalCacheLength = .25,
            HorizontalCacheLength = 0,
            ItemTemplate = _mapCardRowFactory,
            ItemsSource = MapCardRows(maps)
        };
    }

    private void UnbindMapCard(Border card)
    {
        if (card.Tag is MapRecord map && _cardBorders.TryGetValue(map.Id, out var registered)
            && ReferenceEquals(registered, card)) _cardBorders.Remove(map.Id);
        card.Tag = null;
        if (card.Child is Grid content && content.Children[0] is Grid previews
            && previews.Children[0] is Border preview && preview.Child is Image image)
        {
            ReleaseMapPreview(image);
            image.Tag = null;
        }
        MemoryJournal.ReleaseResource(card);
    }

    private void BindMapCard(Border card, MapRecord map)
    {
        card.Tag = map;
        card.Visibility = Visibility.Visible;
        _cardBorders[map.Id] = card;
        var content = (Grid)card.Child;
        var previews = (Grid)content.Children[0];
        var preview = (Border)previews.Children[0];
        // An absent thumbnail on the initial item must not prevent later reuse.
        if (preview.Child is not Image)
            previews.Children[0] = preview = CreatePreviewLayer(GetMapPreviewPath(map, MapScanFloorRules.ResolveScanFloorKey(map)), new Thickness(0));
        if (preview.Child is Image image)
        {
            var path = GetMapPreviewPath(map, MapScanFloorRules.ResolveScanFloorKey(map));
            image.Tag = path;
            if (image.IsLoaded && File.Exists(path)) BindMapPreview(image, path);
        }
        BindMapOriginBadge((Border)previews.Children[1], map);
        ((TextBlock)content.Children[1]).Text = map.DisplayName;
        ((TextBlock)content.Children[2]).Text = BuildRecognitionSummary(map);
        if (card.IsLoaded)
        {
            MemoryJournal.ReleaseResource(card);
            MemoryJournal.TrackResource(card, "MapList.VisibleCard", null, "Microsoft.UI.Xaml.dll");
        }
    }

    private sealed class MapCardRowFactory(MapListPage page) : IElementFactory
    {
        // Reuse complete card control trees; pooled cards hold no map/image.
        // The pool is bounded independently of the number of catalog entries.
        // Recycled rows can still belong to their repeater. Never hand them to
        // another repeater; weak owner keys also let discarded lists die.
        private readonly ConditionalWeakTable<UIElement, Stack<Grid>> _rows = new();
        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var maps = (MapRecord[])args.Data;
            var pool = args.Parent is { } parent ? _rows.GetValue(parent, _ => new()) : null;
            var row = pool is { Count: > 0 } ? pool.Pop() : new Grid();
            if (row.ColumnDefinitions.Count == 0)
                for (var column = 0; column < 3; column++) row.ColumnDefinitions.Add(new ColumnDefinition());
            for (var column = 0; column < maps.Length; column++)
            {
                Border card;
                if (column < row.Children.Count) card = (Border)row.Children[column];
                else
                {
                    card = page.CreateMapCard(maps[column]);
                    Grid.SetColumn(card, column); row.Children.Add(card);
                }
                page.BindMapCard(card, maps[column]);
            }
            for (var column = maps.Length; column < row.Children.Count; column++)
            {
                page.UnbindMapCard((Border)row.Children[column]);
                row.Children[column].Visibility = Visibility.Collapsed;
            }
            page.UpdateSelectedCardVisuals();
            return row;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            var row = (Grid)args.Element;
            foreach (var card in row.Children.OfType<Border>()) page.UnbindMapCard(card);
            if (args.Parent is { } parent)
            {
                var pool = _rows.GetValue(parent, _ => new());
                if (pool.Count < 8) pool.Push(row);
            }
        }
    }
}
