using IDVBuff.Features.Accounts;
using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace IDVBuff.Views;
public sealed partial class MapListPage
{
    private sealed record CoCreationClassSelection(Guid PublicationId, string PublisherHandle,
        string Name, string LocalClassName, MapCoCreationPackage? Package = null, MapImportClassDraft? ImportedClass = null);

    private void RenderCoCreationPublications(StackPanel host, IReadOnlyList<MapCoCreationPublication> publications)
    {
        ReplaceCoCreationGallery(host, publications.Count == 0
            ? CreateCoCreationDescription("还没有发布地图，可以从“模板”开始共创。")
            : CreateCoCreationCards(publications.Select(publication => CreateCoCreationCard(publication.Name,
                CoCreationPublisherLabel(publication.PublisherHandle, publication.PublisherName), ResolveCoCreationCover(publication.CoverUrl),
                () => OpenCoCreationPublicationAsync(publication), opensClass: true, actionLabel: $"编辑已发布地图{publication.Name}"))));
    }

    private async Task OpenCoCreationPublicationAsync(MapCoCreationPublication publication)
    {
        var token = _coCreationLoad?.Token ?? new CancellationToken(true);
        if (!IsCoCreationOwner(publication.PublisherHandle)) throw new InvalidOperationException("账号已切换，请刷新作品列表。");
        if (!_coCreationCloudPackages.TryGetValue(publication.Id, out var package))
        {
            package = await MapCoCreationPackage.OpenAsync(publication, publication.PublisherHandle, _idvmPackageService, token);
            if (token.IsCancellationRequested || !IsCoCreationOwner(publication.PublisherHandle))
            {
                await package.DisposeAsync();
                return;
            }
            var installed = _mapSubscriptionService.GetSubscriptions().FirstOrDefault(p => p.Id == publication.Id);
            if (installed is not null)
            {
                package.Record.ClassBindings = new(installed.ClassBindings, StringComparer.OrdinalIgnoreCase);
                package.Record.CoCreationMapBindings = new(installed.CoCreationMapBindings);
                package.Record.InstalledMapIds = installed.InstalledMapIds.ToList();
            }
            _coCreationCloudPackages[publication.Id] = package;
        }
        var selections = package.Plan.Classes.Select(group => new CoCreationClassSelection(package.Record.Id,
            package.Publication.PublisherHandle!, group.SourceName,
            package.Record.ClassBindings.TryGetValue(group.SourceName, out var binding) ? binding : "共创地图 · " + group.SourceName,
            package, group)).ToArray();
        if (selections.Length == 1) { await OpenCoCreationClassAsync(selections[0]); return; }
        var content = new StackPanel { Spacing = 22 };
        var root = CreateCoCreationGalleryViewport(content);
        var back = CreateCoCreationToolbarButton("← 返回我的地图", "返回我的地图");
        back.Click += async (_, _) => await ShowListAsync();
        content.Children.Add(back);
        content.Children.Add(CreateTitle(publication.Name));
        content.Children.Add(CreateCoCreationCards(selections.Select(selection => CreateCoCreationCard(selection.Name,
            CoCreationPublisherLabel(publication.PublisherHandle, publication.PublisherName),
            selection.ImportedClass?.Maps.FirstOrDefault() is { } first ? CoCreationDraftPreview(first) : null,
            () => OpenCoCreationClassAsync(selection), opensClass: true, badgeText: $"{selection.ImportedClass?.Maps.Count ?? 0} 张地图"))));
        _workflowHost.Content = root;
    }

    private async Task OpenCoCreationClassAsync(CoCreationClassSelection selection)
    {
        if (!IsCoCreationOwner(selection.PublisherHandle))
            throw new InvalidOperationException("账号已切换，请刷新作品列表。");
        if (!await LoadCoCreationLocalClassAsync(selection)) return;
        if (!IsLoaded || !IsCoCreationOwner(selection.PublisherHandle)) return;
        _coCreationSelectedClass = selection;
        _coCreationPackage = selection.Package;
        ShowCoCreationClass(selection);
    }

    private async Task<bool> LoadCoCreationLocalClassAsync(CoCreationClassSelection selection)
    {
        var workflowRevision = _workflowRevision;
        var viewRevision = _coCreationViewRevision;
        var snapshot = await _repository.GetClassCatalogSnapshotAsync(selection.LocalClassName);
        if (!IsLoaded || workflowRevision != _workflowRevision || viewRevision != _coCreationViewRevision
            || !IsCoCreationOwner(selection.PublisherHandle)) return false;
        _loadedMaps = snapshot.Maps;
        _classes = snapshot.Classes;
        _classProperties = snapshot.ClassProperties;
        _variantGroups = snapshot.VariantGroups;
        _loadedCatalogRevision = _repository.GetCatalogRevision();
        return true;
    }

    private void ShowCoCreationClass(CoCreationClassSelection selection)
    {
        var content = new StackPanel { Spacing = 22 };
        var root = CreateCoCreationGalleryViewport(content);
        var back = CreateCoCreationToolbarButton("← 返回共创地图", "返回共创地图");
        back.HorizontalAlignment = HorizontalAlignment.Left;
        back.Click += async (_, _) =>
        {
            _coCreationSelectedClass = null;
            await ShowListAsync();
        };
        content.Children.Add(back);
        content.Children.Add(CreateTitle(selection.Name));
        var cards = new List<Button>();
        if (selection.ImportedClass is { } imported)
        {
            foreach (var draft in imported.Maps)
            {
                var saved = selection.Package is { } package
                    ? MapCoCreationRules.ResolveSavedMap(_loadedMaps, package.Record, draft.SourcePackageMapId) : null;
                cards.Add(CreateCoCreationCard(saved?.DisplayName ?? draft.Title, selection.Name,
                    saved is null ? CoCreationDraftPreview(draft) : GetMapPreviewPath(saved, MapScanFloorRules.ResolveScanFloorKey(saved)),
                    () => OpenCoCreationClassMapAsync(selection, saved, draft)));
            }
        }
        else
        {
            var owned = MapCoCreationRules.GetOwnedClasses(_loadedMaps, _mapSubscriptionService.GetSubscriptions(),
                AccountSession.Identity?.PublisherHandle);
            foreach (var map in owned.Where(c => c.Publication.Id == selection.PublicationId
                && string.Equals(c.Name, selection.LocalClassName, StringComparison.OrdinalIgnoreCase)).SelectMany(c => c.Maps))
                cards.Add(CreateCoCreationCard(map.DisplayName, selection.Name,
                    GetMapPreviewPath(map, MapScanFloorRules.ResolveScanFloorKey(map)),
                    () => OpenCoCreationClassMapAsync(selection, map, null)));
        }
        content.Children.Add(CreateCoCreationDescription($"{cards.Count} 张地图 · 选择地图进入编辑器"));
        content.Children.Add(CreateCoCreationCards(cards));
        _workflowHost.Content = root;
    }

    private async Task OpenCoCreationClassMapAsync(CoCreationClassSelection selection, MapRecord? saved, MapDraft? source)
    {
        if (!IsCoCreationOwner(selection.PublisherHandle))
            throw new InvalidOperationException("账号已切换，请刷新作品列表。");
        _coCreationPackage = selection.Package;
        ResetBatchImport();
        ResetBatchOperation();
        _coCreationSourceDraft = source;
        if (saved is not null)
        {
            _coCreationEditing = true;
            await EditMapAsync(saved);
            if (!_modernEditorActive) _coCreationEditing = false;
            return;
        }
        if (source is null || selection.Package is not { } package)
            throw new InvalidOperationException("地图方案已失效，请刷新作品列表。");
        source = MapCoCreationRules.CreateEditingDraft(source);
        source.Class = selection.LocalClassName;
        source.AcquisitionKind = MapAcquisitionKind.Subscription;
        source.SubscriptionId = selection.PublicationId;
        source.SubscriptionPublisherHandle = selection.PublisherHandle;
        source.SubscriptionPublisherKeyId = package.Record.PublisherKeyId;
        source.SubscriptionVersion = package.Publication.Version;
        source.SubscriptionPublisherIsOfficial = package.Publication.IsOfficialPublisher;
        source.SubscriptionPublisherIsBuilder = package.Publication.IsBuilderPublisher;
        _draft = source;
        _draft.Recognition.EnsureStandardAnchors();
        _activeFloorKey = _draft.Floors.OrderBy(f => f.SortOrder).First().Key;
        _activeAnchorId = null;
        _coCreationEditing = true;
        ShowMarkerEditor();
    }

    private static string? CoCreationDraftPreview(MapDraft draft)
    {
        var key = draft.Floors.OrderBy(f => f.SortOrder).FirstOrDefault()?.Key;
        if (key is not null && draft.FloorPreviewPaths.TryGetValue(key, out var preview)) return preview;
        if (key is not null && draft.FloorPaths.TryGetValue(key, out var source)) return source;
        return draft.FloorOnePath;
    }
}
