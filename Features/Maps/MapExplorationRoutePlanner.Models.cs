using System.Numerics;

namespace IDVBuff.Features.Maps;

public enum MapRoomCoverage { HighValue, Balanced, AllRooms }
public enum MapRoutePreference { Shortest, Simplest }
// AllowedFloors retains its API name, but selects loot exploration only.
// All topology floors remain available for transit to targets and the exit.
public sealed record MapExplorationOptions(string EntranceId, string ExitId,
    IReadOnlySet<string> AllowedFloors, MapRoomCoverage Coverage, MapRoutePreference Preference,
    IReadOnlySet<string> HighValueRoomIds, IReadOnlyList<MapRouteGateEndpoint> Gates,
    bool UseSpecialRoomTeleports = false, bool DenseRoutes = true);
public sealed record MapExplorationStep(string From, string To, string? ModuleId,
    IReadOnlyList<NormalizedPoint> Points, double Length, int Turns)
{
    public bool UsesSideLane { get; init; }
}
public sealed record MapExplorationStage(int Number, string FloorKey, string Color,
    IReadOnlyList<MapExplorationStep> Steps, string? TransferRoomId, string? ArrivalRoomId);
public sealed record MapExplorationRoute(IReadOnlyList<MapExplorationStage> Stages,
    IReadOnlyList<string> VisitedRoomIds, double Length, int Turns, string EntranceId, string ExitId,
    IReadOnlyList<string> SkippedTargetRoomIds)
{
    public IReadOnlyList<MapExplorationBranch> Branches { get; init; } = [];
    public IReadOnlyList<MapExplorationRoomVisit> RoomVisits { get; init; } = [];
    public IReadOnlySet<string>? ExplorationFloors { get; init; }
    public bool DenseRoutes { get; init; }
}

public static partial class MapExplorationRoutePlanner
{
    private sealed record Edge(string From, string To, string? ModuleId,
        IReadOnlyList<NormalizedPoint> Points, double Length, int Turns, int First, int Last, ulong TransferRooms,
        BigInteger Occupied, BigInteger Conflicts, BigInteger Crossings, bool SpecialTeleport, bool UsesSideLane, string? SourceLinkId,
        BigInteger Overlaps = default);
    private readonly record struct State(string Node, int Heading, ulong TransferRooms, uint Floors);
    private sealed record SearchLabel(State State, double Length, int Turns, BigInteger Occupied, SearchLabel? Previous, Edge? Edge,
        BigInteger Rooms = default, int Transfers = 0, BigInteger StageOccupied = default, Spur? Spur = null, bool Returning = false);
    // Forward edges of the current stage which a dense walk may still retrace.
    private sealed record Spur(Edge Edge, Spur? Next);
    // A dense stage turns around only in a room and walks back at most this
    // many other modules. Longer returns hide the walk under its own line.
    private const int MaxReturnModules = 2;
    private sealed record PathResult(IReadOnlyList<Edge> Edges, double Length, int Turns);
}
