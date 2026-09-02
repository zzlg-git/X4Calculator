using X4Calculator.Core.Calculation;
using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.UI.ViewModels;

/// <summary>星图运输投影器的已启用链路输入。调用方只传入用户仍勾选的链路。</summary>
public sealed record StationTransportMapRouteInput(
    StationTransportRoute Route,
    string WareName);

public enum StationTransportMapFlowDirection
{
    Outgoing,
    Incoming
}

/// <summary>不依赖 WPF 的星图世界显示坐标。</summary>
public readonly record struct StationTransportMapPoint(double X, double Y);

/// <summary>供折线动画使用的一个方向运输流；点序始终与货物实际运输方向一致。</summary>
public sealed record StationTransportMapFlow(
    StationTransportMapFlowDirection Direction,
    IReadOnlyList<StationTransportMapPoint> Points,
    IReadOnlyList<StationTransportMapWare> Wares);

/// <summary>一条可悬浮的无向线段，以及实际经过该段的两个方向货物集合。</summary>
public sealed record StationTransportMapSegment(
    StationTransportMapPoint Start,
    StationTransportMapPoint End,
    IReadOnlyList<StationTransportMapWare> OutgoingWares,
    IReadOnlyList<StationTransportMapWare> IncomingWares);

public sealed record StationTransportMapWare(string WareId, string Name);

/// <summary>当前选中站与另一座站之间的全部运输折线和去重线段。</summary>
public sealed record StationTransportMapCorridor(
    string OtherStationId,
    string OtherStationName,
    IReadOnlyList<StationTransportMapFlow> Flows,
    IReadOnlyList<StationTransportMapSegment> Segments);

/// <summary>
/// 把运输网络的精确 SectorRoute 投影为 StarMapView 可直接绘制的世界显示坐标。
/// 路径严格依次使用空间站、超级高速路端点、星门两端和目标空间站的 Display 坐标。
/// </summary>
public sealed class StationTransportMapRouteProjector
{
    public IReadOnlyList<StationTransportMapCorridor> Project(
        StarMapDB starMap,
        Station selectedStation,
        IEnumerable<StationTransportMapRouteInput> selectedLinks,
        IEnumerable<Station>? additionalStations = null)
    {
        ArgumentNullException.ThrowIfNull(starMap);
        ArgumentNullException.ThrowIfNull(selectedStation);
        ArgumentNullException.ThrowIfNull(selectedLinks);

        var stationsById = starMap.PlayerStations
            .Concat(additionalStations ?? [])
            .Where(station => !string.IsNullOrWhiteSpace(station.Id))
            .GroupBy(station => station.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        // 导入站来自 PlayerStations；已放置计划站由独立覆盖层补充。两者都使用当前地图显示坐标。
        if (!stationsById.ContainsKey(selectedStation.Id))
            return Array.Empty<StationTransportMapCorridor>();

        var candidates = new List<ProjectedCandidate>();
        foreach (var input in selectedLinks)
        {
            if (input?.Route is not { Path: { IsReachable: true } path } route)
                continue;

            var isOutgoing = route.SourceStationId.Equals(selectedStation.Id, StringComparison.OrdinalIgnoreCase);
            var isIncoming = route.TargetStationId.Equals(selectedStation.Id, StringComparison.OrdinalIgnoreCase);
            if (isOutgoing == isIncoming)
                continue;

            var otherStationId = isOutgoing ? route.TargetStationId : route.SourceStationId;
            if (!stationsById.TryGetValue(otherStationId, out var otherStation))
                continue;
            if (!stationsById.TryGetValue(route.SourceStationId, out var sourceStation) ||
                !stationsById.TryGetValue(route.TargetStationId, out var targetStation))
                continue;

            var points = BuildPoints(path, sourceStation, targetStation);
            if (points.Count < 2)
                continue;

            candidates.Add(new ProjectedCandidate(
                otherStation.Id,
                otherStation.Name,
                isOutgoing ? StationTransportMapFlowDirection.Outgoing : StationTransportMapFlowDirection.Incoming,
                points,
                new StationTransportMapWare(route.WareId, ResolveWareName(input))));
        }

        return candidates
            .GroupBy(candidate => candidate.OtherStationId, StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildCorridor(group.Key, group))
            .OrderBy(corridor => corridor.OtherStationName, StringComparer.Ordinal)
            .ThenBy(corridor => corridor.OtherStationId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static StationTransportMapCorridor BuildCorridor(
        string otherStationId,
        IEnumerable<ProjectedCandidate> candidates)
    {
        var items = candidates.ToArray();
        var flows = items
            .GroupBy(item => new FlowKey(item.Direction, CreateGeometryKey(item.Points)))
            .Select(group => new StationTransportMapFlow(
                group.Key.Direction,
                group.First().Points,
                SortWares(group.Select(item => item.Ware))))
            .OrderBy(flow => flow.Direction)
            .ThenBy(flow => CreateGeometryKey(flow.Points), StringComparer.Ordinal)
            .ToArray();

        var segments = new Dictionary<SegmentKey, SegmentAccumulator>();
        foreach (var item in items)
        {
            for (var index = 1; index < item.Points.Count; index++)
            {
                var start = item.Points[index - 1];
                var end = item.Points[index];
                if (start == end)
                    continue;

                var key = SegmentKey.Create(start, end);
                if (!segments.TryGetValue(key, out var segment))
                {
                    segment = new SegmentAccumulator(key.First, key.Second);
                    segments.Add(key, segment);
                }

                if (item.Direction == StationTransportMapFlowDirection.Outgoing)
                    segment.Outgoing.Add(item.Ware);
                else
                    segment.Incoming.Add(item.Ware);
            }
        }

        return new StationTransportMapCorridor(
            otherStationId,
            items.Select(item => item.OtherStationName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                ?? otherStationId,
            flows,
            segments.Values
                .OrderBy(segment => segment.Start.X)
                .ThenBy(segment => segment.Start.Y)
                .ThenBy(segment => segment.End.X)
                .ThenBy(segment => segment.End.Y)
                .Select(segment => new StationTransportMapSegment(
                    segment.Start,
                    segment.End,
                    SortWares(segment.Outgoing),
                    SortWares(segment.Incoming)))
                .ToArray());
    }

    private static IReadOnlyList<StationTransportMapPoint> BuildPoints(
        SectorRoute path,
        Station sourceStation,
        Station targetStation)
    {
        var points = new List<StationTransportMapPoint>();
        AddPoint(points, sourceStation.DisplayX, sourceStation.DisplayY);

        foreach (var edge in path.Edges)
        {
            if (edge.SuperHighway is { } highway)
            {
                var followsDeclaredDirection = edge.From.Id.Equals(
                    highway.SectorAId, StringComparison.OrdinalIgnoreCase);
                AddPoint(points,
                    followsDeclaredDirection ? highway.DisplayFromX : highway.DisplayToX,
                    followsDeclaredDirection ? highway.DisplayFromY : highway.DisplayToY);
                AddPoint(points,
                    followsDeclaredDirection ? highway.DisplayToX : highway.DisplayFromX,
                    followsDeclaredDirection ? highway.DisplayToY : highway.DisplayFromY);
                continue;
            }

            if (edge.IsCrossCluster && edge.Gate is { } departureGate && edge.ArrivalGate is { } arrivalGate)
            {
                AddPoint(points, departureGate.DisplayX, departureGate.DisplayY);
                AddPoint(points, arrivalGate.DisplayX, arrivalGate.DisplayY);
                continue;
            }

            // 不用扇区中心或世界坐标猜测缺失接口，否则会画出并不存在的直飞线。
            return Array.Empty<StationTransportMapPoint>();
        }

        AddPoint(points, targetStation.DisplayX, targetStation.DisplayY);
        return points;
    }

    private static void AddPoint(List<StationTransportMapPoint> points, double x, double y)
    {
        var point = new StationTransportMapPoint(NormalizeZero(x), NormalizeZero(y));
        if (points.Count == 0 || points[^1] != point)
            points.Add(point);
    }

    private static double NormalizeZero(double value) => value == 0 ? 0 : value;

    private static string ResolveWareName(StationTransportMapRouteInput input) =>
        string.IsNullOrWhiteSpace(input.WareName) ? input.Route.WareId : input.WareName;

    private static IReadOnlyList<StationTransportMapWare> SortWares(IEnumerable<StationTransportMapWare> wares) =>
        wares
            .GroupBy(ware => ware.WareId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(ware => ware.Name, StringComparer.Ordinal)
                .First())
            .OrderBy(ware => ware.Name, StringComparer.Ordinal)
            .ThenBy(ware => ware.WareId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string CreateGeometryKey(IEnumerable<StationTransportMapPoint> points) =>
        string.Join(";", points.Select(point =>
            $"{BitConverter.DoubleToInt64Bits(point.X):X16},{BitConverter.DoubleToInt64Bits(point.Y):X16}"));

    private sealed record ProjectedCandidate(
        string OtherStationId,
        string OtherStationName,
        StationTransportMapFlowDirection Direction,
        IReadOnlyList<StationTransportMapPoint> Points,
        StationTransportMapWare Ware);

    private readonly record struct FlowKey(
        StationTransportMapFlowDirection Direction,
        string GeometryKey);

    private readonly record struct PointKey(long XBits, long YBits) : IComparable<PointKey>
    {
        public static PointKey From(StationTransportMapPoint point) => new(
            BitConverter.DoubleToInt64Bits(point.X),
            BitConverter.DoubleToInt64Bits(point.Y));

        public int CompareTo(PointKey other)
        {
            var xComparison = XBits.CompareTo(other.XBits);
            return xComparison != 0 ? xComparison : YBits.CompareTo(other.YBits);
        }
    }

    private readonly record struct SegmentKey(
        PointKey FirstKey,
        PointKey SecondKey,
        StationTransportMapPoint First,
        StationTransportMapPoint Second)
    {
        public static SegmentKey Create(StationTransportMapPoint start, StationTransportMapPoint end)
        {
            var startKey = PointKey.From(start);
            var endKey = PointKey.From(end);
            return startKey.CompareTo(endKey) <= 0
                ? new SegmentKey(startKey, endKey, start, end)
                : new SegmentKey(endKey, startKey, end, start);
        }

    }

    private sealed class SegmentAccumulator(
        StationTransportMapPoint start,
        StationTransportMapPoint end)
    {
        public StationTransportMapPoint Start { get; } = start;
        public StationTransportMapPoint End { get; } = end;
        public HashSet<StationTransportMapWare> Outgoing { get; } = [];
        public HashSet<StationTransportMapWare> Incoming { get; } = [];
    }
}
