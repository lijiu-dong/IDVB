namespace IDVBuff.Features.Maps;

public sealed record MapExplorationBranch(int StageNumber, int AttachmentStepIndex,
    NormalizedPoint Junction, string TargetRoomId, IReadOnlyList<MapExplorationStep> Steps);
public sealed record MapExplorationRoomVisit(int Number, string ModuleId, int StageNumber, bool IsBranch);

public static class MapExplorationRoomVisits
{
    public static IReadOnlyList<MapExplorationRoomVisit> Create(MapNavigationTopology topology, MapExplorationRoute route,
        bool includeEmptyPassages = false)
    {
        var modules = topology.Modules.ToDictionary(m => m.Id);
        var numbers = new Dictionary<string, int>(); var marked = new HashSet<string>();
        var endpoints = route.Stages.SelectMany(s => new[] { s.TransferRoomId, s.ArrivalRoomId })
            .Where(id => id is not null).ToHashSet();
        var result = new List<MapExplorationRoomVisit>();
        void Visit(string? id, int stage, bool branch)
        {
            if (id is null || !modules.TryGetValue(id, out var module) || module.IsExcludedFromAutomaticRoutes || !MapExplorationRoutePlanner.IsRoom(module)) return;
            // Number loot stops and real stage transfers, not empty rooms
            // merely crossed on the way to a useful destination.
            if (!includeEmptyPassages && (!MapExplorationRoutePlanner.IsLootTarget(topology, module)
                || route.ExplorationFloors is { } floors && !floors.Contains(module.FloorKey)) && !endpoints.Contains(id)) return;
            var key = MapExplorationRoutePlanner.RoomKey(module);
            var first = !numbers.TryGetValue(key, out var number);
            if (first) numbers[key] = number = numbers.Count + 1;
            // A real stage transfer needs a symbol on its arrival landing too,
            // using the same physical room number. Other landings are passages.
            if ((first || endpoints.Contains(id)) && marked.Add(id)) result.Add(new(number, id, stage, branch));
        }
        foreach (var stage in route.Stages)
        {
            for (var i = 0; i < stage.Steps.Count; i++)
            {
                Visit(stage.Steps[i].ModuleId, stage.Number, false);
                foreach (var branch in route.Branches.Where(b => b.StageNumber == stage.Number && b.AttachmentStepIndex == i)
                    .OrderBy(b => PositionOnPath(b.Junction, stage.Steps[i].Points))
                    .ThenBy(b => b.TargetRoomId, StringComparer.Ordinal)) Visit(branch.TargetRoomId, stage.Number, true);
            }
            Visit(stage.TransferRoomId, stage.Number, false);
            Visit(stage.ArrivalRoomId, stage.Number + 1, false);
        }
        return result;
    }

    private static double PositionOnPath(NormalizedPoint junction, IReadOnlyList<NormalizedPoint> points)
    {
        var distance = 0d;
        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1]; var b = points[i];
            double Length(NormalizedPoint p, NormalizedPoint q) => Math.Sqrt(Math.Pow(p.X - q.X, 2) + Math.Pow(p.Y - q.Y, 2));
            if (MapExplorationRouteTree.OnPath(junction, [a, b])) return distance + Length(a, junction);
            distance += Length(a, b);
        }
        return double.PositiveInfinity;
    }
}
