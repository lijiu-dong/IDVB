using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private static Grid CreateRoutePlanningHost(StackPanel content, FrameworkElement progress)
    {
        // Extend only the viewport into the native dialog's right padding. The
        // content keeps its inset while the scrollbar sits at the outer edge.
        var host = new Grid { MaxHeight = 560, Margin = new Thickness(0, 0, -24, 0), RowSpacing = 12 };
        host.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        host.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var scroll = new ScrollViewer { Content = content, Padding = new Thickness(0, 0, 24, 0),
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        host.Children.Add(scroll);
        progress.Margin = new Thickness(0, 0, 24, 0);
        Grid.SetRow(progress, 1);
        host.Children.Add(progress);
        return host;
    }

    private sealed record RoutePlanningControls(StackPanel Content, ComboBox Entrance, ComboBox Exit,
        ComboBox Coverage, ComboBox Preference, CheckBox[] Floors, CheckBox SpecialTeleports, TextBlock Status)
    {
        public MapExplorationOptions ReadOptions(MapNavigationTopology navigation, IReadOnlyList<MapRouteGateEndpoint> gates)
        {
            var from = (MapRouteGateEndpoint)Entrance.SelectedItem; var to = (MapRouteGateEndpoint)Exit.SelectedItem;
            var entrance = gates.SingleOrDefault(g => g.FloorKey == from.FloorKey && g.AnchorKey == from.AnchorKey)
                ?? throw new InvalidOperationException("地图缺少所选入口门标志。");
            var exit = gates.SingleOrDefault(g => g.FloorKey == to.FloorKey && g.AnchorKey == to.AnchorKey)
                ?? throw new InvalidOperationException("地图缺少所选出口门标志。");
            if (entrance.Id == exit.Id) throw new InvalidOperationException("请选择不同的入口和出口门标志。");
            var allowed = Floors.Where(f => f.IsChecked is true).Select(f => (string)f.Tag)
                .Intersect(navigation.Floors.Select(f => f.FloorKey)).ToHashSet();
            if (!allowed.Contains(entrance.FloorKey) || !allowed.Contains(exit.FloorKey))
                throw new InvalidOperationException("允许的楼层必须包含入口和出口。");
            if (SpecialTeleports.IsChecked is true) MapSpecialRoomTeleports.ValidateSelection(navigation, allowed);
            return new(entrance.Id, exit.Id, allowed,
                (MapRoomCoverage)Coverage.SelectedIndex, (MapRoutePreference)Preference.SelectedIndex,
                navigation.Modules.Where(m => m.IsHighValue).Select(m => m.Id).ToHashSet(), gates,
                SpecialTeleports.IsChecked is true);
        }
    }

    private RoutePlanningControls CreateRoutePlanningControls(IReadOnlyList<MapRouteGateEndpoint> gates, IEnumerable<string> floorKeys,
        IReadOnlySet<string>? specialTeleportFloors = null)
    {
        var content = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        ComboBox GatePicker(string title) => new() { Header = title, ItemsSource = gates,
            DisplayMemberPath = nameof(MapRouteGateEndpoint.DisplayName), HorizontalAlignment = HorizontalAlignment.Stretch };
        var entrance = GatePicker("入口"); var exit = GatePicker("出口");
        entrance.SelectedItem = gates.FirstOrDefault(g => g.AnchorKey == "main-entrance") ?? gates[0];
        exit.SelectedItem = gates.First(g => g.Id != ((MapRouteGateEndpoint)entrance.SelectedItem).Id);
        content.Children.Add(entrance); content.Children.Add(exit);
        var coverage = new ComboBox { Header = "经过房间数量", ItemsSource = new[] { "高价值房间优先", "两者之间", "全房间优先" },
            SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        content.Children.Add(coverage);
        content.Children.Add(new TextBlock { Text = "两者之间：优先高价值房间，再增加约一半普通房间。", TextWrapping = TextWrapping.Wrap });
        var floorPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        var floors = floorKeys.Select(f => new CheckBox { Content = f.ToUpperInvariant(), Tag = f,
            MinWidth = 0, IsChecked = true }).ToArray();
        content.Children.Add(new TextBlock { Text = "允许的楼层" });
        foreach (var floor in floors) floorPanel.Children.Add(floor);
        content.Children.Add(floorPanel);
        var specialTeleports = new CheckBox { Content = "通过特殊房间传送（打夜莺）", IsChecked = false,
            Tag = "special-room-teleports", Visibility = specialTeleportFloors is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed };
        var teleportHint = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = specialTeleports.Visibility };
        void UpdateSpecialTeleports()
        {
            var allowed = floors.Where(f => f.IsChecked is true).Select(f => (string)f.Tag).ToHashSet();
            var missing = specialTeleportFloors?.Except(allowed).Order().ToArray() ?? [];
            specialTeleports.IsEnabled = specialTeleportFloors is { Count: > 0 } && missing.Length == 0;
            if (!specialTeleports.IsEnabled) specialTeleports.IsChecked = false;
            teleportHint.Text = missing.Length > 0
                ? $"需先勾选传送对应楼层：{string.Join("、", missing.Select(f => f.ToUpperInvariant()))}。"
                : "勾选后主路线优先以最短路径到达特殊传送起点，沿途允许短支路探索附近房间，原路返回后继续传送，并经过全部单向传送通道。若最短前段阻断出口，则按距离尝试更长的可完成前段。";
        }
        foreach (var floor in floors)
        {
            floor.Checked += (_, _) => UpdateSpecialTeleports();
            floor.Unchecked += (_, _) => UpdateSpecialTeleports();
        }
        UpdateSpecialTeleports();
        content.Children.Add(specialTeleports); content.Children.Add(teleportHint);
        var preference = new ComboBox { Header = "路线偏好", ItemsSource = new[] { "最短优先", "最简优先（更少的转弯、掉头）" },
            SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        content.Children.Add(preference);
        content.Children.Add(new TextBlock { Text = "自动识别高价值房间和楼梯、下落通路。不同阶段使用不同颜色；主路线不交叉重叠，短支路最多经过两个走廊并进入一个房间，游玩时可原路返回。房间按进入顺序编号，⇅ 表示跨层、★ 表示高价值；其他楼层的同一房间只作通道。无法覆盖的房间会在生成后列出。",
            TextWrapping = TextWrapping.Wrap });

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        content.Children.Add(status);
        content.Children.Add(new TextBlock { Text = "生成后可编辑或整体撤销，再次生成会替换上次规划。",
            TextWrapping = TextWrapping.Wrap, Foreground = EditorMuted });
        return new(content, entrance, exit, coverage, preference, floors, specialTeleports, status);
    }
}
