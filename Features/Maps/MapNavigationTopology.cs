namespace IDVBuff.Features.Maps;

// Navigation is independent of recognition crops: every point belongs to its
// original floor image. Shared door nodes are exact channel endpoints.
public sealed class MapNavigationTopology
{
    public const string CurrentRoutingStyle = "diagonal-center-v1";
    public const int CurrentRouteMetadataVersion = 6;
    public int SchemaVersion { get; set; } = 1;
    public string CoordinateSystem { get; set; } = "normalized-top-left-y-down";
    public string? SourceLevelId { get; set; }
    public string? SourceSha256 { get; set; }
    public string? RoutingStyle { get; set; }
    public int RouteMetadataVersion { get; set; }
    public string? LootEvidence { get; set; }
    public double TurnPenalty { get; set; }
    public List<MapNavigationFloor> Floors { get; set; } = [];
    public List<MapNavigationModule> Modules { get; set; } = [];
    public List<MapNavigationNode> Nodes { get; set; } = [];
    public List<MapNavigationDoor> Doors { get; set; } = [];
    public List<MapNavigationLink> Links { get; set; } = [];
    public List<MapNavigationTransition> FloorTransitions { get; set; } = [];

    public MapNavigationTopology Clone()
    {
        // Copy mutable geometry directly. A JSON round trip creates a full
        // UTF-16 document and duplicates every identifier on each batch map.
        var clone = (MapNavigationTopology)MemberwiseClone();
        clone.Floors = Floors.Select(f => f.Clone()).ToList();
        clone.Modules = Modules.Select(m => m.Clone()).ToList();
        clone.Nodes = Nodes.Select(n => n.Clone()).ToList();
        clone.Doors = Doors.Select(d => d.Clone()).ToList();
        clone.Links = Links.Select(l => l.Clone()).ToList();
        clone.FloorTransitions = FloorTransitions.Select(t => t.Clone()).ToList();
        return clone;
    }
}

public sealed class MapNavigationFloor
{
    internal MapNavigationFloor Clone() => (MapNavigationFloor)MemberwiseClone();
    public string FloorKey { get; set; } = string.Empty;
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public string ImageSha256 { get; set; } = string.Empty;
}

public enum MapFloorTraversalKind { None, Staircase, ObstacleJump, Drop }

public sealed class MapNavigationModule
{
    internal MapNavigationModule Clone()
    {
        var clone = (MapNavigationModule)MemberwiseClone();
        clone.Cells = Cells.Select(c => c.Clone()).ToList();
        clone.SafeCells = SafeCells.Select(c => c.Clone()).ToList();
        return clone;
    }
    public string? SourceRoomId { get; set; }
    public int SourceRoomType { get; set; }
    public int SourceRoomConfigId { get; set; }
    public MapFloorTraversalKind FloorTraversalKind { get; set; }
    public bool IsHighValue { get; set; }
    public bool IsFloorConnector { get; set; }
    // Occupying two floors, a room's name, or a decorative stair mesh does not
    // grant traversal. Only audited source configurations may change floors.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanChangeFloors => IsFloorConnector && (SourceRoomConfigId == 0
        || FloorTraversalKind == MapFloorTraversalKind.Staircase && SourceRoomConfigId is
            10401 or 10402 or 10403 or 10411 or 10601 or 10603 or 10607 or 10608 or 10611 or 10622
        || FloorTraversalKind == MapFloorTraversalKind.ObstacleJump && SourceRoomConfigId == 10605
        || FloorTraversalKind == MapFloorTraversalKind.Drop && SourceRoomConfigId == 10623);
    // A terminal room may be an endpoint, but never a shortcut between doors.
    public bool IsDeadEnd { get; set; }
    public int ChestCandidateCount { get; set; }
    public int HighTierCandidateCount { get; set; }
    public double ChestPreferenceWeight2 { get; set; }
    public double ChestPreferenceWeight3 { get; set; }
    public double LootPriorityScore { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "module";
    public string FloorKey { get; set; } = string.Empty;
    public string CenterNodeId { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => $"{FloorKey} · {Name}";
    public List<MapNavigationCell> Cells { get; set; } = [];
    // Pixel-derived centerline safe zones, bound to this floor's original
    // artwork and wall mask. Empty retains compatibility with older imports.
    public List<MapNavigationCell> SafeCells { get; set; } = [];
}

public sealed class MapNavigationCell
{
    internal MapNavigationCell Clone() => (MapNavigationCell)MemberwiseClone();
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Contains(NormalizedPoint point) => Contains(point.X, point.Y);
    internal bool Contains(double x, double y) => x >= X - 1e-9 && x <= X + Width + 1e-9
        && y >= Y - 1e-9 && y <= Y + Height + 1e-9;
}

public sealed class MapNavigationNode
{
    internal MapNavigationNode Clone()
    {
        var clone = (MapNavigationNode)MemberwiseClone();
        clone.Point = Point.Clone();
        return clone;
    }
    public string Id { get; set; } = string.Empty;
    public string FloorKey { get; set; } = string.Empty;
    public string Kind { get; set; } = "center";
    public NormalizedPoint Point { get; set; } = new();
}

public sealed class MapNavigationDoor
{
    internal MapNavigationDoor Clone() => (MapNavigationDoor)MemberwiseClone();
    public string Id { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string ModuleAId { get; set; } = string.Empty;
    public string ModuleBId { get; set; } = string.Empty;
    public string? SourceKey { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class MapNavigationLink
{
    internal MapNavigationLink Clone()
    {
        var clone = (MapNavigationLink)MemberwiseClone();
        clone.Points = Points.Select(p => p.Clone()).ToList();
        return clone;
    }
    public string Id { get; set; } = string.Empty;
    public string ModuleId { get; set; } = string.Empty;
    public string FromNodeId { get; set; } = string.Empty;
    public string ToNodeId { get; set; } = string.Empty;
    // Cost includes channel length, central preference, and internal bends.
    public double Cost { get; set; }
    public double Length { get; set; }
    public List<NormalizedPoint> Points { get; set; } = [];
}

public sealed class MapNavigationTransition
{
    internal MapNavigationTransition Clone() => (MapNavigationTransition)MemberwiseClone();
    public bool IsSpecialTeleport { get; set; }
    public string Id { get; set; } = string.Empty;
    public string FromNodeId { get; set; } = string.Empty;
    public string ToNodeId { get; set; } = string.Empty;
    public double Cost { get; set; }
    public bool Directed { get; set; }
    public string Evidence { get; set; } = string.Empty;
}

public sealed partial class MapDraft
{
    private readonly object _navigationGate = new();
    private MapNavigationTopology? _navigation;
    private Func<MapNavigationTopology>? _navigationLoader;

    public MapNavigationTopology? Navigation
    {
        get
        {
            lock (_navigationGate)
            {
                if (_navigationLoader is { } load)
                {
                    _navigation = load();
                    _navigationLoader = null;
                }
                return _navigation;
            }
        }
        set { lock (_navigationGate) { _navigation = value; _navigationLoader = null; } }
    }

    internal bool HasNavigation { get { lock (_navigationGate) return _navigation is not null || _navigationLoader is not null; } }
    internal MapNavigationTopology? LoadedNavigation { get { lock (_navigationGate) return _navigation; } }

    // A batch save owns its editable graph. The import plan retains only the
    // validated file loader, rather than every graph processed in the batch.
    internal MapNavigationTopology? LoadNavigationForSave()
    {
        lock (_navigationGate) return _navigationLoader is { } load ? load() : _navigation?.Clone();
    }

    internal void DeferNavigation(Func<MapNavigationTopology> loader)
    {
        lock (_navigationGate) { _navigation = null; _navigationLoader = loader; }
    }
}

public static class MapSpecialRoomTeleports
{
    // Room numbers on a planned route change. Bind the verified connection to
    // source room IDs and the original artwork instead of those display numbers.
    public static bool HasCurrentMetadata(MapNavigationTopology topology)
    {
        if (topology.SourceLevelId?.StartsWith("104", StringComparison.Ordinal) is not true) return true;
        var sources = topology.Modules.Where(m => m.SourceRoomConfigId == 10501 && m.FloorKey == "1f").ToArray();
        var destinations = topology.Modules.Where(m => m.SourceRoomConfigId == 10624 && m.FloorKey == "b1f").ToArray();
        return sources.Length == 1 && destinations.Length == 1 && topology.FloorTransitions.Any(t =>
            t.IsSpecialTeleport && t.Directed && t.FromNodeId == sources[0].CenterNodeId && t.ToNodeId == destinations[0].CenterNodeId);
    }

    public static IReadOnlySet<string> RequiredFloors(MapNavigationTopology topology)
    {
        var endpoints = topology.FloorTransitions.Where(t => t.IsSpecialTeleport)
            .SelectMany(t => new[] { t.FromNodeId, t.ToNodeId }).ToHashSet();
        return topology.Nodes.Where(n => endpoints.Contains(n.Id)).Select(n => n.FloorKey).ToHashSet();
    }

    public static void ValidateSelection(MapNavigationTopology topology, IReadOnlySet<string> allowedFloors)
    {
        var missing = RequiredFloors(topology).Except(allowedFloors).Order().ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"通过特殊房间传送需要勾选对应楼层：{string.Join("、", missing.Select(f => f.ToUpperInvariant()))}。");
    }
}
