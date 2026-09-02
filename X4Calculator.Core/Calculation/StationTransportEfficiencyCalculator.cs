using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

/// <summary>一套可用于站间运输效率计算的完整舰船配置。</summary>
public sealed record StationTransportShipConfiguration(
    ShipInfo Ship,
    EngineInfo Engine,
    ThrusterInfo Thruster,
    int PilotingStars);

/// <summary>站间路线中一段需要计时的飞行计划。</summary>
public readonly record struct StationTransportRoutePlanSegment(
    double DistanceKm,
    bool EndAtGate,
    bool IsGateCrossing = false);

/// <summary>站间运输效率计算结果。</summary>
public sealed class StationTransportEfficiencyResult
{
    public RouteDistanceResult Distance { get; }
    public IReadOnlyList<StationTransportRoutePlanSegment> RoutePlan { get; }
    public FlightStats FlightStats { get; }
    public double TotalSeconds { get; }
    public double EfficiencyM3PerSecond { get; }

    public StationTransportEfficiencyResult(
        RouteDistanceResult distance,
        IReadOnlyList<StationTransportRoutePlanSegment> routePlan,
        FlightStats flightStats,
        double totalSeconds,
        double efficiencyM3PerSecond)
    {
        Distance = distance;
        RoutePlan = routePlan;
        FlightStats = flightStats;
        TotalSeconds = totalSeconds;
        EfficiencyM3PerSecond = efficiencyM3PerSecond;
    }
}

/// <summary>
/// 把存档空间站坐标、扇区路线与完整舰船配置组合为运输耗时和效率。
/// 分段和辅助耗时均先四舍五入到 0.1 秒再求和，与舰船比较页的现有口径一致。
/// </summary>
public sealed class StationTransportEfficiencyCalculator
{
    private readonly RouteDistanceCalculator _distanceCalculator;

    public StationTransportEfficiencyCalculator(StarMapDB starMap)
    {
        ArgumentNullException.ThrowIfNull(starMap);
        _distanceCalculator = new RouteDistanceCalculator(starMap);
    }

    public StationTransportEfficiencyResult Calculate(
        SectorRoute route,
        Vec3 sourceSectorPosition,
        Vec3 targetSectorPosition,
        StationTransportShipConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configuration.Ship);
        ArgumentNullException.ThrowIfNull(configuration.Engine);
        ArgumentNullException.ThrowIfNull(configuration.Thruster);

        var distance = _distanceCalculator.Calculate(route, sourceSectorPosition, targetSectorPosition);
        var routePlan = BuildRoutePlan(route, distance);
        var stats = FlightCalculator.Calculate(
            configuration.Ship,
            configuration.Engine,
            configuration.Thruster,
            configuration.PilotingStars);
        var totalSeconds = CalculateTotalSeconds(stats, configuration.Ship, routePlan);
        var efficiency = totalSeconds > 0 && configuration.Ship.CargoCapacity > 0
            ? configuration.Ship.CargoCapacity / totalSeconds
            : 0;

        return new StationTransportEfficiencyResult(distance, routePlan, stats, totalSeconds, efficiency);
    }

    /// <summary>
    /// 把路线距离转换为飞行计划。跨扇区起点与首接口重合时，
    /// 仍保留一段 0 km / 6.5 s 的接口穿越，但不把它算作巡航启动。
    /// </summary>
    public static IReadOnlyList<StationTransportRoutePlanSegment> BuildRoutePlan(
        SectorRoute route,
        RouteDistanceResult distance)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(distance);

        var plan = new List<StationTransportRoutePlanSegment>();
        if (route.Sectors.Count == 1)
        {
            plan.Add(new StationTransportRoutePlanSegment(distance.TotalKm, EndAtGate: false));
            return plan;
        }

        if (distance.StartPointToGateKm > 0)
            plan.Add(new StationTransportRoutePlanSegment(distance.StartPointToGateKm, EndAtGate: true));
        else
            plan.Add(new StationTransportRoutePlanSegment(0, EndAtGate: true, IsGateCrossing: true));

        foreach (var segment in distance.Legs.SelectMany(leg => leg.Segments))
            plan.Add(new StationTransportRoutePlanSegment(segment.DistanceKm, EndAtGate: true));

        plan.Add(new StationTransportRoutePlanSegment(distance.EndGateToEndPointKm, EndAtGate: false));
        return plan;
    }

    /// <summary>
    /// 按舰船比较页的现有口径求总耗时：飞行段 + 180° 旋转 + L/XL 进港与离港。
    /// M/S 舰船不计进离港耗时。
    /// </summary>
    public static double CalculateTotalSeconds(
        FlightStats stats,
        ShipInfo ship,
        IReadOnlyList<StationTransportRoutePlanSegment> routePlan,
        bool includeDock = true,
        bool includeUndock = true)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(ship);
        ArgumentNullException.ThrowIfNull(routePlan);

        double total = 0;
        foreach (var segment in routePlan)
        {
            if (segment.DistanceKm > 0)
            {
                total += Math.Round(
                    FlightTimeCalculator.CalculateSegmentTimeKm(stats, segment.DistanceKm, segment.EndAtGate),
                    1,
                    MidpointRounding.AwayFromZero);
            }
            else if (segment.IsGateCrossing)
            {
                total += FlightTimeCalculator.GateCrossingSeconds;
            }
        }

        var rotationSeconds = FlightTimeCalculator.CalculateRotationTime(stats);
        if (rotationSeconds > 0)
            total += Math.Round(rotationSeconds, 1, MidpointRounding.AwayFromZero);

        if (ship.SizeCategory is "L" or "XL")
        {
            if (includeDock)
            {
                total += Math.Round(
                    FlightTimeCalculator.CalculateDockTime(stats),
                    1,
                    MidpointRounding.AwayFromZero);
            }
            if (includeUndock && ship.Length > 0)
            {
                total += Math.Round(
                    FlightTimeCalculator.CalculateUndockTime(stats, ship.Length),
                    1,
                    MidpointRounding.AwayFromZero);
            }
        }

        return total;
    }
}
