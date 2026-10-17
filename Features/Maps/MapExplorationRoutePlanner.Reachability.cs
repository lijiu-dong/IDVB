using System.Numerics;

namespace IDVBuff.Features.Maps;

public static partial class MapExplorationRoutePlanner
{
    // Exit reachability is an optimistic pruning query against a fixed label:
    // it does not collect rooms or add future occupancy. Cache only positive
    // witnesses, rechecking all their exclusions against each new label.
    private sealed class ExitReachability
    {
        private readonly record struct Arc(int To, Edge Edge);
        private readonly record struct Witness(BigInteger Before, BigInteger After, BigInteger Stage, ulong Transfers);
        private readonly Dictionary<string, int> _ids;
        private readonly Arc[][] _adjacent;
        private readonly List<Witness>?[] _witnesses;
        private readonly int[] _seen, _parents, _pending;
        private readonly Edge?[] _parentEdges;
        private readonly int _exit;
        private readonly bool _dense;
        private readonly CancellationToken _token;
        private int _generation;

        public ExitReachability(IReadOnlyDictionary<string, List<Edge>> adjacent, string exit, bool dense, CancellationToken token)
        {
            _ids = adjacent.Keys.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index);
            _adjacent = _ids.Keys.Select(id => adjacent[id].Where(e => !e.SpecialTeleport)
                .Select(e => new Arc(_ids[e.To], e)).ToArray()).ToArray();
            _exit = _ids[exit]; _dense = dense; _token = token;
            var count = adjacent.Count * 2;
            _witnesses = new List<Witness>?[count]; _seen = new int[count];
            _parents = new int[count]; _pending = new int[count]; _parentEdges = new Edge?[count];
        }

        public bool CanFinish(SearchLabel label) => CanFinishFrom(label, [label.State.Node]);

        // A dense stage cannot walk over its own channel, except by retracing
        // its spur. The caller lists the nodes such a retrace can still reach.
        public bool CanFinishFrom(SearchLabel label, IEnumerable<string> starts)
        {
            _token.ThrowIfCancellationRequested();
            var past = _dense ? label.Occupied & ~label.StageOccupied : label.Occupied;
            bool Fits(Witness witness) => (witness.Transfers & label.State.TransferRooms) == 0
                && (past & witness.Before) == BigInteger.Zero && (label.Occupied & witness.After) == BigInteger.Zero
                && (!_dense || (label.StageOccupied & witness.Stage) == BigInteger.Zero);
            foreach (var node in starts)
                if (_ids[node] == _exit || From(_ids[node] * 2)) return true;
            return false;

            bool From(int start)
            {
                if (_witnesses[start]?.Any(Fits) == true) return true;
                if (++_generation == int.MaxValue) { Array.Clear(_seen); _generation = 1; }
                _seen[start] = _generation; _parents[start] = -1; _pending[0] = start;
                var count = 1;
                while (count > 0)
                {
                    _token.ThrowIfCancellationRequested();
                    var current = _pending[--count];
                    if (_witnesses[current] is { } cached)
                        foreach (var witness in cached)
                            if (Fits(witness)) { Remember(current, witness); return true; }
                    var changed = (current & 1) != 0;
                    foreach (var arc in _adjacent[current / 2])
                    {
                        var edge = arc.Edge;
                        if ((edge.TransferRooms & label.State.TransferRooms) != 0
                            || ((changed ? label.Occupied : past) & edge.Conflicts) != BigInteger.Zero
                            || _dense && !changed && (label.StageOccupied & (edge.Crossings | edge.Overlaps)) != BigInteger.Zero) continue;
                        var next = arc.To * 2 + (changed || edge.ModuleId is null ? 1 : 0);
                        if (arc.To == _exit) { Remember(current, Prepend(current, edge, default)); return true; }
                        if (_seen[next] == _generation) continue;
                        _seen[next] = _generation; _parents[next] = current; _parentEdges[next] = edge;
                        _pending[count++] = next;
                    }
                }
                return false;
            }
        }

        private static Witness Prepend(int state, Edge edge, Witness suffix) => (state & 1) != 0
            ? new(0, edge.Conflicts | suffix.After, 0, edge.TransferRooms | suffix.Transfers)
            : new(edge.Conflicts | suffix.Before, suffix.After, edge.Crossings | edge.Overlaps | suffix.Stage, edge.TransferRooms | suffix.Transfers);

        private void Remember(int state, Witness witness)
        {
            while (true)
            {
                var cached = _witnesses[state] ??= [];
                if (!cached.Contains(witness))
                {
                    if (cached.Count == 4) cached.RemoveAt(0);
                    cached.Add(witness);
                }
                var parent = _parents[state];
                if (parent < 0) return;
                witness = Prepend(parent, _parentEdges[state]!, witness); state = parent;
            }
        }
    }
}
