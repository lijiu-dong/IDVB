namespace IDVBuff.Features.Maps;

public static partial class MapExplorationRoutePlanner
{
    // Side lanes belong to interactions between complete stages. Restore an
    // unshared stage as a connected unit, so individual channel fan-ins cannot
    // leave offsets or tiny return bends at otherwise empty room centers.
    private static PathResult CenterUnsharedStages(MapNavigationTopology topology, PathResult path,
        IReadOnlyDictionary<(string Link, string From, string To), Edge> originals, TransitTurns transit, bool closed, string exitId,
        CancellationToken token)
    {
        static string Original(string id) => id.EndsWith("@lane-a", StringComparison.Ordinal) || id.EndsWith("@lane-b", StringComparison.Ordinal)
            ? id[..^7] : id;
        var modules = topology.Modules.ToDictionary(m => m.Id);
        var floors = topology.Floors.ToDictionary(f => f.FloorKey);
        var edges = path.Edges.ToArray(); var stageIds = new int[edges.Length]; var stage = 0;
        for (var i = 0; i < edges.Length; i++) { stageIds[i] = stage; if (edges[i].ModuleId is null) stage++; }
        var changed = false;
        foreach (var id in stageIds.Distinct())
        {
            var indices = Enumerable.Range(0, edges.Length).Where(i => stageIds[i] == id && edges[i].ModuleId is not null).ToArray();
            Center(indices);
        }
        // A shared channel in one part of a stage must not keep unrelated
        // rooms offset. Fixed centers and unchanged door ports bound connected
        // runs which can be restored without moving either neighboring run.
        var run = new List<int>();
        for (var i = 0; i < edges.Length; i++)
        {
            if (edges[i].ModuleId is not null) run.Add(i);
            if (edges[i].ModuleId is null || Original(edges[i].To) == edges[i].To)
            { Center(run); run.Clear(); }
        }
        Center(run);
        if (!changed) return CompactTransit(path, transit);
        var turns = 0; var heading = 0;
        foreach (var edge in edges) { turns += edge.Turns + Bend(heading, edge.First); heading = edge.Last; }
        return CompactTransit(new(edges, path.Length, turns), transit);

        void Center(IReadOnlyList<int> indices)
        {
            if (!indices.Any(i => edges[i].UsesSideLane)) return;
            var candidate = edges.ToArray();
            foreach (var index in indices)
            {
                var edge = edges[index];
                if (edge.SourceLinkId is not null && originals.TryGetValue((edge.SourceLinkId, Original(edge.From), Original(edge.To)), out var original))
                    candidate[index] = original;
            }
            if (!Valid(candidate)) return;
            edges = candidate; changed = true;
        }

        bool Valid(IReadOnlyList<Edge> candidate)
        {
            if (!HasNoStageCrossings(CompactTransit(new(candidate, 0, 0), transit).Edges, token)) return false;
            for (var i = 0; i < candidate.Count; i++)
            {
                var a = candidate[i];
                if (a.ModuleId is null) continue;
                var floor = modules[a.ModuleId].FloorKey;
                for (var j = i + 1; j < candidate.Count; j++)
                {
                    var b = candidate[j];
                    if (b.ModuleId is null || modules[b.ModuleId].FloorKey != floor) continue;
                    if (stageIds[i] == stageIds[j]) continue;
                    var joint = j == i + 1 && MapNavigationRules.Same(a.Points[^1], b.Points[0]);
                    var closure = closed && i == 0 && j == candidate.Count - 1 && MapNavigationRules.Same(a.Points[0], b.Points[^1]);
                    if (MapExplorationRouteGeometry.Conflicts(a.Points, b.Points, joint || closure)) return false;
                    if (stageIds[i] != stageIds[j] && b.To != exitId
                        && MapExplorationRouteChannels.Crowded(a.Points, b.Points, floors[floor])) return false;
                }
            }
            return !candidate.Select((e, index) => (Edge: e, Stage: stageIds[index]))
                .Where(p => p.Edge.ModuleId is not null && p.Edge.To != exitId)
                .GroupBy(p => modules[p.Edge.ModuleId!].FloorKey)
                .Any(group => MapExplorationRouteGeometry.ExceedsLaneCapacity(group.Select(p => (p.Stage, p.Edge.Points)), floors[group.Key]));
        }
    }
}
