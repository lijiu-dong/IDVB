using IDVBuff.Features.Maps;
using IDVBuff.Appearance;
using IDVBuff.Presentation.Theming;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapListPage : UserControl
{

    private static double CalculateModernAnnotationFontSize(MapAnnotation annotation, double pixelWidth, double pixelHeight)
    {
        var requested = annotation.FontSize ?? CalculateFittingFontSize(annotation.Text ?? string.Empty, pixelWidth, pixelHeight);
        var fitting = CalculateFittingFontSize(annotation.Text ?? string.Empty, pixelWidth, pixelHeight);
        return Math.Min(requested, fitting);
    }

    private void ShowModernTextProperties(Button placementTarget)
    {
        var defaults = _editorPreferenceState.TextDefaults;
        var panel = new StackPanel { Spacing = 10, Width = 330 };
        panel.Children.Add(new TextBlock
        {
            Text = "文字属性",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = EditorText
        });

        panel.Children.Add(new TextBlock { Text = "字号", Foreground = EditorMuted });
        var sizes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (label, size) in new[] { ("小", 12d), ("中", 16d), ("大", 20d), ("特大", 24d) })
        {
            var selectedSize = size;
            var button = new ToggleButton
            {
                Content = label,
                IsChecked = Math.Abs(defaults.FontSize - size) < .001d,
                MinWidth = 54,
                Foreground = EditorText
            };
            BindModernToggleTheme(button);
            button.Click += async (_, _) =>
            {
                defaults.FontSize = selectedSize;
                await SaveEditorPreferencesAsync();
            };
            sizes.Children.Add(button);
        }
        panel.Children.Add(sizes);

        panel.Children.Add(new TextBlock { Text = "字体", Foreground = EditorMuted });
        var installedFonts = GetModernInstalledFontFamilies();
        var fontPicker = new AutoSuggestBox
        {
            PlaceholderText = "系统默认",
            Text = defaults.FontFamily,
            ItemsSource = installedFonts
        };
        fontPicker.TextChanged += async (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                return;
            defaults.FontFamily = fontPicker.Text.Trim();
            fontPicker.ItemsSource = installedFonts
                .Where(name => name.Contains(fontPicker.Text, StringComparison.CurrentCultureIgnoreCase))
                .Take(50)
                .ToArray();
            await SaveEditorPreferencesAsync();
        };
        fontPicker.SuggestionChosen += async (_, args) =>
        {
            defaults.FontFamily = args.SelectedItem?.ToString() ?? string.Empty;
            fontPicker.Text = defaults.FontFamily;
            await SaveEditorPreferencesAsync();
        };
        panel.Children.Add(fontPicker);

        panel.Children.Add(CreateModernTextStyleToggle("粗体", () => defaults.IsBold,
            value => defaults.IsBold = value));
        panel.Children.Add(CreateModernTextStyleToggle("斜体", () => defaults.IsItalic,
            value => defaults.IsItalic = value));
        panel.Children.Add(CreateModernTextStyleToggle("删除线", () => defaults.IsStrikethrough,
            value => defaults.IsStrikethrough = value));

        new Flyout { Content = panel, Placement = FlyoutPlacementMode.RightEdgeAlignedTop }.ShowAt(placementTarget);
    }

    private ToggleSwitch CreateModernTextStyleToggle(string label, Func<bool> getValue, Action<bool> setValue)
    {
        var toggle = new ToggleSwitch
        {
            Header = label,
            IsOn = getValue(),
            Foreground = EditorText
        };
        toggle.Toggled += async (_, _) =>
        {
            setValue(toggle.IsOn);
            await SaveEditorPreferencesAsync();
        };
        return toggle;
    }

    private void ShowModernLineProperties(Button placementTarget)
    {
        var defaults = _editorPreferenceState.LineDefaults;
        var panel = new StackPanel { Spacing = 10, Width = 300 };
        panel.Children.Add(new TextBlock
        {
            Text = "直线属性",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = EditorText
        });
        var mode = new ComboBox
        {
            Header = "绘制方式",
            ItemsSource = new[] { "自由直线", "连续直线" },
            SelectedIndex = defaults.Mode == MapEditorLineMode.Continuous ? 1 : 0
        };
        mode.SelectionChanged += async (_, _) =>
        {
            defaults.Mode = mode.SelectedIndex == 1 ? MapEditorLineMode.Continuous : MapEditorLineMode.Free;
            EndModernContinuousLine();
            await SaveEditorPreferencesAsync();
        };
        panel.Children.Add(mode);
        var axis = new ToggleSwitch
        {
            Header = "轴向约束",
            IsOn = defaults.AxisConstraintEnabled,
            Foreground = EditorText
        };
        var diagonal = new ToggleSwitch
        {
            Header = "允许 45°",
            IsOn = defaults.AllowDiagonalConstraint,
            IsEnabled = defaults.AxisConstraintEnabled,
            Foreground = EditorText
        };
        axis.Toggled += async (_, _) =>
        {
            defaults.AxisConstraintEnabled = axis.IsOn;
            if (!axis.IsOn)
                defaults.AllowDiagonalConstraint = false;
            diagonal.IsOn = defaults.AllowDiagonalConstraint;
            diagonal.IsEnabled = axis.IsOn;
            await SaveEditorPreferencesAsync();
        };
        diagonal.Toggled += async (_, _) =>
        {
            defaults.AllowDiagonalConstraint = diagonal.IsOn && axis.IsOn;
            await SaveEditorPreferencesAsync();
        };
        panel.Children.Add(axis);
        panel.Children.Add(diagonal);
        new Flyout { Content = panel, Placement = FlyoutPlacementMode.RightEdgeAlignedTop }.ShowAt(placementTarget);
    }

    private static IReadOnlyList<string> GetModernInstalledFontFamilies()
    {
        try
        {
            return System.Drawing.FontFamily.Families
                .Select(family => family.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or ArgumentException)
        {
            return [];
        }
    }

    private static string ModernToolHint(MapEditorTool tool) => tool switch
    {
        MapEditorTool.Select => "选择并调整画布元素。",
        MapEditorTool.Text => "拖出文字区域。",
        MapEditorTool.Marker => "点击放置标记图案；再次点击标记工具可更换图案。",
        MapEditorTool.Line => "拖动创建一条有方向的直线。",
        MapEditorTool.AutomaticRoute => "点击阶段起点和终点；靠近已有端点或虚线预览通路时自动吸附，Esc 取消起点。",
        MapEditorTool.Rectangle => "拖动创建无填充矩形。",
        MapEditorTool.Crop => "拖出当前楼层的识别区域。",
        MapEditorTool.FreeCrop => "按住鼠标自由框选任意形状的识别区域。",
        MapEditorTool.Anchor => "连续拖动创建锚点。",
        MapEditorTool.Conceal => "拖动涂抹背景；再次点击可调整笔头。",
        MapEditorTool.Pan => "拖动画布视图。",
        _ => string.Empty
    };

    private void RefreshModernToolVisuals()
    {
        _modernToolState.AutomaticRouteAvailable = CanUseModernAutomaticRoute();
        foreach (var (tool, button) in _editorToolButtons)
        {
            var isSelected = tool == _modernToolState.ActiveTool;
            ThemeButton.Apply(button, isSelected ? ThemeButtonRole.Accent : ThemeButtonRole.Standard);
            button.BorderThickness = new Thickness(isSelected ? 1 : 0);
            button.IsEnabled = _modernToolState.CanUse(tool);
        }
    }

    private void SwitchModernFloor(string floorKey, bool fitWhenLoaded)
    {
        if (_draft is null || !_draft.Floors.Any(floor => string.Equals(floor.Key, floorKey, StringComparison.OrdinalIgnoreCase)))
            return;
        using var floorOperation = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Editor.Floor", $"{_draft.Id}/{floorKey}");
        RecordModernEditorMemory("floor-begin");
        var viewRevision = ++_modernViewRevision;
        ResetModernAutomaticRoute();
        CancelModernInteraction(restoreGeometry: true);
        EndModernContinuousLine();
        _modernToolState.Reset();
        _activeFloorKey = floorKey;
        _modernToolState.ActiveFloorKey = floorKey;
        _modernSelection = null;
        _modernSelectedAnnotationIds.Clear();
        _modernAnnotationSelectionAnchorId = null;
        _activeAnchorId = null;
        RefreshModernToolVisuals();

        if (_modernEditorHeader is not null)
        {
            foreach (var button in FindDescendants<Button>(_modernEditorHeader).Where(button => button.Tag is string))
            {
                var selected = string.Equals(button.Tag as string, floorKey, StringComparison.OrdinalIgnoreCase);
                ThemeButton.Apply(button, selected ? ThemeButtonRole.Accent : ThemeButtonRole.Standard);
            }
        }

        var imagePath = _draft.AppearancePalette is null ? GetActiveFloorImagePath()
            : _repository.GetDraftAppearanceImagePath(_draft, _activeFloorKey);
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            UpdateModernFloorResolution(0, 0);
            return;
        }
        if (!_modernFloorBitmaps.TryGetValue(floorKey, out var entry))
        {
            ReleaseModernFloorBitmaps(floorKey);
            var (sourceWidth, sourceHeight) = MapImageDimensions.Read(imagePath);
            RecordModernEditorMemory("metadata-read", imagePath);
            var resource = $"ModernEditor.Floor/{_draft.Id}/{floorKey}/{imagePath}";
            var request = IDVBuff.Core.Diagnostics.MemoryJournal.Request(resource, nativeModule: "Microsoft.UI.Xaml.dll");
            entry = new ModernFloorBitmap(sourceWidth, sourceHeight);
            IDVBuff.Core.Diagnostics.MemoryJournal.TrackResource(entry.Source, resource, null, "Microsoft.UI.Xaml.dll", request);
            IDVBuff.Core.Diagnostics.MemoryJournal.ObserveManagedObject(entry.Source, resource);
            _modernFloorBitmaps[floorKey] = entry;
            _ = LoadModernFloorBitmapAsync(entry, imagePath, floorKey, fitWhenLoaded, viewRevision);
        }
        _modernBitmap = entry;
        if (_modernImage is not null)
        {
            _modernImage.Source = entry.Source;
            _modernImage.Visibility = IsModernItemVisible("image", "image") ? Visibility.Visible : Visibility.Collapsed;
        }
        SetModernStatus($"正在编辑 {GetModernFloorDisplayName(floorKey)}。", false);
        UpdateModernFloorResolution(entry.SourceWidth, entry.SourceHeight);
        RefreshModernLayerList();
        // Annotation state belongs to the selected floor, independently of the
        // asynchronous image decode. Cached equal-size floors may not emit a
        // new ImageOpened event before the viewport reports itself settled.
        if (_modernScene is not null && _modernCanvas is not null)
        {
            _modernScene.Width = _modernCanvas.Width = entry.SourceWidth;
            _modernScene.Height = _modernCanvas.Height = entry.SourceHeight;
            RenderModernEditor();
        }
        ApplyModernFloorBitmap(
            _modernBitmap,
            entry.SourceWidth,
            entry.SourceHeight,
            fitWhenLoaded, viewRevision);
        RecordModernEditorMemory("floor-bound", imagePath);
    }

    private void ApplyModernFloorBitmap(
        ModernFloorBitmap bitmap,
        int sourceWidth,
        int sourceHeight,
        bool fitWhenLoaded, long viewRevision)
    {
        if (!ReferenceEquals(_modernBitmap, bitmap)
            || _modernScene is null
            || _modernCanvas is null
            || bitmap.PixelWidth <= 0
            || bitmap.PixelHeight <= 0)
        {
            return;
        }
        UpdateModernFloorResolution(sourceWidth, sourceHeight);
        _modernScene.Width = sourceWidth;
        _modernScene.Height = sourceHeight;
        _modernCanvas.Width = sourceWidth;
        _modernCanvas.Height = sourceHeight;
        // Floor metadata establishes the annotation canvas synchronously in
        // SwitchModernFloor. Decoding only supplies the background pixels;
        // rebuilding the same annotations here creates another native scene
        // without changing any geometry. Zoom fitting owns stroke updates.
        if (fitWhenLoaded)
            QueueAutomaticModernCanvasFit(viewRevision);
    }

    private string GetModernFloorDisplayName(string floorKey) =>
        _draft?.Floors.FirstOrDefault(floor => string.Equals(floor.Key, floorKey, StringComparison.OrdinalIgnoreCase))?.DisplayName
        ?? floorKey;

}
