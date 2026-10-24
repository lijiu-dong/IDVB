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
        ComboBox Coverage, ComboBox Preference, CheckBox[] Floors, CheckBox SpecialTeleports, CheckBox DenseRoutes, TextBlock Status)
    {
        public MapExplorationOptions ReadOptions(MapNavigationTopology navigation, IReadOnlyList<MapRouteGateEndpoint> gates)
        {
            if (Entrance.SelectedItem is not MapRouteGateEndpoint from || Exit.SelectedItem is not MapRouteGateEndpoint to)
                throw new InvalidOperationException("请选择有效的入口和出口；关闭密集路线时需要两个不同点位。");
            var entrance = gates.SingleOrDefault(g => g.FloorKey == from.FloorKey && g.AnchorKey == from.AnchorKey)
                ?? throw new InvalidOperationException("地图缺少所选入口门标志。");
            var exit = gates.SingleOrDefault(g => g.FloorKey == to.FloorKey && g.AnchorKey == to.AnchorKey)
                ?? throw new InvalidOperationException("地图缺少所选出口门标志。");
            if (DenseRoutes.IsChecked is not true && entrance.Id == exit.Id)
                throw new InvalidOperationException("关闭密集路线时，请选择不同的入口和出口门标志。");
            var allowed = Floors.Where(f => f.IsChecked is true).Select(f => (string)f.Tag)
                .Intersect(navigation.Floors.Select(f => f.FloorKey)).ToHashSet();
            if (SpecialTeleports.IsChecked is true)
                MapSpecialRoomTeleports.ValidateSelection(navigation, navigation.Floors.Select(f => f.FloorKey).ToHashSet());
            return new(entrance.Id, exit.Id, allowed,
                (MapRoomCoverage)Coverage.SelectedIndex, (MapRoutePreference)Preference.SelectedIndex,
                navigation.Modules.Where(m => m.IsHighValue).Select(m => m.Id).ToHashSet(), gates,
                SpecialTeleports.IsChecked is true, DenseRoutes.IsChecked is true);
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
        exit.SelectedItem = gates.FirstOrDefault(g => g.Id != ((MapRouteGateEndpoint)entrance.SelectedItem).Id) ?? gates[0];
        content.Children.Add(entrance); content.Children.Add(exit);
        var denseRoutes = new CheckBox { Content = "密集路线", IsChecked = true, Tag = "dense-routes" };
        content.Children.Add(denseRoutes);
        content.Children.Add(new TextBlock { Text = "仅不同颜色阶段共用通道时安排最多两条并行路线；同一阶段只能在房间内掉头、沿原通道退出最多两个模块的短死胡同，不会长距离折返或穿越已有线条。不同阶段不能重叠或交错，须保持墙体和边缘间距，宽度不足时不挤入第二条。允许同点进出；最后接向出口的一段只放宽阶段间距。关闭后必须选择不同出入口。",
            TextWrapping = TextWrapping.Wrap });
        void SeparateGates()
        {
            if (denseRoutes.IsChecked is true || entrance.SelectedItem is not MapRouteGateEndpoint from
                || exit.SelectedItem is not MapRouteGateEndpoint to || from.Id != to.Id) return;
            exit.SelectedItem = gates.FirstOrDefault(g => g.Id != from.Id);
        }
        denseRoutes.Unchecked += (_, _) => SeparateGates();
        entrance.SelectionChanged += (_, _) => SeparateGates();
        exit.SelectionChanged += (_, _) => SeparateGates();
        var coverage = new ComboBox { Header = "经过房间数量", ItemsSource = new[] { "高价值房间优先", "两者之间", "全房间优先" },
            SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        content.Children.Add(coverage);
        content.Children.Add(new TextBlock { Text = "两者之间：优先高价值房间，再增加约一半普通房间。已确认没有宝箱候选点的房间不计入搜箱目标；必要通行和跨层仍可经过。监狱不参与任何自动路线。", TextWrapping = TextWrapping.Wrap });
        var floorPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        var floors = floorKeys.Select(f => new CheckBox { Content = f.ToUpperInvariant(), Tag = f,
            MinWidth = 0, IsChecked = true }).ToArray();
        content.Children.Add(new TextBlock { Text = "搜房楼层" });
        foreach (var floor in floors) floorPanel.Children.Add(floor);
        content.Children.Add(floorPanel);
        content.Children.Add(new TextBlock { Text = "仅勾选要搜房的楼层。优先不经过未勾选楼层；仅在勾选楼层与入口、出口无法连通时借道，不为增加搜房覆盖绕行其他楼层。", TextWrapping = TextWrapping.Wrap });
        var specialTeleports = new CheckBox { Content = "通过特殊房间传送（打夜莺）", IsChecked = false,
            Tag = "special-room-teleports", Visibility = specialTeleportFloors is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed };
        specialTeleports.IsEnabled = specialTeleportFloors is { Count: > 0 };
        var teleportHint = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = specialTeleports.Visibility,
            Text = "勾选后主路线优先以最短路径到达特殊传送起点，仅在勾选的搜房楼层增加附近房间支路，原路返回后继续传送，并经过全部单向传送通道。传送不受搜房楼层勾选限制。若最短前段阻断出口，则按距离尝试更长的可完成前段。" };
        content.Children.Add(specialTeleports); content.Children.Add(teleportHint);
        var preference = new ComboBox { Header = "路线偏好", ItemsSource = new[] { "最短优先", "最简优先（更少的转弯、掉头）" },
            SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        content.Children.Add(preference);
        content.Children.Add(new TextBlock { Text = "自动识别高价值房间和楼梯、下落通路。不同阶段使用不同颜色；同一阶段只在短死胡同内原路退出，不会沿已画线条长距离折返，也不能穿越自己的路线。短支路最多经过两个走廊并进入一个房间，只能在分叉点接入主线，游玩时可原路返回。房间按进入顺序编号，⇅ 表示跨层、★ 表示高价值；其他楼层的同一房间只作通道。无法覆盖的房间会在生成后列出。",
            TextWrapping = TextWrapping.Wrap });

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        content.Children.Add(status);
        content.Children.Add(new TextBlock { Text = "生成后可编辑或整体撤销，再次生成会替换上次规划。",
            TextWrapping = TextWrapping.Wrap, Foreground = EditorMuted });
        return new(content, entrance, exit, coverage, preference, floors, specialTeleports, denseRoutes, status);
    }
}
