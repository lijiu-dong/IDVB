using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class MapListPage : UserControl
{
    private sealed record ClassPropertiesDialogState(ContentDialog Dialog, ToggleSwitch Toggle,
        Slider Intensity, TextBlock IntensityValue, ComboBox Downsample, ComboBox ScanFloor, TextBlock ClassName);
    private ClassPropertiesDialogState? _classPropertiesDialogState;
    private bool _classPropertiesDialogShowing;

    private async Task ShowClassPropertiesDialogAsync()
    {
        if (_isPackageOperation || _classPropertiesDialogShowing || string.IsNullOrWhiteSpace(_selectedClass))
            return;

        var current = _classProperties.TryGetValue(_selectedClass, out var value)
            ? value.Clone()
            : new MapClassProperties();
        var state = _classPropertiesDialogState;
        var toggle = state?.Toggle ?? new ToggleSwitch
        {
            Header = "去除背景",
            IsOn = current.RemoveBackground,
            OffContent = "关闭",
            OnContent = "开启",
            Foreground = FluentTheme.Brush(this, "TextFillColorPrimaryBrush")
        };
        toggle.IsOn = current.RemoveBackground;
        var intensityValue = state?.IntensityValue ?? new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Text = MapBackgroundProcessor.ClampBackgroundRemovalIntensity(
                current.BackgroundRemovalIntensity).ToString(),
            Foreground = FluentTheme.Brush(this, "TextFillColorSecondaryBrush")
        };
        intensityValue.Text = MapBackgroundProcessor.ClampBackgroundRemovalIntensity(current.BackgroundRemovalIntensity).ToString();
        var intensitySlider = state?.Intensity ?? new Slider
        {
            Header = "去除背景强度",
            Minimum = MapBackgroundProcessor.MinBackgroundRemovalIntensity,
            Maximum = MapBackgroundProcessor.MaxBackgroundRemovalIntensity,
            Value = MapBackgroundProcessor.ClampBackgroundRemovalIntensity(
                current.BackgroundRemovalIntensity),
            SmallChange = 1,
            StepFrequency = 1,
            IsEnabled = toggle.IsOn,
            MinWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        intensitySlider.Value = MapBackgroundProcessor.ClampBackgroundRemovalIntensity(current.BackgroundRemovalIntensity);
        intensitySlider.IsEnabled = toggle.IsOn;
        if (state is null)
        {
            intensitySlider.ValueChanged += (_, args) =>
                intensityValue.Text = MapBackgroundProcessor.ClampBackgroundRemovalIntensity((int)Math.Round(args.NewValue)).ToString();
            toggle.Toggled += (_, _) => intensitySlider.IsEnabled = toggle.IsOn;
        }
        var downsampleCombo = state?.Downsample ?? new ComboBox
        {
            Header = "地图图片降采样",
            MinWidth = 280,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (state is null)
        {
            downsampleCombo.Items.Add(new ComboBoxItem { Content = "0（不降低）", Tag = 0 });
            for (var factor = 2; factor <= 8; factor++)
                downsampleCombo.Items.Add(new ComboBoxItem { Content = $"降低 {factor} 倍", Tag = factor });
        }
        var currentDownsampleFactor = MapRepository.ClampImageDownsampleFactor(
            current.ImageDownsampleFactor);
        downsampleCombo.SelectedItem = downsampleCombo.Items
            .OfType<ComboBoxItem>()
            .First(item => (int)item.Tag == currentDownsampleFactor);
        var classMaps = _loadedMaps
            .Where(map => string.Equals(
                map.Class,
                _selectedClass,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var scanFloorCombo = state?.ScanFloor ?? new ComboBox
        {
            Header = "用于扫描的楼层",
            MinWidth = 280,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "请选择楼层 ID"
        };
        scanFloorCombo.Items.Clear();
        var configuredScanFloor = MapScanFloorRules.NormalizeFloorIdentity(
            current.ScanFloorKey);
        var compatibilityPrimaryFloor = classMaps.Length == 0
            ? null
            : MapScanFloorRules.NormalizeFloorIdentity(
                MapFloorRules.GetPrimaryFloorKey(classMaps[0]));
        foreach (var option in MapScanFloorRules.BuildOptions(classMaps))
        {
            var isDefaultPrimary = classMaps.All(map =>
                MapScanFloorRules.ResolveFloorKey(map, option.FloorIdentity)
                    is { } floorKey
                && MapScanFloorRules.IsPrimaryFloor(map, floorKey));
            var item = new ComboBoxItem
            {
                Content = option.IsEligible
                    ? $"{option.DisplayName}（{option.FloorIdentity}）· {(isDefaultPrimary ? "默认主楼层" : "其他楼层")}"
                    : $"{option.DisplayName}（{option.FloorIdentity}，不可用）",
                Tag = option.FloorIdentity,
                IsEnabled = option.IsEligible
            };
            if (!option.IsEligible)
                ToolTipService.SetToolTip(item, option.FailureReason);
            scanFloorCombo.Items.Add(item);
            if (string.Equals(
                    option.FloorIdentity,
                    configuredScanFloor,
                    StringComparison.Ordinal))
            {
                scanFloorCombo.SelectedItem = item;
            }
            else if (scanFloorCombo.SelectedItem is null
                && configuredScanFloor is null
                && option.IsEligible
                && string.Equals(
                    option.FloorIdentity,
                    compatibilityPrimaryFloor,
                    StringComparison.Ordinal))
            {
                scanFloorCombo.SelectedItem = item;
            }
        }
        scanFloorCombo.SelectedItem ??= scanFloorCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.IsEnabled);
        var className = state?.ClassName ?? new TextBlock
        {
            Text = $"地图类：{_selectedClass}",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        className.Text = $"地图类：{_selectedClass}";
        var content = state?.Dialog.Content as StackPanel ?? new StackPanel { Spacing = 12, MinWidth = 320 };
        if (state is null)
        {
            content.Children.Add(className);
            content.Children.Add(new TextBlock
            {
                Text = "该设置会重建此类的全部地图和楼层。人工遮瑕层始终保留。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = FluentTheme.Brush(this, "TextFillColorSecondaryBrush")
            });
            content.Children.Add(toggle);
            var intensityPanel = new Grid { ColumnSpacing = 12 };
            intensityPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            intensityPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            intensityPanel.Children.Add(intensitySlider);
            Grid.SetColumn(intensityValue, 1);
            intensityPanel.Children.Add(intensityValue);
            content.Children.Add(intensityPanel);
            content.Children.Add(new TextBlock
            {
                Text = "数值越高，越宽的颜色范围会被认定为背景。此强度仅保存在本机地图类设置中，不会写入 IDVM 数据包。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = FluentTheme.Brush(this, "TextFillColorSecondaryBrush")
            });
            content.Children.Add(downsampleCombo);
            content.Children.Add(new TextBlock
            {
                Text = "先缩小整张原图，再按映射后的归一化坐标裁剪。原图始终保留在本机，可随时切换为其他倍数或恢复为 0；原图与该设置不会写入 IDVM。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = FluentTheme.Brush(this, "TextFillColorSecondaryBrush")
            });
            content.Children.Add(scanFloorCombo);
            content.Children.Add(new TextBlock
            {
                Text = "默认主楼层使用大门与侧门；其他楼层必须标记可选的“次要门特征”。楼层 ID 忽略大小写，并且必须覆盖此地图类的每张地图。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = FluentTheme.Brush(this, "TextFillColorSecondaryBrush")
            });
            }
        var dialog = state?.Dialog ?? new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "地图类编辑",
            Content = content,
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = scanFloorCombo.SelectedItem
                is ComboBoxItem { IsEnabled: true }
        };
        dialog.XamlRoot = XamlRoot;
        dialog.IsPrimaryButtonEnabled = scanFloorCombo.SelectedItem is ComboBoxItem { IsEnabled: true };
        if (state is null)
        {
            scanFloorCombo.SelectionChanged += (_, _) =>
                dialog.IsPrimaryButtonEnabled = scanFloorCombo.SelectedItem is ComboBoxItem { IsEnabled: true };
            _classPropertiesDialogState = new(dialog, toggle, intensitySlider, intensityValue, downsampleCombo, scanFloorCombo, className);
            IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedObject(dialog, "UI.MapClassProperties.Dialog");
        }
        ContentDialogResult dialogResult;
        _classPropertiesDialogShowing = true;
        try { dialogResult = await dialog.ShowThemedAsync(this); }
        finally { _classPropertiesDialogShowing = false; }
        if (dialogResult != ContentDialogResult.Primary)
            return;

        var selectedScanFloor = MapScanFloorRules.NormalizeFloorIdentity(
            (scanFloorCombo.SelectedItem as ComboBoxItem)?.Tag as string);
        var removeBackground = toggle.IsOn;
        var removeBackgroundChanged = removeBackground != current.RemoveBackground;
        var backgroundRemovalIntensity = MapBackgroundProcessor.ClampBackgroundRemovalIntensity(
            (int)Math.Round(intensitySlider.Value));
        var backgroundRemovalIntensityChanged = backgroundRemovalIntensity
            != MapBackgroundProcessor.ClampBackgroundRemovalIntensity(
                current.BackgroundRemovalIntensity);
        var scanFloorChanged = !string.Equals(
            selectedScanFloor,
            configuredScanFloor,
            StringComparison.Ordinal);
        var downsampleFactor = (int)((downsampleCombo.SelectedItem as ComboBoxItem)?.Tag ?? 0);
        var downsampleChanged = downsampleFactor != currentDownsampleFactor;
        if (!removeBackgroundChanged && !backgroundRemovalIntensityChanged
            && !downsampleChanged && !scanFloorChanged)
            return;

        SetClassEditBusy(true);
        try
        {
            await RunMapOperationAsync(downsampleChanged ? "正在处理地图尺寸……" : "正在更新地图类……", async () =>
            {
                using (MapOperationProgress.Range(0, .9))
                {
                    if (downsampleChanged)
                    {
                        await Task.Run(() => _repository.SetClassImageDownsamplingAsync(
                            _selectedClass,
                            downsampleFactor,
                            removeBackground,
                            backgroundRemovalIntensity));
                    }
                    else if (removeBackgroundChanged || backgroundRemovalIntensityChanged)
                    {
                        await Task.Run(() => _repository.SetClassBackgroundRemovalAsync(
                            _selectedClass,
                            removeBackground,
                            backgroundRemovalIntensity));
                    }
                    if (scanFloorChanged)
                    {
                        await _repository.SetClassScanFloorAsync(
                            _selectedClass,
                            selectedScanFloor);
                    }
                }
                MapOperationProgress.Report(.9, "正在刷新地图……");
                _selectedMapIds.Clear();
                if (!App.IsSafeMode)
                    await MapOperationProgress.RunAsync(.9, 1, () => App.Session.RefreshMapCacheAsync());
                await ShowListAsync();
            });
        }
        catch (Exception exception)
        {
            var failure = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "地图类更新失败",
                Content = new TextBlock
                {
                    Text = exception.Message,
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = "关闭"
            };
            await failure.ShowThemedAsync(this);
        }
        finally
        {
            SetClassEditBusy(false);
        }
    }

    private void SetClassEditBusy(bool busy)
    {
        if (_classEditButton is not null)
        {
            _classEditButton.IsEnabled = !busy;
            _classEditButton.Content = busy ? "处理中…" : "地图类编辑";
        }
        if (_classComboBox is not null)
            _classComboBox.IsEnabled = !busy;
        if (_editButton is not null)
            _editButton.IsEnabled = !busy && HasSelection;
        if (_deleteButton is not null)
            _deleteButton.IsEnabled = !busy && HasSelection;
        if (_importButton is not null)
            _importButton.IsEnabled = !busy && !_isPackageOperation;
        if (_publishButton is not null)
            _publishButton.IsEnabled = !busy && !_isPackageOperation && _loadedMaps.Count > 0;
    }
}
