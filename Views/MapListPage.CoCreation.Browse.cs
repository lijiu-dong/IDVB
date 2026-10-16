using IDVBuff.Appearance;
using IDVBuff.Features.Accounts;
using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private Border CreateCoCreationSectionSelector()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var surface = new Border
        {
            Child = row, CornerRadius = new CornerRadius(24), Padding = new Thickness(4),
            Background = FluentTheme.Brush(this, "ControlFillColorDefaultBrush"),
            BorderBrush = FluentTheme.Brush(this, "CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1)
        };
        var choices = new List<ToggleButton>();
        // Keep resource identities stable while WinUI resolves control templates.
        var selectedBackground = new SolidColorBrush();
        var selectedForeground = new SolidColorBrush();
        var template = CreateCoCreationSelectorTemplate();
        foreach (var (section, label) in new[]
        {
            (CoCreationSection.Community, "社区"), (CoCreationSection.Templates, "模板"), (CoCreationSection.Mine, "我的")
        })
        {
            var button = new ToggleButton
            {
                Content = label, IsChecked = section == _coCreationSection, CornerRadius = new CornerRadius(20),
                Padding = new Thickness(20, 10, 20, 10), MinHeight = 40, BorderThickness = new Thickness(0),
                Template = template, FocusVisualPrimaryBrush = FluentTheme.Brush(this, ThemeToken.TextMuted)
            };
            var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled", "Checked", "CheckedPointerOver", "CheckedPressed" })
            {
                button.Resources["ToggleButtonBackground" + state] = state.Contains("Checked", StringComparison.Ordinal)
                    ? selectedBackground : state == "PointerOver"
                        ? FluentTheme.Brush(this, ThemeToken.ControlHover) : transparent;
                button.Resources["ToggleButtonBorderBrush" + state] = transparent;
                button.Resources["ToggleButtonForeground" + state] = state.Contains("Checked", StringComparison.Ordinal)
                    ? selectedForeground : FluentTheme.Brush(this, ThemeToken.Text);
            }
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
            button.Click += async (_, _) =>
            {
                if (_coCreationOpening) { button.IsChecked = section == _coCreationSection; return; }
                foreach (var choice in choices) choice.IsChecked = ReferenceEquals(choice, button);
                if (_coCreationSection == section) return;
                _coCreationLoad?.Cancel();
                _coCreationSection = section;
                await ShowListAsync();
            };
            choices.Add(button);
            row.Children.Add(button);
        }
        void UpdateSelectionTheme(ThemeSnapshot snapshot)
        {
            selectedBackground.Color = ThemeResources.ToColor(snapshot[
                snapshot.IsHighContrast ? ThemeToken.Selection : ThemeToken.ControlPressed]);
            selectedForeground.Color = ThemeResources.ToColor(snapshot[
                snapshot.IsHighContrast ? ThemeToken.SelectionText : ThemeToken.Text]);
        }
        FluentTheme.Observe(surface, UpdateSelectionTheme);
        UpdateSelectionTheme(FluentTheme.Snapshot(surface));
        return surface;
    }

    private async Task LoadCoCreationCommunityAsync(StackPanel host, bool acquired, CancellationToken token)
    {
        try
        {
            var catalog = acquired ? await CoCreationPublicationClient.GetAcquiredAsync(token)
                : await CoCreationPublicationClient.GetCommunityAsync(token);
            if (token.IsCancellationRequested) return;
            var cards = catalog.Publications.Select(publication => CreateCoCreationCard(publication.Name,
                string.IsNullOrWhiteSpace(publication.PublisherName) ? "社区地图" : publication.PublisherName,
                ResolveCoCreationCover(publication.CoverUrl), () => GetCoCreationMapAsync(publication, null, acquired),
                actionLabel: $"{(acquired ? "查看已获取地图" : "获取社区地图")}{publication.Name}", badgeText: "订阅"))
                .Concat(catalog.Maps.Select(map => CreateCoCreationCard(map.Name, "社区地图包", null,
                    () => GetCoCreationMapAsync(null, map, acquired),
                    actionLabel: $"{(acquired ? "查看已获取地图" : "获取社区地图")}{map.Name}", badgeText: "IDVM"))).ToArray();
            ReplaceCoCreationGallery(host, cards.Length == 0
                ? CreateCoCreationDescription(acquired ? "还没有获取地图，可以到“社区”浏览并获取。" : "官网暂时没有展示地图。")
                : CreateCoCreationCards(cards));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
                ReplaceCoCreationGallery(host, CreateCoCreationDescription((acquired ? "获取记录读取失败：" : "社区加载失败：") + exception.Message));
        }
    }

    private async Task GetCoCreationMapAsync(MapCoCreationPublication? publication, MapCoCreationDownload? download, bool acquired)
    {
        var token = _coCreationLoad?.Token ?? new CancellationToken(true);
        var name = publication?.Name ?? download!.Name;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = name,
            Content = CreateCoCreationDescription(acquired ? "这份地图已保存在当前账号的获取记录中，可添加到本地地图列表。"
                : "获取后会保存在当前账号的获取记录中，并可添加到本地地图列表。"),
            PrimaryButtonText = AccountSession.Identity is null ? "登录后获取" : acquired ? "添加到本地" : "获取并添加到本地",
            CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowThemedAsync(this) != ContentDialogResult.Primary) return;
        token.ThrowIfCancellationRequested();
        if (AccountSession.Identity is null) { await AccountSession.LoginAsync(token); return; }
        if (!acquired)
            await CoCreationPublicationClient.AcquireAsync(publication?.Id, download?.ResourceKey ?? download?.Filename, token);
        token.ThrowIfCancellationRequested();
        if (publication is not null)
            await ShowMapSubscriptionsDialogAsync(new Button(), new Button(), initialLink: publication.SubscriptionLink);
        else
        {
            var result = await RunMapOperationAsync("正在下载共创地图……", async () =>
            {
                var imported = await MapOperationProgress.RunAsync(0, .9, () => Task.Run(() => ImportCoCreationDownloadAsync(download!, token), token));
                MapOperationProgress.Report(.9, "正在刷新地图……");
                await MapOperationProgress.RunAsync(.9, 1, RefreshTemplateMapCacheAsync);
                return imported;
            });
            MapClassDiagnosticCoordinator.Instance.Trigger(_repository);
            token.ThrowIfCancellationRequested();
            if (IsLoaded && LocalTemplateClassAdded is { } navigate && result.CreatedClasses.FirstOrDefault() is { } first)
                await navigate(first);
        }
    }

    private async Task<IdvmImportResult> ImportCoCreationDownloadAsync(MapCoCreationDownload download, CancellationToken token)
    {
        const long maximum = 256L * 1024 * 1024;
        if (!Uri.TryCreate(download.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host != "download.xgflee.com" || !uri.AbsolutePath.StartsWith("/maps/", StringComparison.Ordinal)
            || download.Size <= 0 || download.Size > maximum)
            throw new InvalidDataException("社区地图下载地址或大小无效。");
        var path = Path.Combine(Path.GetTempPath(), "IDVB-Community-" + Guid.NewGuid().ToString("N") + ".idvm");
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) })
                { Timeout = TimeSpan.FromMinutes(2) };
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != download.Size)
                throw new InvalidDataException("社区地图已更新，请刷新后重新获取。");
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var destination = File.Create(path))
            {
                var buffer = new byte[81920];
                long received = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token)) != 0)
                {
                    received += count;
                    if (received > download.Size) throw new InvalidDataException("社区地图下载长度超出预期。");
                    await destination.WriteAsync(buffer.AsMemory(0, count), token);
                    MapOperationProgress.Report(.45 * received / download.Size, "正在下载共创地图……");
                }
                if (received != download.Size) throw new InvalidDataException("社区地图下载不完整。");
            }
            await using var plan = await MapOperationProgress.RunAsync(.45, .65, () => _idvmPackageService.InspectAsync(path, token));
            token.ThrowIfCancellationRequested();
            return await MapOperationProgress.RunAsync(.65, 1, () => _idvmPackageService.ImportAsync(plan, token));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
