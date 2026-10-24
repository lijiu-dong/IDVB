using IDVBuff.Features.Maps;
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

    private void AttachMarkerHostScroller()
    {
        if (_markerSurface is null)
            return;

        var hostScroller = FindAncestorScrollViewer(_markerSurface);
        if (ReferenceEquals(_markerHostScroller, hostScroller))
            return;

        DetachMarkerHostScroller();
        _markerHostScroller = hostScroller;
        if (_markerHostScroller is null)
            return;

        _markerHostScroller.ViewChanged += MarkerHostScroller_ViewChanged;
        _markerHostScroller.SizeChanged += MarkerHostScroller_SizeChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ++_workflowRevision;
        _accountService.Changed -= CoCreationAccountChanged;
        _initialListReady.TrySetCanceled();
        LocalTemplateClassAdded = null;
        CommunityMapsRequested = null;
        CloseHoldPreviewImmediately();
        ResetMarkerEditorSession();
        ResetBatchImport();
        ResetBatchOperation();
        ReleaseMapCardPreviews();
        if (_mapCardsRepeater is { } repeater)
        {
            repeater.ItemsSource = null;
            repeater.ItemTemplate = new DataTemplate();
        }
        if (_mapCardsSurface is not null) _mapCardsSurface.Child = null;
        _mapCardsSurface = null;
        _mapCardsRepeater = null;
        _mapCardsEmpty = null;
        _mapCardRowFactory = null;
        _cardBorders.Clear();
        // The unloaded page has no catalog consumer. WinRT wrappers can outlive
        // this navigation boundary; they must not keep a whole catalog/draft
        // attached while waiting for the CLR/WinUI lifetime sweep.
        _draft = null;
        _loadedMaps = [];
        _classes = [];
        _classProperties = new Dictionary<string, MapClassProperties>(StringComparer.OrdinalIgnoreCase);
        _variantGroups = [];
        _filterGroups = [];
        _surveyProjects = [];
        _loadedCatalogRevision = null;
        _selectedMapIds.Clear();
        _selectedTagFilters.Clear();
        _classComboBox = null;
        _mapFilterButton = null;
        _editButton = _deleteButton = _variantButton = _classEditButton = _importButton = _publishButton = null;
        _workflowHost.Content = null;
        _ = CloseCoCreationAsync();
        DetachMarkerHostScroller();
        if (ParentScrollViewer is not null)
            ParentScrollViewer.SizeChanged -= OnParentScrollViewerSizeChanged;
    }

    private void DetachMarkerHostScroller()
    {
        if (_markerHostScroller is null)
            return;

        _markerHostScroller.ViewChanged -= MarkerHostScroller_ViewChanged;
        _markerHostScroller.SizeChanged -= MarkerHostScroller_SizeChanged;
        _markerHostScroller = null;
    }

    private void MarkerHostScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
        PositionMarkerControlPanel();

    private void MarkerHostScroller_SizeChanged(object sender, SizeChangedEventArgs e) =>
        PositionMarkerControlPanel();

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer scrollViewer)
                return scrollViewer;
        }

        return null;
    }

    private static Rect Intersect(Rect first, Rect second)
    {
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);
        return right > left && bottom > top
            ? new Rect(left, top, right - left, bottom - top)
            : Rect.Empty;
    }
}
