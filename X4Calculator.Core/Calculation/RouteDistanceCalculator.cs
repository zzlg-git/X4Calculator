using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

/// <summary>
/// 运输路线距离计算（第 3 步）：把扇区级路径转换成"巡航飞行段"的距离明细。
///
/// 全程 = 起始段（用户输入：起点→起点 cluster 内第一个接口）
///      + 各 cluster 区内自动巡航段（星门/SH 之间、SH 出口→下一接口）
///      + 结束段（用户输入：终点 cluster 内最后一个接口→终点）。
///
/// 瞬时段（不计距离/时间）：星门/加速器穿越、超级高速路（SH）穿越 —— 与星门同类，巡航引擎不启动。
/// 巡航飞行段（计距离）：每段从上一个接口位置飞向下一个接口位置（SH 出口→下一 SH 入口 / 星门亦算）。
///   - 星门 / SH 入口 / SH 出口均用真实世界坐标（银河绝对坐标，xz 平面勾股，Y≈0）
///   - 直飞兜底边（无 SH 的扇区对）用扇区中心距近似
/// 坐标偏差来自直线近似 vs 游戏实际飞行（走曲线/避障），用户已确认十几到数十 km 可接受。
/// </summary>
public sealed class RouteDistanceCalculator
{
    private readonly StarMapDB _starMap;

    public RouteDistanceCalculator(StarMapDB starMap) => _starMap = starMap;

    /// <summary>
    /// 计算整条路线的距离。
    /// </summary>
    /// <param name="route">SectorGraph 输出的扇区级路径。</param>
    /// <param name="startPointToGateKm">起点→起点 cluster 内第一个接口（SH 入口/星门）距离（km，用户输入，覆盖起始段）。</param>
    /// <param name="endGateToEndPointKm">终点 cluster 内最后一个接口（SH 出口/星门）→终点距离（km，用户输入，覆盖结束段）。</param>
    public RouteDistanceResult Calculate(SectorRoute route, double startPointToGateKm, double endGateToEndPointKm)
    {
        ArgumentNullException.ThrowIfNull(route);

        // 沿路径遍历每条边，把"自动巡航飞行段"按所在 cluster 归组。
        // currentPos 跟踪当前接口位置（最近一次穿越/飞行的落脚点）。
        // 首段（起点→第一个接口）由用户输入覆盖：currentPos 初始 null → 第一条边不产生自动段。
        var autoByCluster = new Dictionary<string, List<FlightSegment>>(StringComparer.OrdinalIgnoreCase);
        Vec3? currentPos = null;
        string currentLabel = "起点";

        foreach (var edge in route.Edges)
        {
            var fromSector = edge.From;
            var clusterId = fromSector.ClusterId;

            if (edge.IsCrossCluster)
            {
                // 星门/加速器穿越：飞向门（From 扇区内）→ 瞬时穿越 → 落脚在目标 cluster 的对应门
                var gate = edge.Gate!;
                if (currentPos is not null)
                    AddSegment(autoByCluster, clusterId, fromSector.Name, currentLabel, $"门 {gate.Id}", currentPos.Value, gate.WorldPos);
                // SectorGraph 已按 TargetGateId/互逆连接名解析出精确抵达门；
                // 仅为兼容历史或手工构造的 RouteEdge 才回退到目标 cluster 推断。
                var counterpart = edge.ArrivalGate ?? FindCounterpart(gate, edge.To.ClusterId, fromSector.ClusterId);
                currentPos = counterpart?.WorldPos ?? edge.ArrivalWorldPos;
                currentLabel = $"门 {counterpart?.Id ?? edge.To.Name}";
            }
            else if (edge.SuperHighway is { } link)
            {
                // 超级高速路：飞向入口 zone（From 内）→ 瞬时穿越 → 落脚在出口 zone（To 内）。
                // ⚠️ SH 穿越本身瞬时不计；但出口后飞向下一接口的巡航段会由下一条边产生。
                var entrance = EntranceWorldPos(link, fromSector, edge.To);
                var exit = ExitWorldPos(link, fromSector, edge.To);
                if (currentPos is not null)
                    AddSegment(autoByCluster, clusterId, fromSector.Name, currentLabel, $"高速路 {link.Id} 入口", currentPos.Value, entrance);
                currentPos = exit;
                currentLabel = $"高速路 {link.Id} 出口";
            }
            else
            {
                // 直飞兜底（无 SH 的扇区对）：飞向 From 中心 → 直飞至 To 中心（整段巡航）
                if (currentPos is not null)
                    AddSegment(autoByCluster, clusterId, fromSector.Name, currentLabel, $"{fromSector.Name} 中心", currentPos.Value, fromSector.WorldPos);
                AddSegment(autoByCluster, clusterId, fromSector.Name, $"{fromSector.Name} 中心", $"{edge.To.Name} 中心",
                    fromSector.WorldPos, edge.To.WorldPos);
                currentPos = edge.To.WorldPos;
                currentLabel = $"{edge.To.Name} 中心";
            }
        }
        // 循环结束：currentPos = 终点 cluster 内最后一个接口；最后一段（→用户终点）由 end 用户输入覆盖。

        // 组装 Legs：按路径顺序把所有 cluster 的自动段列出（含起点/终点 cluster）。
        var legs = new List<ClusterLeg>();
        double autoTotal = 0;
        var clusterOrder = new List<string>();
        foreach (var s in route.Sectors)
            if (!clusterOrder.Contains(s.ClusterId, StringComparer.OrdinalIgnoreCase))
                clusterOrder.Add(s.ClusterId);
        for (int k = 0; k < clusterOrder.Count; k++)
        {
            var clusterId = clusterOrder[k];
            var segs = autoByCluster.GetValueOrDefault(clusterId) ?? new List<FlightSegment>();
            double d = segs.Sum(s => s.DistanceKm);
            autoTotal += d;
            legs.Add(new ClusterLeg(clusterId, isStart: k == 0, isEnd: k == clusterOrder.Count - 1, d, segs));
        }

        var totalKm = startPointToGateKm + autoTotal + endGateToEndPointKm;
        return new RouteDistanceResult(startPointToGateKm, endGateToEndPointKm, legs, autoTotal, totalKm);
    }

    /// <summary>
    /// 使用空间站的扇区内坐标计算整条路线距离。
    /// 同扇区直接计算两站之间的三维直线距离；跨扇区时分别计算
    /// 起点站→首个出发接口与最后一个抵达接口→终点站的距离。
    /// </summary>
    /// <param name="route">SectorGraph 输出的可达路线。</param>
    /// <param name="startSectorPosition">起点站相对起点 Sector 的坐标（米）。</param>
    /// <param name="endSectorPosition">终点站相对终点 Sector 的坐标（米）。</param>
    public RouteDistanceResult Calculate(
        SectorRoute route,
        Vec3 startSectorPosition,
        Vec3 endSectorPosition)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!route.IsReachable || route.Sectors.Count == 0)
            throw new ArgumentException("路线必须可达且包含起终扇区。", nameof(route));

        var startWorldPosition = route.Sectors[0].WorldPos + startSectorPosition;
        var endWorldPosition = route.Sectors[^1].WorldPos + endSectorPosition;

        if (route.Sectors.Count == 1)
        {
            var directKm = startWorldPosition.DistanceTo(endWorldPosition) / 1000.0;
            return Calculate(route, directKm, 0);
        }

        if (route.Edges.Count == 0)
            throw new ArgumentException("跨扇区路线必须包含接口边。", nameof(route));

        var startKm = startWorldPosition.DistanceTo(route.Edges[0].DepartureWorldPos) / 1000.0;
        var endKm = route.Edges[^1].ArrivalWorldPos.DistanceTo(endWorldPosition) / 1000.0;
        return Calculate(route, startKm, endKm);
    }

    /// <summary>把一段巡航飞行加入对应 cluster 的自动段列表。</summary>
    private static void AddSegment(
        Dictionary<string, List<FlightSegment>> byCluster, string clusterId, string sectorName,
        string from, string to, Vec3 fromPos, Vec3 toPos)
    {
        if (!byCluster.TryGetValue(clusterId, out var list))
            byCluster[clusterId] = list = new List<FlightSegment>();
        list.Add(new FlightSegment(sectorName, from, to, DistanceKm(fromPos, toPos)));
    }

    /// <summary>SH 边 From→To 方向下，入口 zone 世界坐标（位于 From 扇区内）。</summary>
    private static Vec3 EntranceWorldPos(SectorLink link, SectorInfo from, SectorInfo to)
        => string.Equals(from.Id, link.SectorAId, StringComparison.OrdinalIgnoreCase)
            ? link.EntranceWorldPos   // From==A：入口即 link 入口
            : link.ExitWorldPos;      // From==B（反向）：入口即 link 出口

    /// <summary>SH 边 From→To 方向下，出口 zone 世界坐标（位于 To 扇区内）。</summary>
    private static Vec3 ExitWorldPos(SectorLink link, SectorInfo from, SectorInfo to)
        => string.Equals(from.Id, link.SectorAId, StringComparison.OrdinalIgnoreCase)
            ? link.ExitWorldPos
            : link.EntranceWorldPos;

    /// <summary>在 inClusterId 内找指向 fromClusterId 的门（成对门的另一侧）。</summary>
    private GateInfo? FindCounterpart(GateInfo gate, string inClusterId, string fromClusterId)
    {
        return _starMap.Gates.Values.FirstOrDefault(g =>
            string.Equals(g.ClusterId, inClusterId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(g.TargetClusterId, fromClusterId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>xz 平面距离（km），Y 忽略（X4 门在黄道平面，Y≈0）。</summary>
    private static double DistanceKm(Vec3 a, Vec3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz) / 1000.0;
    }
}

/// <summary>一次路线距离计算结果。</summary>
public sealed class RouteDistanceResult
{
    /// <summary>起始段距离（km，用户输入：起点→起点 cluster 内第一个接口）。</summary>
    public double StartPointToGateKm { get; }

    /// <summary>结束段距离（km，用户输入：终点 cluster 内最后一个接口→终点）。</summary>
    public double EndGateToEndPointKm { get; }

    /// <summary>各 cluster 的自动巡航飞行段（按路径顺序，含起点/终点 cluster）。</summary>
    public IReadOnlyList<ClusterLeg> Legs { get; }

    /// <summary>全部自动巡航飞行段距离总和（km，不含用户输入两端段）。</summary>
    public double AutomaticTotalKm { get; }

    /// <summary>全程 = 起始 + 自动 + 结束（km）。</summary>
    public double TotalKm { get; }

    public RouteDistanceResult(double start, double end, IReadOnlyList<ClusterLeg> legs, double automaticTotal, double total)
    {
        StartPointToGateKm = start;
        EndGateToEndPointKm = end;
        Legs = legs;
        AutomaticTotalKm = automaticTotal;
        TotalKm = total;
    }
}

/// <summary>路径中一个星区（cluster）的区内巡航飞行段明细。</summary>
public sealed class ClusterLeg
{
    /// <summary>所在星区 ID。</summary>
    public string ClusterId { get; }

    /// <summary>是否为起点 cluster（第一段由用户输入覆盖）。</summary>
    public bool IsStart { get; }

    /// <summary>是否为终点 cluster（最后一段由用户输入覆盖）。</summary>
    public bool IsEnd { get; }

    /// <summary>本 cluster 自动巡航飞行距离总和（km）。</summary>
    public double DistanceKm { get; }

    /// <summary>自动巡航飞行段明细（瞬时段——星门/SH 穿越——不在此列）。</summary>
    public IReadOnlyList<FlightSegment> Segments { get; }

    public ClusterLeg(string clusterId, bool isStart, bool isEnd, double distanceKm, IReadOnlyList<FlightSegment> segments)
    {
        ClusterId = clusterId;
        IsStart = isStart;
        IsEnd = isEnd;
        DistanceKm = distanceKm;
        Segments = segments;
    }
}

/// <summary>一段连续巡航飞行（计距离/耗时；星门/SH 瞬时段不在此列）。</summary>
public sealed class FlightSegment
{
    /// <summary>所在扇区名（From 侧扇区，UI 显示用）。</summary>
    public string SectorName { get; }

    /// <summary>起点位置描述（接口/门/扇区中心等）。</summary>
    public string From { get; }

    /// <summary>终点位置描述。</summary>
    public string To { get; }

    /// <summary>本段距离（km）。</summary>
    public double DistanceKm { get; }

    public FlightSegment(string sectorName, string from, string to, double distanceKm)
    {
        SectorName = sectorName;
        From = from;
        To = to;
        DistanceKm = distanceKm;
    }
}
