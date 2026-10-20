using IDVBuff.Core.Contracts.Online;
using IDVBuff.Features.Maps;
using IDVBuff.Features.Accounts;
using IDVBuff.UpdateCore;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private bool _packagePromptBusy;

    private TeachingTip CreatePublishTeachingTip(
        Button importButton,
        Button publishButton)
    {
        var exportPackage = CreateTeachingTipChoiceButton("导出地图包");
        var updating = GetOwnedPublication() is not null;
        var publishWebsite = CreateTeachingTipChoiceButton(updating ? "更新到官网" : "发布到官网");
        var editMetadata = updating ? CreateTeachingTipChoiceButton("修改社区资料") : null;
        var choices = new StackPanel { Spacing = 8 };
        choices.Children.Add(exportPackage);
        choices.Children.Add(publishWebsite);
        if (editMetadata is not null) choices.Children.Add(editMetadata);
        var tip = CreatePackageActionTeachingTip(
            publishButton,
            updating ? "更新" : "发布",
            updating ? "更新地图内容会上传地图包；只改名称或封面请选择“修改社区资料”。"
                : "请选择导出普通 IDVM 地图包，或生成官网签名发布目录。",
            choices);
        var choosing = false;

        exportPackage.Click += async (_, _) =>
        {
            if (choosing || _packagePromptBusy || _isPackageOperation) return;
            choosing = true;
            try
            {
                await CloseTeachingTipAsync(tip);
                await ShowExportIdvmDialogAsync(importButton, publishButton);
            }
            finally { choosing = false; }
        };
        publishWebsite.Click += async (_, _) =>
        {
            if (choosing || _packagePromptBusy || _isPackageOperation) return;
            choosing = true;
            try
            {
                await CloseTeachingTipAsync(tip);
                await ShowWebsitePublishDialogAsync(importButton, publishButton);
            }
            finally { choosing = false; }
        };
        if (editMetadata is not null) editMetadata.Click += async (_, _) =>
        {
            if (choosing || _packagePromptBusy || _isPackageOperation) return;
            choosing = true;
            try
            {
                await CloseTeachingTipAsync(tip);
                await ShowPublicationMetadataDialogAsync(importButton, publishButton);
            }
            finally { choosing = false; }
        };
        return tip;
    }

    private async Task ShowExportIdvmDialogAsync(
        Button importButton,
        Button publishButton)
    {
        if (_packagePromptBusy || _isPackageOperation) return;
        _packagePromptBusy = true;
        using var operation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("UI.Idvm.ExportPrompt", _selectedClass);
        var request = IDVBuff.Core.Diagnostics.MemoryJournal.Request("UI.Idvm.ExportPrompt", nativeModule: "Microsoft.UI.Xaml.dll");
        using var ownership = IDVBuff.Core.Diagnostics.MemoryJournal.Own("UI.Idvm.ExportPrompt",
            nativeModule: "Microsoft.UI.Xaml.dll", requestId: request);
        try { await ShowExportIdvmDialogCoreAsync(importButton, publishButton); }
        finally { _packagePromptBusy = false; }
    }

    private async Task ShowExportIdvmDialogCoreAsync(Button importButton, Button publishButton)
    {
        var currentCount = GetVisibleMaps().Count;
        var totalCount = _loadedMaps.Count;
        var choices = new StackPanel { Spacing = 8 };
        choices.Children.Add(new TextBlock
        {
            Text = $"当前地图类：{_selectedClass}（{currentCount} 张地图）",
            TextWrapping = TextWrapping.Wrap
        });
        choices.Children.Add(new TextBlock
        {
            Text = $"全部地图类：{totalCount} 张地图",
            TextWrapping = TextWrapping.Wrap
        });
        var compression = new ComboBox { Header = "地图包格式", HorizontalAlignment = HorizontalAlignment.Stretch };
        compression.Items.Add(new ComboBoxItem { Content = "最高无损压缩（需新版 Windows 客户端）", Tag = IdvmCompressionMode.Maximum });
        compression.Items.Add(new ComboBoxItem { Content = "兼容压缩（按原有格式导出）", Tag = IdvmCompressionMode.Compatible });
        compression.SelectedIndex = 0;
        choices.Children.Add(compression);
        choices.Children.Add(new TextBlock
        {
            Text = "两种格式均包含完整地图资源。最高压缩导出可能耗时较长，导入需支持 IDVM 1.6。",
            TextWrapping = TextWrapping.Wrap
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "选择导出范围",
            Content = choices,
            PrimaryButtonText = "当前地图类",
            SecondaryButtonText = "全部地图类",
            CloseButtonText = "取消",
            IsPrimaryButtonEnabled = currentCount > 0,
            IsSecondaryButtonEnabled = totalCount > 0,
            DefaultButton = currentCount > 0
                ? ContentDialogButton.Primary
                : ContentDialogButton.Secondary
        };
        var result = await dialog.ShowThemedAsync(this);
        if (result == ContentDialogResult.None)
            return;
        await ExportIdvmAsync(
            result == ContentDialogResult.Primary
                ? IdvmExportScope.CurrentClass
                : IdvmExportScope.AllClasses,
            importButton,
            publishButton,
            compression.SelectedItem is ComboBoxItem { Tag: IdvmCompressionMode mode } ? mode : IdvmCompressionMode.Maximum);
    }

    private async Task ShowWebsitePublishDialogAsync(
        Button importButton,
        Button publishButton)
    {
        if (_packagePromptBusy || _isPackageOperation) return;
        _packagePromptBusy = true;
        try { await ShowWebsitePublishDialogCoreAsync(importButton, publishButton); }
        finally { _packagePromptBusy = false; }
    }

    private async Task ShowWebsitePublishDialogCoreAsync(Button importButton, Button publishButton)
    {
        AccountIdentity identity;
        try
        {
            identity = await _accountService.RequirePublishAccessAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("需要登录", exception.Message);
            return;
        }
        var currentCount = GetVisibleMaps().Count;
        var previous = GetOwnedPublication();
        if (previous?.ClassBindings.Count > 1)
        {
            await ShowMessageAsync("无法从单个地图类更新", "这个已发布地图包包含多个地图类。为避免新版移除其他地图类，请勿从当前单个地图类覆盖发布。请将需要独立更新的地图类分别发布。");
            return;
        }
        var isUpdate = previous is not null;
        var packageNameText = previous?.PackageName ?? _selectedClass;
        var coverPath = previous?.CoverPath;
        if (coverPath is not null && _repository.IsGeneratedPublicationCover(coverPath)) coverPath = null;
        var selectedScope = currentCount > 0 ? IdvmExportScope.CurrentClass : IdvmExportScope.AllClasses;
        var validationMessage = string.Empty;
        while (true)
        {
            var publisher = new TextBox { Header = "发布者账号", Text = identity.DisplayName, IsReadOnly = true };
            var scope = new ComboBox { Header = "发布地图", HorizontalAlignment = HorizontalAlignment.Stretch };
            scope.Items.Add(new ComboBoxItem
            {
                Content = $"当前地图类：{_selectedClass}（{currentCount} 张）",
                Tag = IdvmExportScope.CurrentClass,
                IsEnabled = currentCount > 0
            });
            scope.Items.Add(new ComboBoxItem { Content = $"全部地图类（{_loadedMaps.Count} 张）", Tag = IdvmExportScope.AllClasses });
            scope.SelectedIndex = selectedScope == IdvmExportScope.CurrentClass ? 0 : 1;
            scope.IsEnabled = !isUpdate;
            var packageName = new TextBox { Header = "地图包名称", Text = packageNameText, MaxLength = 80 };
            var content = new StackPanel { Spacing = 10, MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Stretch };
            content.Children.Add(publisher);
            content.Children.Add(scope);
            content.Children.Add(packageName);
            content.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(coverPath)
                    ? "封面：自动使用主楼层预览图"
                    : $"封面：{Path.GetFileName(coverPath)}",
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrEmpty(validationMessage))
                content.Children.Add(new TextBlock { Text = validationMessage, TextWrapping = TextWrapping.Wrap,
                    Foreground = FluentTheme.Brush(this, "SystemFillColorCriticalBrush") });
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = isUpdate ? "更新 IDVB 官网地图包" : "发布到 IDVB 官网",
                Content = content,
                PrimaryButtonText = isUpdate ? "更新" : "发布",
                SecondaryButtonText = "选择封面",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            var result = await dialog.ShowThemedAsync(this);
            packageNameText = packageName.Text.Trim();
            if (scope.SelectedItem is ComboBoxItem { Tag: IdvmExportScope value }) selectedScope = value;
            if (result == ContentDialogResult.None) return;
            if (result == ContentDialogResult.Secondary)
            {
                await Task.Delay(250);
                coverPath = await PickPublicationCoverAsync() ?? coverPath;
                continue;
            }
            if (!string.IsNullOrWhiteSpace(packageNameText)) break;
            validationMessage = "请填写地图包名称。";
        }
        var usesAutomaticCover = string.IsNullOrWhiteSpace(coverPath);
        try { coverPath = ResolvePublicationCoverPath(coverPath, selectedScope); }
        catch (Exception exception) { await ShowMessageAsync("无法读取发布封面", exception.Message); return; }
        await PublishMapsAsync(
            isUpdate ? IdvmExportScope.CurrentClass : selectedScope,
            identity.DisplayName,
            intendedForWebsite: true,
            packageNameText, coverPath, usesAutomaticCover, previous?.Id,
            null,
            importButton,
            publishButton);
    }

    private static async Task CloseTeachingTipAsync(TeachingTip tip)
    {
        if (!tip.IsOpen)
            return;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Complete(TeachingTip sender, TeachingTipClosedEventArgs args)
        {
            sender.Closed -= Complete;
            completion.TrySetResult(true);
        }
        tip.Closed += Complete;
        tip.IsOpen = false;
        _ = await Task.WhenAny(completion.Task, Task.Delay(600));
        tip.Closed -= Complete;
    }

    private async Task ExportIdvmAsync(
        IdvmExportScope scope,
        Button importButton,
        Button exportButton,
        IdvmCompressionMode compressionMode)
    {
        var suggestedName = scope == IdvmExportScope.CurrentClass
            ? $"IDVB-{SanitizeFileName(_selectedClass)}-{DateTime.Now:yyyyMMdd-HHmmss}"
            : $"IDVB-All-{DateTime.Now:yyyyMMdd-HHmmss}";
        var destination = await PickIdvmDestinationAsync(suggestedName);
        if (destination is null)
            return;

        SetPackageOperationState(importButton, exportButton, isBusy: true, "正在导出…");
        try
        {
            await RunMapOperationAsync("正在打包地图……", () => Task.Run(() => _idvmPackageService.ExportAsync(
                scope,
                scope == IdvmExportScope.CurrentClass ? _selectedClass : null,
                destination,
                compressionMode: compressionMode)));
            await ShowMessageAsync("数据包导出完成", $"已保存到：\n{destination}");
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("数据包导出失败", exception.Message);
        }
        finally
        {
            SetPackageOperationState(importButton, exportButton, isBusy: false, null);
        }
    }

    private async Task PublishMapsAsync(
        IdvmExportScope scope,
        string publisherText,
        bool intendedForWebsite,
        string packageName,
        string coverPath,
        bool usesAutomaticCover,
        Guid? publicationId,
        byte[]? existingContentKey,
        Button importButton,
        Button exportButton)
    {
        var publisherDisplayName = publisherText.Trim();
        var publishingClass = scope == IdvmExportScope.CurrentClass ? _selectedClass : null;
        var isOfficialPublisher = false;
        var isBuilderPublisher = false;
        if (intendedForWebsite)
        {
            try
            {
                var identity = await _accountService.RequirePublishAccessAsync();
                publisherText = identity.PublisherHandle;
                publisherDisplayName = identity.DisplayName;
                isOfficialPublisher = identity.IsOfficial;
                isBuilderPublisher = identity.IsBuilder;
            }
            catch (Exception exception)
            {
                await ShowMessageAsync("需要登录", exception.Message);
                return;
            }
        }
        string publisherHandle;
        try
        {
            publisherHandle = MapSubscriptionProtocol.NormalizePublisherHandle(publisherText);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("发布者账号无效", exception.Message);
            return;
        }

        var outputDirectory = intendedForWebsite
            ? Path.Combine(AppDataPaths.RootDirectory, "MapPublishing", "WebsiteOutbox")
            : await PickPublicationFolderAsync();
        if (string.IsNullOrWhiteSpace(outputDirectory))
            return;

        SetPackageOperationState(importButton, exportButton, isBusy: true, "正在发布…");
        using var publicationProtection = AppDataPaths.ProtectCachePath(outputDirectory);
        string? uploadedLink = null;
        try
        {
            var subscriptionLink = await RunMapOperationAsync(publicationId is null ? "正在发布地图……" : "正在更新已发布地图……", async () =>
            {
                var publication = await MapOperationProgress.RunAsync(0, .6, () => Task.Run(() => _mapPublicationService.PublishAsync(
                    scope,
                    publishingClass,
                    outputDirectory,
                    publisherHandle,
                    publisherDisplayName,
                    isOfficialPublisher,
                    isBuilderPublisher,
                    intendedForWebsite,
                    packageName,
                    coverPath,
                    publicationId,
                    existingContentKey,
                    usesAutomaticCover: usesAutomaticCover)));
                MapOperationProgress.Report(.6, "正在上传地图……");
                var linkText = intendedForWebsite
                    ? await MapOperationProgress.RunAsync(.6, .9, () => _communityService.PublishAsync(new(
                        publication.PublicationId, publication.PublisherHandle, publication.SubscriptionLink,
                        publication.OutputDirectory, publication.PackagePath, publication.PackageName,
                        publication.CoverPath), CancellationToken.None))
                    : publication.SubscriptionLink;
                if (intendedForWebsite) uploadedLink = linkText;
                if (intendedForWebsite)
                {
                    var publishedMaps = publication.ExportedMaps;
                    MapOperationProgress.Report(.92, "正在登记发布结果……");
                    var link = MapSubscriptionLink.Parse(publication.SubscriptionLink);
                    await _repository.MarkPublishedMapsAsSubscriptionAsync(
                        publishedMaps.Select(map => map.Id).ToArray(), publication.PublicationId,
                        publication.PublisherDisplayName, link.PublisherKeyId, publication.Version,
                        publication.IsOfficialPublisher, publication.IsBuilderPublisher);
                    await _mapSubscriptionService.RegisterPublishedAsync(publication, linkText, publishedMaps);
                    await ShowListAsync();
                }
                return linkText;
            });
            var data = new DataPackage();
            data.SetText(subscriptionLink);
            Clipboard.SetContent(data);
            var publisherName = intendedForWebsite
                ? _accountService.Identity?.DisplayName ?? publisherHandle
                : publisherHandle;
            var targetText = intendedForWebsite
                ? "已发布到 IDVB 官网"
                : "已生成本地更新订阅";
            await ShowMessageAsync(
                publicationId is null ? "地图发布完成" : "地图包更新完成",
                $"{targetText}\n发布者：{publisherName}\n订阅链接已复制到剪贴板。");
        }
        catch (Exception exception)
        {
            IDVBuff.Diagnostics.OutputLog.Write("WARN", "MAP/PUBLISH", "Map publication did not complete.", exception);
            await ShowMessageAsync(uploadedLink is null ? "地图发布失败" : "地图已发布，本地登记未完成",
                exception.Message + (uploadedLink is null ? string.Empty : $"\n请保留以下订阅链接，勿重复新建发布：\n{uploadedLink}"));
        }
        finally
        {
            SetPackageOperationState(importButton, exportButton, isBusy: false, null);
        }
    }

    private MapSubscriptionRecord? GetOwnedPublication()
    {
        var maps = GetVisibleMaps();
        var ids = maps.Where(map => map.AcquisitionKind == MapAcquisitionKind.Subscription)
            .Select(map => map.SubscriptionId)
            .Distinct()
            .ToArray();
        if (ids.Length != 1 || ids[0] is not { } id) return null;
        var identity = _accountService.Identity;
        return identity is null ? null : _mapSubscriptionService.GetSubscriptions()
            .SingleOrDefault(record => record.Id == id
                && MapPublicationRules.CanUpdate(maps, record, identity.PublisherHandle));
    }

    private string GetWebsiteActionText() => GetOwnedPublication() is null ? "发布" : "更新";

    private string ResolvePublicationCoverPath(string? selectedPath, IdvmExportScope scope)
    {
        if (!string.IsNullOrWhiteSpace(selectedPath) && File.Exists(selectedPath)) return selectedPath;
        var map = (scope == IdvmExportScope.CurrentClass ? GetVisibleMaps() : _loadedMaps)
            .OrderBy(item => item.SequenceNumber).FirstOrDefault()
            ?? throw new InvalidOperationException("当前地图类没有可用于封面的地图。");
        var floor = MapFloorRules.GetOrderedFloors(map).First();
        var preview = _repository.GetFloorThumbnailPath(map, floor.Key);
        if (!File.Exists(preview)) throw new FileNotFoundException("找不到主楼层处理后的预览图。", preview);
        return preview;
    }

    private async Task<string?> PickIdvmDestinationAsync(string suggestedName)
    {
#if IDVB_DEVELOPMENT_BUILD
        if (_developmentExportDestination is { } replayDestination)
        {
            _developmentExportDestination = null;
            return ValidateDevelopmentPackagePath(replayDestination, mustExist: false);
        }
#endif
        try
        {
            var picker = new FileSavePicker(((App)Application.Current).MainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedName,
                DefaultFileExtension = ".idvm",
                CommitButtonText = "导出",
                FileTypeChoices =
                {
                    { "IDVM 地图数据包", new List<string> { ".idvm" } }
                }
            };
            var result = await picker.PickSaveFileAsync();
            if (result is null || string.IsNullOrWhiteSpace(result.Path))
                return null;
            return Path.GetExtension(result.Path).Equals(".idvm", StringComparison.OrdinalIgnoreCase)
                ? result.Path
                : result.Path + ".idvm";
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法打开保存选择器", exception.Message);
            return null;
        }
    }
}
