namespace X4Calculator.Core.Calculation;

/// <summary>
/// 已由上游落实为同一稳定参考空间完整 pose 的普通 Linear FlightPoint。
/// Deadline 小于等于 <see cref="OosNavigationPathQueue.NativeDeadlineSentinelLimit"/> 表示未标记。
/// </summary>
public sealed record OosNavigationPathQueueEntry(
    OosDockingFlightPoint Point,
    double Deadline = -1d);

/// <summary>一次普通 worker 前处理的队列快照和可选 Linear 重设目标。</summary>
public sealed record OosNavigationPathQueueWorkerResult(
    bool ConsumedDirty,
    bool PendingMerged,
    int ExpiredHeadCount,
    int MarkedHeadCountDroppedByPending,
    bool IsDirty,
    OosLinearPose? ReplanTarget,
    int ActiveAndPendingPointCount,
    int RemainingPointCount,
    bool HeadBoost,
    bool HeadTravel);

/// <summary>一次真正 Linear 完成回调的同步出队结果。</summary>
public sealed record OosNavigationPathQueueCompletionResult(
    bool Popped,
    bool WasLastActivePointCandidate,
    bool IsDirty,
    int ActiveAndPendingPointCount,
    int RemainingPointCount,
    bool HeadBoost,
    bool HeadTravel);

/// <summary>一次已交付的 abort/StopMoving 命令的队列结果。</summary>
public sealed record OosNavigationPathQueueAbortResult(
    int GraceDeadlineMarkedCount,
    int ActiveClearedCount,
    int PendingClearedCount,
    bool IsDirty,
    int ActiveAndPendingPointCount,
    int RemainingPointCount,
    bool HeadBoost,
    bool HeadTravel);

/// <summary>
/// 普通 Linear FlightPoint 的活动/待处理状态层。
/// 它只编排已落实的点、期限和 F33 dirty 消费，不生成 pose、不积分运动，也不改变控制器速度或姿态。
/// </summary>
public sealed class OosNavigationPathQueue
{
    public const double NativeDeadlineSentinelLimit = -0.9999d;
    private const double AbortGraceSeconds = 2d;

    private readonly List<OosNavigationPathQueueEntry> _active;
    private readonly List<OosNavigationPathQueueEntry> _pending;
    private bool _dirty;

    public OosNavigationPathQueue(
        IEnumerable<OosNavigationPathQueueEntry> active,
        IEnumerable<OosNavigationPathQueueEntry>? pending = null,
        bool initialDirty = false)
    {
        ArgumentNullException.ThrowIfNull(active);
        var activeEntries = active.ToArray();
        var pendingEntries = pending?.ToArray() ?? [];
        ValidateEntries(activeEntries, nameof(active));
        ValidateEntries(pendingEntries, nameof(pending));

        _active = [.. activeEntries];
        _pending = [.. pendingEntries];
        _dirty = initialDirty;
    }

    /// <summary>活动点的防御性快照；调用方无法借此修改队列。</summary>
    public IReadOnlyList<OosNavigationPathQueueEntry> Active => Array.AsReadOnly(_active.ToArray());

    /// <summary>待处理点的防御性快照；调用方无法借此修改队列。</summary>
    public IReadOnlyList<OosNavigationPathQueueEntry> Pending => Array.AsReadOnly(_pending.ToArray());

    public bool IsDirty => _dirty;
    public int ActiveAndPendingPointCount => _active.Count + _pending.Count;
    public int RemainingPointCount => Math.Max(1, ActiveAndPendingPointCount);
    public bool HeadBoost => _active.Count != 0 && _active[0].Point.Boost;
    public bool HeadTravel => _active.Count != 0 && _active[0].Point.Travel;

    /// <summary>把已交付给本层、但尚未由 worker 合并的点按原始顺序加入待处理队列。</summary>
    public void AppendPending(IEnumerable<OosNavigationPathQueueEntry> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var entries = points.ToArray();
        ValidateEntries(entries, nameof(points));
        _pending.AddRange(entries);
    }

    /// <summary>
    /// E89F10 的普通 StopMoving 分支：只给未标记的活动点写 now+2，清待处理队列并置 dirty。
    /// 已标记点保留原期限，因此重复 abort 不会延长宽限。
    /// </summary>
    public OosNavigationPathQueueAbortResult AbortDelivered(double now) =>
        AbortPathDelivered(now, clearImmediately: false);

    /// <summary>
    /// E89F10 的已交付 abort。clearImmediately=true 对应立即清活动点的分支；false 对应 StopMoving 宽限。
    /// </summary>
    public OosNavigationPathQueueAbortResult AbortPathDelivered(double now, bool clearImmediately)
    {
        ValidateTime(now, nameof(now));
        var marked = 0;
        var activeCleared = 0;
        if (clearImmediately)
        {
            activeCleared = _active.Count;
            _active.Clear();
        }
        else
        {
            for (var index = 0; index < _active.Count; index++)
            {
                if (_active[index].Deadline <= NativeDeadlineSentinelLimit)
                {
                    _active[index] = _active[index] with { Deadline = now + AbortGraceSeconds };
                    marked++;
                }
            }
        }

        var pendingCleared = _pending.Count;
        _pending.Clear();
        _dirty = true;
        return AbortResult(marked, activeCleared, pendingCleared);
    }

    /// <summary>
    /// 执行普通 worker 在 Linear 前的可证明子序列：连续过期头、待处理队列合并和 F33 消费。
    /// 调用方必须显式证明 ordinaryWorkerEligible；否则 dirty 保留且没有 ReplanTarget。
    /// </summary>
    public OosNavigationPathQueueWorkerResult WorkerBeforeLinear(
        double now,
        OosLinearPose readBankPose,
        bool ordinaryWorkerEligible)
    {
        ValidateTime(now, nameof(now));
        ValidatePose(readBankPose, nameof(readBankPose));

        var expired = RemoveExpiredHeads(now);
        var merged = _pending.Count != 0;
        var droppedForPending = merged ? RemoveMarkedHeadsForPending() : 0;
        if (merged)
        {
            _active.AddRange(_pending);
            _pending.Clear();
            _dirty = true;
        }

        OosLinearPose? target = null;
        var consumed = ordinaryWorkerEligible && _dirty;
        if (consumed)
        {
            target = _active.Count == 0 ? readBankPose : _active[0].Point.Target;
            _dirty = false;
        }

        return new(
            consumed,
            merged,
            expired,
            droppedForPending,
            _dirty,
            target,
            ActiveAndPendingPointCount,
            RemainingPointCount,
            HeadBoost,
            HeadTravel);
    }

    /// <summary>
    /// 仅在现有 Linear 已通过一次新的真实完成回调时调用。同步弹出一个活动头，置 dirty，且不在同 tick 重建下一点。
    /// 此层不镜像 FCM+6F 锁存；调用方不得轮询持续的 <c>Completed</c> 状态而重复调用本方法。
    /// </summary>
    public OosNavigationPathQueueCompletionResult OnLinearCompletion()
    {
        if (_active.Count == 0)
            throw new InvalidOperationException("A Linear completion callback requires a corresponding active FlightPoint.");

        var wasLastActiveCandidate = _active.Count == 1;
        _active.RemoveAt(0);
        _dirty = true;

        return new(
            true,
            wasLastActiveCandidate,
            _dirty,
            ActiveAndPendingPointCount,
            RemainingPointCount,
            HeadBoost,
            HeadTravel);
    }

    private int RemoveExpiredHeads(double now)
    {
        var removed = 0;
        while (_active.Count != 0 && IsMarked(_active[0]) && _active[0].Deadline < now)
        {
            _active.RemoveAt(0);
            removed++;
        }
        return removed;
    }

    private int RemoveMarkedHeadsForPending()
    {
        var removed = 0;
        while (_active.Count != 0 && IsMarked(_active[0]))
        {
            _active.RemoveAt(0);
            removed++;
        }
        return removed;
    }

    private static bool IsMarked(OosNavigationPathQueueEntry entry) =>
        entry.Deadline > NativeDeadlineSentinelLimit;

    private OosNavigationPathQueueAbortResult AbortResult(int marked, int activeCleared, int pendingCleared) => new(
        marked,
        activeCleared,
        pendingCleared,
        _dirty,
        ActiveAndPendingPointCount,
        RemainingPointCount,
        HeadBoost,
        HeadTravel);

    private static void ValidateEntries(
        IReadOnlyList<OosNavigationPathQueueEntry> entries,
        string parameterName)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index] ?? throw new ArgumentException("FlightPoint entries cannot be null.", parameterName);
            if (entry.Point is null)
                throw new ArgumentException("FlightPoint entries must contain a point.", parameterName);
            if (!double.IsFinite(entry.Deadline))
                throw new ArgumentOutOfRangeException(parameterName, $"FlightPoint {index} deadline must be finite.");
            // 普通 move_to 使用 -1 作为缓存制动距离的接近半径。
            // 此队列不使用 docking 的非负捕获半径契约。
            if (!float.IsFinite(entry.Point.Radius) || (entry.Point.Radius < 0 && entry.Point.Radius != -1f))
                throw new ArgumentOutOfRangeException(parameterName, $"FlightPoint {index} radius must be -1 or finite and non-negative.");
            ValidatePose(entry.Point.Target, parameterName);
        }
    }

    private static void ValidateTime(double value, string parameterName)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName, "Time must be finite.");
    }

    private static void ValidatePose(OosLinearPose pose, string parameterName)
    {
        ValidateVector(pose.Position, parameterName);
        ValidateVector(pose.XAxis, parameterName);
        ValidateVector(pose.YAxis, parameterName);
        ValidateVector(pose.ZAxis, parameterName);
    }

    private static void ValidateVector(OosLinearVector4 vector, string parameterName)
    {
        if (!float.IsFinite(vector.X) || !float.IsFinite(vector.Y) ||
            !float.IsFinite(vector.Z) || !float.IsFinite(vector.W))
            throw new ArgumentOutOfRangeException(parameterName, "Pose components must be finite float32 values.");
    }
}
