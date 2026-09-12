using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

/// <summary>事件输入的证据边界；重放结果不能自动升级为事前预测。</summary>
public enum OosCrossGateScenarioBasis
{
    ConditionalEventReplay,
    PredeclaredScenario
}

public enum OosCrossGateTraversalBranch
{
    ExplicitCapitalWarp,
    MoveIntoGateFallback
}

public enum OosCrossGateMoveActionRole
{
    EntryApproach,
    ExitIntermediate,
    ExitSafePoint
}

/// <summary>
/// move.gate.xml 的脚本调度边界。Eligible 是脚本最早可继续的时刻，不代表 worker/manager/事件系统
/// 在该时刻必然接受；实际接受仍由后续显式相位输入负责。
/// </summary>
public sealed record OosCrossGatePredeclaredScriptSchedule(
    OosCrossGateTraversalBranch Branch,
    double? StopSubmitSeconds,
    double? PreWarpWaitSeconds,
    double? WarpEffectWaitSeconds,
    double? PostWarpTransitionWaitSeconds,
    double? FallbackDestinationSectorWaitSeconds,
    double SafePointTimerSeconds,
    double? WarpSubmitSeconds,
    double? WarpDeliverySeconds,
    double? ContextSubmitSeconds,
    double AcrossEligibleSeconds,
    double ExitPointsSubmitSeconds,
    double SafePointSubmitSeconds,
    double IntermediateTimerDeadlineSeconds,
    double SafePointTimerDeadlineSeconds);

public static class OosCrossGatePredeclaredScenario
{
    public const double PostWarpConnectionWaitSeconds = .001;
    public const double PostWarpEffectWaitSeconds = .002;
    public const double FallbackDestinationSectorWaitSeconds = .2;
    public const double IntermediateTimerSeconds = 0;

    public static OosCrossGatePredeclaredScriptSchedule CreateExplicitCapitalWarp(
        double stopSubmitSeconds,
        double preWarpWaitSeconds,
        double warpEffectWaitSeconds,
        double warpDeliverySeconds,
        double contextSubmitSeconds,
        double postWarpTransitionWaitSeconds,
        double exitPointsSubmitSeconds,
        double safePointSubmitSeconds,
        double safePointTimerSeconds)
    {
        RequireRange(preWarpWaitSeconds, 6, 7, nameof(preWarpWaitSeconds));
        RequireRange(warpEffectWaitSeconds, 1, 1.5, nameof(warpEffectWaitSeconds));
        RequireRange(postWarpTransitionWaitSeconds, 1.5, 2, nameof(postWarpTransitionWaitSeconds));
        RequireRange(safePointTimerSeconds, 11, 17, nameof(safePointTimerSeconds));
        NonNegative(stopSubmitSeconds, nameof(stopSubmitSeconds));
        var warpSubmit = stopSubmitSeconds + preWarpWaitSeconds + warpEffectWaitSeconds;
        var acrossEligible = warpSubmit + PostWarpConnectionWaitSeconds +
            PostWarpEffectWaitSeconds + postWarpTransitionWaitSeconds;
        if (!double.IsFinite(warpSubmit) || !double.IsFinite(acrossEligible))
            throw new ArgumentOutOfRangeException(nameof(stopSubmitSeconds));
        if (!double.IsFinite(warpDeliverySeconds) || warpDeliverySeconds < warpSubmit ||
            warpDeliverySeconds > acrossEligible)
            throw new ArgumentOutOfRangeException(nameof(warpDeliverySeconds),
                "Predeclared successful explicit warp delivery must occur from submit through the destination-context check.");
        if (!double.IsFinite(contextSubmitSeconds) || contextSubmitSeconds < warpDeliverySeconds ||
            contextSubmitSeconds > acrossEligible)
            throw new ArgumentOutOfRangeException(nameof(contextSubmitSeconds),
                "Context submission must occur after warp delivery and before the destination-context check.");
        if (!double.IsFinite(exitPointsSubmitSeconds) || exitPointsSubmitSeconds < acrossEligible)
            throw new ArgumentOutOfRangeException(nameof(exitPointsSubmitSeconds));
        if (!double.IsFinite(safePointSubmitSeconds) || safePointSubmitSeconds < exitPointsSubmitSeconds)
            throw new ArgumentOutOfRangeException(nameof(safePointSubmitSeconds));
        return new(OosCrossGateTraversalBranch.ExplicitCapitalWarp, stopSubmitSeconds,
            preWarpWaitSeconds, warpEffectWaitSeconds, postWarpTransitionWaitSeconds, null,
            safePointTimerSeconds, warpSubmit, warpDeliverySeconds, contextSubmitSeconds,
            acrossEligible, exitPointsSubmitSeconds,
            safePointSubmitSeconds, exitPointsSubmitSeconds,
            safePointSubmitSeconds + safePointTimerSeconds);
    }

    /// <summary>
    /// line 462/463 的 200ms 只属于 move-into-gate fallback。当前连续 product driver 尚未闭合该分支，
    /// 但保留真实脚本边界，不能把 200ms 加进 explicit-warp 路径。
    /// </summary>
    public static OosCrossGatePredeclaredScriptSchedule CreateMoveIntoGateFallback(
        double gateMoveActionReturnSeconds,
        double exitPointsSubmitSeconds,
        double safePointSubmitSeconds,
        double safePointTimerSeconds)
    {
        NonNegative(gateMoveActionReturnSeconds, nameof(gateMoveActionReturnSeconds));
        RequireRange(safePointTimerSeconds, 11, 17, nameof(safePointTimerSeconds));
        var acrossEligible = gateMoveActionReturnSeconds + FallbackDestinationSectorWaitSeconds;
        if (!double.IsFinite(acrossEligible) || !double.IsFinite(exitPointsSubmitSeconds) ||
            exitPointsSubmitSeconds < acrossEligible)
            throw new ArgumentOutOfRangeException(nameof(exitPointsSubmitSeconds));
        if (!double.IsFinite(safePointSubmitSeconds) || safePointSubmitSeconds < exitPointsSubmitSeconds)
            throw new ArgumentOutOfRangeException(nameof(safePointSubmitSeconds));
        return new(OosCrossGateTraversalBranch.MoveIntoGateFallback, null,
            null, null, null, FallbackDestinationSectorWaitSeconds, safePointTimerSeconds,
            null, null, null, acrossEligible, exitPointsSubmitSeconds,
            safePointSubmitSeconds, exitPointsSubmitSeconds,
            safePointSubmitSeconds + safePointTimerSeconds);
    }

    private static void RequireRange(double value, double minimum, double maximum, string parameterName)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void NonNegative(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}

/// <summary>
/// 由 SectorRoute 选定、且两侧均由存档扫描闭合姿态的单个普通门对。
/// PreparedEndpoint 当前只保留存档 sector id，而 GateInfo 使用 macro id，故归属映射由保留两种 id 的
/// 路由调用方显式确认，不能在此按字符串相等猜测。
/// </summary>
public sealed record OosCrossGateRouteContract(
    GateInfo? DepartureGate,
    GateInfo? ArrivalGate,
    bool? DepartureGateBelongsToSourceSector,
    bool? ArrivalGateBelongsToTargetSector);

public sealed record OosCrossGateExitGeometryInput(
    X4RigidTransform ShipInDestination,
    X4RigidTransform ExitGateInDestination,
    float ShipLength,
    float ShipSafeSize,
    float ExitGateSize,
    OosGateExitSide Side,
    OosGateNextPositionCondition NextPosition,
    OosGateSafePointConditions SafePointConditions,
    bool OrdinarySuccessfulSingleShip);

public sealed record OosCrossGateEntryGeometryInput(
    X4RigidTransform ShipInSector,
    X4RigidTransform GateInSector,
    float ShipDistanceToGate,
    float ShipSize,
    float ShipSafeSize,
    float GateSize,
    float RandomOffsetY,
    OosGateEntryLateralSide LateralSide,
    OosGateEntrySafePointConditions SafePointConditions,
    bool OrdinarySuccessfulSingleCapitalGate);

public abstract record OosCrossGateTripEvent(OosGatePhase Phase);

/// <summary>下一 gate move_to 的默认 abortpath 消费；与 source timer 和 StopMoving 都是独立边界。</summary>
public sealed record OosCrossGateAbortPathEvent(
    OosGatePhase Phase,
    bool ClearImmediately) : OosCrossGateTripEvent(Phase);

public sealed record OosCrossGateDeliverPointsEvent(
    OosGatePhase Phase,
    IReadOnlyList<OosNavigationPathQueueEntry> Points) : OosCrossGateTripEvent(Phase);

/// <summary>入口无需绕行时 ApproachPoint 必须为空；需要绕行时只核对并交付调用方给出的真实 pose/模式。</summary>
public sealed record OosCrossGateDeliverEntryPointEvent(
    OosGatePhase Phase,
    OosCrossGateEntryGeometryInput Geometry,
    OosNavigationPathQueueEntry? ApproachPoint,
    bool? NoBoost = null,
    X4RigidTransform? PointReferenceInCommonSpace = null) : OosCrossGateTripEvent(Phase);

/// <summary>
/// 普通出口脚本先提交的 interpos。safepos 只在 intermediate MoveTo 返回后另行生成和提交。
/// </summary>
public sealed record OosCrossGateDeliverExitPointsEvent(
    OosGatePhase Phase,
    OosCrossGateExitGeometryInput Geometry,
    OosNavigationPathQueueEntry IntermediatePoint,
    bool? NoBoost = null,
    X4RigidTransform? PointReferenceInCommonSpace = null) : OosCrossGateTripEvent(Phase);

/// <summary>intermediate MoveTo 已返回后才提交的 safepos；其 timer 从本相位开始。</summary>
public sealed record OosCrossGateDeliverExitSafePointEvent(
    OosGatePhase Phase,
    OosNavigationPathQueueEntry SafePoint,
    bool? NoBoost = null,
    X4RigidTransform? PointReferenceInCommonSpace = null) : OosCrossGateTripEvent(Phase);

public sealed record OosCrossGateStopMovingEvent(OosGatePhase Phase) : OosCrossGateTripEvent(Phase);
public sealed record OosCrossGateWarpEvent(OosGatePhase Phase, OosLinearPose Pose) : OosCrossGateTripEvent(Phase);
public sealed record OosCrossGateContextEvent(
    OosGatePhase Phase,
    ulong NewSpace,
    bool? OrdinaryBranchWithoutOtherEffects) : OosCrossGateTripEvent(Phase);
public sealed record OosCrossGateManagerEvent(
    OosGatePhase Phase,
    OosGateManagerBoundary Boundary) : OosCrossGateTripEvent(Phase);
public sealed record OosCrossGateWorkerEvent(
    OosGatePhase Phase,
    double DeltaSeconds,
    bool? OrdinaryCopyEligible,
    bool? DirtyConsumerEligible,
    bool? RequireOrientation,
    bool? SuppressCompletion,
    bool? OrdinaryLastWaypointApproachEligible = null,
    bool? SuppressLastWaypointApproach = null) : OosCrossGateTripEvent(Phase);
public sealed record OosCrossGateParameterRefreshEvent(
    OosGatePhase Phase,
    int MotionBankIndex,
    OosEngineModeUpdateInputs? Controls,
    bool? ListenerEffectsAlreadyApplied) : OosCrossGateTripEvent(Phase);
public sealed record OosCrossGateModeRequestEvent(
    OosGatePhase Phase,
    int MotionBankIndex,
    OosNavigationModeRequest Request) : OosCrossGateTripEvent(Phase);
public sealed record OosCrossGateTravelStartEvent(
    OosGatePhase Phase,
    int MotionBankIndex) : OosCrossGateTripEvent(Phase);
/// <summary>显式 warp 提交时刻；实际 pose 交付仍由后续 WarpEvent 表示。</summary>
public sealed record OosCrossGateWarpSubmittedEvent(OosGatePhase Phase) : OosCrossGateTripEvent(Phase);
public sealed record OosCrossGateDeliverMoveCompletionEvent(
    OosGatePhase Phase,
    OosCrossGateMoveActionRole Role) : OosCrossGateTripEvent(Phase);
public sealed record OosCrossGateAcceptMoveActionEvent(
    OosGatePhase Phase,
    OosCrossGateMoveActionRole Role,
    OosMoveActionCandidate Candidate,
    bool? IsDirectLeaf) : OosCrossGateTripEvent(Phase);

/// <summary>
/// 一次性连续状态输入。SourceDeparture 从“源站装货完成”计时，Driver 从其 route-start pose/velocity
/// 和 elapsed 时刻继续；Events 是接受全序，只允许提供事件边界和实际 dt，不提供逐 tick 实测
/// pose/velocity 回填。
/// </summary>
public sealed record OosCrossGateTripInput(
    OosCrossGateScenarioBasis Basis,
    OosCrossGateRouteContract? Route,
    OosGateSegmentDriver? Driver,
    IReadOnlyList<OosCrossGateTripEvent>? Events,
    OosCrossGatePredeclaredScriptSchedule? PredeclaredSchedule,
    OosMoveActionHandoff? EntryApproachAction,
    OosMoveActionHandoff? ExitIntermediateAction,
    OosMoveActionHandoff? ExitSafePointAction,
    OosSourceDepartureHandoffResult? SourceDeparture,
    OosTransportDestinationScenario Destination,
    bool? PreservesContinuousControllerState,
    bool? NoUnmodeledMotionEffects);

public sealed record OosCrossGateTripSimulation(
    OosCrossGateScenarioBasis Basis,
    OosTransportTripSimulation Trip,
    double ExitIntermediateScriptReturnSeconds,
    double ExitScriptReturnSeconds,
    double ExitSafePointCompletionSeconds,
    OosGateExitTargets ExitTargets);

public sealed record OosCrossGateTripResult(
    OosTransportPhaseStatus Status,
    OosCrossGateScenarioBasis Basis,
    OosCrossGateTripSimulation? Simulation,
    IReadOnlyList<string> UnknownReasons)
{
    public bool IsPredeclaredPrediction =>
        Status == OosTransportPhaseStatus.Conditional &&
        Basis == OosCrossGateScenarioBasis.PredeclaredScenario;
}

/// <summary>
/// 跨门条件单程的一次性接线入口。它执行显式 gate 事件，再从同一个 Linear/Dynamics 状态接到
/// 目的 zone、泊位、landing 与卸货。未闭合的运动输入返回 Unknown，不返回已知阶段之和。
/// </summary>
public static class OosCrossGateTripSimulator
{
    private const double PositionTolerance = .001;

    public static IReadOnlyList<string> RequiredPredeclaredDynamicInputs { get; } =
    [
        "Exact source script-handoff clock plus preserved last-worker pose, velocity and unfinished path.",
        "Complete dual motion banks, path queue, dynamics parameters and driver clock at handoff.",
        "Declared worker/manager/AI periods, phase offsets, same-time order and ordinary accepted branches.",
        "Declared warp/context delivery phases plus listener, parent, constraint and reservation conditions.",
        "Native type-2 point reference/predecessor, noboost, finish-on-approach publisher and action-consumer policies.",
        "Destination berth, refresh period/phase, scheduler and explicit random-state inputs."
    ];

    public static OosCrossGateTripResult Simulate(
        OosTransportShipProfile profile,
        OosTransportPreparedEndpoint source,
        OosTransportPreparedEndpoint target,
        string sourceBerthId,
        OosCrossGateTripInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBerthId);
        ArgumentNullException.ThrowIfNull(input);
        if (!Enum.IsDefined(input.Basis)) throw new ArgumentOutOfRangeException(nameof(input));

        try
        {
            if (source.SectorId == target.SectorId)
                throw new NotSupportedException("UNKNOWN: a cross-gate trip requires distinct source and destination sectors.");
            if (source.Berths.All(x => x.Id != sourceBerthId))
                throw new NotSupportedException("UNKNOWN: the declared source berth is not eligible at the source endpoint.");
            var route = ValidateRoute(input.Route);
            if (input.PreservesContinuousControllerState != true)
                throw new NotSupportedException("UNKNOWN: one continuous Linear/Path/Dynamics controller is required through the gate.");
            if (input.NoUnmodeledMotionEffects != true)
                throw new NotSupportedException("UNKNOWN: unresolved parent, listener, constraint or reservation effects may change motion.");
            if (input.Driver is null)
                throw new NotSupportedException("UNKNOWN: the complete gate driver state is required.");
            if (input.Driver.PhaseCount != 0 || input.Driver.LastPhaseSequence != -1)
                throw new NotSupportedException("UNKNOWN: cross-gate simulation requires a fresh one-shot driver.");
            if (input.Events is null || input.Events.Count == 0)
                throw new NotSupportedException("UNKNOWN: an explicit ordered gate event timeline is required.");
            if (input.ExitIntermediateAction is null)
                throw new NotSupportedException("UNKNOWN: the exit intermediate-point zero-timer action state is required.");
            if (input.ExitSafePointAction is null)
                throw new NotSupportedException("UNKNOWN: the exit safe-point action completion/timer state is required.");
            if (input.Destination is null)
                throw new NotSupportedException("UNKNOWN: explicit destination scheduler conditions are required.");
            var sourceDepartureSeconds = ValidateSourceDeparture(input.SourceDeparture, input.Driver);
            if (!input.Destination.NoOtherConstraints || !input.Destination.NoBerthQueue ||
                !input.Destination.NoOtherZoneCandidates || !input.Destination.NoNavigationTransitionWait)
                throw new NotSupportedException("UNKNOWN: destination obstruction, berth queue, zone priority or Navigation wait is unresolved.");
            if (!input.Destination.OrdinaryDestinationWorkerResolutionConfirmed)
                throw new NotSupportedException("UNKNOWN: the first destination worker's engine/event resolution branch is unresolved.");

            ValidateScenarioBasis(input);

            var targets = OosTransportTripSimulator.PrepareDestinationTargets(profile, target,
                input.Destination.InitialBerthSeed, input.Destination.GenericTargetSeed);
            var state = ExecuteGateEvents(input.Driver, input.EntryApproachAction, input.ExitIntermediateAction,
                input.ExitSafePointAction, input.Events, route, cancellationToken);
            if (state.ExitTargets is null)
                throw new NotSupportedException("UNKNOWN: verified exit interpos/safepos geometry and submitted FlightPoints are required.");
            if (state.ExitSafePointCompletionSeconds is null)
                throw new NotSupportedException("UNKNOWN: the exit safe FlightPoint has not physically completed.");
            if (state.ExitIntermediateScriptReturnSeconds is null)
                throw new NotSupportedException("UNKNOWN: the exit intermediate MoveTo zero-timer was not accepted.");
            if (state.ExitScriptReturnSeconds is null)
                throw new NotSupportedException("UNKNOWN: the exit MoveTo action has not accepted a completion/timer winner.");
            if (input.Driver.Flight.Now + 1e-9 < state.ExitScriptReturnSeconds)
                throw new NotSupportedException("UNKNOWN: no continuous worker state reaches the accepted exit script return.");
            if (input.Driver.PendingOrderCount != 0)
                throw new NotSupportedException("UNKNOWN: unconsumed gate manager FIFO orders remain.");
            if (input.Driver.Path.Pending.Count != 0)
                throw new NotSupportedException("UNKNOWN: pending exit/navigation FlightPoints remain unmerged.");
            if (input.Driver.Path.Active.Count != 0)
                throw new NotSupportedException("UNKNOWN: destination replan requires the gate FlightPoint queue to be empty.");
            var readBank = input.Driver.Banks[input.Driver.ReadIndex];
            if (!Near(input.Driver.Flight.Pose, readBank.Pose) ||
                !Near(input.Driver.Flight.Velocity, readBank.Velocity))
                throw new NotSupportedException("UNKNOWN: final worker output has not been committed to the current read bank.");

            var handoff = input.Driver.Flight.Now;
            var phases = new List<OosTransportTripPhase>
            {
                new("sourceDeparture", 0, sourceDepartureSeconds),
                new("sourceRouteToGateAndPhysicalExit", sourceDepartureSeconds, handoff)
            };
            var trip = OosTransportTripSimulator.ContinueToDestination(profile, source, target, sourceBerthId,
                targets.Initial, targets.Generic, input.Destination, input.Driver.Dynamics, input.Driver.Flight,
                phases, handoff, replanAtStart: true, checkSourceZoneOverlap: false,
                cancellationToken: cancellationToken);
            return new(OosTransportPhaseStatus.Conditional, input.Basis,
                new(input.Basis, trip, state.ExitIntermediateScriptReturnSeconds.Value,
                    state.ExitScriptReturnSeconds.Value,
                    state.ExitSafePointCompletionSeconds.Value, state.ExitTargets), []);
        }
        catch (NotSupportedException ex)
        {
            return new(OosTransportPhaseStatus.Unknown, input.Basis, null, [ex.Message]);
        }
    }

    private static GateExecutionState ExecuteGateEvents(
        OosGateSegmentDriver driver,
        OosMoveActionHandoff? entryAction,
        OosMoveActionHandoff intermediateAction,
        OosMoveActionHandoff safePointAction,
        IReadOnlyList<OosCrossGateTripEvent> events,
        ResolvedRoute route,
        CancellationToken cancellationToken)
    {
        long sequence = -1;
        var now = driver.LastPhaseTime;
        OosGateExitTargets? exitTargets = null;
        double? safeComplete = null;
        double? intermediateScriptReturn = null;
        double? safePointScriptReturn = null;
        double? entryScriptReturn = null;
        var sawWarp = false;
        var sawWarpSubmit = false;
        var sawContext = false;
        var sawManager = false;
        var sawStop = false;
        var sawEntryGeometry = false;
        var sawEntryAbortPath = false;
        ulong? submittedContextSpace = null;
        OosNavigationPathQueueEntry? entryApproach = null;
        X4RigidTransform? entryPointReference = null;
        bool? entryNoBoost = null;
        var entryApproachTriggered = false;
        OosNavigationPathQueueEntry? exitIntermediatePoint = null;
        var exitIntermediateCompleted = false;
        OosNavigationPathQueueEntry? exitSafePoint = null;
        X4RigidTransform? exitIntermediatePointReference = null;
        X4RigidTransform? exitSafePointReference = null;
        bool? exitNoBoost = null;

        foreach (var item in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(item);
            if (item.Phase.Sequence <= sequence || !double.IsFinite(item.Phase.Now) || item.Phase.Now < now)
                throw new ArgumentException("Cross-gate events require an explicit increasing sequence and nondecreasing elapsed time.", nameof(events));
            sequence = item.Phase.Sequence;
            now = item.Phase.Now;

            switch (item)
            {
                case OosCrossGateAbortPathEvent e:
                    if (sawEntryGeometry || sawEntryAbortPath || !e.ClearImmediately)
                        throw new NotSupportedException(
                            "UNKNOWN: gate approach requires its native immediate abortpath before point submission.");
                    driver.DeliverAbortPath(e.Phase, e.ClearImmediately);
                    sawEntryAbortPath = true;
                    break;
                case OosCrossGateDeliverEntryPointEvent e:
                {
                    var point = PrepareAndValidateEntry(driver, e, route.DepartureTransform);
                    if (point is not null)
                    {
                        if (!sawEntryAbortPath)
                            throw new NotSupportedException(
                                "UNKNOWN: gate approach point was submitted without the move_to default abortpath.");
                        RequireUnmarkedPoint(point, "entry approach");
                        entryApproach = point;
                        entryPointReference = e.PointReferenceInCommonSpace ??
                            throw new NotSupportedException("UNKNOWN: entry point reference is required for pending-merge heading validation.");
                        entryNoBoost = e.NoBoost;
                        driver.DeliverPoints(e.Phase, [point]);
                    }
                    sawEntryGeometry = true;
                    break;
                }
                case OosCrossGateDeliverPointsEvent e:
                    driver.DeliverPoints(e.Phase, e.Points);
                    break;
                case OosCrossGateDeliverExitPointsEvent e:
                    if (!sawStop || !sawWarpSubmit || !sawWarp || !sawContext ||
                        driver.PendingOrderCount != 0 || submittedContextSpace is null ||
                        driver.ContextSpace != submittedContextSpace)
                        throw new NotSupportedException(
                            "UNKNOWN: exit points require ordered warp/context submission and accepted manager FIFO.");
                    if (exitTargets is not null)
                        throw new NotSupportedException("UNKNOWN: multiple exit geometry submissions require separate action generations.");
                    exitTargets = PrepareAndValidateExit(driver, e, route.ArrivalTransform);
                    RequireUnmarkedPoint(e.IntermediatePoint, "exit intermediate");
                    exitIntermediatePoint = e.IntermediatePoint;
                    exitIntermediatePointReference = e.PointReferenceInCommonSpace ??
                        throw new NotSupportedException("UNKNOWN: exit intermediate point reference is required for pending-merge heading validation.");
                    exitNoBoost = e.NoBoost;
                    driver.DeliverPoints(e.Phase, [e.IntermediatePoint]);
                    break;
                case OosCrossGateDeliverExitSafePointEvent e:
                    if (exitTargets is null ||
                        !Near(Position(e.SafePoint.Point.Target), exitTargets.SafePosition))
                        throw new NotSupportedException("UNKNOWN: exit safe point does not match the verified geometry target.");
                    ValidateScriptPointModes(e.SafePoint, entryApproach: false, exitNoBoost);
                    if (e.NoBoost != exitNoBoost)
                        throw new NotSupportedException("UNKNOWN: exit safe-point noboost differs from the intermediate script branch.");
                    exitSafePointReference = e.PointReferenceInCommonSpace ??
                        throw new NotSupportedException("UNKNOWN: exit safe-point reference is required for pending-merge heading validation.");
                    if (intermediateScriptReturn is null)
                        throw new NotSupportedException("UNKNOWN: exit safe point was submitted before intermediate MoveTo returned.");
                    RequireUnmarkedPoint(e.SafePoint, "exit safe");
                    exitSafePoint = e.SafePoint;
                    driver.DeliverPoints(e.Phase, [e.SafePoint]);
                    break;
                case OosCrossGateStopMovingEvent e:
                    if (entryApproach is not null && !entryApproachTriggered)
                        throw new NotSupportedException("UNKNOWN: gate-entry FlightPoint did not reach the native finish-on-approach threshold before StopMoving.");
                    if (entryApproach is not null &&
                        (entryScriptReturn is null || entryAction?.Continued != true ||
                         entryScriptReturn > e.Phase.Now + 1e-9))
                        throw new NotSupportedException("UNKNOWN: gate-entry MoveTo did not return before StopMoving.");
                    driver.DeliverStopMoving(e.Phase);
                    sawStop = true;
                    break;
                case OosCrossGateWarpEvent e:
                    if (!sawWarpSubmit)
                        throw new NotSupportedException("UNKNOWN: warp delivery preceded explicit warp submission.");
                    driver.SubmitExplicitWarpPose(e.Phase, e.Pose);
                    sawWarp = true;
                    break;
                case OosCrossGateContextEvent e:
                    if (!sawWarp)
                        throw new NotSupportedException("UNKNOWN: context submission preceded warp delivery.");
                    driver.SubmitContextChange(e.Phase, e.NewSpace, e.OrdinaryBranchWithoutOtherEffects);
                    submittedContextSpace = e.NewSpace;
                    sawContext = true;
                    break;
                case OosCrossGateManagerEvent e:
                    driver.FinishManager(e.Phase, e.Boundary);
                    sawManager = true;
                    break;
                case OosCrossGateWorkerEvent e:
                {
                    var head = driver.Path.Active.Count != 0 ? driver.Path.Active[0] :
                        driver.Path.Pending.Count != 0 ? driver.Path.Pending[0] : null;
                    if (!entryApproachTriggered && entryApproach is not null &&
                        ReferenceEquals(head, entryApproach) &&
                        (e.OrdinaryLastWaypointApproachEligible is null ||
                         e.SuppressLastWaypointApproach is null))
                        throw new NotSupportedException(
                            "UNKNOWN: entry finish-on-approach requires explicit ordinary and suppression branch conditions.");
                    ValidatePendingScriptPointAtMerge(driver, entryApproach, entryApproach: true,
                        entryNoBoost, entryPointReference);
                    ValidatePendingScriptPointAtMerge(driver, exitIntermediatePoint, entryApproach: false,
                        exitNoBoost, exitIntermediatePointReference);
                    ValidatePendingScriptPointAtMerge(driver, exitSafePoint, entryApproach: false,
                        exitNoBoost, exitSafePointReference);
                    var result = driver.StepWorker(e.Phase, e.DeltaSeconds, e.OrdinaryCopyEligible,
                        e.DirtyConsumerEligible, e.RequireOrientation, e.SuppressCompletion);
                    if (result.Completion?.Popped == true && exitTargets is not null && head is not null &&
                        ReferenceEquals(head, exitSafePoint))
                        safeComplete = e.Phase.Now;
                    if (!entryApproachTriggered && entryApproach is not null &&
                        ReferenceEquals(head, entryApproach) &&
                        OosTransportArrivalGeometry.IsLastWaypointApproached(
                            result.Linear.U,
                            driver.Flight.Length,
                            entryApproach.Point.Radius,
                            driver.Flight.Parameters.Proximity,
                            result.Queue.RemainingPointCount,
                            ordinaryLinearBranch: e.OrdinaryLastWaypointApproachEligible == true,
                            suppressApproach: e.SuppressLastWaypointApproach == true))
                        entryApproachTriggered = true;
                    if (result.Completion?.Popped == true && ReferenceEquals(head, exitIntermediatePoint))
                        exitIntermediateCompleted = true;
                    break;
                }
                case OosCrossGateParameterRefreshEvent e:
                    driver.ForceParameterRefresh(e.Phase, e.MotionBankIndex, e.Controls, e.ListenerEffectsAlreadyApplied);
                    break;
                case OosCrossGateModeRequestEvent e:
                    driver.DeliverModeRequest(e.Phase, e.MotionBankIndex, e.Request);
                    break;
                case OosCrossGateTravelStartEvent e:
                    driver.DeliverTravelStart(e.Phase, e.MotionBankIndex);
                    break;
                case OosCrossGateWarpSubmittedEvent:
                    if (!sawStop)
                        throw new NotSupportedException("UNKNOWN: explicit warp submission preceded StopMoving.");
                    sawWarpSubmit = true;
                    break;
                case OosCrossGateDeliverMoveCompletionEvent e:
                    var physicallyCompleted = e.Role switch
                    {
                        OosCrossGateMoveActionRole.EntryApproach => entryApproachTriggered,
                        OosCrossGateMoveActionRole.ExitIntermediate => exitIntermediateCompleted,
                        OosCrossGateMoveActionRole.ExitSafePoint => safeComplete is not null,
                        _ => false
                    };
                    if (!physicallyCompleted)
                        throw new NotSupportedException(
                            e.Role == OosCrossGateMoveActionRole.EntryApproach
                                ? "UNKNOWN: entry MoveTo completion was published before its native finish-on-approach threshold."
                                : "UNKNOWN: normal MoveTo completion was published before its exact FlightPoint physically completed.");
                    SelectAction(e.Role, entryAction, intermediateAction, safePointAction)
                        .DeliverNormalCompletion(e.Phase.Sequence, e.Phase.Now);
                    break;
                case OosCrossGateAcceptMoveActionEvent e:
                    if (SelectAction(e.Role, entryAction, intermediateAction, safePointAction)
                        .Accept(e.Phase.Sequence, e.Phase.Now, e.Candidate, e.IsDirectLeaf) ==
                        OosMoveActionAcceptance.Continued)
                    {
                        if (e.Role == OosCrossGateMoveActionRole.EntryApproach)
                            entryScriptReturn = e.Phase.Now;
                        else if (e.Role == OosCrossGateMoveActionRole.ExitIntermediate)
                            intermediateScriptReturn = e.Phase.Now;
                        else
                            safePointScriptReturn = e.Phase.Now;
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(events), item.GetType().Name, "Unsupported cross-gate event type.");
            }
        }

        if (!sawEntryGeometry || !sawStop || !sawWarpSubmit || !sawWarp || !sawContext || !sawManager)
            throw new NotSupportedException("UNKNOWN: verified gate entry, stop, warp submit/delivery, context submission and manager acceptance are all required.");
        if (entryApproach is null && entryScriptReturn is not null)
            throw new NotSupportedException("UNKNOWN: straight-through gate entry cannot consume an approach MoveTo action.");
        if (entryApproach is null && sawEntryAbortPath)
            throw new NotSupportedException("UNKNOWN: straight-through gate entry cannot invent an approach move_to abortpath.");
        return new(exitTargets, safeComplete, intermediateScriptReturn, safePointScriptReturn);
    }

    private static void ValidateScenarioBasis(OosCrossGateTripInput input)
    {
        if (input.Basis == OosCrossGateScenarioBasis.ConditionalEventReplay)
        {
            if (input.PredeclaredSchedule is not null)
                throw new NotSupportedException("UNKNOWN: replay evidence cannot carry a predeclared script schedule.");
            return;
        }

        var schedule = input.PredeclaredSchedule ??
            throw new NotSupportedException("UNKNOWN: predeclared prediction requires the move.gate script schedule inputs.");
        if (schedule.Branch != OosCrossGateTraversalBranch.ExplicitCapitalWarp)
            throw new NotSupportedException("UNKNOWN: the move-into-gate fallback lacks a complete continuous product-driver handoff.");
        if (schedule.PreWarpWaitSeconds is not { } preWarpWait ||
            schedule.WarpEffectWaitSeconds is not { } warpEffectWait ||
            schedule.PostWarpTransitionWaitSeconds is not { } postWarpWait ||
            schedule.FallbackDestinationSectorWaitSeconds is not null ||
            schedule.StopSubmitSeconds is not { } stopSubmit ||
            schedule.WarpSubmitSeconds is not { } warpSubmit ||
            schedule.WarpDeliverySeconds is not { } warpDelivery ||
            schedule.ContextSubmitSeconds is not { } contextSubmit)
            throw new NotSupportedException("UNKNOWN: explicit-warp script wait and delivery inputs are incomplete.");
        RequireRange(preWarpWait, 6, 7, nameof(schedule.PreWarpWaitSeconds));
        RequireRange(warpEffectWait, 1, 1.5, nameof(schedule.WarpEffectWaitSeconds));
        RequireRange(postWarpWait, 1.5, 2, nameof(schedule.PostWarpTransitionWaitSeconds));
        RequireRange(schedule.SafePointTimerSeconds, 11, 17, nameof(schedule.SafePointTimerSeconds));
        RequireTime(stopSubmit, nameof(schedule.StopSubmitSeconds));
        RequireTime(schedule.ExitPointsSubmitSeconds, nameof(schedule.ExitPointsSubmitSeconds));
        RequireEqual(warpSubmit, stopSubmit + preWarpWait + warpEffectWait,
            "warp submit does not match the declared 6..7s and 1..1.5s waits");
        RequireEqual(schedule.AcrossEligibleSeconds,
            warpSubmit + OosCrossGatePredeclaredScenario.PostWarpConnectionWaitSeconds +
            OosCrossGatePredeclaredScenario.PostWarpEffectWaitSeconds + postWarpWait,
            "across-gate eligibility does not match the declared 1ms, 2ms and 1.5..2s waits");
        if (warpDelivery < warpSubmit || warpDelivery > schedule.AcrossEligibleSeconds)
            throw new NotSupportedException("UNKNOWN: actual warp delivery is outside the declared submit/context-check interval.");
        if (contextSubmit < warpDelivery || contextSubmit > schedule.AcrossEligibleSeconds)
            throw new NotSupportedException("UNKNOWN: context submit is outside the declared warp-delivery/context-check interval.");
        if (schedule.ExitPointsSubmitSeconds < schedule.AcrossEligibleSeconds)
            throw new NotSupportedException("UNKNOWN: exit points were submitted before the explicit-warp branch became eligible.");
        RequireEqual(schedule.IntermediateTimerDeadlineSeconds, schedule.ExitPointsSubmitSeconds,
            "intermediate MoveTo timer is not the native zero-second deadline");
        RequireEqual(schedule.SafePointTimerDeadlineSeconds,
            schedule.SafePointSubmitSeconds + schedule.SafePointTimerSeconds,
            "safe MoveTo timer does not match the declared 11..17s deadline");
        RequireEqual(input.ExitIntermediateAction!.TimerDeadline,
            schedule.IntermediateTimerDeadlineSeconds, "intermediate action timer state differs from the script schedule");
        RequireEqual(input.ExitSafePointAction!.TimerDeadline,
            schedule.SafePointTimerDeadlineSeconds, "safe-point action timer state differs from the script schedule");

        var events = input.Events!;
        var stop = RequireSingle<OosCrossGateStopMovingEvent>(events);
        var submit = RequireSingle<OosCrossGateWarpSubmittedEvent>(events);
        var delivery = RequireSingle<OosCrossGateWarpEvent>(events);
        var context = RequireSingle<OosCrossGateContextEvent>(events);
        var exit = RequireSingle<OosCrossGateDeliverExitPointsEvent>(events);
        var safeSubmit = RequireSingle<OosCrossGateDeliverExitSafePointEvent>(events);
        RequireEqual(stop.Phase.Now, stopSubmit, "stop event differs from the script schedule");
        RequireEqual(submit.Phase.Now, warpSubmit, "warp-submit event differs from the script schedule");
        RequireEqual(delivery.Phase.Now, warpDelivery, "warp-delivery event differs from the script schedule");
        RequireEqual(context.Phase.Now, contextSubmit, "context-submit event differs from the declared scheduler");
        RequireEqual(exit.Phase.Now, schedule.ExitPointsSubmitSeconds,
            "exit-point submission differs from the script schedule");
        RequireEqual(safeSubmit.Phase.Now, schedule.SafePointSubmitSeconds,
            "safe-point submission differs from the script schedule");
        if (!(stop.Phase.Sequence < submit.Phase.Sequence &&
              submit.Phase.Sequence < delivery.Phase.Sequence &&
              delivery.Phase.Sequence < context.Phase.Sequence &&
              context.Phase.Sequence < exit.Phase.Sequence))
            throw new NotSupportedException(
                "UNKNOWN: stop, warp submit/delivery, context and exit points are not in native acceptance order.");

        var intermediateAccepts = events.OfType<OosCrossGateAcceptMoveActionEvent>()
            .Where(x => x.Role == OosCrossGateMoveActionRole.ExitIntermediate).ToArray();
        if (intermediateAccepts is not [{ IsDirectLeaf: true } intermediate])
            throw new NotSupportedException("UNKNOWN: exactly one direct-leaf intermediate action acceptance is required.");
        if (intermediate.Candidate == OosMoveActionCandidate.Timer &&
            intermediate.Phase.Now < schedule.IntermediateTimerDeadlineSeconds)
            throw new NotSupportedException("UNKNOWN: intermediate timer was accepted before its eligibility deadline.");
        if (intermediate.Candidate == OosMoveActionCandidate.NormalCompletion &&
            !events.OfType<OosCrossGateDeliverMoveCompletionEvent>().Any(x =>
                x.Role == OosCrossGateMoveActionRole.ExitIntermediate &&
                x.Phase.Sequence < intermediate.Phase.Sequence))
            throw new NotSupportedException("UNKNOWN: intermediate normal completion was not delivered before acceptance.");
        if (exit.Phase.Sequence >= intermediate.Phase.Sequence ||
            intermediate.Phase.Sequence >= safeSubmit.Phase.Sequence ||
            intermediate.Phase.Now > safeSubmit.Phase.Now + 1e-9)
            throw new NotSupportedException("UNKNOWN: safe point was submitted before intermediate MoveTo returned.");
        var safeAccepts = events.OfType<OosCrossGateAcceptMoveActionEvent>()
            .Where(x => x.Role == OosCrossGateMoveActionRole.ExitSafePoint).ToArray();
        if (safeAccepts is not [{ IsDirectLeaf: true } safeAccept] ||
            safeSubmit.Phase.Sequence >= safeAccept.Phase.Sequence)
            throw new NotSupportedException("UNKNOWN: safe-point action acceptance must follow its submission.");
        if (safeAccept.Candidate == OosMoveActionCandidate.Timer &&
            safeAccept.Phase.Now < schedule.SafePointTimerDeadlineSeconds)
            throw new NotSupportedException("UNKNOWN: safe-point timer was accepted before its eligibility deadline.");
        if (safeAccept.Candidate == OosMoveActionCandidate.NormalCompletion &&
            !events.OfType<OosCrossGateDeliverMoveCompletionEvent>().Any(x =>
                x.Role == OosCrossGateMoveActionRole.ExitSafePoint &&
                x.Phase.Sequence > safeSubmit.Phase.Sequence &&
                x.Phase.Sequence < safeAccept.Phase.Sequence))
            throw new NotSupportedException("UNKNOWN: safe-point normal completion was not delivered before acceptance.");
    }

    private static T RequireSingle<T>(IReadOnlyList<OosCrossGateTripEvent> events)
        where T : OosCrossGateTripEvent
    {
        var matches = events.OfType<T>().ToArray();
        if (matches.Length != 1)
            throw new NotSupportedException($"UNKNOWN: predeclared scenario requires exactly one {typeof(T).Name}.");
        return matches[0];
    }

    private static void RequireRange(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new NotSupportedException($"UNKNOWN: {name} is outside the native script range {minimum}..{maximum}s.");
    }

    private static void RequireTime(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
            throw new NotSupportedException($"UNKNOWN: {name} is not a finite nonnegative elapsed time.");
    }

    private static void RequireEqual(double? actual, double expected, string reason)
    {
        if (actual is not { } value || Math.Abs(value - expected) > 1e-9)
            throw new NotSupportedException($"UNKNOWN: {reason}.");
    }

    private static OosMoveActionHandoff SelectAction(
        OosCrossGateMoveActionRole role,
        OosMoveActionHandoff? entryAction,
        OosMoveActionHandoff intermediateAction,
        OosMoveActionHandoff safePointAction) => role switch
        {
            OosCrossGateMoveActionRole.EntryApproach => entryAction ??
                throw new NotSupportedException("UNKNOWN: entry MoveTo action state is required for the approach branch."),
            OosCrossGateMoveActionRole.ExitIntermediate => intermediateAction,
            OosCrossGateMoveActionRole.ExitSafePoint => safePointAction,
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };

    private static void RequireUnmarkedPoint(OosNavigationPathQueueEntry point, string role)
    {
        if (point.Deadline > OosNavigationPathQueue.NativeDeadlineSentinelLimit)
            throw new NotSupportedException($"UNKNOWN: {role} FlightPoint must not carry a pre-existing deadline.");
    }

    private static OosNavigationPathQueueEntry? PrepareAndValidateEntry(
        OosGateSegmentDriver driver,
        OosCrossGateDeliverEntryPointEvent e,
        X4RigidTransform routeGateTransform)
    {
        ArgumentNullException.ThrowIfNull(e.Geometry);
        var g = e.Geometry;
        if (!Near(g.GateInSector, routeGateTransform))
            throw new NotSupportedException("UNKNOWN: gate-entry geometry does not use the route's saved departure-gate transform.");
        var declaredShipPose = OosTransportTripSimulator.Pose(g.ShipInSector.Rotation, g.ShipInSector.Position);
        if (!Near(driver.Flight.Pose, declaredShipPose))
            throw new NotSupportedException("UNKNOWN: gate-entry ship pose does not match the continuous driver state.");
        var result = OosTransportGateEntryGeometry.PrepareCapitalEntry(g.ShipInSector, g.GateInSector,
            g.ShipDistanceToGate, g.ShipSize, g.ShipSafeSize, g.GateSize, g.RandomOffsetY,
            g.LateralSide, g.SafePointConditions, g.OrdinarySuccessfulSingleCapitalGate);
        if (!result.RequiresApproach)
        {
            if (e.ApproachPoint is not null)
                throw new NotSupportedException("UNKNOWN: an entry FlightPoint was supplied for the native straight-through branch.");
            return null;
        }
        if (e.ApproachPoint is null || result.Target is null ||
            !Near(Position(e.ApproachPoint.Point.Target), result.Target.SafePosition))
            throw new NotSupportedException("UNKNOWN: the submitted gate-entry FlightPoint does not match verified safe geometry.");
        ValidateScriptPointModes(e.ApproachPoint, entryApproach: true, e.NoBoost);
        return e.ApproachPoint;
    }

    private static double ValidateSourceDeparture(
        OosSourceDepartureHandoffResult? sourceDeparture,
        OosGateSegmentDriver driver)
    {
        if (sourceDeparture is null || sourceDeparture.TotalDuration.Status == OosTransportPhaseStatus.Unknown ||
            sourceDeparture.TotalDuration.MinimumSeconds is not { } minimum ||
            sourceDeparture.TotalDuration.MaximumSeconds is not { } maximum ||
            sourceDeparture.TotalDuration.ReferenceSeconds is not { } elapsed ||
            minimum != maximum || minimum != elapsed)
            throw new NotSupportedException("UNKNOWN: an exact source recovery/departure/clearance handoff is required.");
        if (sourceDeparture.RouteStartPose is not { } pose || sourceDeparture.RouteStartVelocity is not { } velocity)
            throw new NotSupportedException("UNKNOWN: source departure did not provide the continuous route-start pose and velocity.");
        if (Math.Abs(driver.LastPhaseTime-elapsed) > 1e-9 || driver.Flight.Now > elapsed + 1e-9)
            throw new NotSupportedException("UNKNOWN: gate event times must remain elapsed seconds from source loading complete.");
        if (!Near(driver.Flight.Pose, pose) || !Near(driver.Flight.Velocity, velocity))
            throw new NotSupportedException("UNKNOWN: gate driver does not begin from the source departure handoff state.");
        return elapsed;
    }

    private static ResolvedRoute ValidateRoute(OosCrossGateRouteContract? route)
    {
        if (route?.DepartureGate is not { } departure || route.ArrivalGate is not { } arrival)
            throw new NotSupportedException("UNKNOWN: the route's departure and arrival gates are required.");
        if (!departure.FoundInSave || !arrival.FoundInSave ||
            departure.SaveGateSectorTransform is not { } departureTransform ||
            arrival.SaveGateSectorTransform is not { } arrivalTransform)
            throw new NotSupportedException("UNKNOWN: both route gates require save-confirmed sector transforms.");
        if (route.DepartureGateBelongsToSourceSector != true ||
            route.ArrivalGateBelongsToTargetSector != true)
            throw new NotSupportedException("UNKNOWN: route gate ownership has not been mapped to both prepared endpoint sectors.");
        if (string.IsNullOrWhiteSpace(departure.TargetGateId) ||
            !string.Equals(departure.TargetGateId, arrival.Id, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("UNKNOWN: the departure gate's explicit saved target does not identify the arrival gate.");
        return new(departureTransform, arrivalTransform);
    }

    private static OosGateExitTargets PrepareAndValidateExit(
        OosGateSegmentDriver driver,
        OosCrossGateDeliverExitPointsEvent e,
        X4RigidTransform routeGateTransform)
    {
        ArgumentNullException.ThrowIfNull(e.Geometry);
        ArgumentNullException.ThrowIfNull(e.IntermediatePoint);
        var g = e.Geometry;
        if (!Near(g.ExitGateInDestination, routeGateTransform))
            throw new NotSupportedException("UNKNOWN: gate-exit geometry does not use the route's saved arrival-gate transform.");
        var declaredShipPose = OosTransportTripSimulator.Pose(
            g.ShipInDestination.Rotation, g.ShipInDestination.Position);
        if (!Near(driver.Banks[driver.ReadIndex].Pose, declaredShipPose))
            throw new NotSupportedException("UNKNOWN: exit geometry ship pose does not match the accepted read-bank state.");
        var result = OosTransportGateGeometry.PrepareExit(g.ShipInDestination, g.ExitGateInDestination,
            g.ShipLength, g.ShipSafeSize, g.ExitGateSize, g.Side, g.NextPosition,
            g.SafePointConditions, g.OrdinarySuccessfulSingleShip);
        if (!Near(Position(e.IntermediatePoint.Point.Target), result.IntermediatePosition))
            throw new NotSupportedException("UNKNOWN: submitted exit FlightPoint position does not match verified interpos geometry.");
        ValidateScriptPointModes(e.IntermediatePoint, entryApproach: false, e.NoBoost);
        return result;
    }

    private static void ValidateScriptPointModes(
        OosNavigationPathQueueEntry point,
        bool entryApproach,
        bool? noBoost)
    {
        if (noBoost is not { } resolvedNoBoost)
            throw new NotSupportedException("UNKNOWN: move.gate noboost is required to validate FlightPoint modes.");
        if (point.Point.Radius != -1f ||
            point.Point.Boost != (!entryApproach && !resolvedNoBoost) ||
            point.Point.Travel != (entryApproach && !resolvedNoBoost))
            throw new NotSupportedException(
                "UNKNOWN: move.gate FlightPoint radius/boost/travel modes do not match the native script branch.");
    }

    private static void ValidatePendingScriptPointAtMerge(
        OosGateSegmentDriver driver,
        OosNavigationPathQueueEntry? point,
        bool entryApproach,
        bool? noBoost,
        X4RigidTransform? pointReference)
    {
        if (point is null) return;
        var pending = driver.Path.Pending;
        var index = -1;
        for (var i = 0; i < pending.Count; i++)
            if (ReferenceEquals(pending[i], point)) { index = i; break; }
        if (index < 0) return;
        if (noBoost is not { } resolvedNoBoost || pointReference is not { } reference)
            throw new NotSupportedException("UNKNOWN: script point merge lacks noboost or reference input.");

        OosLinearPose predecessor;
        if (index > 0)
        {
            predecessor = pending[index - 1].Point.Target;
        }
        else
        {
            var retained = driver.Path.Active
                .SkipWhile(x => x.Deadline > OosNavigationPathQueue.NativeDeadlineSentinelLimit)
                .ToArray();
            predecessor = retained.Length != 0
                ? retained[^1].Point.Target
                : driver.Banks[driver.ReadIndex].Pose;
        }
        var expected = OosGateScriptPointFactory.CreatePoint(
            point.Point.Target.Position, predecessor, entryApproach, resolvedNoBoost, reference);
        if (point.Deadline != expected.Deadline || point.Point.Radius != expected.Point.Radius ||
            point.Point.Boost != expected.Point.Boost || point.Point.Travel != expected.Point.Travel ||
            !Near(point.Point.Target, expected.Point.Target))
            throw new NotSupportedException(
                "UNKNOWN: script point heading changed between submission and its pending-merge worker.");
    }

    private static Vec3 Position(OosLinearPose pose) =>
        new(pose.Position.X, pose.Position.Y, pose.Position.Z);

    private static bool Near(Vec3 left, Vec3 right) => left.DistanceTo(right) <= PositionTolerance;

    private static bool Near(OosLinearPose left, OosLinearPose right) =>
        Near(Position(left), Position(right)) && Near(left.XAxis, right.XAxis) &&
        Near(left.YAxis, right.YAxis) && Near(left.ZAxis, right.ZAxis);

    private static bool Near(OosLinearVector4 left, OosLinearVector4 right) =>
        Math.Abs(left.X-right.X) <= PositionTolerance &&
        Math.Abs(left.Y-right.Y) <= PositionTolerance &&
        Math.Abs(left.Z-right.Z) <= PositionTolerance &&
        Math.Abs(left.W-right.W) <= PositionTolerance;

    private static bool Near(X4RigidTransform left, X4RigidTransform right) =>
        Near(OosTransportTripSimulator.Pose(left.Rotation, left.Position),
            OosTransportTripSimulator.Pose(right.Rotation, right.Position));

    private sealed record GateExecutionState(
        OosGateExitTargets? ExitTargets,
        double? ExitSafePointCompletionSeconds,
        double? ExitIntermediateScriptReturnSeconds,
        double? ExitScriptReturnSeconds);

    private sealed record ResolvedRoute(
        X4RigidTransform DepartureTransform,
        X4RigidTransform ArrivalTransform);
}
