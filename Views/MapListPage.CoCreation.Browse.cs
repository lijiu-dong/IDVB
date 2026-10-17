using IDVBuff.Appearance;
using IDVBuff.Features.Accounts;
using IDVBuff.Features.Maps;
using IDVBuff.UpdateCore;
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

    /// <summary>Switches an idle co-creation gallery back to its community section.</summary>
    internal async Task SelectCoCreationCommunityAsync()
    {
        if (!_coCreationMode || _coCreationEditing || _coCreationOpening
            || _coCreationSection == CoCreationSection.Community && _coCreationSelectedClass is null) return;
        _coCreationLoad?.Cancel();
        _coCreationSection = CoCreationSection.Community;
        _coCreationSelectedClass = null;
        await ShowListAsync();
    }

    private async Task LoadCoCreationCommunityAsync(StackPanel host, bool acquired, CancellationToken token)
    {
        try
        {
            // Entering the gallery compares installed subscriptions against the API versions.
            var reconcile = _mapSubscriptionService.ReconcileInstalledMapsAsync(token);
            var catalog = acquired ? await CoCreationPublicationClient.GetAcquiredAsync(token)
                : await CoCreationPublicationClient.GetCommunityAsync(token);
            await reconcile;
            if (token.IsCancellationRequested) return;
            var subscriptions = _mapSubscriptionService.GetSubscriptions();
            var cards = catalog.Publications.Select(publication =>
                {
                    var installed = FindCoCreationSubscription(subscriptions, publication);
                    var updateAvailable = installed is not null && IsCoCreationUpdateAvailable(installed, publication);
                    return CreateCoCreationCard(publication.Name,
                        string.IsNullOrWhiteSpace(publication.PublisherName) ? "社区地图" : publication.PublisherName,
                        ResolveCoCreationCover(publication.CoverUrl), () => GetCoCreationMapAsync(publication, null, acquired),
                        actionLabel: installed is null ? $"{(acquired ? "查看已获取地图" : "获取社区地图")}{publication.Name}"
                            : updateAvailable ? $"更新社区地图{publication.Name}" : $"查看已添加地图{publication.Name}",
                        badgeText: installed is null ? "订阅" : "已添加", updateAvailable: updateAvailable);
                })
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

    private static MapSubscriptionRecord? FindCoCreationSubscription(
        IReadOnlyList<MapSubscriptionRecord> subscriptions, MapCoCreationPublication publication)
    {
        string? feed = null;
        try { feed = MapSubscriptionLink.Parse(publication.SubscriptionLink).FeedUri.AbsoluteUri; }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidDataException) { }
        return subscriptions.FirstOrDefault(record => feed is not null
                   && string.Equals(record.FeedUri, feed, StringComparison.OrdinalIgnoreCase))
               ?? subscriptions.FirstOrDefault(record => record.Id == publication.Id);
    }

    /// <summary>API versions are timestamp-prefixed, so an ordinal comparison orders them.</summary>
    internal static bool IsCoCreationUpdateAvailable(MapSubscriptionRecord installed, MapCoCreationPublication publication) =>
        string.IsNullOrWhiteSpace(installed.LastAppliedVersion)
        || !string.IsNullOrWhiteSpace(publication.Version)
           && string.CompareOrdinal(publication.Version, installed.LastAppliedVersion) > 0;

    private async Task GetCoCreationMapAsync(MapCoCreationPublication? publication, MapCoCreationDownload? download, bool acquired)
    {
        var token = _coCreationLoad?.Token ?? new CancellationToken(true);
        var name = publication?.Name ?? download!.Name;
        var installed = publication is null ? null
            : FindCoCreationSubscription(_mapSubscriptionService.GetSubscriptions(), publication);
        if (installed is not null)
        {
            var updateAvailable = IsCoCreationUpdateAvailable(installed, publication!);
            var current = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = name,
                Content = CreateCoCreationDescription(updateAvailable
                    ? $"社区有新版本{FormatCoCreationVersion(publication!.Version)}，本地为{FormatCoCreationVersion(installed.LastAppliedVersion)}。更新后会替换本地对应地图。"
                    : "这份地图已添加到本地地图列表，当前已是最新版本。"),
                PrimaryButtonText = updateAvailable ? "更新" : string.Empty,
                CloseButtonText = updateAvailable ? "取消" : "确定", DefaultButton = ContentDialogButton.Primary
            };
            if (await current.ShowThemedAsync(this) != ContentDialogResult.Primary) return;
            token.ThrowIfCancellationRequested();
            await ApplyCoCreationSubscriptionAsync(publication!, installed, token);
            return;
        }
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
            await ApplyCoCreationSubscriptionAsync(publication, null, token);
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

    private static string FormatCoCreationVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "未知版本" : version;

    /// <summary>Adds the publication's subscription when needed, then installs its latest version.</summary>
    private async Task ApplyCoCreationSubscriptionAsync(MapCoCreationPublication publication, MapSubscriptionRecord? installed,
        CancellationToken token)
    {
        var result = await RunMapOperationAsync(installed is null ? "正在获取社区地图……" : "正在更新社区地图……", async () =>
        {
            if (installed is null)
                await _mapSubscriptionService.AddAsync(publication.SubscriptionLink, token);
            else if (!installed.Enabled)
                await _mapSubscriptionService.SetEnabledAsync(installed.Id, true, token);
            var checkedResult = await MapOperationProgress.RunAsync(0, .95,
                () => Task.Run(() => _mapSubscriptionService.CheckAndApplyAsync(token), token));
            if (checkedResult.AppliedCount > 0 && !App.IsSafeMode)
            {
                MapOperationProgress.Report(.95, "正在刷新地图……");
                await MapOperationProgress.RunAsync(.95, 1, () => App.Session.RefreshMapCacheAsync());
            }
            return checkedResult;
        });
        token.ThrowIfCancellationRequested();
        var record = FindCoCreationSubscription(_mapSubscriptionService.GetSubscriptions(), publication);
        if (!string.IsNullOrWhiteSpace(record?.LastError))
            await ShowMessageAsync(installed is null ? "获取社区地图失败" : "更新社区地图失败", record.LastError);
        else if (result.AppliedCount > 0)
            MapClassDiagnosticCoordinator.Instance.Trigger(_repository);
        if (IsLoaded) await ShowListAsync();
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
