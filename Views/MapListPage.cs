using IDVBuff.Features.Maps;
using IDVBuff.Core.Contracts.Online;
using IDVBuff.Features.Accounts;
using IDVBuff.Diagnostics;
using IDVBuff.Survey.Domain;
using Microsoft.Extensions.DependencyInjection;
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
using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace IDVBuff.Views;

/// <summary>
/// The map-management workflow shown under 加页手记 / 地图列表.
/// </summary>
public sealed partial class MapListPage : UserControl
{
    /// <summary>Returns the primary import action for onboarding emphasis.</summary>
    public FrameworkElement? GetImportControl() => _importButton;
    private static readonly TimeSpan WorkflowEnterDuration = TimeSpan.FromMilliseconds(200);

    private enum BatchOperationType { None, Import, Edit, Delete }

    private static readonly Color AccentBlue = Color.FromArgb(255, 46, 132, 225);
    private static readonly Color MainEntranceBlue = Color.FromArgb(255, 38, 133, 255);
    private static readonly Color SideEntranceGreen = Color.FromArgb(255, 63, 207, 123);
    private static readonly Color SecondFloorPurple = Color.FromArgb(255, 132, 94, 247);
    private static readonly Color OptionalAnchorOrange = Color.FromArgb(255, 236, 150, 61);
    private static readonly Color RecognitionRegionOrange = Color.FromArgb(255, 239, 103, 42);
    private static readonly Color RecognitionRegionRed = Color.FromArgb(255, 235, 55, 55);
    private static readonly Color DisabledGray = Color.FromArgb(255, 210, 210, 210);
    private static readonly Color[] AnnotationColors =
    [
        Color.FromArgb(255, 255, 59, 48),   // 0: 红
        Color.FromArgb(255, 255, 149, 0),   // 1: 橙
        Color.FromArgb(255, 255, 204, 0),   // 2: 黄
        Color.FromArgb(255, 52, 199, 89),   // 3: 绿
        Color.FromArgb(255, 50, 173, 230),  // 4: 青
        Color.FromArgb(255, 0, 122, 255),   // 5: 蓝
        Color.FromArgb(255, 175, 82, 222),  // 6: 紫
        Color.FromArgb(255, 255, 45, 85),   // 7: 粉
        Color.FromArgb(255, 242, 242, 242), // 8: 白
    ];
    private const double MarkerPanelInset = 12d;
    private const double MarkerPanelTopSafeInset = 48d;
    private const double MarkerPanelPreferredWidth = 170d;

    private readonly MapRepository _repository;
    private readonly IdvmPackageService _idvmPackageService;
    private readonly IMapSubscriptionService _mapSubscriptionService;
    private readonly IMapPublicationService _mapPublicationService;
    private readonly IAccountService _accountService;
    private readonly ICommunityService _communityService;
    private readonly ContentPresenter _workflowHost = new();
    internal ScrollViewer? ParentScrollViewer { get; set; }
    internal Action<bool>? NavigationCompactStateChanged { get; set; }
    private readonly Dictionary<Guid, Border> _cardBorders = [];
    private readonly Dictionary<string, BitmapImage> _previewImages =
        new(StringComparer.OrdinalIgnoreCase);
    private Button? _editButton;
    private Button? _deleteButton;
    private Button? _variantButton;
    private Button? _classEditButton;
    private Button? _importButton;
    private Button? _publishButton;
    private HashSet<Guid> _selectedMapIds = [];
    private Guid? _lastClickedMapId;
    private IReadOnlyList<MapRecord> _loadedMaps = [];
    private IReadOnlyList<MapTagGroup> _filterGroups = [];
    private readonly Dictionary<Guid, HashSet<string>> _selectedTagFilters = [];
    private Border? _mapCardsSurface;
    private IReadOnlyList<MapVariantGroup> _variantGroups = [];
    private IReadOnlyList<SurveyProjectSummary> _surveyProjects = [];
    private IReadOnlyList<string> _classes = [];
    private IReadOnlyDictionary<string, MapClassProperties> _classProperties =
        new Dictionary<string, MapClassProperties>(StringComparer.OrdinalIgnoreCase);
    private string _selectedClass = string.Empty;
    private bool _hasInitializedClassSelection;
    private bool _surveyProjectsCollapsed = ShellLayoutMemory.Load().SurveyProjectsCollapsed;
    private bool _isPackageOperation;
    private bool _editMapPending;
    private ComboBox? _classComboBox;
    private List<MapRecord>? _batchQueue;
    private int _batchQueueIndex;
    private BatchOperationType _batchType;
    private MapDraft? _draft;
    private Grid? _markerSurface;
    private Canvas? _markerCanvas;
    private Canvas? _markerPanelCanvas;
    private ScrollViewer? _markerHostScroller;
    private Border? _markerControlPanel;
    private Button? _markerConfirmButton;
    private string _activeFloorKey = "1f";
    private Guid? _activeAnchorId;
    private List<MapDraft>? _batchDrafts;
    private int _batchDraftIndex;
    private List<ImportFloorEntry>? _pendingImportFloors;
    private string? _selectedImportFloorKey;
    private Border? _selectedImportFloorCard;
    private readonly Dictionary<ImportFloorEntry, Border> _importFloorCards = [];
    private Grid? _importFloorCardsGrid;
    private ScrollViewer? _importFloorScrollViewer;
    private Button? _importAddFloorCard;
    private DispatcherQueueTimer? _importClickTimer;
    private DispatcherQueueTimer? _importHoldTimer;
    private DispatcherQueueTimer? _importDragFollowTimer;
    private DispatcherQueueTimer? _importDropSettleTimer;
    private ImportFloorEntry? _pendingImportClickEntry;
    private ImportFloorEntry? _draggedImportFloor;
    private Border? _draggedImportFloorCard;
    private Point _importDragStartPoint;
    private Point _importDragPointerPosition;
    private Point _importDragPointerInScrollViewer;
    private Vector3 _importDragVisualTranslation;
    private bool _isDraggingImportFloor;
    private Point? _dragStart;
    private NormalizedRectangle? _pendingMarker;
    private bool _isSelectingRecognitionRegion;
    private bool _isAnnotationPanelOpen;
    private int _selectedAnnotationColor;
    private MapAnnotationType _activeAnnotationType;
    private Point? _panelDragStart;
    private Point _panelDragOrigin;
    private Point _panelPositionRatio = new(1d, 0d);
    private double _imageAspectRatio = 16d / 9d;

    public MapListPage(bool coCreationMode = false)
        : this(App.Services.GetRequiredService<MapRepository>(), coCreationMode,
            App.Services.GetRequiredService<IMapSubscriptionService>(),
            App.Services.GetRequiredService<IMapPublicationService>(),
            AccountSession.Service, CoCreationPublicationClient.Service)
    {
    }

    internal MapListPage(MapRepository repository, bool coCreationMode,
        IMapSubscriptionService subscriptions, IMapPublicationService publications,
        IAccountService accounts, ICommunityService community)
    {
        _repository = repository;
        _coCreationMode = coCreationMode;
        _idvmPackageService = new IdvmPackageService(_repository);
        _mapSubscriptionService = subscriptions;
        _mapPublicationService = publications;
        _accountService = accounts;
        _communityService = community;
        if (coCreationMode) _accountService.Changed += CoCreationAccountChanged;
        Content = _workflowHost;
        Loaded += MapListPage_Loaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => UpdateSelectedCardVisuals();
        FluentTheme.Observe(this, _ => UpdateSelectedCardVisuals());
        AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(MapListPage_KeyDown), true);
    }

    private bool HasSelection => _selectedMapIds.Count > 0;

    private bool IsBatchOperation => _batchQueue is { Count: > 0 };

    private void ResetBatchOperation()
    {
        _batchQueue = null;
        _batchQueueIndex = 0;
        _batchType = BatchOperationType.None;
    }

    private bool TryAdvanceBatchQueue()
    {
        if (!IsBatchOperation || _batchQueue is null || _batchQueueIndex + 1 >= _batchQueue.Count)
            return false;

        _batchQueueIndex++;
        _activeFloorKey = "1f";
        _activeAnchorId = null;
        _isSelectingRecognitionRegion = false;
        _pendingMarker = null;
        _dragStart = null;
        return true;
    }

    private async Task StartBatchOperationAsync(BatchOperationType type)
    {
        if (_selectedMapIds.Count == 0)
            return;

        _batchQueue = _loadedMaps
            .Where(map => _selectedMapIds.Contains(map.Id))
            .OrderBy(map => map.SequenceNumber)
            .ToList();
        _batchQueueIndex = 0;
        _batchType = type;

        if (type == BatchOperationType.Delete)
        {
            await ExecuteBatchDeleteAsync();
            return;
        }

        var firstMap = _batchQueue[0];
        if (type == BatchOperationType.Edit)
            await EditMapAsync(firstMap);
        else
            await ImportMapAsync(firstMap);
    }

    private async Task ExecuteBatchDeleteAsync()
    {
        if (_batchQueue is null || _batchQueue.Count == 0)
            return;

        var count = _batchQueue.Count;
        var label = count == 1 ? _batchQueue[0].DisplayName : $"{count} 个地图";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除地图？",
            Content = $"将永久删除 {label} 及其图片和识别标记数据。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowThemedAsync(this) != ContentDialogResult.Primary)
        {
            ResetBatchOperation();
            return;
        }

        try
        {
            await RunMapOperationAsync("正在删除地图……", async () =>
            {
                var deleted = 0;
                foreach (var map in _batchQueue)
                {
                    await Task.Run(() => _repository.DeleteAsync(map.Id));
                    MapOperationProgress.Report(.9 * ++deleted / _batchQueue.Count, "正在删除地图……");
                }

                if (!App.IsSafeMode)
                    await MapOperationProgress.RunAsync(.9, 1, () => App.Session.RefreshMapCacheAsync());
            });
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("批量删除失败", exception.Message);
        }

        _selectedMapIds.Clear();
        _lastClickedMapId = null;
        ResetBatchOperation();
        await ShowListAsync();
    }

    private async Task ImportMapAsync(MapRecord map)
    {
        try
        {
            var draft = await _repository.CreateDraftAsync(map.Id);
            if (draft is null)
            {
                await ShowMessageAsync("地图不存在", "该地图已被删除，请刷新列表。");
                await ShowListAsync();
                return;
            }
            _activeFloorKey = "1f";
            _activeAnchorId = null;
            _draft = draft;
            _draft.Recognition.EnsureStandardAnchors();
            await ShowImportAsync(_draft);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法导入地图", exception.Message);
        }
    }

    private async void MapListPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MapListPage_Loaded;
        try
        {
            var startupTimer = Stopwatch.StartNew();
            var prepared = _coCreationMode ? null : App.TakeMapListStartupData();
            await ShowListAsync(prepared);
            _initialListReady.TrySetResult();
            OutputLog.Write("INFO", "STARTUP",
                $"Map list first render: startupSnapshotAvailable={prepared is not null}, elapsedMs={startupTimer.Elapsed.TotalMilliseconds:F1}.");
            if (!App.IsSafeMode) _ = RepairMapMetadataInBackgroundAsync();
        }
        catch (Exception exception)
        {
            _initialListReady.TrySetException(exception);
            System.Diagnostics.Debug.WriteLine($"Map list load failed: {exception}");
            _workflowHost.Content = CreateDescription("地图列表加载失败：" + exception.Message);
        }
    }

    private async Task RepairMapMetadataInBackgroundAsync()
    {
        try
        {
            await _repository.RepairImageMetadataAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Map metadata repair failed: {exception}");
        }
    }

    private void MapListPage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_modernEditorActive)
        {
            HandleModernMarkerEditorKeyDown(e);
            return;
        }

        if (e.Key == Windows.System.VirtualKey.A
            && (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
        {
            _selectedMapIds = GetVisibleMaps().Select(map => map.Id).ToHashSet();
            UpdateSelectedCardVisuals();
            e.Handled = true;
        }
    }

    private MapCatalogRevision? _loadedCatalogRevision;
    private long _workflowRevision;

    private bool IsWorkflowCurrent(long revision, string stage)
    {
        if (revision == _workflowRevision) return true;
        IDVBuff.Core.Diagnostics.MemoryJournal.Current?.Record("workflow-superseded",
            new { revision, currentRevision = _workflowRevision, stage });
        return false;
    }

    private async Task ShowListAsync(MapListStartupData? prepared = null)
    {
        using var memory = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("UI.MapList.Show", _coCreationMode ? "co-creation" : "local");
        var workflowRevision = ++_workflowRevision;
        ResetMarkerEditorSession();
        ResetBatchOperation();
        if (_coCreationMode)
        {
            // The community/template gallery consumes remote/template metadata,
            // not every local drawing. A selected owned class resolves its own
            // records before displaying saved-map identities.
            if (_coCreationSelectedClass is { } selection)
            {
                if (!await LoadCoCreationLocalClassAsync(selection)) return;
            }
            else
            {
                _loadedMaps = [];
                _classes = [];
                _classProperties = new Dictionary<string, MapClassProperties>(StringComparer.OrdinalIgnoreCase);
                _variantGroups = [];
                _loadedCatalogRevision = null;
            }
            if (!IsWorkflowCurrent(workflowRevision, "co-creation-class")) return;
            ReleaseMapCardPreviews();
            await ShowCoCreationAsync();
            return;
        }
        var revision = _repository.GetCatalogRevision();
        if (prepared is not null && prepared.Revision != revision
            && !_repository.IsMetadataOnlyRevisionAdvance(prepared.Revision, revision))
            prepared = null;
        var metadataOnly = _loadedCatalogRevision is { } loaded && _repository.IsMetadataOnlyRevisionAdvance(loaded, revision);
        var scanFloorOnly = false;
        if (_loadedCatalogRevision is { } previous
            && _repository.TryGetScanFloorAdvance(previous, revision, out var changedClass, out var floorKey))
        {
            var updatedProperties = _classProperties.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
            if (updatedProperties.TryGetValue(changedClass, out var properties))
                properties.ScanFloorKey = floorKey;
            else updatedProperties[changedClass] = new MapClassProperties { ScanFloorKey = floorKey };
            _classProperties = updatedProperties;
            foreach (var map in _loadedMaps.Where(map => string.Equals(map.Class, changedClass, StringComparison.OrdinalIgnoreCase)))
                map.ClassProperties = updatedProperties[changedClass].Clone();
            scanFloorOnly = true;
        }
        var snapshot = prepared?.Catalog ?? (_loadedCatalogRevision == revision || metadataOnly || scanFloorOnly
            ? new MapCatalogSnapshot(_classes, _loadedMaps) { ClassProperties = _classProperties, VariantGroups = _variantGroups }
            : await _repository.GetListCatalogSnapshotAsync());
        if (!IsWorkflowCurrent(workflowRevision, "list-catalog")) return;
        // Page-local, detached metadata is reusable until an actual catalog
        // mutation. Switching gallery tabs must not clone every map again.
        _loadedCatalogRevision = revision;
        if (metadataOnly)
            IDVBuff.Core.Diagnostics.MemoryJournal.Current?.Record("catalog-metadata-reuse", new { revision,
                meaning = "list identity/tags unchanged; editors still resolve current image bindings by id" });
        _classes = snapshot.Classes;
        _classProperties = snapshot.ClassProperties;
        _loadedMaps = snapshot.Maps;
        _variantGroups = snapshot.VariantGroups;
        if (scanFloorOnly && !_coCreationMode && _mapCardsRepeater is not null)
        {
            IDVBuff.Core.Diagnostics.MemoryJournal.Current?.Record("catalog-scan-floor-reuse", new { revision,
                meaning = "only scan-floor property changed; retained list records and control tree" });
            RefreshMapCardsOnly();
            return;
        }
        var filterGroups = prepared?.FilterGroups
            ?? (await new MapTagStore().LoadAsync(snapshot.Maps, snapshot.Classes))
                .Where(group => group.IsEnabled)
                .ToArray();
        if (!IsWorkflowCurrent(workflowRevision, "list-tags")) return;
        var surveyProjects = prepared?.SurveyProjects
            ?? (App.IsSafeMode
                ? []
                : await App.Session.GetSurveyProjectsAsync());
        if (!IsWorkflowCurrent(workflowRevision, "list-projects")) return;
        _filterGroups = filterGroups;
        _surveyProjects = surveyProjects;
        ReleaseMapCardPreviews();
        if (prepared is not null)
        {
            foreach (var (path, bitmap) in prepared.PreviewImages)
                _previewImages[path] = bitmap;
        }
        if (!_hasInitializedClassSelection)
        {
            // The match control panel and this page intentionally share the
            // same persisted preference. This page only reads it: changing
            // the local filter below must never update settings.json.
            _selectedClass = MapRuntimeSettingsRules.ResolveMapClass(
                _classes,
                App.IsSafeMode ? null : App.Session.LastSelectedMapClass)
                ?? string.Empty;
            _hasInitializedClassSelection = true;
        }
        else if (!_classes.Any(name => string.Equals(
            name,
            _selectedClass,
            StringComparison.OrdinalIgnoreCase)))
        {
            _selectedClass = _classes.FirstOrDefault() ?? string.Empty;
        }
        ShowListFromLoadedSnapshot();
    }

}
