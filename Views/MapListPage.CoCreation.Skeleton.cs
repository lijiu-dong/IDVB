using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private sealed class CoCreationCardContent
    {
        public required FrameworkElement Cover { get; init; }
        public required FrameworkElement Title { get; init; }
        public required FrameworkElement Byline { get; init; }
        public bool CoverReady { get; set; }
        public Action? RevealCover { get; set; }
    }

    private sealed class CoCreationGalleryState(MapListPage owner, Grid grid)
    {
        public List<CoCreationSlot> Slots { get; } = [];
        public bool Pending { get; private set; } = true;
        private int _columns;
        private int _rows;
        private int _revision;
        public void Unloaded(object sender, RoutedEventArgs args)
        {
            ++_revision;
            foreach (var slot in Slots) slot.CancelRetirement();
        }
        public void Arrange()
        {
            var columns = Math.Clamp((int)Math.Floor((grid.ActualWidth + 24) / 268), 1, 4);
            var rows = (Slots.Count + columns - 1) / columns;
            if (_columns != columns || _rows != rows)
            {
                _columns = columns; _rows = rows;
                grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
                for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                for (var i = 0; i < rows; i++) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            }
            for (var i = 0; i < Slots.Count; i++) { Grid.SetColumn(Slots[i].Root, i % columns); Grid.SetRow(Slots[i].Root, i / columns); }
        }
        public void AddSlot()
        {
            var slot = new CoCreationSlot(owner, Slots.Count);
            Slots.Add(slot); grid.Children.Add(slot.Root);
        }
        public void BeginLoading() => Pending = true;
        public async Task FillAsync(Button[] cards)
        {
            var revision = ++_revision;
            Pending = true;
            UpdateCoCreationScroll(grid);
            // Allocate every actual position as a skeleton before binding data. Existing
            // slots stay parented, so their entrance and glow never restart on hydration.
            while (Slots.Count < cards.Length) AddSlot();
            Arrange();
            foreach (var (slot, index) in Slots.Take(cards.Length).Select((slot, i) => (slot, i))) slot.Bind(cards[index]);
            var surplus = Slots.Skip(cards.Length).ToArray();
            await Task.WhenAll(surplus.Select(slot => slot.RetireAsync()));
            if (revision != _revision) return;
            foreach (var slot in surplus) { grid.Children.Remove(slot.Root); Slots.Remove(slot); }
            Arrange();
            Pending = false;
            UpdateCoCreationScroll(grid);
        }
    }

    private sealed class CoCreationSlot
    {
        public Grid Root { get; } = new() { VerticalAlignment = VerticalAlignment.Top };
        private readonly Border _cover;
        private readonly Border _title;
        private readonly Border _byline;
        private readonly StackPanel _skeleton;
        private DateTimeOffset? _entered;
        private readonly int _delay;
        private int _binding;
        private int _retirementRevision;
        private Microsoft.UI.Xaml.Media.Animation.Storyboard? _retirementAnimation;
        private Button? _card;
        private Action? _releaseCard;
        public CoCreationSlot(MapListPage owner, int index)
        {
            _delay = Math.Min(index, 7) * 45;
            _cover = owner.CreateCoCreationGlowingBar(192, radius: 14);
            _title = owner.CreateCoCreationGlowingBar(19, 128);
            _byline = owner.CreateCoCreationGlowingBar(16, 88);
            var metadata = new StackPanel { Spacing = 5 };
            metadata.Children.Add(_title); metadata.Children.Add(_byline);
            _skeleton = new StackPanel { Spacing = 10, IsHitTestVisible = false };
            _skeleton.Children.Add(_cover); _skeleton.Children.Add(metadata);
            Root.Children.Add(_skeleton);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Root, "正在加载地图卡片");
            Root.SizeChanged += (_, args) => _cover.Height = Math.Max(1, args.NewSize.Width * .75);
            Root.Loaded += (_, _) => _entered ??= DateTimeOffset.UtcNow;
            Root.Unloaded += (_, _) => CancelRetirement();
            AnimateCoCreationCard(Root, index);
        }
        public void Bind(Button card)
        {
            DetachCard();
            var binding = ++_binding;
            Root.Height = double.NaN;
            _skeleton.Opacity = 1;
            foreach (var bar in new[] { _cover, _title, _byline }) { bar.Visibility = Visibility.Visible; bar.Opacity = 1; }
            var state = (CoCreationCardContent)card.Tag;
            state.Cover.Opacity = state.Title.Opacity = state.Byline.Opacity = 0;
            Root.Children.Insert(0, card);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Root, string.Empty);
            var scheduled = false;
            var mountRevision = 0;
            async void Reveal()
            {
                if (scheduled || !Root.IsLoaded || !card.IsLoaded) return;
                scheduled = true;
                var mount = mountRevision;
                // New positions first enter as skeletons, even when metadata is cached.
                var remaining = 300 + _delay - (DateTimeOffset.UtcNow - (_entered ?? DateTimeOffset.UtcNow)).TotalMilliseconds;
                if (CoCreationAnimationsEnabled && remaining > 0) await Task.Delay((int)remaining);
                if (binding != _binding || mount != mountRevision || !Root.IsLoaded || !card.IsLoaded) return;
                RevealCoCreationElement(state.Title, _title);
                RevealCoCreationElement(state.Byline, _byline);
                var coverRevealing = false;
                state.RevealCover = () =>
                {
                    if (binding != _binding || mount != mountRevision || !Root.IsLoaded || !card.IsLoaded || coverRevealing) return;
                    coverRevealing = true;
                    RevealCoCreationElement(state.Cover, _cover);
                };
                if (state.CoverReady) state.RevealCover();
            }
            // Child/parent Loaded order and cached image events are independent.
            // Reconcile after either loads, and retain both hooks across remounts.
            void Loaded(object sender, RoutedEventArgs args) => Root.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (binding == _binding) Reveal(); });
            void Unloaded(object sender, RoutedEventArgs args)
            { mountRevision++; scheduled = false; state.RevealCover = null; }
            card.Loaded += Loaded;
            card.Unloaded += Unloaded;
            Root.Loaded += Loaded;
            Root.Unloaded += Unloaded;
            _card = card;
            _releaseCard = () =>
            {
                card.Loaded -= Loaded; card.Unloaded -= Unloaded;
                Root.Loaded -= Loaded; Root.Unloaded -= Unloaded;
                state.RevealCover = null; Root.Children.Remove(card);
            };
            if (card.IsLoaded && Root.IsLoaded) Reveal();
        }
        public Button? DetachCard()
        {
            CancelRetirement();
            var card = _card;
            _binding++; _releaseCard?.Invoke();
            _card = null; _releaseCard = null;
            return card;
        }
        public async Task RetireAsync()
        {
            CancelRetirement();
            var retirement = _retirementRevision;
            var binding = ++_binding;
            if (!CoCreationAnimationsEnabled || !Root.IsLoaded) return;
            var fade = CoCreationFade(_skeleton, 1, 0, 220);
            StartRetirementAnimation(fade);
            await Task.Delay(230);
            if (retirement != _retirementRevision || binding != _binding || !Root.IsLoaded) return;
            StopRetirementAnimation();
            _skeleton.Opacity = 0;
            // Collapse height after fading, so an empty/excess row does not jump away.
            var height = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = Root.ActualHeight, To = 0, Duration = TimeSpan.FromMilliseconds(180), EnableDependentAnimation = true,
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseInOut }
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(height, Root);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(height, "Height");
            var collapse = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            collapse.Children.Add(height); StartRetirementAnimation(collapse);
            await Task.Delay(190);
            if (retirement != _retirementRevision || binding != _binding || !Root.IsLoaded) return;
            StopRetirementAnimation();
            Root.Height = 0;
        }

        public void CancelRetirement()
        {
            ++_retirementRevision;
            StopRetirementAnimation();
        }

        private void StartRetirementAnimation(Microsoft.UI.Xaml.Media.Animation.Storyboard animation)
        {
            _retirementAnimation = animation;
            var request = IDVBuff.Core.Diagnostics.MemoryJournal.Request("UI.CoCreation.RetirementAnimation", nativeModule: "Microsoft.UI.Xaml.dll");
            IDVBuff.Core.Diagnostics.MemoryJournal.TrackResource(animation, "UI.CoCreation.RetirementAnimation", null, "Microsoft.UI.Xaml.dll", request);
            IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedObject(animation, "UI.CoCreation.RetirementAnimation");
            animation.Begin();
        }

        private void StopRetirementAnimation()
        {
            var animation = _retirementAnimation;
            _retirementAnimation = null;
            if (animation is null) return;
            try { animation.Stop(); }
            finally { IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(animation); }
        }
    }

    private Grid CreateCoCreationSkeleton()
    {
        var grid = CreateCoCreationCards([]);
        var state = (CoCreationGalleryState)grid.Tag;
        state.BeginLoading();
        // Enough rows for the visible viewport plus one; never just a single row.
        var columns = Math.Clamp((int)Math.Floor((Math.Min(1120, Math.Max(244, ActualWidth - 104)) + 24) / 268), 1, 4);
        var rows = Math.Max(3, (int)Math.Ceiling(Math.Max(600, ActualHeight) / 220) + 1);
        for (var i = 0; i < Math.Max(12, columns * rows); i++) state.AddSlot();
        state.Arrange();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, "正在加载地图");
        return grid;
    }

    private Grid CreateCoCreationCards(IEnumerable<Button> cards)
    {
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 32, HorizontalAlignment = HorizontalAlignment.Stretch };
        var state = new CoCreationGalleryState(this, grid);
        grid.Unloaded += state.Unloaded;
        grid.Tag = state;
        grid.Loaded += (_, _) => { state.Arrange(); UpdateCoCreationScroll(grid); };
        grid.SizeChanged += (_, _) => state.Arrange();
        var items = cards.ToArray();
        _ = state.FillAsync(items);
        return grid;
    }

    private static ScrollViewer? CoCreationScroll(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll) return scroll;
        return null;
    }
    private static void UpdateCoCreationScroll(FrameworkElement element)
    {
        var scroll = CoCreationScroll(element);
        if (scroll is null) return;
        bool Pending(DependencyObject node)
        {
            if (node is Grid { Tag: CoCreationGalleryState state } && state.Pending) return true;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) if (Pending(VisualTreeHelper.GetChild(node, i))) return true;
            return false;
        }
        var pending = Pending(scroll);
        scroll.VerticalScrollMode = pending ? ScrollMode.Disabled : ScrollMode.Enabled;
        scroll.VerticalScrollBarVisibility = pending ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
    }

    private async void ReplaceCoCreationGallery(StackPanel host, UIElement content)
    {
        var original = host.Children.OfType<Grid>().FirstOrDefault(grid => grid.Tag is CoCreationGalleryState);
        var incoming = content as Grid ?? (content as StackPanel)?.Children.OfType<Grid>().FirstOrDefault(grid => grid.Tag is CoCreationGalleryState);
        if (original?.Tag is CoCreationGalleryState state && incoming?.Tag is CoCreationGalleryState replacement)
        {
            // The incoming gallery has not loaded yet; detach only its real buttons.
            var cards = replacement.Slots.Select(slot => slot.DetachCard()!).ToArray();
            var fill = state.FillAsync(cards);
            if (content is StackPanel panel)
                foreach (var extra in panel.Children.Where(child => !ReferenceEquals(child, incoming)).ToArray())
                { panel.Children.Remove(extra); host.Children.Add(extra); }
            await fill;
            return;
        }
        if (original?.Tag is CoCreationGalleryState empty)
        {
            var revision = _coCreationViewRevision;
            var wasLoaded = host.IsLoaded;
            await empty.FillAsync([]);
            if (revision != _coCreationViewRevision || (wasLoaded && !host.IsLoaded)) return;
        }
        host.Children.Clear(); host.Children.Add(content);
        UpdateCoCreationScroll(host);
    }
}
