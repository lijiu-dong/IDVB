using IDVBuff.Features.Maps;
using IDVBuff.Features.Accounts;
using IDVBuff.Survey.Domain;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Windows.Storage.Pickers;
using System.Numerics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapListPage : UserControl
{
    private Border CreateMapCard(MapRecord map)
    {
        var card = new Border
        {
            Tag = map,
            Margin = new Thickness(11),
            Padding = new Thickness(11),
            Background = new SolidColorBrush(),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(9)
        };
        _cardBorders[map.Id] = card;
        card.Loaded += (_, _) =>
        {
            if (card.Tag is not MapRecord) return;
            IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(card);
            IDVBuff.Core.Diagnostics.MemoryJournal.TrackResource(card, "MapList.VisibleCard", null, "Microsoft.UI.Xaml.dll");
        };
        card.Unloaded += (_, _) => IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(card);
        AttachCardInteractionFeedback(card);
        AttachHoldPreview(card);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var previews = new Grid { Margin = new Thickness(11, 0, 11, 0), Height = 160 };
        previews.SizeChanged += (_, _) =>
        {
            if (previews.ActualWidth > 0)
                previews.Height = Math.Round(previews.ActualWidth / 1.6);
        };
        var scanFloorKey = MapScanFloorRules.ResolveScanFloorKey(map);
        previews.Children.Add(CreatePreviewLayer(
            GetMapPreviewPath(map, scanFloorKey),
            new Thickness(0)));
        previews.Children.Add(CreateMapOriginBadge(map));
        content.Children.Add(previews);

        var label = new TextBlock
        {
            Text = map.DisplayName,
            Foreground = new SolidColorBrush(),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };
        Grid.SetRow(label, 1);
        content.Children.Add(label);
        var readiness = new TextBlock
        {
            Text = BuildRecognitionSummary(map),
            FontSize = 11,
            Foreground = new SolidColorBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        };
        Grid.SetRow(readiness, 2);
        content.Children.Add(readiness);
        var variantLabel = new TextBlock
        {
            Tag = "variant-group",
            Foreground = new SolidColorBrush(),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        };
        Grid.SetRow(variantLabel, 3);
        content.Children.Add(variantLabel);
        card.Child = content;

        card.Tapped += (_, _) => { if (card.Tag is MapRecord current) SelectMap(current); };
        card.DoubleTapped += async (_, _) =>
        {
            if (card.Tag is not MapRecord current) return;
            SelectMap(current);
            await ImportMapAsync(current);
        };
        if (App.IsSafeMode)
        {
            var flyout = new MenuFlyout();
            var showNow = new MenuFlyoutItem { Text = "立刻展示" };
            showNow.Click += async (_, _) => { if (card.Tag is MapRecord current) await ShowMapImmediatelyAsync(current); };
            flyout.Items.Add(showNow);
            card.ContextFlyout = flyout;
        }
        return card;
    }

    private Border CreateMapOriginBadge(MapRecord map)
    {
        var badge = new Border
        {
            Background = new SolidColorBrush(), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(7),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)), FontSize = 11 }
        };
        BindMapOriginBadge(badge, map);
        return badge;
    }

    private void BindMapOriginBadge(Border badge, MapRecord map)
    {
        var label = map.AcquisitionKind switch
        {
            MapAcquisitionKind.ImportedPackage => "导入",
            MapAcquisitionKind.Subscription => "订阅",
            _ when string.Equals(map.Source, "survey", StringComparison.Ordinal) => "本地 · 测绘",
            _ => "本地"
        };
        var name = map.SubscriptionPublisherHandle;
        if (name?.StartsWith("@u_", StringComparison.OrdinalIgnoreCase) == true
            && string.Equals(name, _accountService.Identity?.PublisherHandle, StringComparison.OrdinalIgnoreCase))
            name = _accountService.Identity!.DisplayName;
        else if (string.IsNullOrWhiteSpace(name) || name.StartsWith("@u_", StringComparison.OrdinalIgnoreCase))
            name = "未知作者";
        if (map.AcquisitionKind == MapAcquisitionKind.Subscription)
            label += " · @" + name.TrimStart('@');
        var background = map.SubscriptionPublisherIsBuilder
            ? Color.FromArgb(225, 233, 133, 34)
            : map.SubscriptionPublisherIsOfficial
                ? Color.FromArgb(225, 22, 135, 255)
                : Color.FromArgb(205, 22, 29, 38);
        ((TextBlock)badge.Child).Text = label;
        var brush = (SolidColorBrush)badge.Background;
        if (brush.Color != background) brush.Color = background;
        ToolTipService.SetToolTip(badge, map.AcquisitionKind == MapAcquisitionKind.Subscription
            ? $"订阅版本：{map.SubscriptionVersion ?? "未知"}" : null);
    }


    private async Task ShowMapImmediatelyAsync(MapRecord map)
    {
        var floorKey = MapScanFloorRules.ResolveScanFloorKey(map);
        if (MapFloorRules.GetFloorProfile(map, floorKey) is null)
            return;
        var path = _repository.GetFloorOverlayPath(map, floorKey);
        if (!File.Exists(path))
            path = _repository.GetFloorRecognitionPath(map, floorKey);
        if (!File.Exists(path))
        {
            await ShowMessageAsync("无法展示地图", "没有找到该地图的楼层图片。");
            return;
        }
        await DirectMapDisplayWindow.ShowAsync(map, _repository, floorKey);
    }

    private Border CreatePreviewLayer(string path, Thickness margin)
    {
        var border = new Border
        {
            Margin = margin,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Black),
            CornerRadius = new CornerRadius(6)
        };
        if (File.Exists(path))
        {
            var image = new Image
            {
                Stretch = Stretch.UniformToFill,
                Tag = path
            };
            image.Loaded += (_, _) =>
            {
                if (image.Source is null && image.Tag is string current && File.Exists(current))
                    BindMapPreview(image, current);
            };
            image.Unloaded += (_, _) =>
            {
                ReleaseMapPreview(image);
            };
            border.Child = image;
        }
        return border;
    }

    private string GetMapPreviewPath(MapRecord map, string floorKey)
    {
        if (MapFloorRules.GetOrderedFloors(map).All(floor => !string.Equals(floor.Key, floorKey, StringComparison.Ordinal)))
            return string.Empty;
        var thumbnailPath = _repository.GetFloorThumbnailPath(map, floorKey);
        return File.Exists(thumbnailPath)
            ? thumbnailPath
            : _repository.GetFloorRecognitionPath(map, floorKey);
    }

    private BitmapImage GetPreviewBitmap(string path)
    {
        if (_previewImages.TryGetValue(path, out var bitmap))
            return bitmap;
        bitmap = CreatePreviewBitmap();
        ConfigurePreviewBitmap(bitmap, path);
        return bitmap;
    }

    private static BitmapImage CreatePreviewBitmap()
    {
        var bitmap = new BitmapImage { DecodePixelWidth = 400, CreateOptions = BitmapCreateOptions.IgnoreImageCache };
        IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedObject(bitmap, "MapList.PreviewWrapper");
        // No captured page, image or prior path. One handler per reusable source.
        bitmap.ImageOpened += static (sender, _) =>
        {
            if (sender is BitmapImage decoded) IDVBuff.Core.Diagnostics.MemoryJournal.Current?.Record("preview-decoded",
                new { path = decoded.UriSource?.LocalPath, decoded.PixelWidth, decoded.PixelHeight, decoded.DecodePixelWidth,
                    meaning = "source dimensions and requested decode width; not measured native heap payload" });
        };
        return bitmap;
    }

    private void ConfigurePreviewBitmap(BitmapImage bitmap, string path)
    {
        var request = IDVBuff.Core.Diagnostics.MemoryJournal.Request("MapList.Preview/" + path,
            nativeModule: "Microsoft.UI.Xaml.dll");
        IDVBuff.Core.Diagnostics.MemoryJournal.TrackResource(bitmap, "MapList.Preview/" + path,
            null, "Microsoft.UI.Xaml.dll", request);
        bitmap.UriSource = new Uri(path);
        _previewImages[path] = bitmap;
    }

    private void BindMapPreview(Image image, string path)
    {
        ReleaseMapPreview(image);
        // The bitmap wrapper follows the pooled Image. Rebinding a class clears
        // its decoded surface but does not manufacture another native wrapper.
        var bitmap = _mapPreviewBitmaps.GetValue(image, static _ => CreatePreviewBitmap());
        ConfigurePreviewBitmap(bitmap, path);
        image.Source = bitmap;
        _activeMapPreviewImages.Add(image);
    }

    private void ReleaseMapPreview(Image image)
    {
        _activeMapPreviewImages.Remove(image);
        if (image.Source is not BitmapImage bitmap) return;
        if (bitmap.UriSource?.LocalPath is string path && _previewImages.TryGetValue(path, out var cached)
            && ReferenceEquals(cached, bitmap)) _previewImages.Remove(path);
        bitmap.UriSource = null;
        image.Source = null;
        IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(bitmap);
    }

    private void ReleaseMapCardPreviews()
    {
        CloseHoldPreviewImmediately();
        // Weak-table enumeration can expose finalized WinRT projections. Only
        // mounted consumers own a live Image; pooled sources already have no URI.
        foreach (var image in _activeMapPreviewImages.ToArray()) ReleaseMapPreview(image);
        foreach (var bitmap in _previewImages.Values)
        {
            bitmap.UriSource = null;
            IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(bitmap);
        }
        _previewImages.Clear();
    }

    internal void RetireImageConsumers() => ReleaseMapCardPreviews();

    private void SelectMap(MapRecord map)
    {
        var isCtrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        var isShift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

        if (isCtrl)
        {
            // Ctrl+Click: toggle selection
            if (_selectedMapIds.Contains(map.Id))
                _selectedMapIds.Remove(map.Id);
            else
                _selectedMapIds.Add(map.Id);
            _lastClickedMapId = map.Id;
        }
        else if (isShift && _lastClickedMapId is { } lastId)
        {
            // Shift+Click: range select from last clicked to this item
            var orderedIds = GetVisibleMaps().Select(m => m.Id).ToList();
            var lastIndex = orderedIds.IndexOf(lastId);
            var currentIndex = orderedIds.IndexOf(map.Id);
            if (lastIndex >= 0 && currentIndex >= 0)
            {
                var start = Math.Min(lastIndex, currentIndex);
                var end = Math.Max(lastIndex, currentIndex);
                for (var i = start; i <= end; i++)
                    _selectedMapIds.Add(orderedIds[i]);
            }
            // Don't update _lastClickedMapId on shift-click to allow extending range
        }
        else
        {
            // Plain click: single select
            _selectedMapIds = [map.Id];
            _lastClickedMapId = map.Id;
        }

        UpdateSelectedCardVisuals();
    }

    private async Task ToggleSelectedVariantGroupAsync()
    {
        var selectedMapIds = _selectedMapIds.ToArray();
        var className = _selectedClass;
        if (selectedMapIds.Length < 2)
        {
            await ShowMessageAsync("无法绑定或解绑变体", "请至少选择两张地图来绑定或解绑变体。");
            return;
        }
        try
        {
            await RunMapOperationAsync("正在更新变体绑定……", async () =>
            {
                MapOperationProgress.Report(.1, "正在更新变体绑定……");
                await Task.Run(() => _repository.ToggleVariantGroupAsync(className, selectedMapIds));
                MapOperationProgress.Report(.9, "正在刷新地图……");
                if (!App.IsSafeMode)
                    await MapOperationProgress.RunAsync(.9, 1, () => App.Session.RefreshMapCacheAsync());
                await ShowListAsync();
            });
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法绑定或解绑变体", exception.Message);
        }
    }

    private async Task EditMapAsync(MapRecord map)
    {
        if (_editMapPending || _modernEditorActive) return;
        _editMapPending = true;
        using var editOperation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Editor.Open", map.Id.ToString());
        var workflowRevision = ++_workflowRevision;
        try
        {
            var draft = await _repository.CreateDraftFromSnapshotAsync(map);
            if (!IsWorkflowCurrent(workflowRevision, "edit-draft")) return;
            if (draft is null)
            {
                await ShowMessageAsync("地图不存在", "该地图已被删除，请刷新列表。");
                await ShowListAsync();
                return;
            }
            _activeFloorKey = "1f";
            _activeAnchorId = null;
            if (!IsBatchOperation)
                ResetBatchImport();
            _draft = draft;
            RecordModernEditorMemory("draft-loaded");
            _draft.Recognition.EnsureStandardAnchors();
            ShowMarkerEditor();
            RecordModernEditorMemory("editor-created");
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法编辑地图", exception.Message);
        }
        finally { _editMapPending = false; }
    }

    private async Task DeleteSelectedMapAsync(MapRecord map)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除地图？",
            Content = $"将永久删除 {map.DisplayName} 及其两张图片和识别标记数据。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowThemedAsync(this) != ContentDialogResult.Primary)
            return;

        try
        {
            await RunMapOperationAsync("正在删除地图……", async () =>
            {
                await Task.Run(() => _repository.DeleteAsync(map.Id));
                MapOperationProgress.Report(.9, "正在刷新地图……");
                if (!App.IsSafeMode)
                    await MapOperationProgress.RunAsync(.9, 1, () => App.Session.RefreshMapCacheAsync(map.Id));
                _selectedMapIds.Remove(map.Id);
                if (_lastClickedMapId == map.Id)
                    _lastClickedMapId = null;
                await ShowListAsync();
            });
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("删除失败", exception.Message);
        }
    }
}
