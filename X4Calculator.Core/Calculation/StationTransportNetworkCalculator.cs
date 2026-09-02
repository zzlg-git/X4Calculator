using X4Calculator.Core.Data;

namespace X4Calculator.Core.Calculation;

/// <summary>运输链路计算使用的空间站职责。</summary>
public enum StationTransportStationDuty
{
    Factory,
    Trade,
    Terminal
}

/// <summary>某货物在空间站上的报价方向快照。</summary>
public readonly record struct StationTransportOfferDirections(bool HasBuyOffer, bool HasSellOffer);

/// <summary>
/// 运输链路计算所需的导入空间站快照。
/// NetCapacityPerMinute 只应包含已建模块的净产能；规划模块不应进入此快照。
/// </summary>
public sealed record StationTransportStationSnapshot(
    string StationId,
    string StationName,
    string SectorId,
    StationTransportStationDuty Duty,
    int ManagerStars,
    IReadOnlyDictionary<string, double> NetCapacityPerMinute,
    IReadOnlyDictionary<string, StationTransportOfferDirections> OfferDirections)
{
    public double GetNetCapacity(string wareId) =>
        NetCapacityPerMinute.GetValueOrDefault(wareId);

    public StationTransportOfferDirections GetOfferDirections(string wareId) =>
        OfferDirections.GetValueOrDefault(wareId);
}

/// <summary>一条唯一的 source → target + ware 运输候选边。</summary>
public sealed record StationTransportRoute(
    string WareId,
    string SourceStationId,
    string SourceStationName,
    StationTransportStationDuty SourceDuty,
    string TargetStationId,
    string TargetStationName,
    StationTransportStationDuty TargetDuty,
    int JumpCount)
{
    /// <summary>供地图和距离展示复用的精确扇区路径。</summary>
    public SectorRoute? Path { get; init; }
}

/// <summary>一次后台计算得到的完整运输网络。</summary>
public sealed class StationTransportNetwork
{
    public StationTransportNetwork(IReadOnlyList<StationTransportRoute> routes)
    {
        Routes = routes;
    }

    public IReadOnlyList<StationTransportRoute> Routes { get; }
}

/// <summary>
/// 根据导入空间站的已建净产能和报价方向，计算可能的站间运输链路。
/// 每一对空间站仅枚举一次；两个方向均在同一轮中判断，避免为“运入”和“运出”重复搜索。
/// </summary>
public sealed class StationTransportNetworkCalculator
{
    private const double CapacityEpsilon = 0.0000001;

    public StationTransportNetwork Calculate(
        IEnumerable<StationTransportStationSnapshot> stations,
        SectorGraph sectorGraph,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stations);
        ArgumentNullException.ThrowIfNull(sectorGraph);

        var stationList = stations.ToArray();
        ValidateUniqueStationIds(stationList);

        var routes = new List<StationTransportRoute>();
        var seen = new HashSet<RouteKey>();
        var reachableRoutes = new Dictionary<ReachabilityKey, SectorRoute?>();

        for (var leftIndex = 0; leftIndex < stationList.Length; leftIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = stationList[leftIndex];
            for (var rightIndex = leftIndex + 1; rightIndex < stationList.Length; rightIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var right = stationList[rightIndex];
                if (string.Equals(left.StationId, right.StationId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var wareIds = left.NetCapacityPerMinute.Keys
                    .Concat(right.NetCapacityPerMinute.Keys)
                    .Concat(left.OfferDirections.Keys)
                    .Concat(right.OfferDirections.Keys)
                    .Where(wareId => !string.IsNullOrWhiteSpace(wareId))
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var wareId in wareIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TryAddRoute(left, right, wareId, sectorGraph, reachableRoutes, seen, routes);
                    TryAddRoute(right, left, wareId, sectorGraph, reachableRoutes, seen, routes);
                }
            }
        }

        return new StationTransportNetwork(routes
            .OrderBy(route => route.WareId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(route => route.SourceStationName, StringComparer.Ordinal)
            .ThenBy(route => route.TargetStationName, StringComparer.Ordinal)
            .ToArray());
    }

    private static void TryAddRoute(
        StationTransportStationSnapshot source,
        StationTransportStationSnapshot target,
        string wareId,
        SectorGraph sectorGraph,
        IDictionary<ReachabilityKey, SectorRoute?> reachableRoutes,
        ISet<RouteKey> seen,
        ICollection<StationTransportRoute> routes)
    {
        if (!CanTransport(source, target, wareId)) return;

        var reachabilityKey = new ReachabilityKey(
            source.SectorId, target.SectorId, Math.Max(0, source.ManagerStars));
        if (!reachableRoutes.TryGetValue(reachabilityKey, out var path))
        {
            var route = sectorGraph.FindRouteById(source.SectorId, target.SectorId);
            path = route is { IsReachable: true } && route.JumpCount <= reachabilityKey.ManagerStars
                ? route
                : null;
            reachableRoutes[reachabilityKey] = path;
        }
        if (path == null) return;

        var key = new RouteKey(source.StationId, target.StationId, wareId);
        if (!seen.Add(key)) return;

        routes.Add(new StationTransportRoute(
            wareId,
            source.StationId,
            source.StationName,
            source.Duty,
            target.StationId,
            target.StationName,
            target.Duty,
            path.JumpCount)
        {
            Path = path
        });
    }

    private static bool CanTransport(
        StationTransportStationSnapshot source,
        StationTransportStationSnapshot target,
        string wareId)
    {
        var sourceCapacity = source.GetNetCapacity(wareId);
        var targetCapacity = target.GetNetCapacity(wareId);
        var sourceOffers = source.GetOfferDirections(wareId);
        var targetOffers = target.GetOfferDirections(wareId);

        return source.Duty switch
        {
            StationTransportStationDuty.Factory when sourceCapacity > CapacityEpsilon => target.Duty switch
            {
                StationTransportStationDuty.Factory => targetCapacity < -CapacityEpsilon,
                StationTransportStationDuty.Trade => targetOffers.HasBuyOffer,
                StationTransportStationDuty.Terminal => targetOffers.HasBuyOffer,
                _ => false
            },
            StationTransportStationDuty.Trade when sourceOffers.HasSellOffer => target.Duty switch
            {
                StationTransportStationDuty.Factory => targetCapacity < -CapacityEpsilon,
                StationTransportStationDuty.Terminal => targetOffers.HasBuyOffer,
                _ => false
            },
            _ => false
        };
    }

    private static void ValidateUniqueStationIds(IEnumerable<StationTransportStationSnapshot> stations)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var station in stations)
        {
            if (string.IsNullOrWhiteSpace(station.StationId))
                throw new ArgumentException("运输链路计算要求每个空间站都有非空的唯一 ID。", nameof(stations));
            if (!ids.Add(station.StationId))
                throw new ArgumentException($"运输链路计算收到重复空间站 ID：{station.StationId}。", nameof(stations));
        }
    }

    private readonly record struct RouteKey(string SourceStationId, string TargetStationId, string WareId);
    private readonly record struct ReachabilityKey(string SourceSectorId, string TargetSectorId, int ManagerStars);
}
