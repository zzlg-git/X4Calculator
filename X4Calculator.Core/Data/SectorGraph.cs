using X4Calculator.Core.Models;
using X4Calculator.Core.Calculation;
using System.Text.RegularExpressions;

namespace X4Calculator.Core.Data;

/// <summary>
/// 扇区导航图与最短路径计算（离线复刻游戏内星门跳数寻路）。
///
/// 图构建规则（对齐 slepher/x4-station-calculator 的 buildSectorGraph + 用户确认的扩展）：
/// - 节点 = 全部扇区（Sector）。
/// - 同 cluster 内：仅高速路（0 跳）。每条超级高速路（SectorLink）方向 = 入口→出口，
///   双向成对时两个方向各有边；单向高速路（LaneCount == 1，如 Savage Spur I→II）只建沿方向边。
///   ⚠️ 明确禁止直飞兜底（2026-08-12 用户修正）：X4 中同 cluster 扇区间不直接跨空间飞行，
///   船会走星门/高速路绕行而非直飞数万公里；此前兜底把 Savage Spur I↔II 误建成双向
///   0 跳直飞（51054km），被 Dijkstra 用来"省跳"产生虚假路径。
/// - 跨 cluster：扇区 A 有门指向 cluster C，且 cluster C 内某扇区 B 有门指向 A 所在 cluster
///   （成对门存在）→ A↔B 双向边（1 跳），边记录对应穿越方向的门。
///   ⚠️ 必须有回程门才建边，避免单向门造成虚假连通。
///
/// 跳数语义：同 cluster 移动 = 0 跳，跨 cluster（穿越星门/加速器）= 1 跳。
/// 最短路径用 Dijkstra：权重 = 跳数×JumpWeight + 边距离（跳数主导，同跳数下优先更短距离）。
/// 不限制跳数上限（&gt;5 跳由 UI 层自行处理）。
/// </summary>
public sealed class SectorGraph
{
    private static readonly Regex StaticGateIdPattern = new(
        @"^connection_ClusterGate(?<src>\d+)To(?<dst>\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly Dictionary<string, SectorInfo> _sectorsById;
    private readonly Dictionary<string, List<SectorInfo>> _sectorsByName; // 英文/中文名 -> 扇区（容错重名）
    private readonly Dictionary<string, List<RouteEdge>> _adjacency;

    // Dijkstra 权重：跳数主导（JumpWeight >> 任何距离累计）
    private const double JumpWeight = 1e12;

    private SectorGraph(
        Dictionary<string, SectorInfo> sectorsById,
        Dictionary<string, List<SectorInfo>> sectorsByName,
        Dictionary<string, List<RouteEdge>> adjacency)
    {
        _sectorsById = sectorsById;
        _sectorsByName = sectorsByName;
        _adjacency = adjacency;
    }

    /// <summary>
    /// 从已加载的星图构建导航图。
    /// </summary>
    public static SectorGraph Build(StarMapDB starMap)
    {
        var byId = new Dictionary<string, SectorInfo>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, List<SectorInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sector in starMap.Sectors.Values)
        {
            byId[sector.Id] = sector;
            foreach (var alias in SectorNameAliases(sector))
            {
                var key = alias.ToLowerInvariant();
                if (!byName.TryGetValue(key, out var list))
                {
                    list = new List<SectorInfo>();
                    byName[key] = list;
                }
                if (!list.Contains(sector)) list.Add(sector);
            }
        }

        var adj = new Dictionary<string, List<RouteEdge>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in byId.Keys)
            adj[id] = new List<RouteEdge>();

        // ── 1. 同 cluster 边（0 跳）：仅高速路（每条 sechighway 有方向） ────
        // 明确禁止直飞兜底（用户 2026-08-12）：X4 中同 cluster 扇区间不直接跨空间飞行，
        // 船会走星门/高速路绕行而非直飞数万公里。单向高速路（LaneCount == 1，如
        // Savage Spur I→II）只建沿方向边，反向不可达 —— 此前兜底把 Savage Spur I↔II
        // 误建成双向 0 跳直飞（51054km），被 Dijkstra 用来"省跳"导致虚假路径。
        foreach (var group in byId.Values.GroupBy(s => s.ClusterId, StringComparer.OrdinalIgnoreCase))
        {
            var sectors = group.ToList();
            if (sectors.Count <= 1) continue;

            // 每条 sechighway 连接方向：SectorA(入口) → SectorB(出口)；双向成对时两条各建一方向。
            foreach (var link in starMap.SectorLinks)
            {
                if (!string.Equals(link.ClusterId, group.Key, StringComparison.OrdinalIgnoreCase)) continue;
                if (!byId.TryGetValue(link.SectorAId, out var ha) || !byId.TryGetValue(link.SectorBId, out var hb)) continue;
                if (string.Equals(ha.Id, hb.Id, StringComparison.OrdinalIgnoreCase)) continue; // 自环防御
                var hwDist = ha.WorldPos.DistanceTo(hb.WorldPos);
                var departure = string.Equals(ha.Id, link.SectorAId, StringComparison.OrdinalIgnoreCase)
                    ? link.EntranceWorldPos : link.ExitWorldPos;
                var arrival = string.Equals(ha.Id, link.SectorAId, StringComparison.OrdinalIgnoreCase)
                    ? link.ExitWorldPos : link.EntranceWorldPos;
                adj[ha.Id].Add(new RouteEdge(ha, hb, isCrossCluster: false, gate: null, superHighway: link,
                    departureWorldPos: departure, arrivalWorldPos: arrival) { DistanceMeters = hwDist, SearchCost = hwDist });
            }
        }

        // ── 2. 跨 cluster 边（1 跳）：成对门存在才建边 ────────────────────
        var gatesBySector = starMap.Gates.Values
            .GroupBy(g => g.SectorId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var sectorsByCluster = byId.Values
            .GroupBy(s => s.ClusterId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var addedCross = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // "from|to" 去重
        void AddCrossPair(SectorInfo from, GateInfo departureGate, SectorInfo to, GateInfo arrivalGate)
        {
            if (addedCross.Add($"{from.Id}|{to.Id}"))
                adj[from.Id].Add(new RouteEdge(from, to, isCrossCluster: true, gate: departureGate, superHighway: null,
                    departureWorldPos: departureGate.WorldPos, arrivalWorldPos: arrivalGate.WorldPos,
                    arrivalGate: arrivalGate));
            if (addedCross.Add($"{to.Id}|{from.Id}"))
                adj[to.Id].Add(new RouteEdge(to, from, isCrossCluster: true, gate: arrivalGate, superHighway: null,
                    departureWorldPos: arrivalGate.WorldPos, arrivalWorldPos: departureGate.WorldPos,
                    arrivalGate: departureGate));
        }

        foreach (var (sectorId, gates) in gatesBySector)
        {
            if (!byId.TryGetValue(sectorId, out var fromSector)) continue;
            foreach (var gate in gates)
            {
                if (string.IsNullOrEmpty(gate.TargetClusterId)) continue;
                if (!sectorsByCluster.TryGetValue(gate.TargetClusterId, out var targetSectors)) continue;

                // 精确目标门可能位于目标 cluster 的任一 Sector。找到后只建立这一对门，
                // 不能再把同一个出发门虚构连接到该 cluster 的其它回程门。
                var exactReturnGate = targetSectors
                    .SelectMany(targetSector =>
                        gatesBySector.GetValueOrDefault(targetSector.Id) ?? Enumerable.Empty<GateInfo>())
                    .Where(candidate => string.Equals(
                        candidate.TargetClusterId, fromSector.ClusterId, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault(candidate =>
                        string.Equals(gate.TargetGateId, candidate.Id, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(candidate.TargetGateId, gate.Id, StringComparison.OrdinalIgnoreCase) ||
                        AreReciprocalStaticGateIds(gate.Id, candidate.Id));
                if (exactReturnGate != null && byId.TryGetValue(exactReturnGate.SectorId, out var exactTargetSector))
                {
                    AddCrossPair(fromSector, gate, exactTargetSector, exactReturnGate);
                    continue;
                }

                // 显式 ID 是权威关系；若当前数据集找不到该门，也不能退回到其它门。
                if (!string.IsNullOrWhiteSpace(gate.TargetGateId) || HasStaticGateIdentity(gate.Id)) continue;

                // 静态数据没有精确目标门 ID 时才按目标 cluster 的既有规则回退。
                foreach (var toSector in targetSectors)
                {
                    var returnGate = (gatesBySector.GetValueOrDefault(toSector.Id) ?? Enumerable.Empty<GateInfo>())
                        .FirstOrDefault(candidate => string.Equals(
                            candidate.TargetClusterId, fromSector.ClusterId, StringComparison.OrdinalIgnoreCase) &&
                            string.IsNullOrWhiteSpace(candidate.TargetGateId));
                    if (returnGate is null) continue;
                    AddCrossPair(fromSector, gate, toSector, returnGate);
                }
            }
        }

        return new SectorGraph(byId, byName, adj);
    }

    private static bool HasStaticGateIdentity(string gateId) => StaticGateIdPattern.IsMatch(gateId);

    private static bool AreReciprocalStaticGateIds(string leftId, string rightId)
    {
        var left = StaticGateIdPattern.Match(leftId);
        var right = StaticGateIdPattern.Match(rightId);
        return left.Success && right.Success &&
               left.Groups["src"].Value.Equals(right.Groups["dst"].Value, StringComparison.OrdinalIgnoreCase) &&
               left.Groups["dst"].Value.Equals(right.Groups["src"].Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 按英文名、中文名或“英文｜中文”显示名查询最短路径，忽略大小写。
    /// 返回 null 表示起/终点名称无法解析；否则可用 <see cref="SectorRoute.IsReachable"/> 区分可达与否。
    /// </summary>
    public SectorRoute? FindRoute(string startSectorName, string endSectorName)
    {
        var start = ResolveSector(startSectorName);
        var end = ResolveSector(endSectorName);
        return FindRouteInternal(start, end, startSectorName, endSectorName);
    }

    /// <summary>按扇区 macro ID 查询最短路径。</summary>
    public SectorRoute? FindRouteById(string startSectorId, string endSectorId)
    {
        var start = _sectorsById.GetValueOrDefault(startSectorId);
        var end = _sectorsById.GetValueOrDefault(endSectorId);
        return FindRouteInternal(start, end, start?.Name ?? startSectorId, end?.Name ?? endSectorId);
    }

    /// <summary>
    /// 按实际飞行耗时寻找路线。搜索状态是“刚穿越的接口”，因此每条边的权重为上一个出口到
    /// 下一个入口的巡航耗时；所有边权非负，Dijkstra 不会为了降低成本而重复经过扇区。
    /// 两端用户输入的距离属于已选首/末接口，故不参与不同接口之间的比较。
    /// </summary>
    public SectorRoute? FindFastestRoute(
        string startSectorName, string endSectorName, FlightStats stats,
        string? startNextSectorName = null, string? endPreviousSectorName = null)
    {
        var start = ResolveSector(startSectorName);
        var end = ResolveSector(endSectorName);
        if (start is null || end is null)
            return SectorRoute.Create(startSectorName, endSectorName, false, -1, Array.Empty<SectorInfo>(), Array.Empty<RouteEdge>());
        if (string.Equals(start.Id, end.Id, StringComparison.OrdinalIgnoreCase))
            return SectorRoute.Create(startSectorName, endSectorName, true, 0, new[] { start }, Array.Empty<RouteEdge>());

        var initial = (_adjacency.GetValueOrDefault(start.Id) ?? new List<RouteEdge>())
            .Where(e => string.IsNullOrWhiteSpace(startNextSectorName) || NameMatches(e.To, startNextSectorName))
            .ToList();
        if (initial.Count == 0)
            return SectorRoute.Create(startSectorName, endSectorName, false, -1, Array.Empty<SectorInfo>(), Array.Empty<RouteEdge>());

        // state = the last traversed edge.  This retains its arrival coordinate for the next segment cost.
        var dist = new Dictionary<RouteEdge, double>();
        var prev = new Dictionary<RouteEdge, RouteEdge?>();
        var pq = new PriorityQueue<RouteEdge, double>();
        foreach (var edge in initial)
        {
            dist[edge] = 0; // 起点→首接口由用户输入，不能用扇区中心距离替代。
            prev[edge] = null;
            pq.Enqueue(edge, 0);
        }

        RouteEdge? best = null;
        while (pq.Count > 0)
        {
            var edge = pq.Dequeue();
            var currentCost = dist[edge];
            if (edge.To.Id.Equals(end.Id, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(endPreviousSectorName) || NameMatches(edge.From, endPreviousSectorName)))
            {
                best = edge;
                break;
            }

            foreach (var next in _adjacency.GetValueOrDefault(edge.To.Id) ?? Enumerable.Empty<RouteEdge>())
            {
                // 正耗时最短路无需回环；显式禁止立即回退也避免零距离接口产生无意义往返。
                if (next.To.Id.Equals(edge.From.Id, StringComparison.OrdinalIgnoreCase)) continue;
                var meters = edge.ArrivalWorldPos.DistanceTo(next.DepartureWorldPos);
                var nextCost = currentCost + FlightTimeCalculator.CalculateSegmentTime(stats, meters, endAtGate: true);
                if (!dist.TryGetValue(next, out var old) || nextCost < old)
                {
                    dist[next] = nextCost;
                    prev[next] = edge;
                    pq.Enqueue(next, nextCost);
                }
            }
        }

        if (best is null)
            return SectorRoute.Create(startSectorName, endSectorName, false, -1, Array.Empty<SectorInfo>(), Array.Empty<RouteEdge>());

        var edges = new List<RouteEdge>();
        for (RouteEdge? cursor = best; cursor is not null; cursor = prev[cursor]) edges.Add(cursor);
        edges.Reverse();
        var sectors = new List<SectorInfo> { start };
        sectors.AddRange(edges.Select(e => e.To));
        return SectorRoute.Create(startSectorName, endSectorName, true, edges.Count(e => e.IsCrossCluster), sectors, edges);
    }

    /// <summary>
    /// 枚举首接口×末接口组合（扇区接口数很小），保留仍可能因起终点的未知相对位置而成为最优的组合。
    /// 用户输入距离始终是“当前所选接口”的距离；其它接口的距离差不超过同扇区接口间最大距离，
    /// 因而可换算成可补偿的耗时上界，淘汰明显绕行而不引入任意秒数阈值。
    /// </summary>
    public RouteChoiceOptions GetRouteChoiceOptions(
        string startSectorName, string endSectorName, FlightStats stats,
        double startPointToInterfaceKm, double endInterfaceToPointKm)
    {
        var start = ResolveSector(startSectorName);
        var end = ResolveSector(endSectorName);
        if (start is null || end is null || start.Id.Equals(end.Id, StringComparison.OrdinalIgnoreCase))
            return RouteChoiceOptions.Empty;

        var starts = (_adjacency.GetValueOrDefault(start.Id) ?? new List<RouteEdge>())
            .GroupBy(e => e.To.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var ends = _adjacency.Values.SelectMany(x => x)
            .Where(e => e.To.Id.Equals(end.Id, StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.From.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (starts.Count == 0 || ends.Count == 0) return RouteChoiceOptions.Empty;

        var alternatives = new List<(string StartNext, string EndPrevious, SectorRoute Route, double Seconds)>();
        foreach (var first in starts)
        foreach (var last in ends)
        {
            var route = FindFastestRoute(startSectorName, endSectorName, stats, first.To.Name, last.From.Name);
            if (route is { IsReachable: true })
                alternatives.Add((first.To.Name, last.From.Name, route, GetInternalSearchSeconds(route, stats)));
        }
        if (alternatives.Count == 0) return RouteChoiceOptions.Empty;

        var bestSeconds = alternatives.Min(x => x.Seconds);
        var compensableSeconds = GetEndpointDistanceVariationSeconds(stats, startPointToInterfaceKm,
                MaxPairDistanceMeters(starts.Select(e => e.DepartureWorldPos)), endAtGate: true)
            + GetEndpointDistanceVariationSeconds(stats, endInterfaceToPointKm,
                MaxPairDistanceMeters(ends.Select(e => e.ArrivalWorldPos)), endAtGate: false);
        var viable = alternatives.Where(x => x.Seconds <= bestSeconds + compensableSeconds).ToList();
        return new RouteChoiceOptions(
            viable.Select(x => x.StartNext).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            viable.Select(x => x.EndPrevious).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>距离 D 的已知接口到未知同扇区接口，距离至多变化 span；返回严格按当前飞行模型计算的最大耗时变化。</summary>
    private static double GetEndpointDistanceVariationSeconds(FlightStats stats, double distanceKm, double spanMeters, bool endAtGate)
    {
        var distanceMeters = Math.Max(0, distanceKm) * 1000.0;
        var lower = Math.Max(0, distanceMeters - spanMeters);
        var upper = distanceMeters + spanMeters;
        var known = FlightTimeCalculator.CalculateSegmentTime(stats, distanceMeters, endAtGate);
        return Math.Max(
            Math.Abs(FlightTimeCalculator.CalculateSegmentTime(stats, lower, endAtGate) - known),
            Math.Abs(FlightTimeCalculator.CalculateSegmentTime(stats, upper, endAtGate) - known));
    }

    private static double MaxPairDistanceMeters(IEnumerable<Vec3> positions)
    {
        var list = positions.ToList();
        double max = 0;
        for (var i = 0; i < list.Count; i++)
        for (var j = i + 1; j < list.Count; j++)
            max = Math.Max(max, list[i].DistanceTo(list[j]));
        return max;
    }

    /// <summary>路线中首/末用户段以外的已知接口间耗时。</summary>
    private static double GetInternalSearchSeconds(SectorRoute route, FlightStats stats)
    {
        double seconds = 0;
        for (var i = 1; i < route.Edges.Count; i++)
        {
            var previous = route.Edges[i - 1];
            var current = route.Edges[i];
            seconds += FlightTimeCalculator.CalculateSegmentTime(
                stats, previous.ArrivalWorldPos.DistanceTo(current.DepartureWorldPos), endAtGate: true);
        }
        return seconds;
    }

    /// <summary>查询两扇区间的跳数（跨 cluster 边数）；名称无效或不可达返回 null。</summary>
    public int? GetJumpDistance(string startSectorName, string endSectorName)
    {
        var route = FindRoute(startSectorName, endSectorName);
        return route is { IsReachable: true } ? route.JumpCount : null;
    }

    private SectorRoute? FindRouteInternal(SectorInfo? start, SectorInfo? end, string startName, string endName)
    {
        if (start is null || end is null)
            return SectorRoute.Create(startName, endName, isReachable: false, jumpCount: -1,
                sectors: Array.Empty<SectorInfo>(), edges: Array.Empty<RouteEdge>());

        if (string.Equals(start.Id, end.Id, StringComparison.OrdinalIgnoreCase))
            return SectorRoute.Create(startName, endName, isReachable: true, jumpCount: 0,
                sectors: new[] { start }, edges: Array.Empty<RouteEdge>());

        // Dijkstra：权重 = 跳数×JumpWeight + 边距离（跳数主导；同跳数下优先高速路/更短距离）
        var dist = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [start.Id] = 0 };
        var prev = new Dictionary<string, (string From, RouteEdge Edge)>(StringComparer.OrdinalIgnoreCase);
        var finalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pq = new PriorityQueue<string, double>();
        pq.Enqueue(start.Id, 0);

        while (pq.Count > 0)
        {
            var current = pq.Dequeue();
            if (!finalized.Add(current)) continue; // 已确定最短权重
            if (current == end.Id) break;

            var curDist = dist[current];
            foreach (var edge in _adjacency.GetValueOrDefault(current) ?? Enumerable.Empty<RouteEdge>())
            {
                if (finalized.Contains(edge.To.Id)) continue;
                var nd = curDist + (edge.IsCrossCluster ? JumpWeight : 0) + edge.SearchCost;
                if (!dist.TryGetValue(edge.To.Id, out var prevDist) || nd < prevDist)
                {
                    dist[edge.To.Id] = nd;
                    prev[edge.To.Id] = (current, edge);
                    pq.Enqueue(edge.To.Id, nd);
                }
            }
        }

        if (!prev.ContainsKey(end.Id))
            return SectorRoute.Create(startName, endName, isReachable: false, jumpCount: -1,
                sectors: Array.Empty<SectorInfo>(), edges: Array.Empty<RouteEdge>());

        // 回溯重建路径
        var sectors = new List<SectorInfo>();
        var edges = new List<RouteEdge>();
        var cursor = end.Id;
        while (cursor != start.Id)
        {
            var (fromId, edge) = prev[cursor];
            sectors.Add(_sectorsById[cursor]);
            edges.Add(edge);
            cursor = fromId;
        }
        sectors.Add(start);
        sectors.Reverse();
        edges.Reverse();

        var jump = edges.Count(e => e.IsCrossCluster);
        return SectorRoute.Create(startName, endName, isReachable: true, jumpCount: jump,
            sectors: sectors, edges: edges);
    }

    /// <summary>解析英文名、中文名或双语显示名；重名取第一个。找不到返回 null。</summary>
    private SectorInfo? ResolveSector(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        foreach (var alias in SplitNameParts(input))
        {
            var match = _sectorsByName.GetValueOrDefault(alias.ToLowerInvariant())?.FirstOrDefault();
            if (match != null) return match;
        }
        return null;
    }

    private static bool NameMatches(SectorInfo sector, string input)
        => SplitNameParts(input).Any(inputPart => SectorNameAliases(sector)
            .Any(alias => alias.Equals(inputPart, StringComparison.OrdinalIgnoreCase)));

    private static IEnumerable<string> SectorNameAliases(SectorInfo sector)
        => SplitNameParts($"{sector.Name}｜{sector.SearchDisplayName}").Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> SplitNameParts(string value)
        => value.Split(['｜', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part));


}

/// <summary>界面需要展示的首/末接口扇区候选。</summary>
public sealed class RouteChoiceOptions
{
    public static RouteChoiceOptions Empty { get; } = new(Array.Empty<string>(), Array.Empty<string>());

    public IReadOnlyList<string> StartNextHopOptions { get; }
    public IReadOnlyList<string> EndPreviousHopOptions { get; }

    public RouteChoiceOptions(IReadOnlyList<string> startNextHopOptions, IReadOnlyList<string> endPreviousHopOptions)
    {
        StartNextHopOptions = startNextHopOptions;
        EndPreviousHopOptions = endPreviousHopOptions;
    }
}

/// <summary>
/// 一次最短路径查询结果。起终点有效且可达时，包含路径扇区序列与逐条边信息。
/// </summary>
public sealed class SectorRoute
{
    /// <summary>起点显示名（用户输入原样）。</summary>
    public string StartSectorName { get; }

    /// <summary>终点显示名（用户输入原样）。</summary>
    public string EndSectorName { get; }

    /// <summary>是否可达（起终点有效且图中存在路径）。</summary>
    public bool IsReachable { get; }

    /// <summary>跨 cluster 边数 = 星门/加速器穿越次数。不可达时为 -1。</summary>
    public int JumpCount { get; }

    /// <summary>路径经过的扇区序列（含首尾）。</summary>
    public IReadOnlyList<SectorInfo> Sectors { get; }

    /// <summary>相邻扇区间的边（数量 = Sectors.Count - 1）。</summary>
    public IReadOnlyList<RouteEdge> Edges { get; }

    internal static SectorRoute Create(
        string start, string end, bool isReachable, int jumpCount,
        IReadOnlyList<SectorInfo> sectors, IReadOnlyList<RouteEdge> edges)
        => new(start, end, isReachable, jumpCount, sectors, edges);

    private SectorRoute(
        string start, string end, bool isReachable, int jumpCount,
        IReadOnlyList<SectorInfo> sectors, IReadOnlyList<RouteEdge> edges)
    {
        StartSectorName = start;
        EndSectorName = end;
        IsReachable = isReachable;
        JumpCount = jumpCount;
        Sectors = sectors;
        Edges = edges;
    }
}

/// <summary>
/// 路径中相邻两扇区之间的一条边。
/// </summary>
public sealed class RouteEdge
{
    /// <summary>起点扇区。</summary>
    public SectorInfo From { get; }

    /// <summary>终点扇区。</summary>
    public SectorInfo To { get; }

    /// <summary>是否跨 cluster（此边计 1 跳）。</summary>
    public bool IsCrossCluster { get; }

    /// <summary>跨 cluster 时穿越的星门/加速器（含世界坐标，供后续距离计算）。同 cluster 边为 null。</summary>
    public GateInfo? Gate { get; }

    /// <summary>跨 cluster 穿越后抵达的目标星门/加速器。同 cluster 边为 null。</summary>
    public GateInfo? ArrivalGate { get; }

    /// <summary>同 cluster 边上的双向超级高速路（如有）；直飞时为 null。</summary>
    public SectorLink? SuperHighway { get; }

    /// <summary>沿当前方向进入接口的坐标。</summary>
    public Vec3 DepartureWorldPos { get; }

    /// <summary>穿越后抵达目标扇区的接口坐标。</summary>
    public Vec3 ArrivalWorldPos { get; }

    /// <summary>这条边的近似飞行距离（米）。跨 cluster 门边暂为 0（第 3 步细化）；高速路/直飞用扇区中心距。</summary>
    public double DistanceMeters { get; set; }

    /// <summary>Dijkstra 搜索权重中的"距离"部分（内部用；直飞兜底边放大以确保优先高速路）。</summary>
    public double SearchCost { get; set; }

    public RouteEdge(
        SectorInfo from,
        SectorInfo to,
        bool isCrossCluster,
        GateInfo? gate,
        SectorLink? superHighway,
        Vec3? departureWorldPos = null,
        Vec3? arrivalWorldPos = null,
        GateInfo? arrivalGate = null)
    {
        From = from;
        To = to;
        IsCrossCluster = isCrossCluster;
        Gate = gate;
        ArrivalGate = arrivalGate;
        SuperHighway = superHighway;
        DepartureWorldPos = departureWorldPos ?? gate?.WorldPos ?? from.WorldPos;
        ArrivalWorldPos = arrivalWorldPos ?? to.WorldPos;
    }

    /// <summary>人类可读描述（调试/测试用）。</summary>
    public override string ToString()
        => IsCrossCluster
            ? $"{From.Name} → {To.Name} [跨cluster 门:{Gate?.Id}]"
            : $"{From.Name} → {To.Name} [同cluster {(SuperHighway is null ? "直飞" : $"高速路:{SuperHighway.Id}")}]";
}
