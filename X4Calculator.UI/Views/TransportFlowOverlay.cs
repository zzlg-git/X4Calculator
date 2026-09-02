using System.Windows;
using System.Windows.Media;

namespace X4Calculator.UI.Views;

/// <summary>渲染运输通道中单向运送的一种货物。</summary>
public sealed record TransportFlowCargo(string WareId, string WareName);

/// <summary>
/// 屏幕空间中的一条运输通道。各点按选中空间站到远端空间站的方向排列；
/// 运出箭头沿该顺序移动，运入箭头沿相反方向移动。
/// </summary>
public sealed record TransportFlowCorridor(
    string Key,
    IReadOnlyList<Point> Points,
    IReadOnlyList<TransportFlowCargo> OutgoingCargo,
    IReadOnlyList<TransportFlowCargo> IncomingCargo);

/// <summary>一条物理线段，以及仅经过该线段的准确货物集合。</summary>
public sealed record TransportFlowHitSegment(
    Point Start,
    Point End,
    IReadOnlyList<TransportFlowCargo> OutgoingCargo,
    IReadOnlyList<TransportFlowCargo> IncomingCargo);

/// <summary>指针下方所有运输通道线段的聚合信息。</summary>
public sealed record TransportFlowHitResult(
    Point Pointer,
    double Distance,
    IReadOnlyList<string> CorridorKeys,
    IReadOnlyList<TransportFlowCargo> OutgoingCargo,
    IReadOnlyList<TransportFlowCargo> IncomingCargo);

/// <summary>由 WPF 覆盖层和确定性测试共享的纯屏幕空间命中测试。</summary>
public static class TransportFlowHitTester
{
    public static TransportFlowHitResult? Find(
        IReadOnlyList<TransportFlowCorridor> corridors,
        Point pointer,
        double tolerance = TransportFlowOverlay.DefaultHitTolerance)
    {
        ArgumentNullException.ThrowIfNull(corridors);
        if (!double.IsFinite(tolerance) || tolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance));

        var hits = new List<(TransportFlowCorridor Corridor, double Distance)>();
        foreach (var corridor in corridors)
        {
            var distance = DistanceToPolyline(pointer, corridor.Points);
            if (distance <= tolerance)
                hits.Add((corridor, distance));
        }

        if (hits.Count == 0) return null;

        return new TransportFlowHitResult(
            pointer,
            hits.Min(hit => hit.Distance),
            hits.Select(hit => hit.Corridor.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            DistinctCargo(hits.SelectMany(hit => hit.Corridor.OutgoingCargo)),
            DistinctCargo(hits.SelectMany(hit => hit.Corridor.IncomingCargo)));
    }

    public static TransportFlowHitResult? FindSegments(
        IReadOnlyList<TransportFlowHitSegment> segments,
        Point pointer,
        double tolerance = TransportFlowOverlay.DefaultHitTolerance)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (!double.IsFinite(tolerance) || tolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance));

        var hits = segments
            .Select((segment, index) => (Segment: segment, Index: index,
                Distance: DistanceToPolyline(pointer, [segment.Start, segment.End])))
            .Where(hit => hit.Distance <= tolerance)
            .ToArray();
        if (hits.Length == 0) return null;

        return new TransportFlowHitResult(
            pointer,
            hits.Min(hit => hit.Distance),
            hits.Select(hit => $"segment.{hit.Index}").ToArray(),
            DistinctCargo(hits.SelectMany(hit => hit.Segment.OutgoingCargo)),
            DistinctCargo(hits.SelectMany(hit => hit.Segment.IncomingCargo)));
    }

    private static IReadOnlyList<TransportFlowCargo> DistinctCargo(IEnumerable<TransportFlowCargo> cargo) =>
        cargo.Where(item => !string.IsNullOrWhiteSpace(item.WareId))
            .GroupBy(item => item.WareId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.WareName, StringComparer.Ordinal)
            .ThenBy(item => item.WareId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static double DistanceToPolyline(Point pointer, IReadOnlyList<Point> points)
    {
        if (points.Count < 2) return double.PositiveInfinity;
        var bestSquared = double.PositiveInfinity;
        for (var index = 1; index < points.Count; index++)
        {
            var start = points[index - 1];
            var end = points[index];
            if (!IsFinite(start) || !IsFinite(end)) continue;
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var lengthSquared = dx * dx + dy * dy;
            var factor = lengthSquared <= double.Epsilon
                ? 0
                : Math.Clamp(((pointer.X - start.X) * dx + (pointer.Y - start.Y) * dy) /
                             lengthSquared, 0, 1);
            var offsetX = pointer.X - (start.X + factor * dx);
            var offsetY = pointer.Y - (start.Y + factor * dy);
            bestSquared = Math.Min(bestSquared, offsetX * offsetX + offsetY * offsetY);
        }

        return Math.Sqrt(bestSquared);
    }

    private static bool IsFinite(Point point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
}

/// <summary>
/// 在一个合并的视觉对象中绘制所有活动的空间站运输通道。静态路线缓存为冻结几何图形，
/// 每个动画帧则按颜色批量绘制所有可见箭头。几何图形使用屏幕坐标，因此星图缩放时，
/// 线宽、箭头尺寸和命中容差保持不变。
/// </summary>
public sealed class TransportFlowOverlay : FrameworkElement
{
    public static readonly Color OutgoingColor = Color.FromRgb(0xA6, 0xE3, 0xA1);
    public static readonly Color IncomingColor = Color.FromRgb(0xF3, 0x8B, 0xA8);

    public const double DefaultHitTolerance = 7;
    public const double LineThickness = 1.25;
    public const double ArrowSpacing = 20;
    public const double ArrowLength = 4.5;
    public const double ArrowHalfWidth = 2.5;
    public const double AnimationSpeed = 30;
    public const double TwoWayLaneOffset = 2.5;

    public static readonly DependencyProperty OutgoingBrushProperty = DependencyProperty.Register(
        nameof(OutgoingBrush), typeof(Brush), typeof(TransportFlowOverlay),
        new FrameworkPropertyMetadata(new SolidColorBrush(OutgoingColor),
            FrameworkPropertyMetadataOptions.AffectsRender, OnFlowBrushChanged));
    public static readonly DependencyProperty IncomingBrushProperty = DependencyProperty.Register(
        nameof(IncomingBrush), typeof(Brush), typeof(TransportFlowOverlay),
        new FrameworkPropertyMetadata(new SolidColorBrush(IncomingColor),
            FrameworkPropertyMetadataOptions.AffectsRender, OnFlowBrushChanged));

    private Pen _outgoingLinePen = null!;
    private Pen _incomingLinePen = null!;
    private Pen _outgoingArrowPen = null!;
    private Pen _incomingArrowPen = null!;

    private IReadOnlyList<TransportFlowCorridor> _corridors = Array.Empty<TransportFlowCorridor>();
    private IReadOnlyList<TransportFlowHitSegment> _hitSegments = Array.Empty<TransportFlowHitSegment>();
    private IReadOnlyList<PreparedCorridor> _preparedCorridors = Array.Empty<PreparedCorridor>();
    private Geometry _outgoingLineGeometry = Geometry.Empty;
    private Geometry _incomingLineGeometry = Geometry.Empty;
    private bool _geometryDirty = true;
    private bool _renderingSubscribed;
    private bool _animationSuspendedForTesting;
    private double _phasePixels;
    private TimeSpan _lastRenderingTime;

    public TransportFlowOverlay()
    {
        RebuildPens();
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public IReadOnlyList<TransportFlowCorridor> Corridors => _corridors;

    public Brush OutgoingBrush
    {
        get => (Brush)GetValue(OutgoingBrushProperty);
        set => SetValue(OutgoingBrushProperty, value);
    }

    public Brush IncomingBrush
    {
        get => (Brush)GetValue(IncomingBrushProperty);
        set => SetValue(IncomingBrushProperty, value);
    }

    /// <summary>当前全局动画相位，单位为设备无关像素。</summary>
    public double AnimationPhasePixels => _phasePixels;

    internal int GeometryRebuildCount { get; private set; }
    internal int LastRenderedArrowCount { get; private set; }
    internal int LastStaticGeometryDrawCount { get; private set; }
    internal int LastArrowGeometryDrawCount { get; private set; }

    internal void SetAnimationPhaseForTesting(double phasePixels)
    {
        _animationSuspendedForTesting = true;
        UnsubscribeRendering();
        _phasePixels = phasePixels;
        InvalidateVisual();
    }

    public void SetCorridors(IEnumerable<TransportFlowCorridor>? corridors)
    {
        _corridors = corridors?.Where(IsRenderable).Select(Clone).ToArray()
            ?? Array.Empty<TransportFlowCorridor>();
        _geometryDirty = true;
        UpdateRenderingSubscription();
        InvalidateVisual();
    }

    public void SetHitSegments(IEnumerable<TransportFlowHitSegment>? segments)
    {
        _hitSegments = segments?.Where(segment => IsFinite(segment.Start) && IsFinite(segment.End))
            .Select(segment => segment with
            {
                OutgoingCargo = segment.OutgoingCargo.ToArray(),
                IncomingCargo = segment.IncomingCargo.ToArray()
            })
            .ToArray() ?? Array.Empty<TransportFlowHitSegment>();
    }

    public TransportFlowHitResult? FindHit(Point pointer, double tolerance = DefaultHitTolerance) =>
        _hitSegments.Count > 0
            ? TransportFlowHitTester.FindSegments(_hitSegments, pointer, tolerance)
            : TransportFlowHitTester.Find(_corridors, pointer, tolerance);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        EnsurePreparedGeometry();

        LastStaticGeometryDrawCount = 0;
        if (!_outgoingLineGeometry.IsEmpty())
        {
            drawingContext.DrawGeometry(null, _outgoingLinePen, _outgoingLineGeometry);
            LastStaticGeometryDrawCount++;
        }
        if (!_incomingLineGeometry.IsEmpty())
        {
            drawingContext.DrawGeometry(null, _incomingLinePen, _incomingLineGeometry);
            LastStaticGeometryDrawCount++;
        }

        LastRenderedArrowCount = 0;
        LastArrowGeometryDrawCount = 0;
        DrawArrowBatch(drawingContext, outgoing: true);
        DrawArrowBatch(drawingContext, outgoing: false);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _geometryDirty = true;
        UpdateRenderingSubscription();
        InvalidateVisual();
    }

    private void DrawArrowBatch(DrawingContext drawingContext, bool outgoing)
    {
        if (!_preparedCorridors.Any(corridor => outgoing
                ? corridor.VisibleOutgoingSegments.Count > 0
                : corridor.VisibleIncomingSegments.Count > 0))
            return;

        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        var arrowCount = 0;
        using (var context = geometry.Open())
        {
            foreach (var corridor in _preparedCorridors)
            {
                var visibleSegments = outgoing
                    ? corridor.VisibleOutgoingSegments
                    : corridor.VisibleIncomingSegments;
                var allSegments = outgoing
                    ? corridor.OutgoingSegments
                    : corridor.IncomingSegments;
                if (visibleSegments.Count == 0 || allSegments.Count == 0) continue;
                arrowCount += AppendArrows(
                    context,
                    visibleSegments,
                    allSegments[^1].EndDistance,
                    forward: outgoing);
            }
        }
        if (arrowCount == 0) return;
        geometry.Freeze();
        drawingContext.DrawGeometry(null, outgoing ? _outgoingArrowPen : _incomingArrowPen, geometry);
        LastRenderedArrowCount += arrowCount;
        LastArrowGeometryDrawCount++;
    }

    private int AppendArrows(
        StreamGeometryContext context,
        IReadOnlyList<VisibleFlowSegment> visibleSegments,
        double totalLength,
        bool forward)
    {
        if (totalLength < ArrowLength * 2) return 0;
        var arrowCount = 0;
        var phase = PositiveModulo(_phasePixels, ArrowSpacing);
        var gridOrigin = forward
            ? phase
            : PositiveModulo(totalLength - phase, ArrowSpacing);

        foreach (var visible in visibleSegments)
        {
            var segment = visible.Segment;
            var firstIndex = Math.Ceiling((visible.StartDistance - gridOrigin) / ArrowSpacing - 1e-12);
            var distance = gridOrigin + firstIndex * ArrowSpacing;
            if (visible.SegmentIndex > 0 &&
                Math.Abs(distance - segment.StartDistance) < 1e-9)
                distance += ArrowSpacing;

            for (; distance <= visible.EndDistance + 1e-9; distance += ArrowSpacing)
            {
                if (distance <= ArrowLength || distance >= totalLength - ArrowLength) continue;
                var factor = Math.Clamp((distance - segment.StartDistance) /
                                        (segment.EndDistance - segment.StartDistance), 0, 1);
                var tip = segment.Start + (segment.End - segment.Start) * factor;
                var tangent = forward
                    ? segment.Tangent
                    : new Vector(-segment.Tangent.X, -segment.Tangent.Y);
                var normal = new Vector(-tangent.Y, tangent.X);
                var back = tip - tangent * ArrowLength;
                context.BeginFigure(tip, isFilled: false, isClosed: false);
                context.LineTo(back + normal * ArrowHalfWidth, isStroked: true, isSmoothJoin: false);
                context.BeginFigure(tip, isFilled: false, isClosed: false);
                context.LineTo(back - normal * ArrowHalfWidth, isStroked: true, isSmoothJoin: false);
                arrowCount++;
            }
        }
        return arrowCount;
    }

    private static List<FlowSegment> BuildSegments(IReadOnlyList<Point> points, double laneOffset)
    {
        var compactPoints = new List<Point>(points.Count);
        foreach (var point in points)
        {
            if (compactPoints.Count == 0 || (point - compactPoints[^1]).Length > 0.01)
                compactPoints.Add(point);
        }
        if (compactPoints.Count < 2) return [];

        var normals = new Vector[compactPoints.Count - 1];
        for (var index = 0; index < normals.Length; index++)
        {
            var direction = compactPoints[index + 1] - compactPoints[index];
            direction.Normalize();
            normals[index] = new Vector(-direction.Y, direction.X);
        }

        var shiftedPoints = new Point[compactPoints.Count];
        shiftedPoints[0] = compactPoints[0] + normals[0] * laneOffset;
        shiftedPoints[^1] = compactPoints[^1] + normals[^1] * laneOffset;
        for (var index = 1; index < compactPoints.Count - 1; index++)
        {
            var miter = normals[index - 1] + normals[index];
            if (miter.Length < 0.001)
            {
                shiftedPoints[index] = compactPoints[index] + normals[index] * laneOffset;
                continue;
            }
            miter.Normalize();
            var denominator = Vector.Multiply(miter, normals[index]);
            var scale = Math.Abs(denominator) < 0.25 ? laneOffset : laneOffset / denominator;
            scale = Math.Clamp(scale, -Math.Abs(laneOffset) * 2, Math.Abs(laneOffset) * 2);
            shiftedPoints[index] = compactPoints[index] + miter * scale;
        }

        var segments = new List<FlowSegment>(shiftedPoints.Length - 1);
        var total = 0d;
        for (var index = 1; index < shiftedPoints.Length; index++)
        {
            var start = shiftedPoints[index - 1];
            var end = shiftedPoints[index];
            var vector = end - start;
            var length = vector.Length;
            if (length <= 0.01 || !double.IsFinite(length)) continue;
            vector.Normalize();
            segments.Add(new FlowSegment(start, end, vector, total, total + length));
            total += length;
        }

        return segments;
    }

    private void EnsurePreparedGeometry()
    {
        if (!_geometryDirty) return;
        _geometryDirty = false;
        GeometryRebuildCount++;

        var viewport = CreateExpandedViewport();
        _preparedCorridors = _corridors.Select(corridor => Prepare(corridor, viewport)).ToArray();
        _outgoingLineGeometry = BuildLineGeometry(
            _preparedCorridors.SelectMany(corridor => corridor.VisibleOutgoingSegments));
        _incomingLineGeometry = BuildLineGeometry(
            _preparedCorridors.SelectMany(corridor => corridor.VisibleIncomingSegments));
        UpdateRenderingSubscription();
    }

    private Rect CreateExpandedViewport()
    {
        if (RenderSize.Width <= 0 || RenderSize.Height <= 0) return Rect.Empty;
        var margin = Math.Sqrt(ArrowLength * ArrowLength + ArrowHalfWidth * ArrowHalfWidth) +
                     LineThickness;
        return new Rect(-margin, -margin,
            RenderSize.Width + margin * 2, RenderSize.Height + margin * 2);
    }

    private static Geometry BuildLineGeometry(IEnumerable<VisibleFlowSegment> visibleSegments)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        var hasLines = false;
        using (var context = geometry.Open())
        {
            foreach (var visible in visibleSegments)
            {
                var segment = visible.Segment;
                var length = segment.EndDistance - segment.StartDistance;
                var startFactor = Math.Clamp((visible.StartDistance - segment.StartDistance) / length, 0, 1);
                var endFactor = Math.Clamp((visible.EndDistance - segment.StartDistance) / length, 0, 1);
                var start = segment.Start + (segment.End - segment.Start) * startFactor;
                var end = segment.Start + (segment.End - segment.Start) * endFactor;
                context.BeginFigure(start, isFilled: false, isClosed: false);
                context.LineTo(end, isStroked: true, isSmoothJoin: false);
                hasLines = true;
            }
        }
        if (!hasLines) return Geometry.Empty;
        geometry.Freeze();
        return geometry;
    }

    private static IReadOnlyList<VisibleFlowSegment> FindVisibleSegments(
        IReadOnlyList<FlowSegment> segments,
        Rect viewport)
    {
        if (viewport.IsEmpty) return [];
        var visible = new List<VisibleFlowSegment>();
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (!TryClipSegment(segment.Start, segment.End, viewport, out var startFactor, out var endFactor))
                continue;
            var length = segment.EndDistance - segment.StartDistance;
            visible.Add(new VisibleFlowSegment(
                segment,
                index,
                segment.StartDistance + length * startFactor,
                segment.StartDistance + length * endFactor));
        }
        return visible;
    }

    private static bool TryClipSegment(
        Point start,
        Point end,
        Rect bounds,
        out double startFactor,
        out double endFactor)
    {
        startFactor = 0;
        endFactor = 1;
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        return ClipParameter(-dx, start.X - bounds.Left, ref startFactor, ref endFactor) &&
               ClipParameter(dx, bounds.Right - start.X, ref startFactor, ref endFactor) &&
               ClipParameter(-dy, start.Y - bounds.Top, ref startFactor, ref endFactor) &&
               ClipParameter(dy, bounds.Bottom - start.Y, ref startFactor, ref endFactor);
    }

    private static bool ClipParameter(
        double direction,
        double distance,
        ref double startFactor,
        ref double endFactor)
    {
        if (Math.Abs(direction) < 1e-12) return distance >= 0;
        var ratio = distance / direction;
        if (direction < 0)
        {
            if (ratio > endFactor) return false;
            if (ratio > startFactor) startFactor = ratio;
        }
        else
        {
            if (ratio < startFactor) return false;
            if (ratio < endFactor) endFactor = ratio;
        }
        return true;
    }

    private static double PositiveModulo(double value, double divisor) =>
        (value % divisor + divisor) % divisor;

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateRenderingSubscription();

    private void OnUnloaded(object sender, RoutedEventArgs e) => UnsubscribeRendering();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        UpdateRenderingSubscription();

    private void UpdateRenderingSubscription()
    {
        var hasPotentialArrows = _geometryDirty
            ? _corridors.Count > 0
            : _preparedCorridors.Any(HasPotentialVisibleArrow);
        if (!_animationSuspendedForTesting && IsLoaded && IsVisible && hasPotentialArrows)
        {
            if (_renderingSubscribed) return;
            CompositionTarget.Rendering += OnRendering;
            _renderingSubscribed = true;
            return;
        }

        UnsubscribeRendering();
    }

    private static bool HasPotentialVisibleArrow(PreparedCorridor corridor) =>
        HasPotentialVisibleArrow(corridor.VisibleOutgoingSegments, corridor.OutgoingSegments) ||
        HasPotentialVisibleArrow(corridor.VisibleIncomingSegments, corridor.IncomingSegments);

    private static bool HasPotentialVisibleArrow(
        IReadOnlyList<VisibleFlowSegment> visibleSegments,
        IReadOnlyList<FlowSegment> allSegments)
    {
        if (visibleSegments.Count == 0 || allSegments.Count == 0) return false;
        var lastArrowDistance = allSegments[^1].EndDistance - ArrowLength;
        return visibleSegments.Any(segment =>
            segment.EndDistance > ArrowLength && segment.StartDistance < lastArrowDistance);
    }

    private void UnsubscribeRendering()
    {
        if (!_renderingSubscribed) return;
        CompositionTarget.Rendering -= OnRendering;
        _renderingSubscribed = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs rendering) return;
        if (Window.GetWindow(this)?.WindowState == WindowState.Minimized) return;
        if (_lastRenderingTime != default &&
            rendering.RenderingTime - _lastRenderingTime < TimeSpan.FromMilliseconds(30)) return;
        _lastRenderingTime = rendering.RenderingTime;
        _phasePixels = rendering.RenderingTime.TotalSeconds * AnimationSpeed;
        InvalidateVisual();
    }

    private static bool IsRenderable(TransportFlowCorridor corridor) =>
        !string.IsNullOrWhiteSpace(corridor.Key) && corridor.Points.Count >= 2 &&
        (corridor.OutgoingCargo.Count > 0 || corridor.IncomingCargo.Count > 0);

    private static bool IsFinite(Point point) => double.IsFinite(point.X) && double.IsFinite(point.Y);

    private static TransportFlowCorridor Clone(TransportFlowCorridor corridor) => new(
        corridor.Key,
        corridor.Points.ToArray(),
        corridor.OutgoingCargo.ToArray(),
        corridor.IncomingCargo.ToArray());

    private static PreparedCorridor Prepare(TransportFlowCorridor corridor, Rect viewport)
    {
        var hasOutgoing = corridor.OutgoingCargo.Count > 0;
        var hasIncoming = corridor.IncomingCargo.Count > 0;
        IReadOnlyList<FlowSegment> outgoing = hasOutgoing
            ? BuildSegments(corridor.Points, hasIncoming ? -TwoWayLaneOffset : 0)
            : [];
        IReadOnlyList<FlowSegment> incoming = hasIncoming
            ? BuildSegments(corridor.Points, hasOutgoing ? TwoWayLaneOffset : 0)
            : [];
        return new PreparedCorridor(
            outgoing,
            incoming,
            FindVisibleSegments(outgoing, viewport),
            FindVisibleSegments(incoming, viewport));
    }

    private static void OnFlowBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((TransportFlowOverlay)d).RebuildPens();

    private void RebuildPens()
    {
        _outgoingLinePen = CreatePen(OutgoingBrush, 0.5, LineThickness);
        _incomingLinePen = CreatePen(IncomingBrush, 0.5, LineThickness);
        _outgoingArrowPen = CreatePen(OutgoingBrush, 1, LineThickness);
        _incomingArrowPen = CreatePen(IncomingBrush, 1, LineThickness);
    }

    private static Pen CreatePen(Brush source, double opacity, double thickness)
    {
        var brush = source.CloneCurrentValue();
        brush.Opacity *= opacity;
        brush.Freeze();
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    private sealed record FlowSegment(
        Point Start,
        Point End,
        Vector Tangent,
        double StartDistance,
        double EndDistance);

    private sealed record VisibleFlowSegment(
        FlowSegment Segment,
        int SegmentIndex,
        double StartDistance,
        double EndDistance);

    private sealed record PreparedCorridor(
        IReadOnlyList<FlowSegment> OutgoingSegments,
        IReadOnlyList<FlowSegment> IncomingSegments,
        IReadOnlyList<VisibleFlowSegment> VisibleOutgoingSegments,
        IReadOnlyList<VisibleFlowSegment> VisibleIncomingSegments);
}
