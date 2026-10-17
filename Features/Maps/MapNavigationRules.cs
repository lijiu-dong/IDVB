using System.Security.Cryptography;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public static partial class MapNavigationRules
{
    public const int MaxJsonBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 32
    };

    public static MapNavigationTopology Read(byte[] bytes)
    {
        if (bytes.Length > MaxJsonBytes) Fail("导航数据超过 8 MiB。");
        try
        {
            CheckShape(bytes);
            var topology = JsonSerializer.Deserialize<MapNavigationTopology>(bytes, JsonOptions)
                ?? throw new InvalidDataException("导航数据为空。");
            Validate(topology);
            return topology;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("导航 JSON 无效。", exception);
        }
    }

    public static void Validate(MapNavigationTopology topology, IEnumerable<string>? floorKeys = null)
    {
        if (topology.SchemaVersion != 1 || topology.CoordinateSystem != "normalized-top-left-y-down"
            || !double.IsFinite(topology.TurnPenalty) || topology.TurnPenalty < 0
            || topology.Floors is null || topology.Modules is null || topology.Nodes is null
            || topology.Doors is null || topology.Links is null || topology.FloorTransitions is null)
            Fail("不支持的导航结构或坐标系。");
        var floors = Index(topology.Floors!, f => f.FloorKey, 32);
        var modules = Index(topology.Modules!, m => m.Id, 4096);
        var nodes = Index(topology.Nodes!, n => n.Id, 16384);
        if (floors.Count == 0 || modules.Count == 0 || nodes.Count == 0) Fail("导航结构缺少楼层、模块或节点。");
        if (floorKeys is not null && floors.Keys.Except(floorKeys, StringComparer.Ordinal).Any())
            Fail("导航结构引用了地图中不存在的楼层。");
        foreach (var floor in floors.Values)
            if (floor.ImageWidth is < 1 or > 32768 || floor.ImageHeight is < 1 or > 32768
                || !IsSha256(floor.ImageSha256)) Fail("导航楼层图片信息无效。");
        foreach (var node in nodes.Values)
            if (!floors.ContainsKey(node.FloorKey) || node.Point?.IsValid is not true
                || node.Kind is not ("center" or "door" or "landing")) Fail("导航节点无效。");
        foreach (var module in modules.Values)
        {
            if (module.SourceRoomConfigId < 0 || !Enum.IsDefined(module.FloorTraversalKind)
                || module.SourceRoomConfigId > 0 && module.IsFloorConnector && !module.CanChangeFloors
                || topology.RoutingStyle == MapNavigationTopology.CurrentRoutingStyle
                    && topology.RouteMetadataVersion >= MapNavigationTopology.CurrentRouteMetadataVersion
                    && topology.SourceLevelId is not null && module.SourceRoomConfigId == 0)
                Fail("模板房间缺少已核对的源配置或楼层通行方式。");
            if (module.ChestCandidateCount < 0 || module.HighTierCandidateCount < 0
                || module.HighTierCandidateCount > module.ChestCandidateCount
                || !double.IsFinite(module.LootPriorityScore) || module.LootPriorityScore < 0
                || !double.IsFinite(module.ChestPreferenceWeight2) || module.ChestPreferenceWeight2 < 0
                || !double.IsFinite(module.ChestPreferenceWeight3) || module.ChestPreferenceWeight3 < 0)
                Fail("房间宝箱优先级数据无效。");
            if (!floors.ContainsKey(module.FloorKey) || string.IsNullOrWhiteSpace(module.Name)
                || module.Cells is null || module.Cells.Count is < 1 or > 4096
                || module.SafeCells is null || module.SafeCells.Count > 16384
                || topology.RoutingStyle == "geometry-safezone-v2" && module.SafeCells.Count == 0
                || !nodes.TryGetValue(module.CenterNodeId, out var center)
                || center.FloorKey != module.FloorKey || center.Kind != "center") Fail("导航模块无效。");
            foreach (var cell in module.Cells!.Concat(module.SafeCells!))
                if (cell is null || !double.IsFinite(cell.X) || !double.IsFinite(cell.Y)
                    || !double.IsFinite(cell.Width) || !double.IsFinite(cell.Height)
                    || cell.X < -1e-9 || cell.Y < -1e-9 || cell.Width <= 0 || cell.Height <= 0
                    || cell.X + cell.Width > 1 + 1e-9 || cell.Y + cell.Height > 1 + 1e-9)
                    Fail("导航模块范围无效。");
            foreach (var cell in module.SafeCells!)
                if (!ContainsRectangle(module.Cells, cell.X, cell.Y, cell.X + cell.Width, cell.Y + cell.Height))
                    Fail("安全区越出模块范围。");
            if (!module.Cells.Any(c => c.Contains(nodes[module.CenterNodeId].Point))) Fail("模块中心位于模块外。");
        }
        var doors = Index(topology.Doors!, d => d.Id, 16384);
        var doorByNode = new Dictionary<string, MapNavigationDoor>(StringComparer.Ordinal);
        foreach (var door in doors.Values)
        {
            if (!nodes.TryGetValue(door.NodeId, out var node) || node.Kind != "door"
                || !modules.TryGetValue(door.ModuleAId, out var a)
                || !modules.TryGetValue(door.ModuleBId, out var b) || a.Id == b.Id
                || a.FloorKey != node.FloorKey || b.FloorKey != node.FloorKey
                || !a.Cells.Any(c => c.Contains(node.Point)) || !b.Cells.Any(c => c.Contains(node.Point))
                || !doorByNode.TryAdd(door.NodeId, door)) Fail("门框必须连接同楼层的两个模块。");
        }
        if (nodes.Values.Any(n => n.Kind == "door" && !doorByNode.ContainsKey(n.Id))) Fail("门框节点缺少连接关系。");
        var links = Index(topology.Links!, l => l.Id, 65536);
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        long totalPoints = 0;
        foreach (var link in links.Values)
        {
            if (!modules.TryGetValue(link.ModuleId, out var module)
                || !nodes.TryGetValue(link.FromNodeId, out var from) || !nodes.TryGetValue(link.ToNodeId, out var to)
                || from.Id == to.Id || from.FloorKey != module.FloorKey || to.FloorKey != module.FloorKey
                || !Positive(link.Cost) || !Positive(link.Length) || link.Cost + 1e-8 < link.Length
                || link.Points is null || link.Points.Count is < 2 or > 8192) Fail("模块通道无效。");
            module = modules[link.ModuleId];
            foreach (var id in new[] { link.FromNodeId, link.ToNodeId })
                if (id != module.CenterNodeId && (!doorByNode.TryGetValue(id, out var door)
                    || (door.ModuleAId != module.Id && door.ModuleBId != module.Id)))
                    Fail("通道端点必须是本模块中心或相邻门框中心。");
            var pair = string.Join("|", new[] { link.FromNodeId, link.ToNodeId }.Order(StringComparer.Ordinal));
            if (!pairs.Add(module.Id + "|" + pair)) Fail("同模块通道重复。");
            totalPoints += link.Points!.Count;
            if (totalPoints > 250000) Fail("导航折线点数超过限制。");
            if (!Same(link.Points[0], nodes[link.FromNodeId].Point)
                || !Same(link.Points[^1], nodes[link.ToNodeId].Point)) Fail("通道端点偏离门框或模块中心。");
            foreach (var point in link.Points)
                if (point?.IsValid is not true || !module.Cells.Any(c => c.Contains(point))) Fail("通道越出模块范围。");
            // Check the entire segment, not just its vertices: prevents a chord
            // through the hollow part of an L/U-shaped module.
            var floor = floors[module.FloorKey];
            for (var i = 1; i < link.Points.Count; i++)
            {
                var a = link.Points[i - 1]; var b = link.Points[i];
                if (module.SafeCells.Count > 0 && !ContainsSegment(module.SafeCells, a, b)) Fail("通道越出墙体安全区。");
                var dx = (b.X - a.X) * floor.ImageWidth; var dy = (b.Y - a.Y) * floor.ImageHeight;
                var count = (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy) / 4);
                for (var j = 1; j < count; j++)
                {
                    var x = a.X + (b.X - a.X) * j / count;
                    var y = a.Y + (b.Y - a.Y) * j / count;
                    var contained = false;
                    foreach (var cell in module.Cells)
                        if (cell.Contains(x, y)) { contained = true; break; }
                    if (!contained)
                        Fail("通道穿过模块外部。");
                }
            }
        }
        foreach (var transition in Index(topology.FloorTransitions!, t => t.Id, 4096).Values)
        {
            if (!nodes.TryGetValue(transition.FromNodeId, out var a) || !nodes.TryGetValue(transition.ToNodeId, out var b)
                || !transition.IsSpecialTeleport && a.FloorKey == b.FloorKey || !Positive(transition.Cost)
                || string.IsNullOrWhiteSpace(transition.Evidence)) Fail("跨楼层通道必须有明确连接依据。");
            var from = topology.Modules.SingleOrDefault(m => m.CenterNodeId == transition.FromNodeId);
            var to = topology.Modules.SingleOrDefault(m => m.CenterNodeId == transition.ToNodeId);
            if (transition.IsSpecialTeleport)
            {
                if (!transition.Directed || from is null || to is null
                    || from.Kind == "corridor" || to.Kind == "corridor"
                    || from.IsDeadEnd || to.IsDeadEnd || from.Id == to.Id)
                    Fail("特殊房间传送必须是两个有效房间之间的单向连接。");
                if ((from!.SourceRoomConfigId > 0 || to!.SourceRoomConfigId > 0)
                    && (topology.SourceLevelId?.StartsWith("104", StringComparison.Ordinal) is not true
                        || from.SourceRoomConfigId != 10501 || to.SourceRoomConfigId != 10624
                        || from.FloorKey != "1f" || to.FloorKey != "b1f"))
                    Fail("特殊房间传送与已确认的源房间不一致。");
                continue;
            }
            if ((from?.SourceRoomConfigId ?? 0) == 0 && (to?.SourceRoomConfigId ?? 0) == 0) continue;
            if (from is null || to is null || !from.CanChangeFloors || !to.CanChangeFloors
                || from.SourceRoomConfigId != to.SourceRoomConfigId || from.SourceRoomId != to.SourceRoomId
                || from.FloorTraversalKind != to.FloorTraversalKind)
                Fail("源房间没有已确认的跨楼层通行能力。");
            var config = from!.SourceRoomConfigId;
            if (from.FloorTraversalKind == MapFloorTraversalKind.ObstacleJump)
            {
                if (!(from.FloorKey == "1f" && to!.FloorKey == "2f"
                    || from.FloorKey == "2f" && to!.FloorKey == "1f"))
                    Fail("餐厅障碍物通道只能连接已确认的 1F 与 2F。");
            }
            else if (from.FloorTraversalKind == MapFloorTraversalKind.Drop)
            {
                if (!transition.Directed || from.FloorKey != "1f" || to!.FloorKey != "b1f")
                    Fail("下落房间只允许从 1F 下落到 B1F。");
            }
            else
            {
                var lower = config is 10411 or 10622 ? "b1f" : "1f";
                var upper = config is 10411 or 10622 ? "1f" : "2f";
                if (!(from.FloorKey == lower && to!.FloorKey == upper
                    || from.FloorKey == upper && to!.FloorKey == lower))
                    Fail("楼梯连接的目标楼层与源房间配置不符。");
            }
        }
    }

    public static void ValidateForRouting(MapNavigationTopology topology)
    {
        Validate(topology);
        if (!MapSpecialRoomTeleports.HasCurrentMetadata(topology))
            Fail("模板特殊传送数据已过期，请重新打开路线规划以刷新导航数据。");
        if (topology.SourceLevelId is not null && topology.RoutingStyle == MapNavigationTopology.CurrentRoutingStyle
            && topology.RouteMetadataVersion < MapNavigationTopology.CurrentRouteMetadataVersion)
            Fail("模板楼层通行数据已过期，请重新打开路线规划以刷新导航数据。");
    }

    public static bool ContainsSegment(IReadOnlyList<MapNavigationCell> cells, NormalizedPoint a, NormalizedPoint b)
    {
        var intervals = new List<(double Start, double End)>();
        foreach (var cell in cells)
        {
            var begin = 0d; var end = 1d;
            bool Clip(double origin, double delta, double minimum, double maximum)
            {
                if (Math.Abs(delta) < 1e-14) return origin >= minimum - 1e-9 && origin <= maximum + 1e-9;
                var first = (minimum - origin) / delta; var last = (maximum - origin) / delta;
                if (first > last) (first, last) = (last, first);
                begin = Math.Max(begin, first); end = Math.Min(end, last);
                return begin <= end + 1e-9;
            }
            if (Clip(a.X, b.X - a.X, cell.X, cell.X + cell.Width)
                && Clip(a.Y, b.Y - a.Y, cell.Y, cell.Y + cell.Height)) intervals.Add((begin, end));
        }
        var covered = 0d;
        foreach (var interval in intervals.OrderBy(interval => interval.Start))
        {
            if (interval.Start > covered + 1e-9) return false;
            covered = Math.Max(covered, interval.End);
            if (covered >= 1 - 1e-9) return true;
        }
        return false;
    }

    public static bool ContainsRectangle(IReadOnlyList<MapNavigationCell> cells, double left, double top, double right, double bottom)
    {
        var ys = cells.SelectMany(cell => new[] { cell.Y, cell.Y + cell.Height })
            .Where(y => y > top && y < bottom).Append(top).Append(bottom).Distinct().Order().ToArray();
        for (var i = 1; i < ys.Length; i++)
            if (!ContainsSegment(cells, new() { X = left, Y = (ys[i - 1] + ys[i]) / 2 }, new() { X = right, Y = (ys[i - 1] + ys[i]) / 2 })) return false;
        return true;
    }

    public static async Task ValidateImagesAsync(MapNavigationTopology topology,
        IReadOnlyDictionary<string, string> images, CancellationToken cancellationToken = default)
    {
        Validate(topology, images.Keys);
        foreach (var floor in topology.Floors)
        {
            using var memory = IDVBuff.Core.Diagnostics.MemoryJournal.BeginOperation("Navigation.ValidateImage", floor.FloorKey);
            await using var input = File.OpenRead(images[floor.FloorKey]);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(hash, floor.ImageSha256, StringComparison.Ordinal))
                Fail($"楼层 {floor.FloorKey} 的底图与导航数据不一致，请重新生成导航数据。");
            var image = MapImageFileValidation.ValidateImageFile(images[floor.FloorKey], cancellationToken);
            if (image.Width != floor.ImageWidth || image.Height != floor.ImageHeight)
                Fail($"楼层 {floor.FloorKey} 的底图尺寸与导航数据不一致。");
        }
    }

    // Source world-floor numbers and editor floor keys need not have the same
    // ordinal. Import may rebind keys only when the complete image is identical.
    public static async Task<MapNavigationTopology> BindImportedFloorsAsync(MapNavigationTopology source,
        IReadOnlyDictionary<string, string> images, CancellationToken cancellationToken = default)
    {
        Validate(source);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in images)
        {
            await using var input = File.OpenRead(pair.Value);
            hashes[pair.Key] = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
        }
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var floor in source.Floors)
        {
            if (hashes.TryGetValue(floor.FloorKey, out var declared) && declared == floor.ImageSha256)
                keys[floor.FloorKey] = floor.FloorKey;
            else
            {
                var matching = hashes.Where(p => p.Value == floor.ImageSha256).Select(p => p.Key).ToArray();
                if (matching.Length != 1) Fail($"导航楼层 {floor.FloorKey} 无法唯一匹配当前地图原图。");
                keys[floor.FloorKey] = matching[0];
            }
        }
        if (keys.Values.Distinct(StringComparer.Ordinal).Count() != keys.Count) Fail("多个导航楼层映射到了同一地图楼层。");
        var topology = source.Clone();
        foreach (var floor in topology.Floors) floor.FloorKey = keys[floor.FloorKey];
        foreach (var module in topology.Modules) module.FloorKey = keys[module.FloorKey];
        foreach (var node in topology.Nodes) node.FloorKey = keys[node.FloorKey];
        await ValidateImagesAsync(topology, images, cancellationToken);
        return topology;
    }

    // Only called after an existing image binding was verified and IDVB itself
    // resampled/copied the whole image. Normalized geometry remains unchanged.
    internal static async Task RebindGeneratedImagesAsync(MapNavigationTopology topology,
        IReadOnlyDictionary<string, string> images, CancellationToken cancellationToken = default)
    {
        foreach (var floor in topology.Floors)
        {
            await using var input = File.OpenRead(images[floor.FloorKey]);
            floor.ImageSha256 = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant();
            var image = MapImageFileValidation.ValidateImageFile(images[floor.FloorKey], cancellationToken);
            floor.ImageWidth = image.Width; floor.ImageHeight = image.Height;
        }
    }

    private static Dictionary<string, T> Index<T>(List<T> values, Func<T, string> id, int maximum) where T : class
    {
        if (values.Count > maximum) Fail("导航结构数量超出限制。");
        var result = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is null) Fail("导航结构包含空对象。");
            var key = id(value!);
            if (string.IsNullOrWhiteSpace(key) || key.Length > 256 || !result.TryAdd(key, value!)) Fail("导航标识无效或重复。");
        }
        return result;
    }
    internal static bool Same(NormalizedPoint? a, NormalizedPoint? b) => a is not null && b is not null
        && Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;
    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string message) => throw new InvalidDataException(message);
}
