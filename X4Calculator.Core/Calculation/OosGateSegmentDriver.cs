namespace X4Calculator.Core.Calculation;

/// <summary>调用方提供的接受全序；Now 是该相位的实际时间，Sequence 不由时间戳排序生成。</summary>
public readonly record struct OosGatePhase(long Sequence, double Now);

/// <summary>
/// 普通运动 bank 的已建模投影。AngularVelocity=null 表示当前 Linear 内核未输出该字段；不能把它当成零。
/// Auxiliary90/A0 对应 worker 复制的两段字段，保持不透明，不参与新造物理。
/// </summary>
public sealed record OosGateMotionBank(
    OosLinearPose Pose,
    OosLinearVector4 Velocity,
    OosLinearVector4? AngularVelocity,
    OosLinearVector4 Auxiliary90,
    OosLinearVector4 AuxiliaryA0,
    ulong Space,
    ulong Reference);

public sealed record OosGateInitialState(
    OosGateMotionBank? Bank0,
    OosGateMotionBank? Bank1,
    int? ReadIndex,
    int? WriteIndex,
    ulong? ContextSpace,
    ulong? Reference,
    bool? CompletionLatched,
    bool? OrdinarySpaceAndSharedParameterCacheConfirmed);

/// <summary>仅表示真正截取的 FIFO 前缀；LookupSucceeded 与该前缀逐项对应。</summary>
public sealed record OosGateManagerBoundary(
    bool? Eligible,
    bool? WorkersComplete,
    int? AcceptedPrefixCount,
    IReadOnlyList<bool>? LookupSucceeded,
    float? NativeDeltaSeconds);

public sealed record OosGateManagerResult(int Executed, int Skipped, bool Swapped, int PendingCount);

public sealed record OosGateWorkerResult(
    OosLinearFlightStepResult Linear,
    OosNavigationPathQueueWorkerResult Queue,
    OosNavigationPathQueueCompletionResult? Completion,
    bool Replanned,
    int WriteIndex,
    bool AngularVelocityOutputKnown);

/// <summary>已在 dirty 提交边界解析的跨 reference 目标命令；不是运动 bank 或 FCM checkpoint。</summary>
public sealed record OosGateObservedResolvedTarget(
    OosLinearPose Pose,
    ulong SourceReference,
    ulong MotionSpace,
    bool? AlreadyResolvedAtDirtySubmission);

/// <summary>
/// 显式相位驱动的有界普通门段条件重放。复用原 FCM、点队列及 Dynamics；不从到期时间猜测接受相位。
/// 不恢复原始 FCM，不生成安全点，不穷举父对象 listener；两参数 bank 尚不一致的情景不受支持。
/// 当前 Linear 不输出一般角速度，因此该投影不提供完整原生 bank 或完整单程验收。
/// </summary>
public sealed class OosGateSegmentDriver
{
    private sealed record Order(OosLinearPose? Offset, ulong? Space);
    private readonly OosGateMotionBank[] _banks;
    private readonly List<Order> _orders = [];
    private readonly int _maximumPhases;
    private long _lastSequence = -1;
    private int _phaseCount;
    private double _lastTime;
    private bool _contextDirty;
    private bool _completionLatched;
    private readonly OosTransportDynamics? _dynamics;

    public OosLinearFlightController Flight { get; }
    public OosNavigationPathQueue Path { get; }
    public OosTransportDynamics Dynamics => _dynamics ??
        throw new NotSupportedException("UNKNOWN: observed-parameter projection has no engine or Dynamics state.");
    public bool IsObservedParameterProjection => _dynamics is null;
    public int ReadIndex { get; private set; }
    public int WriteIndex { get; private set; }
    public ulong ContextSpace { get; private set; }
    public ulong Reference { get; private set; }
    public int PendingOrderCount => _orders.Count;
    public bool CompletionLatched => _completionLatched;
    public int PhaseCount => _phaseCount;
    public long LastPhaseSequence => _lastSequence;
    public double LastPhaseTime => _lastTime;
    public bool IsDirty => _contextDirty || Path.IsDirty;
    public IReadOnlyList<OosGateMotionBank> Banks => Array.AsReadOnly((OosGateMotionBank[])_banks.Clone());

    /// <summary>
    /// initialPhaseTime 可保留脚本交接时钟，Flight.Now 仍为最后一次 worker 时钟。
    /// 连续普通运动投影可显式保留未知的初始角速度字段；这不提供完整角速度模拟。
    /// </summary>
    public OosGateSegmentDriver(
        OosLinearFlightController flight,
        OosNavigationPathQueue path,
        OosTransportDynamics dynamics,
        OosGateInitialState initial,
        int maximumPhases,
        bool requireCompleteAngularState,
        double? initialPhaseTime = null,
        bool allowUnknownInitialAngularVelocity = false)
        : this(flight, path, dynamics ?? throw new ArgumentNullException(nameof(dynamics)), initial,
            maximumPhases, requireCompleteAngularState, observedParameterProjection: false, ordinarySpaceConfirmed: null,
            initialPhaseTime, allowUnknownInitialAngularVelocity)
    {
    }

    /// <summary>
    /// 仅验证显式外生缓存驱动的运动/路径投影；不要求或捏造引擎/共享缓存状态。
    /// 普通空间条件仍必须明确；刷新、模式请求、旅行交付及普通 StepWorker 在此模式下全部拒绝。
    /// </summary>
    public static OosGateSegmentDriver CreateObservedParameterProjection(
        OosLinearFlightController flight,
        OosNavigationPathQueue path,
        OosGateInitialState initial,
        int maximumPhases,
        bool? ordinarySpaceConfirmed,
        bool requireCompleteAngularState) =>
        new(flight, path, null, initial, maximumPhases, requireCompleteAngularState,
            observedParameterProjection: true, ordinarySpaceConfirmed: ordinarySpaceConfirmed);

    private OosGateSegmentDriver(
        OosLinearFlightController flight,
        OosNavigationPathQueue path,
        OosTransportDynamics? dynamics,
        OosGateInitialState initial,
        int maximumPhases,
        bool requireCompleteAngularState,
        bool observedParameterProjection,
        bool? ordinarySpaceConfirmed,
        double? initialPhaseTime = null,
        bool allowUnknownInitialAngularVelocity = false)
    {
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(initial);
        if (maximumPhases <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPhases));
        if (requireCompleteAngularState)
            throw new NotSupportedException("UNKNOWN: the existing Linear kernel does not output general angular velocity.");
        if (initial.Bank0 is null || initial.Bank1 is null ||
            initial.ReadIndex is not (0 or 1) || initial.WriteIndex is not (0 or 1) ||
            initial.ReadIndex == initial.WriteIndex || initial.ContextSpace is null or 0 || initial.Reference is null ||
            initial.CompletionLatched is null ||
            (observedParameterProjection ? ordinarySpaceConfirmed != true : initial.OrdinarySpaceAndSharedParameterCacheConfirmed != true))
            throw new NotSupportedException("UNKNOWN: both initial banks, distinct indices, reference, space, latch and ordinary shared-cache branch are required.");
        ValidateBank(initial.Bank0);
        ValidateBank(initial.Bank1);
        if (!allowUnknownInitialAngularVelocity &&
            (initial.Bank0.AngularVelocity is null || initial.Bank1.AngularVelocity is null))
            throw new NotSupportedException("UNKNOWN: both initial angular velocities are required.");
        var phaseTime = initialPhaseTime ?? flight.Now;
        if (!double.IsFinite(phaseTime) || phaseTime < flight.Now)
            throw new ArgumentOutOfRangeException(nameof(initialPhaseTime), "The event clock cannot precede the last worker clock.");
        Flight = flight;
        Path = path;
        _dynamics = dynamics;
        _banks = [initial.Bank0, initial.Bank1];
        ReadIndex = initial.ReadIndex.Value;
        WriteIndex = initial.WriteIndex.Value;
        ContextSpace = initial.ContextSpace.Value;
        Reference = initial.Reference.Value;
        _completionLatched = initial.CompletionLatched.Value;
        _lastTime = phaseTime;
        _maximumPhases = maximumPhases;
    }

    /// <summary>先入 FIFO，再仅同步写读 bank 的 pose/reference；与 context 提交独立。</summary>
    public void SubmitExplicitWarpPose(OosGatePhase phase, OosLinearPose pose)
    {
        CheckPhase(phase);
        ValidatePose(pose);
        _orders.Add(new(pose, null));
        _banks[ReadIndex] = _banks[ReadIndex] with { Pose = pose, Reference = 0 };
        CommitPhase(phase);
    }

    /// <summary>只接受已排除空间变换、特殊环境、pose 修复及残留点清理的普通分支。</summary>
    public void SubmitContextChange(OosGatePhase phase, ulong newSpace, bool? ordinaryBranchWithoutOtherEffects)
    {
        CheckPhase(phase);
        if (ordinaryBranchWithoutOtherEffects != true)
            throw new NotSupportedException("UNKNOWN: SetContext requires explicit ordinary no-transform/no-other-effects conditions.");
        if (newSpace == 0)
            throw new NotSupportedException("UNKNOWN: ordinary SetContext requires a non-null target space.");
        _orders.Add(new(null, newSpace));
        CommitPhase(phase);
    }

    public void DeliverPoints(OosGatePhase phase, IEnumerable<OosNavigationPathQueueEntry> points)
    {
        CheckPhase(phase);
        Path.AppendPending(points);
        CommitPhase(phase);
    }

    /// <summary>
    /// 交付 StopMoving 的期限/队列动作并清已建模的 FCM+6F 完成锁存。
    /// 原生同时清 +6E；当前 Linear 未暴露该字段，不能据此声称恢复了完整 abort 状态。
    /// </summary>
    public OosNavigationPathQueueAbortResult DeliverStopMoving(OosGatePhase phase)
    {
        CheckPhase(phase);
        var result = Path.AbortDelivered(phase.Now);
        _completionLatched = false;
        CommitPhase(phase);
        return result;
    }

    /// <summary>
    /// 新 move_to 的显式 abortpath 消费；与计时器续行及 StopMoving 的两秒期限动作区分。
    /// 不改变 FCM worker 时钟、速度或积分进度。
    /// </summary>
    public OosNavigationPathQueueAbortResult DeliverAbortPath(OosGatePhase phase, bool clearImmediately)
    {
        CheckPhase(phase);
        var result = Path.AbortPathDelivered(phase.Now, clearImmediately);
        _completionLatched = false;
        CommitPhase(phase);
        return result;
    }

    public OosGateManagerResult FinishManager(OosGatePhase phase, OosGateManagerBoundary boundary)
    {
        CheckPhase(phase);
        ArgumentNullException.ThrowIfNull(boundary);
        if (boundary.Eligible is null)
            throw new NotSupportedException("UNKNOWN: manager eligibility is missing.");
        if (boundary.Eligible == false)
        {
            CommitPhase(phase);
            return new(0, 0, false, _orders.Count);
        }
        if (boundary.WorkersComplete != true || boundary.AcceptedPrefixCount is null ||
            boundary.LookupSucceeded is null || boundary.NativeDeltaSeconds is null)
            throw new NotSupportedException("UNKNOWN: completed workers, accepted FIFO prefix, lookup results and native dt are required.");
        var count = boundary.AcceptedPrefixCount.Value;
        var lookups = boundary.LookupSucceeded.ToArray();
        var dt = boundary.NativeDeltaSeconds.Value;
        if (count < 0 || count > _orders.Count || lookups.Length != count || !float.IsFinite(dt))
            throw new ArgumentException("The accepted prefix, lookup count or native dt is invalid.", nameof(boundary));
        // 在修改前验证整个已分离批次，包括被跳过的偏移查找。
        var projectedReference = Reference;
        var projectedSpace = ContextSpace;
        for (var i = 0; i < count; i++)
        {
            if (!lookups[i]) continue;
            if (_orders[i].Offset.HasValue) projectedReference = 0;
            else if (_orders[i].Space is { } space && space != projectedSpace)
            {
                if (projectedReference != 0)
                    throw new NotSupportedException("UNKNOWN: ordinary SetContext requires no additional reference object.");
                projectedSpace = space;
            }
        }
        var executed = 0;
        foreach (var (order, index) in _orders.Take(count).Select((order, index) => (order, index)))
        {
            if (!lookups[index]) continue;
            executed++;
            if (order.Offset is { } pose)
            {
                for (var i = 0; i < 2; i++) _banks[i] = _banks[i] with { Pose = pose, Reference = 0 };
                Reference = 0;
            }
            else if (order.Space is { } space && space != ContextSpace)
            {
                ContextSpace = space;
                for (var i = 0; i < 2; i++) _banks[i] = _banks[i] with { Space = space };
                _contextDirty = true;
            }
        }
        _orders.RemoveRange(0, count);
        var swap = MathF.Abs(dt) >= 0.0001f;
        if (swap) { ReadIndex = 1 - ReadIndex; WriteIndex = 1 - WriteIndex; }
        CommitPhase(phase);
        return new(executed, count - executed, swap, _orders.Count);
    }

    public OosGateWorkerResult StepWorker(
        OosGatePhase phase, double deltaSeconds, bool? ordinaryCopyEligible, bool? dirtyConsumerEligible,
        bool? requireOrientation, bool? suppressCompletion) =>
        StepWorkerCore(phase, deltaSeconds, ordinaryCopyEligible, dirtyConsumerEligible,
            requireOrientation, suppressCompletion, Dynamics.Parameters, null);

    /// <summary>
    /// 与普通 worker 共用相同 bank/复制/路径/FCM 更新；参数是当前已观察缓存，不调用 Dynamics。
    /// 跨 reference 解析出的目标仅在显式 dirty 消费时替代目标命令，不恢复曲线、进度或运动状态。
    /// </summary>
    public OosGateWorkerResult StepWorkerWithObservedParameters(
        OosGatePhase phase, double deltaSeconds, bool? ordinaryCopyEligible, bool? dirtyConsumerEligible,
        bool? requireOrientation, bool? suppressCompletion,
        OosLinearFlightParameters observedParameters,
        OosGateObservedResolvedTarget? observedResolvedReplanTarget = null)
    {
        if (!IsObservedParameterProjection)
            throw new NotSupportedException("Observed parameters cannot bypass the normal Dynamics prediction mode.");
        return StepWorkerCore(phase, deltaSeconds, ordinaryCopyEligible, dirtyConsumerEligible,
            requireOrientation, suppressCompletion, observedParameters, observedResolvedReplanTarget);
    }

    private OosGateWorkerResult StepWorkerCore(
        OosGatePhase phase, double deltaSeconds, bool? ordinaryCopyEligible, bool? dirtyConsumerEligible,
        bool? requireOrientation, bool? suppressCompletion,
        OosLinearFlightParameters sourceParameters, OosGateObservedResolvedTarget? resolvedTarget)
    {
        CheckPhase(phase);
        if (ordinaryCopyEligible != true || dirtyConsumerEligible is null ||
            requireOrientation is null || suppressCompletion is null)
            throw new NotSupportedException("UNKNOWN: ordinary copy, queue consumer and completion controls must be explicit.");
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        // 在复制/路径消费前验证：无效的外部输入不得改变驱动器。
        ValidateParameters(sourceParameters);
        if (resolvedTarget is not null)
        {
            ValidatePose(resolvedTarget.Pose);
            var hasSurvivingPoint = Path.Pending.Count != 0 || Path.Active.SkipWhile(entry =>
                entry.Deadline > OosNavigationPathQueue.NativeDeadlineSentinelLimit && entry.Deadline < phase.Now).Any();
            if (resolvedTarget.AlreadyResolvedAtDirtySubmission != true ||
                resolvedTarget.SourceReference == 0 || resolvedTarget.SourceReference == resolvedTarget.MotionSpace ||
                resolvedTarget.MotionSpace != ContextSpace || dirtyConsumerEligible != true ||
                !(IsDirty || Path.Pending.Count != 0) || !hasSurvivingPoint)
                throw new NotSupportedException("UNKNOWN: a cross-reference target needs an explicit resolved dirty submission in the current motion space.");
        }
        var read = _banks[ReadIndex];
        if (read.Reference != Reference)
            throw new NotSupportedException("UNKNOWN: read-bank reference does not match the ordinary worker context.");
        // 仅复制已建模的选定字段；绝不替换 FCM 或复制整个 bank。
        _banks[WriteIndex] = _banks[WriteIndex] with {
            Pose = read.Pose, Velocity = read.Velocity, AngularVelocity = read.AngularVelocity,
            Auxiliary90 = read.Auxiliary90, AuxiliaryA0 = read.AuxiliaryA0,
            Reference = Reference, Space = ContextSpace };
        Flight.ApplyWorkerMotionState(read.Pose, read.Velocity);
        var queue = Path.WorkerBeforeLinear(phase.Now, read.Pose, dirtyConsumerEligible.Value);
        var replan = queue.ConsumedDirty || (_contextDirty && dirtyConsumerEligible.Value);
        var parameters = sourceParameters with {
            RemainingPointCount = queue.RemainingPointCount,
            ActiveAndPendingPointCount = queue.ActiveAndPendingPointCount,
            RequireOrientation = requireOrientation.Value,
            SuppressCompletion = suppressCompletion.Value };
        if (replan)
        {
            Flight.Replan(resolvedTarget?.Pose ?? queue.ReplanTarget ?? (Path.Active.Count == 0 ? read.Pose : Path.Active[0].Point.Target), parameters);
            _contextDirty = false;
            _completionLatched = false;
        }
        var step = Flight.StepAt(phase.Now, deltaSeconds, parameters);
        OosNavigationPathQueueCompletionResult? completion = null;
        if (step.FcmCompletion && !_completionLatched && Path.Active.Count != 0)
        {
            completion = Path.OnLinearCompletion();
            _completionLatched = true;
        }
        var emptyStop = step.PositionReached && queue.ActiveAndPendingPointCount == 0;
        _banks[WriteIndex] = _banks[WriteIndex] with {
            Pose = step.Pose, Velocity = step.Velocity,
            AngularVelocity = emptyStop ? OosLinearVector4.Zero : null };
        CommitPhase(phase);
        return new(step, queue, completion, replan, WriteIndex, emptyStop);
    }

    public OosLinearFlightParameters ForceParameterRefresh(
        OosGatePhase phase, int motionBankIndex, OosEngineModeUpdateInputs? controls,
        bool? listenerEffectsAlreadyApplied)
    {
        var dynamics = Dynamics;
        CheckPhase(phase);
        ValidateControls(controls);
        if (listenerEffectsAlreadyApplied != true)
            throw new NotSupportedException("UNKNOWN: actual listener effects must already be reflected in the engine/event state.");
        var result = WithMotionBank(motionBankIndex, () => dynamics.ForceParameterRefresh(Flight, phase.Now, controls!));
        CommitPhase(phase);
        return result;
    }

    public void DeliverModeRequest(OosGatePhase phase, int motionBankIndex, OosNavigationModeRequest request)
    {
        var dynamics = Dynamics;
        CheckPhase(phase);
        if (!Enum.IsDefined(request.Mode) || !Enum.IsDefined(request.Action))
            throw new ArgumentOutOfRangeException(nameof(request));
        WithMotionBank(motionBankIndex, () => { dynamics.Request(Flight, phase.Now, request); return true; });
        CommitPhase(phase);
    }

    public bool DeliverTravelStart(OosGatePhase phase, int motionBankIndex)
    {
        var dynamics = Dynamics;
        CheckPhase(phase);
        var delivered = WithMotionBank(motionBankIndex, () => dynamics.DeliverTravelStart(Flight, phase.Now));
        CommitPhase(phase);
        return delivered;
    }

    private T WithMotionBank<T>(int index, Func<T> action)
    {
        if (index is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(index));
        var pose = Flight.Pose;
        var velocity = Flight.Velocity;
        Flight.ApplyWorkerMotionState(_banks[index].Pose, _banks[index].Velocity);
        try { return action(); }
        finally { Flight.ApplyWorkerMotionState(pose, velocity); }
    }

    private void CheckPhase(OosGatePhase phase)
    {
        if (phase.Sequence <= _lastSequence || !double.IsFinite(phase.Now) || phase.Now < _lastTime || phase.Now < Flight.Now)
            throw new ArgumentException("An explicit increasing sequence and nondecreasing finite phase time are required.", nameof(phase));
        if (_phaseCount >= _maximumPhases)
            throw new InvalidOperationException("The declared gate phase bound has been reached.");
    }

    private void CommitPhase(OosGatePhase phase)
    {
        _lastSequence = phase.Sequence;
        _lastTime = phase.Now;
        _phaseCount++;
    }

    private static void ValidateControls(OosEngineModeUpdateInputs? controls)
    {
        if (controls?.TravelControl is not { } travel || controls.BoostControl is not { } boost ||
            controls.BoostReleaseEnabled is null || !float.IsFinite(travel) || !float.IsFinite(boost))
            throw new NotSupportedException("UNKNOWN: finite travel/boost controls and boost release eligibility are required.");
    }

    private static void ValidateBank(OosGateMotionBank bank)
    {
        ValidatePose(bank.Pose);
        ValidateVector(bank.Velocity);
        if (bank.AngularVelocity is { } angular) ValidateVector(angular);
        ValidateVector(bank.Auxiliary90);
        ValidateVector(bank.AuxiliaryA0);
    }

    private static void ValidateParameters(OosLinearFlightParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        static bool Rates(OosLinearAngularRates rates) =>
            float.IsFinite(rates.Yaw) && rates.Yaw >= 0 && float.IsFinite(rates.Pitch) && rates.Pitch >= 0 &&
            float.IsFinite(rates.Roll) && rates.Roll >= 0;
        if (!float.IsFinite(parameters.Acceleration) || parameters.Acceleration < 0 ||
            !float.IsFinite(parameters.Deceleration) || parameters.Deceleration < 0 ||
            !float.IsFinite(parameters.SpeedCap) || parameters.SpeedCap < 0 ||
            !float.IsFinite(parameters.Proximity) || parameters.Proximity < 0 ||
            !float.IsFinite(parameters.Curvature) || parameters.RemainingPointCount < 1 ||
            parameters.ActiveAndPendingPointCount is < 0 || !Rates(parameters.TurnRates) || !Rates(parameters.EtaTurnRates))
            throw new ArgumentOutOfRangeException(nameof(parameters), "Finite ordinary Linear parameters are required before worker mutation.");
    }

    private static void ValidatePose(OosLinearPose pose)
    {
        ValidateVector(pose.Position); ValidateVector(pose.XAxis);
        ValidateVector(pose.YAxis); ValidateVector(pose.ZAxis);
    }

    private static void ValidateVector(OosLinearVector4 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z) || !float.IsFinite(value.W))
            throw new ArgumentOutOfRangeException(nameof(value), "Motion fields must be finite float32 values.");
    }
}
