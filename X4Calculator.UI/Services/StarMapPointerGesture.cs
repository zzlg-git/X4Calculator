namespace X4Calculator.UI.Services;

/// <summary>
/// 星图一次左键手势完成后应执行的选择动作。
/// </summary>
public enum StarMapPointerGestureAction
{
    None,
    SelectStation,
    ClearSelection,
    PreserveSelection
}

/// <summary>
/// 星图指针手势结果。只有 <see cref="StarMapPointerGestureAction.SelectStation"/>
/// 会携带 <see cref="StationId"/>。
/// </summary>
public readonly record struct StarMapPointerGestureResult(
    StarMapPointerGestureAction Action,
    string? StationId = null);

/// <summary>
/// 不依赖 WPF MouseDevice 的点击/拖动判定状态机。
/// 调用方应传入 SystemParameters 提供的水平、垂直最小拖动距离。
/// </summary>
public sealed class StarMapPointerGesture
{
    private readonly double _minimumHorizontalDragDistance;
    private readonly double _minimumVerticalDragDistance;
    private double _startX;
    private double _startY;
    private string? _pressedStationId;

    public StarMapPointerGesture(
        double minimumHorizontalDragDistance,
        double minimumVerticalDragDistance)
    {
        if (!double.IsFinite(minimumHorizontalDragDistance) || minimumHorizontalDragDistance < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumHorizontalDragDistance));
        if (!double.IsFinite(minimumVerticalDragDistance) || minimumVerticalDragDistance < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumVerticalDragDistance));

        _minimumHorizontalDragDistance = minimumHorizontalDragDistance;
        _minimumVerticalDragDistance = minimumVerticalDragDistance;
    }

    public bool IsActive { get; private set; }
    public bool IsDragging { get; private set; }

    /// <summary>开始一次手势；stationId 为空表示从地图空白处按下。</summary>
    public void Begin(double x, double y, string? stationId)
    {
        ValidateCoordinate(x, nameof(x));
        ValidateCoordinate(y, nameof(y));

        _startX = x;
        _startY = y;
        _pressedStationId = string.IsNullOrWhiteSpace(stationId) ? null : stationId;
        IsActive = true;
        IsDragging = false;
    }

    /// <summary>
    /// 更新指针位置并返回当前手势是否已经越过拖动阈值。
    /// 一旦成为拖动，即使指针回到起点也不会重新解释为点击。
    /// </summary>
    public bool Update(double x, double y)
    {
        ValidateCoordinate(x, nameof(x));
        ValidateCoordinate(y, nameof(y));
        if (!IsActive) return false;

        if (!IsDragging &&
            (Math.Abs(x - _startX) > _minimumHorizontalDragDistance ||
             Math.Abs(y - _startY) > _minimumVerticalDragDistance))
        {
            IsDragging = true;
        }

        return IsDragging;
    }

    /// <summary>
    /// 完成手势。拖动保持原选择；点击站点选择该站；点击空白清除选择。
    /// </summary>
    public StarMapPointerGestureResult Complete(double x, double y)
    {
        if (!IsActive) return default;

        Update(x, y);
        var result = IsDragging
            ? new StarMapPointerGestureResult(StarMapPointerGestureAction.PreserveSelection)
            : _pressedStationId == null
                ? new StarMapPointerGestureResult(StarMapPointerGestureAction.ClearSelection)
                : new StarMapPointerGestureResult(
                    StarMapPointerGestureAction.SelectStation, _pressedStationId);
        Reset();
        return result;
    }

    /// <summary>取消捕获或中断手势时保持现有选择。</summary>
    public StarMapPointerGestureResult Cancel()
    {
        if (!IsActive) return default;
        Reset();
        return new StarMapPointerGestureResult(StarMapPointerGestureAction.PreserveSelection);
    }

    private void Reset()
    {
        IsActive = false;
        IsDragging = false;
        _pressedStationId = null;
    }

    private static void ValidateCoordinate(double value, string parameterName)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(parameterName);
    }
}
