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
    private enum EditorInteractionKind { None, Create, Move, Resize, Pan }

    private sealed record EditorSelection(MapEditorElementKind Kind, Guid? Id = null);

    // Keep the service-owned brushes: existing controls, including open flyouts,
    // follow mode and accent changes without rebuilding the editing session.
    private Brush EditorBrush(ThemeToken token) => FluentTheme.Brush(this, token);
    private Brush EditorBackground => EditorBrush(ThemeToken.Window);
    private Brush EditorPanel => EditorBrush(ThemeToken.Card);
    private Brush EditorPanelRaised => EditorBrush(ThemeToken.Raised);
    private Brush EditorBorder => EditorBrush(ThemeToken.SurfaceBorder);
    private Brush EditorText => EditorBrush(ThemeToken.Text);
    private Brush EditorMuted => EditorBrush(ThemeToken.TextSecondary);

    private readonly MapEditorToolState _modernToolState = new();
    private readonly RecentAnnotationColors _recentAnnotationColors = new();
    private MapEditorPreferences _editorPreferenceState = new();
    private readonly HashSet<string> _hiddenEditorGroups = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hiddenEditorItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _editorGroupExpansion = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Expander> _modernLayerGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<MapEditorTool, Button> _editorToolButtons = [];
    private readonly MapEditorPreferencesRepository _editorPreferences = new(
        System.IO.Path.Combine(AppDataPaths.RootDirectory, "MapEditor", "preferences.json"));

    private bool _modernEditorActive;
    private bool _recentColorsLoaded;
    private ScrollMode _editorPreviousVerticalScrollMode;
    private ScrollMode _editorPreviousHorizontalScrollMode;
    private ScrollBarVisibility _editorPreviousVerticalBarVisibility;
    private ScrollBarVisibility _editorPreviousHorizontalBarVisibility;
    private Grid? _modernEditorRoot;
    private Grid? _modernEditorHeader;
    private Border? _modernLayerPane;
    private ColumnDefinition? _modernLayerColumn;
    private Button? _modernLayerDrawerButton;
    private Button? _modernExportButton;
    private ScrollViewer? _modernViewport;
    private Grid? _modernScene;
    private Viewbox? _modernSceneHost;
    private double _modernDisplayScale = 1;
    private bool _modernFitPending;
    private bool _modernFitQueued;
    private Image? _modernImage;
    private Canvas? _modernCanvas;
    private StackPanel? _modernLayerList;
    private TextBlock? _modernStatusText;
    private TextBlock? _modernZoomText;
    private TextBlock? _modernFloorResolutionText;
    private FrameworkElement? _modernFloorResolutionContainer;
    private Button? _modernDownsampleButton;
    private Border? _modernColorIndicator;
    private ModernFloorBitmap? _modernBitmap;
    private const int ModernEditorDecodePixelWidth = 2048;
    private sealed class ModernFloorBitmap(int sourceWidth, int sourceHeight) : IDisposable
    {
        internal readonly Microsoft.UI.Xaml.Media.Imaging.SoftwareBitmapSource Source = new();
        private Windows.Graphics.Imaging.SoftwareBitmap? _pixels;
        private readonly CancellationTokenSource _cancellation = new();
        internal int SourceWidth { get; } = sourceWidth;
        internal int SourceHeight { get; } = sourceHeight;
        internal bool IsReady { get; set; }
        internal bool IsDisposed { get; private set; }
        internal int PixelWidth => IsReady ? SourceWidth : 0;
        internal int PixelHeight => IsReady ? SourceHeight : 0;
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            _cancellation.Cancel();
            // Source.Close owns the bitmap's close, including deferred close
            // when an upload is still running. Closing its input ourselves can
            // invalidate an in-flight copy or later device-loss recovery.
            Source.Dispose();
            if (_pixels is not null)
            {
                IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(_pixels);
                _pixels = null;
            }
            IDVBuff.Core.Diagnostics.MemoryJournal.ReleaseResource(Source);
            _cancellation.Dispose();
        }
        internal CancellationToken GetToken() => _cancellation.Token;
        internal void TransferPixels(Windows.Graphics.Imaging.SoftwareBitmap pixels)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            _pixels = pixels;
        }
    }
    private readonly SemaphoreSlim _modernImageLoadGate = new(1, 1);
    private readonly Dictionary<string, ModernFloorBitmap> _modernFloorBitmaps =
        new(StringComparer.OrdinalIgnoreCase);
    private EditorSelection? _modernSelection;
    private readonly HashSet<Guid> _modernSelectedAnnotationIds = [];
    private Guid? _modernAnnotationSelectionAnchorId;
    private EditorInteractionKind _modernInteraction;
    private Point _modernPointerStart;
    private Point _modernPointerCurrent;
    private Point _modernPanStart;
    private double _modernPanHorizontalOffset;
    private double _modernPanVerticalOffset;
    private string _modernResizeHandle = string.Empty;
    private NormalizedRectangle? _modernOriginalBounds;
    private NormalizedPoint? _modernOriginalStart;
    private NormalizedPoint? _modernOriginalEnd;
    private NormalizedRectangle? _modernPendingBounds;
    private NormalizedPoint? _modernPendingStart;
    private NormalizedPoint? _modernPendingEnd;
    private readonly List<NormalizedPoint> _modernFreeCropPoints = [];
    private string _currentAnnotationColor = MapAnnotationColor.Default;
    private bool _modernGridVisible = true;
    private bool _modernSnapEnabled;
    private bool _modernFocusMode;
    private bool _modernLayersAreDrawer;
    private bool _modernLayerDrawerOpen;
    private bool _modernPointerMoved;
    private bool _modernExportRendering;
    private bool _modernExportInProgress;
    private uint? _modernCapturedPointerId;
    private NormalizedPoint? _modernContinuousLineStart;
    private readonly Stack<ModernUndoAction> _modernCreationUndoStack = new();
    private readonly MapConcealStrokeBuilder _modernConcealStroke = new();
    private NormalizedPoint? _modernConcealHoverPoint;
    private NormalizedPoint? _modernGateHoverPoint;
    private Polyline? _modernConcealPreviewStroke;
    private Shape? _modernConcealPreviewTip;
    private int _modernConcealPreviewPointCount;

    private sealed record ModernUndoAction(
        string FloorKey,
        string Description,
        Action Undo,
        NormalizedPoint? ContinuousRestartPoint = null);

    private Border CreateModernViewToolbar()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(10, 8, 10, 8),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        row.Children.Add(CreateViewButton("\uE7C2", "平移", (_, _) => SelectModernTool(MapEditorTool.Pan)));
        row.Children.Add(CreateViewButton("\uE9A6", "适应画布", (_, _) => FitModernCanvas()));
        row.Children.Add(CreateToggleViewButton("网格", _modernGridVisible, (_, toggle) =>
        {
            _modernGridVisible = toggle.IsChecked is true;
            RenderModernEditor();
        }));
        row.Children.Add(CreateToggleViewButton("对齐", _modernSnapEnabled, (_, toggle) => _modernSnapEnabled = toggle.IsChecked is true));
        row.Children.Add(CreateViewButton("\uE738", "缩小", (_, _) => ChangeModernZoom(.8f)));
        _modernZoomText = new TextBlock
        {
            Text = "100%",
            Width = 58,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = EditorText
        };
        row.Children.Add(_modernZoomText);
        row.Children.Add(CreateViewButton("\uE710", "放大", (_, _) => ChangeModernZoom(1.25f)));
        row.Children.Add(CreateViewButton("\uE740", "专注模式", (_, _) => ToggleModernFocusMode()));
        _modernExportButton = CreateViewButton("\uE74E", "导出 PNG", async (_, _) => await ShowModernPngExportDialogAsync());
        row.Children.Add(_modernExportButton);
        _modernLayerDrawerButton = CreateViewButton("\uE8A9", "图层", (_, _) => ToggleModernLayerDrawer());
        _modernLayerDrawerButton.Visibility = Visibility.Collapsed;
        row.Children.Add(_modernLayerDrawerButton);

        row.Children.Add(new Rectangle
        {
            Width = 1,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
            Fill = EditorBorder
        });

        var resPill = new Border
        {
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(6),
            Background = EditorPanelRaised
        };
        var resContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        resContent.Children.Add(new FontIcon
        {
            Glyph = "\uEB9F",
            FontSize = 13,
            Foreground = EditorMuted,
            VerticalAlignment = VerticalAlignment.Center
        });
        _modernFloorResolutionText = new TextBlock
        {
            Text = "原图 -- \u00D7 --",
            FontSize = 11,
            Foreground = EditorText,
            VerticalAlignment = VerticalAlignment.Center
        };
        resContent.Children.Add(_modernFloorResolutionText);
        resPill.Child = resContent;
        ToolTipService.SetToolTip(resPill, "当前楼层原图物理分辨率");
        _modernFloorResolutionContainer = resPill;
        row.Children.Add(resPill);

        _modernDownsampleButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    new FontIcon { Glyph = "\uE790", FontSize = 12 },
                    new TextBlock { Text = "降采样", FontSize = 11, VerticalAlignment = VerticalAlignment.Center }
                }
            },
            Height = 30,
            Padding = new Thickness(8, 2, 8, 2),
            CornerRadius = new CornerRadius(6),
            Background = EditorPanelRaised,
            Foreground = EditorText,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(_modernDownsampleButton, "对当前地图全系列楼层原图执行降采样（直接修改原图 · 不可撤销）");
        _modernDownsampleButton.IsEnabled = !_modernToolState.SpecialElementsReadOnly;
        _modernDownsampleButton.Click += async (_, _) => await ShowModernDownsampleDialogAsync();
        ThemeButton.Apply(_modernDownsampleButton, ThemeButtonRole.Standard);
        row.Children.Add(_modernDownsampleButton);

        var toolbarScroller = new ScrollViewer
        {
            Content = row,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden
        };
        return new Border
        {
            Background = EditorPanel,
            BorderBrush = EditorBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = toolbarScroller
        };
    }

    private Button CreateViewButton(string glyph, string toolTip, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 15 },
            Width = 38,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Foreground = EditorText
        };
        ToolTipService.SetToolTip(button, toolTip);
        button.Click += click;
        return ThemeButton.Apply(button, ThemeButtonRole.Standard);
    }

    private ToggleButton CreateToggleViewButton(string label, bool isChecked, Action<object, ToggleButton> click)
    {
        var button = new ToggleButton
        {
            Content = label,
            IsChecked = isChecked,
            Height = 34,
            Padding = new Thickness(9, 3, 9, 3),
            CornerRadius = new CornerRadius(6),
            Foreground = EditorText
        };
        button.Click += (sender, _) => click(sender, button);
        return BindModernToggleTheme(button);
    }

    private ToggleButton BindModernToggleTheme(ToggleButton button)
    {
        // Bind before the native template resolves its state resources. A checked
        // control must keep the editor's live selection brushes, including when
        // only the accent changes and WinUI does not raise ActualThemeChanged.
        foreach (var (key, token) in ThemeResourceKeys.Brushes)
            if (key.StartsWith("ToggleButton", StringComparison.Ordinal))
                button.Resources[key] = EditorBrush(token);
        return button;
    }

    private Border CreateModernLayerPane()
    {
        var pane = new Grid();
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var paneHeader = new StackPanel { Spacing = 3, Padding = new Thickness(14, 12, 14, 9) };
        paneHeader.Children.Add(new TextBlock
        {
            Text = "图层管理器",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = EditorText
        });
        _modernStatusText = new TextBlock
        {
            Text = "选择一个工具开始编辑。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = EditorMuted
        };
        paneHeader.Children.Add(_modernStatusText);
        pane.Children.Add(paneHeader);
        _modernLayerGroups.Clear();
        _modernLayerList = new StackPanel { Spacing = 3 };
        var listScroller = new ScrollViewer
        {
            Content = _modernLayerList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(listScroller, 1);
        pane.Children.Add(listScroller);

        var actions = new Grid { ColumnSpacing = 10, Padding = new Thickness(10) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cancel = CreateEditorActionButton("取消", ThemeButtonRole.Standard);
        cancel.Click += async (_, _) =>
        {
            ResetBatchOperation();
            _draft = null;
            await ShowListAsync();
        };
        actions.Children.Add(cancel);
        _markerConfirmButton = CreateEditorActionButton("确认", ThemeButtonRole.Accent);
        _markerConfirmButton.Click += async (_, _) => await SaveDraftAsync();
        Grid.SetColumn(_markerConfirmButton, 1);
        actions.Children.Add(_markerConfirmButton);
        Grid.SetRow(actions, 2);
        pane.Children.Add(actions);

        return new Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            Background = EditorPanel,
            BorderBrush = EditorBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = pane
        };
    }

    private static Button CreateEditorActionButton(string text, ThemeButtonRole role) => ThemeButton.Apply(new()
    {
        Content = text,
        Height = 46,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center
    }, role);

    private void EnterModernEditorEnvironment()
    {
        if (_modernEditorActive)
            return;
        _modernEditorActive = true;
        NavigationCompactStateChanged?.Invoke(true);
        if (ParentScrollViewer is not null)
        {
            _editorPreviousVerticalScrollMode = ParentScrollViewer.VerticalScrollMode;
            _editorPreviousHorizontalScrollMode = ParentScrollViewer.HorizontalScrollMode;
            _editorPreviousVerticalBarVisibility = ParentScrollViewer.VerticalScrollBarVisibility;
            _editorPreviousHorizontalBarVisibility = ParentScrollViewer.HorizontalScrollBarVisibility;
            ParentScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
            ParentScrollViewer.HorizontalScrollMode = ScrollMode.Disabled;
            ParentScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            ParentScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            ParentScrollViewer.ChangeView(0, 0, null, true);
            ParentScrollViewer.SizeChanged -= ModernParentViewport_SizeChanged;
            ParentScrollViewer.SizeChanged += ModernParentViewport_SizeChanged;
        }
    }

    private void ResetModernMarkerEditorSession()
    {
        if (!_modernEditorActive)
            return;
        CancelModernInteraction(restoreGeometry: true);
        if (_modernImage is not null)
            _modernImage.Source = null;
        ReleaseModernFloorBitmaps();
        _modernCanvas?.Children.Clear();
        _modernLayerList?.Children.Clear();
        if (_modernScene is not null)
            _modernScene.Children.Clear();
        if (_modernEditorRoot is not null)
            _modernEditorRoot.Children.Clear();
        if (ReferenceEquals(_workflowHost.Content, _modernEditorRoot))
            _workflowHost.Content = null;
        ResetModernAutomaticRoute();
        _modernEditorActive = false;
        NavigationCompactStateChanged?.Invoke(false);
        if (ParentScrollViewer is not null)
        {
            ParentScrollViewer.SizeChanged -= ModernParentViewport_SizeChanged;
            ParentScrollViewer.VerticalScrollMode = _editorPreviousVerticalScrollMode;
            ParentScrollViewer.HorizontalScrollMode = _editorPreviousHorizontalScrollMode;
            ParentScrollViewer.VerticalScrollBarVisibility = _editorPreviousVerticalBarVisibility;
            ParentScrollViewer.HorizontalScrollBarVisibility = _editorPreviousHorizontalBarVisibility;
        }
        _modernEditorRoot = null;
        _modernEditorHeader = null;
        _modernLayerPane = null;
        _modernLayerColumn = null;
        if (_modernViewport is not null)
        {
            _modernViewport.LayoutUpdated -= ModernViewport_LayoutUpdated;
            _modernViewport.SizeChanged -= ModernViewport_SizeChanged;
        }
        _modernViewport = null;
        _modernScene = null;
        _modernSceneHost = null;
        CancelModernCanvasFit();
        _modernFitQueued = false;
        _modernDisplayScale = 1;
        _modernRenderedZoom = double.NaN;
        _modernImage = null;
        _modernCanvas = null;
        _modernLayerList = null;
        _modernExportButton = null;
        _modernLayerDrawerButton = null;
        _modernStatusText = null;
        _modernZoomText = null;
        _modernColorIndicator = null;
        _modernFloorResolutionText = null;
        _modernFloorResolutionContainer = null;
        _modernDownsampleButton = null;
        _modernBitmap = null;
        _modernLayerGroups.Clear();
        _editorToolButtons.Clear();
        _modernCreationUndoStack.Clear();
        _modernFreeCropPoints.Clear();
        _modernConcealStroke.Cancel();
        _modernConcealHoverPoint = null;
        _modernGateHoverPoint = null;
        _modernSelection = null;
        _modernSelectedAnnotationIds.Clear();
        _modernAnnotationSelectionAnchorId = null;
        _modernExportRendering = false;
        _modernExportInProgress = false;
        _modernToolState.Reset();
    }


}
