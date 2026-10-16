using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRecord
{
    private readonly object _navigationGate = new();
    private MapNavigationTopology? _navigation;
    private Func<MapNavigationTopology>? _navigationLoader;

    // Only the repository index uses this reference. IDVM continues exporting
    // the complete topology through its existing data/navigation.json entry.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NavigationFileName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MapNavigationTopology? Navigation
    {
        get
        {
            lock (_navigationGate)
            {
                if (_navigationLoader is { } load)
                {
                    // Retain the loader if reading fails: missing/corrupt
                    // navigation must never silently become an empty route.
                    _navigation = load();
                    _navigationLoader = null;
                }
                return _navigation;
            }
        }
        set
        {
            lock (_navigationGate)
            {
                _navigation = value;
                _navigationLoader = null;
                NavigationFileName = null;
            }
        }
    }

    internal MapNavigationTopology? LoadedNavigation
    {
        get { lock (_navigationGate) return _navigation; }
    }

    // Format/capability checks must not turn a batch metadata snapshot into a
    // cache containing every map's full navigation graph.
    internal bool HasNavigation
    {
        get { lock (_navigationGate) return _navigation is not null || _navigationLoader is not null; }
    }

    // A batch draft owns the loaded topology. Do not also attach it to the
    // long-lived class snapshot, which otherwise retains every processed map.
    internal MapNavigationTopology? LoadNavigationForDraft()
    {
        lock (_navigationGate)
            return _navigationLoader is { } load ? load() : _navigation?.Clone();
    }

    internal void DeferNavigation(string fileName, Func<MapNavigationTopology> loader)
    {
        lock (_navigationGate)
        {
            NavigationFileName = fileName;
            _navigation = null;
            _navigationLoader = loader;
        }
    }

    private void CloneNavigationTo(MapRecord clone)
    {
        lock (_navigationGate)
        {
            // Each detached snapshot loads its own editable object. Copying a
            // catalog must neither open every route file nor share mutations.
            clone.NavigationFileName = NavigationFileName;
            clone._navigationLoader = _navigationLoader;
            clone._navigation = _navigation?.Clone();
        }
    }
}
