namespace X4Calculator.Core.Calculation;

/// <summary>一条候选运输边在稳态下实际承担的货物流量。</summary>
public sealed record StationTransportFlowAllocation(
    StationTransportRoute Route,
    double UnitsPerMinute,
    string InitiatingStationId);

/// <summary>
/// 把空间站净产能分配到已启用运输边。计算完全忽略当前库存：有限负产能先作为硬需求满足，
/// 剩余正产能再由终端站吸收；贸易站只中转已经从上游取得的同量货物。
/// </summary>
public sealed class StationTransportFlowCalculator
{
    private const double FlowEpsilon = 0.0000001d;

    public IReadOnlyList<StationTransportFlowAllocation> Calculate(
        IEnumerable<StationTransportStationSnapshot> stations,
        IEnumerable<StationTransportRoute> enabledRoutes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stations);
        ArgumentNullException.ThrowIfNull(enabledRoutes);
        cancellationToken.ThrowIfCancellationRequested();

        var stationList = stations.ToArray();
        var routes = enabledRoutes
            .Where(route => route.SourceCanInitiate || route.TargetCanInitiate)
            .ToArray();
        var stationsById = stationList
            .Where(station => !string.IsNullOrWhiteSpace(station.StationId))
            .GroupBy(station => station.StationId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var allocations = new List<StationTransportFlowAllocation>();

        foreach (var wareRoutes in routes
                     .GroupBy(route => route.WareId, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = new WareAllocationContext(
                wareRoutes.Key,
                stationList,
                stationsById,
                wareRoutes.ToArray(),
                cancellationToken);
            allocations.AddRange(context.Calculate());
        }

        return allocations
            .OrderBy(allocation => allocation.Route.WareId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(allocation => allocation.Route.SourceStationName, StringComparer.Ordinal)
            .ThenBy(allocation => allocation.Route.TargetStationName, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed class WareAllocationContext
    {
        private readonly string _wareId;
        private readonly IReadOnlyList<StationTransportStationSnapshot> _stations;
        private readonly IReadOnlyDictionary<string, StationTransportStationSnapshot> _stationsById;
        private readonly IReadOnlyDictionary<string, StationTransportRoute[]> _incomingByTarget;
        private readonly Dictionary<string, double> _remainingFactorySupply;
        private readonly Dictionary<StationTransportRoute, double> _flowByRoute = new();
        private readonly CancellationToken _cancellationToken;

        public WareAllocationContext(
            string wareId,
            IReadOnlyList<StationTransportStationSnapshot> stations,
            IReadOnlyDictionary<string, StationTransportStationSnapshot> stationsById,
            IReadOnlyList<StationTransportRoute> routes,
            CancellationToken cancellationToken)
        {
            _wareId = wareId;
            _stations = stations;
            _stationsById = stationsById;
            _incomingByTarget = routes
                .Where(route => stationsById.ContainsKey(route.SourceStationId) &&
                                stationsById.ContainsKey(route.TargetStationId))
                .GroupBy(route => route.TargetStationId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
            _remainingFactorySupply = stations
                .Where(station => station.Duty == StationTransportStationDuty.Factory &&
                                  station.GetNetCapacity(wareId) > FlowEpsilon)
                .ToDictionary(
                    station => station.StationId,
                    station => station.GetNetCapacity(wareId),
                    StringComparer.OrdinalIgnoreCase);
            _cancellationToken = cancellationToken;
        }

        public IReadOnlyList<StationTransportFlowAllocation> Calculate()
        {
            foreach (var target in _stations
                         .Where(station => station.Duty == StationTransportStationDuty.Factory &&
                                           station.GetNetCapacity(_wareId) < -FlowEpsilon)
                         .OrderBy(station => station.StationId, StringComparer.OrdinalIgnoreCase))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var demand = Math.Min(-target.GetNetCapacity(_wareId), RemainingSupply);
                if (demand <= FlowEpsilon) break;
                AllocateToTarget(target.StationId, demand, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }

            AllocateRemainingSupplyToTerminals();

            return _flowByRoute
                .Where(item => item.Value > FlowEpsilon)
                .Select(item => new StationTransportFlowAllocation(
                    item.Key,
                    item.Value,
                    item.Key.SourceCanInitiate
                        ? item.Key.SourceStationId
                        : item.Key.TargetStationId))
                .ToArray();
        }

        private double RemainingSupply => _remainingFactorySupply.Values.Sum();

        private void AllocateRemainingSupplyToTerminals()
        {
            var activeTargets = _stations
                .Where(station => station.Duty == StationTransportStationDuty.Terminal &&
                                  _incomingByTarget.ContainsKey(station.StationId))
                .OrderBy(station => station.StationId, StringComparer.OrdinalIgnoreCase)
                .Select(station => station.StationId)
                .ToList();

            while (activeTargets.Count > 0 && RemainingSupply > FlowEpsilon)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var share = RemainingSupply / activeTargets.Count;
                var nextTargets = new List<string>();
                var allocatedThisRound = 0d;
                foreach (var targetId in activeTargets)
                {
                    var allocated = AllocateToTarget(
                        targetId,
                        share,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    allocatedThisRound += allocated;
                    if (allocated + FlowEpsilon >= share)
                        nextTargets.Add(targetId);
                }

                if (allocatedThisRound <= FlowEpsilon) break;
                activeTargets = nextTargets;
            }
        }

        private double AllocateToTarget(
            string targetStationId,
            double requested,
            ISet<string> visitedTargets)
        {
            if (requested <= FlowEpsilon || !visitedTargets.Add(targetStationId) ||
                !_incomingByTarget.TryGetValue(targetStationId, out var incoming))
                return 0;

            try
            {
                var remaining = requested;
                foreach (var priorityGroup in incoming
                             .OrderBy(route => route.JumpCount)
                             .ThenBy(RouteDistanceRank)
                             .ThenBy(route => route.SourceStationName, StringComparer.Ordinal)
                             .GroupBy(route => new RoutePriority(route.JumpCount, RouteDistanceRank(route))))
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var allocated = AllocateEvenly(priorityGroup.ToArray(), remaining, visitedTargets);
                    remaining -= allocated;
                    if (remaining <= FlowEpsilon) break;
                }
                return requested - remaining;
            }
            finally
            {
                visitedTargets.Remove(targetStationId);
            }
        }

        private double AllocateEvenly(
            IReadOnlyList<StationTransportRoute> routes,
            double requested,
            ISet<string> visitedTargets)
        {
            var active = routes.ToList();
            var remaining = requested;
            while (active.Count > 0 && remaining > FlowEpsilon)
            {
                var share = remaining / active.Count;
                var next = new List<StationTransportRoute>();
                var allocatedThisRound = 0d;
                foreach (var route in active)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    var allocated = ReserveFromRoute(route, share, visitedTargets);
                    allocatedThisRound += allocated;
                    if (allocated + FlowEpsilon >= share)
                        next.Add(route);
                }

                if (allocatedThisRound <= FlowEpsilon) break;
                remaining -= allocatedThisRound;
                active = next;
            }
            return requested - remaining;
        }

        private double ReserveFromRoute(
            StationTransportRoute route,
            double requested,
            ISet<string> visitedTargets)
        {
            if (requested <= FlowEpsilon ||
                !_stationsById.TryGetValue(route.SourceStationId, out var source))
                return 0;

            double allocated;
            if (source.Duty == StationTransportStationDuty.Factory)
            {
                var available = _remainingFactorySupply.GetValueOrDefault(source.StationId);
                allocated = Math.Min(requested, available);
                if (allocated > FlowEpsilon)
                    _remainingFactorySupply[source.StationId] = available - allocated;
            }
            else if (source.Duty == StationTransportStationDuty.Trade)
            {
                allocated = AllocateToTarget(source.StationId, requested, visitedTargets);
            }
            else
            {
                allocated = 0;
            }

            if (allocated > FlowEpsilon)
                _flowByRoute[route] = _flowByRoute.GetValueOrDefault(route) + allocated;
            return allocated;
        }

        private static double RouteDistanceRank(StationTransportRoute route)
        {
            if (route.Path == null) return double.MaxValue;
            return route.Path.Edges.Sum(edge => Math.Max(0, edge.SearchCost));
        }

        private readonly record struct RoutePriority(int JumpCount, double DistanceRank);
    }
}

/// <summary>某一空间站货船池需要承担的一条稳态工作量。</summary>
public readonly record struct StationTransportFleetWorkload(
    double FlowM3PerSecond,
    double OneWayEfficiencyM3PerSecond);

/// <summary>货船池的连续需求、向上取整数量和产能加权等效效率。</summary>
public sealed record StationTransportFleetRequirement(
    double RequiredShipEquivalent,
    long RequiredShipCount,
    double WeightedEfficiencyM3PerSecond)
{
    public static StationTransportFleetRequirement Empty { get; } = new(0, 0, 0);
}

/// <summary>
/// 将稳态流量换算为货船数量。首版按无回程货物处理，因此每条单程工作量乘以 2；
/// 多种货物直接累加实际工作量，不按货物种类数平均分割运力。
/// </summary>
public static class StationTransportFleetCalculator
{
    public static StationTransportFleetRequirement Calculate(
        IEnumerable<StationTransportFleetWorkload> workloads)
    {
        ArgumentNullException.ThrowIfNull(workloads);
        var usable = workloads.Where(item =>
                double.IsFinite(item.FlowM3PerSecond) && item.FlowM3PerSecond > 0 &&
                double.IsFinite(item.OneWayEfficiencyM3PerSecond) && item.OneWayEfficiencyM3PerSecond > 0)
            .ToArray();
        if (usable.Length == 0) return StationTransportFleetRequirement.Empty;

        var oneWayShipEquivalent = usable.Sum(item =>
            item.FlowM3PerSecond / item.OneWayEfficiencyM3PerSecond);
        var requiredShipEquivalent = oneWayShipEquivalent * 2d;
        var requiredShipCount = (long)Math.Ceiling(requiredShipEquivalent);
        var totalFlow = usable.Sum(item => item.FlowM3PerSecond);
        var weightedEfficiency = oneWayShipEquivalent > 0
            ? totalFlow / oneWayShipEquivalent
            : 0;
        return new StationTransportFleetRequirement(
            requiredShipEquivalent,
            requiredShipCount,
            weightedEfficiency);
    }
}
