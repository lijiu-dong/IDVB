using IDVBuff.Features.Accounts;
using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace IDVBuff.Views;
public sealed partial class MapListPage
{
    private readonly bool _coCreationMode;
    private readonly MapCoCreationTemplateCatalog _coCreationTemplates = new();
    private CancellationTokenSource? _coCreationLoad;
    private MapCoCreationPackage? _coCreationPackage;
    private readonly Dictionary<Guid, MapCoCreationPackage> _coCreationCloudPackages = [];
    private StackPanel? _coCreationPublishedHost;
    private bool _coCreationOpening;
    private bool _coCreationEditing;
    private enum CoCreationSection { Community, Templates, Mine }
    private CoCreationSection _coCreationSection = CoCreationSection.Community;
    private int _coCreationViewRevision;
    private CoCreationClassSelection? _coCreationSelectedClass;
    private MapDraft? _coCreationSourceDraft;
    private StackPanel? _coCreationCommunityGallery;

    /// <summary>The community gallery while it is the visible co-creation section.</summary>
    internal FrameworkElement? GetCoCreationCommunityControl() =>
        _coCreationSection == CoCreationSection.Community && _coCreationCommunityGallery is { IsLoaded: true } gallery
            ? gallery : null;

    private async Task ShowCoCreationAsync()
    {
        using var memory = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("UI.CoCreation.Show", _coCreationSection.ToString());
        var workflowRevision = _workflowRevision;
        var revision = ++_coCreationViewRevision;
        _coCreationEditing = false;
        _coCreationSourceDraft = null;
        _draft = null;
        ResetBatchImport();
        if (_coCreationSelectedClass is { } selection && IsCoCreationOwner(selection.PublisherHandle))
        {
            ShowCoCreationClass(selection);
            return;
        }
        _coCreationSelectedClass = null;
        await CloseCoCreationAsync();
        if (revision != _coCreationViewRevision || !IsLoaded || !IsWorkflowCurrent(workflowRevision, "gallery-close")) return;
        _coCreationOpening = false;
        _coCreationLoad = new();
        var token = _coCreationLoad.Token;
        var content = new StackPanel { Spacing = 22 };
        var root = CreateCoCreationGalleryViewport(content);
        var introduction = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var title = CreateTitle("共创地图");
        title.FontSize = 26;
        title.Foreground = FluentTheme.Brush(this, "TextFillColorPrimaryBrush");
        heading.Children.Add(title);
        heading.Children.Add(CreateCoCreationBetaBadge());
        introduction.Children.Add(heading);
        content.Children.Add(introduction);
        var toolbar = new Grid { ColumnSpacing = 12 };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sections = CreateCoCreationSectionSelector();
        Grid.SetColumn(sections, 1);
        toolbar.Children.Add(sections);
        var refreshContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        refreshContent.Children.Add(new FontIcon { Glyph = "\uE72C", FontSize = 13 });
        refreshContent.Children.Add(new TextBlock { Text = "刷新", FontSize = 12 });
        var refresh = CreateCoCreationToolbarButton(refreshContent, "刷新当前选区");
        refresh.Click += async (_, _) => await ShowListAsync();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(refresh);
        actions.Children.Add(CreateTemplateImportButton());
        Grid.SetColumn(actions, 2);
        toolbar.Children.Add(actions);
        content.Children.Add(toolbar);
        content.Children.Add(CreateCoCreationDescription(_coCreationSection switch
        {
            CoCreationSection.Community => "浏览地图社区官网展示的地图，获取后同步到你的账号。",
            CoCreationSection.Templates => "导入共创模板，添加到地图列表后开始共创。",
            _ => "联网查看当前账号已发布的地图和已获取的社区地图。"
        }));
        _workflowHost.Content = root;
        if (_coCreationSection == CoCreationSection.Mine)
        {
            await ShowCoCreationMineAsync(content, token);
            return;
        }
        var gallery = new StackPanel { Spacing = 18 };
        gallery.Children.Add(CreateCoCreationSkeleton());
        content.Children.Add(gallery);
        if (_coCreationSection == CoCreationSection.Community)
        {
            _coCreationCommunityGallery = gallery;
            await LoadCoCreationCommunityAsync(gallery, false, token);
            return;
        }
        try
        {
            var classes = await _coCreationTemplates.LoadClassesAsync(token);
            if (token.IsCancellationRequested || _coCreationEditing || _coCreationSelectedClass is not null) return;
            if (classes.Count == 0)
            {
                ReplaceCoCreationGallery(gallery, CreateTemplateEmptyState(root));
                return;
            }
            var status = CreateCoCreationDescription(string.Empty);
            status.Visibility = Visibility.Collapsed;
            var templates = new StackPanel { Spacing = 18 };
            templates.Children.Add(CreateCoCreationCards(classes.Select(templateClass => CreateCoCreationCard(
                templateClass.Name, "添加到本地", _coCreationTemplates.GetPreviewPath(templateClass.Templates[0]),
                () => AddCoCreationTemplateClassAsync(templateClass, status),
                actionLabel: $"将{templateClass.Name}模板添加到本地", errorTitle: "模板添加失败",
                badgeText: $"{templateClass.Templates.Count} 张地图"))));
            templates.Children.Add(status);
            ReplaceCoCreationGallery(gallery, templates);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
                ReplaceCoCreationGallery(gallery, CreateCoCreationDescription("模板加载失败：" + exception.Message));
        }
    }

    private async Task ShowCoCreationMineAsync(StackPanel content, CancellationToken token)
    {
        if (_accountService.Identity is null)
        {
            content.Children.Add(CreateCoCreationDescription("登录后查看你的地图。"));
            var login = CreateCoCreationToolbarButton("登录地图社区账号", "登录地图社区账号");
            login.Click += async (_, _) =>
            {
                login.IsEnabled = false;
                try { await _accountService.LoginAsync(token); }
                catch (OperationCanceledException) { }
                catch (Exception exception) { if (IsLoaded) await ShowMessageAsync("登录失败", exception.Message); }
                finally { login.IsEnabled = true; }
            };
            content.Children.Add(login);
            return;
        }
        content.Children.Add(CoCreationHeading("我发布的地图"));
        var published = _coCreationPublishedHost = new StackPanel { Spacing = 18 };
        published.Children.Add(CreateCoCreationSkeleton());
        content.Children.Add(published);
        content.Children.Add(CoCreationHeading("我获取的地图"));
        var acquired = new StackPanel { Spacing = 18 };
        acquired.Children.Add(CreateCoCreationSkeleton());
        content.Children.Add(acquired);
        await Task.WhenAll(LoadPublishedAsync(), LoadCoCreationCommunityAsync(acquired, true, token));

        async Task LoadPublishedAsync()
        {
            try
            {
                var publications = await _communityService.GetOwnedAsync(token);
                if (!token.IsCancellationRequested && !_coCreationEditing && _coCreationSelectedClass is null)
                    RenderCoCreationPublications(published, publications);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (!token.IsCancellationRequested)
                {
                    IDVBuff.Diagnostics.OutputLog.Write("WARN", "MAP/COMMUNITY", "Published maps did not load.", exception);
                    ReplaceCoCreationGallery(published, CreateCoCreationDescription("已发布地图读取失败，请稍后重试。"));
                }
            }
        }
    }

    private async Task PrepareCoCreationSaveAsync()
    {
        if (!_coCreationMode || _draft is null) return;
        if (_draft.AcquisitionKind == MapAcquisitionKind.Subscription
            && !IsCoCreationOwner(_coCreationPackage?.Publication.PublisherHandle
                ?? _mapSubscriptionService.GetSubscriptions().FirstOrDefault(s => s.Id == _draft.SubscriptionId)?.PublisherHandle))
            throw new InvalidOperationException("账号已切换，请登录方案发布者账号后保存。");
        if (_draft.Id is not null) return;
        var snapshot = await _repository.GetCatalogSnapshotAsync();
        if (!snapshot.Classes.Contains(_draft.Class, StringComparer.OrdinalIgnoreCase))
            await _repository.CreateClassAsync(_draft.Class);
    }

    private async Task CloseCoCreationAsync()
    {
        _coCreationSelectedClass = null;
        _coCreationSourceDraft = null;
        _coCreationLoad?.Cancel();
        _coCreationLoad?.Dispose();
        _coCreationLoad = null;
        _coCreationPackage = null;
        _coCreationPublishedHost = null;
        var packages = _coCreationCloudPackages.Values.ToArray();
        _coCreationCloudPackages.Clear();
        foreach (var package in packages) await package.DisposeAsync();
    }

    private bool IsCoCreationOwner(string? handle) => !string.IsNullOrWhiteSpace(handle)
        && string.Equals(handle, _accountService.Identity?.PublisherHandle, StringComparison.OrdinalIgnoreCase);
    private void CoCreationAccountChanged(object? sender, EventArgs e)
    {
        _coCreationLoad?.Cancel();
        DispatcherQueue.TryEnqueue(async () =>
        {
            _coCreationSelectedClass = null;
            _coCreationLoad?.Cancel();
            if (IsLoaded && !_coCreationEditing) await ShowListAsync();
        });
    }
}
