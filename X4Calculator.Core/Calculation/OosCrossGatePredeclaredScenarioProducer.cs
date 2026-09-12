using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

/// <summary>
/// 周期相位均从源交接时钟起算。相同时刻采用刷新 → 旅行交付 → worker →
/// manager → 完成发布器 → AI；
/// 这是声明情景，不是从墙钟或事后事件时间猜出的顺序。
/// </summary>
public sealed record OosCrossGatePeriodicSchedule(
    double WorkerPeriodSeconds,
    double WorkerPhaseOffsetSeconds,
    double ManagerPeriodSeconds,
    double ManagerPhaseOffsetSeconds,
    double AiPeriodSeconds,
    double AiPhaseOffsetSeconds,
    double? RefreshPeriodSeconds,
    double? RefreshPhaseOffsetSeconds,
    double? TravelStartDeliveryPeriodSeconds,
    double? TravelStartDeliveryPhaseOffsetSeconds,
    double CompletionPublisherPeriodSeconds,
    double CompletionPublisherPhaseOffsetSeconds,
    int MaximumGeneratedPhases,
    bool RefreshTravelWorkerManagerPublisherAiAtEqualTime,
    bool? ScriptBeforePeriodicAtSameTime);

public sealed record OosCrossGateOrdinaryBranchConditions(
    bool? OrdinaryCopyEligible,
    bool? DirtyConsumerEligible,
    bool? RequireOrientation,
    bool? SuppressCompletion,
    bool? ManagerEligible,
    bool? WorkersCompleteBeforeManager,
    bool? ManagerAcceptsEntireVisibleFifo,
    bool? AllManagerContextLookupsSucceed,
    bool? OrdinaryContextWithoutOtherEffects,
    bool? ListenerEffectsAlreadyApplied,
    bool? EntryApproachingWaypointPublisherEligible,
    bool? SuppressEntryApproachingWaypoint,
    OosMoveActionCandidate? IntermediateConsumerCandidate,
    OosMoveActionCandidate? SafePointConsumerCandidate,
    OosEngineModeUpdateInputs? RefreshControls,
    bool? ParentRefreshMotionMatchesReadBank = null);

/// <summary>
/// stop_moving 前以当时船体 pose 计算 warp 目标所需的条件。当前闭合普通非 accelerator、
/// 有效 next position look-at，且约束/预约不改变候选值的分支。
/// </summary>
public sealed record OosCrossGateWarpGeometryConditions(
    bool? ExitGateIsAccelerator,
    X4RigidTransform ExitZoneInDestination,
    Vec3? DestinationSectorCorePosition,
    OosLinearVector4? NextPosition,
    X4RigidTransform? NextReferenceInDestination,
    bool? CompleteConstraintsDoNotChangeCandidate,
    bool? ReservationAndPreferredClearHaveNoEffect);

/// <summary>出口脚本的静态尺寸和条件；船体 pose 在 across 时从连续驱动器读取。</summary>
public sealed record OosCrossGateExitProductGeometry(
    float ShipLength,
    float ShipSafeSize,
    float ExitGateSize,
    X4RigidTransform ShipParentInDestinationSpace,
    bool? NextPositionInDestinationContext,
    OosGateSafePointConditions SafePointConditions,
    bool OrdinarySuccessfulSingleShip,
    X4RigidTransform PointReferenceInDestinationSpace);

/// <summary>
/// 由配置/存档几何和声明调度生成未来事件；DriverFactory 必须每次返回相同的全新连续初态。
/// FlightPoint 在实际脚本提交边界由 native type-2 点工厂生成；不会接受预先固定的 warp/出口 pose。
/// </summary>
public sealed record OosCrossGatePredeclaredProductInput(
    Func<OosGateSegmentDriver>? DriverFactory,
    OosCrossGateRouteContract? Route,
    OosCrossGateEntryGeometryInput? EntryGeometry,
    X4RigidTransform EntryPointReferenceInSourceSpace,
    OosCrossGateWarpGeometryConditions? WarpGeometry,
    OosCrossGateExitProductGeometry? ExitGeometry,
    bool? NoBoost,
    ulong? ScriptRandomState,
    double WarpDeliveryDelaySeconds,
    double ContextSubmitDelaySeconds,
    ulong DestinationSpace,
    OosCrossGatePeriodicSchedule? Scheduler,
    OosCrossGateOrdinaryBranchConditions? Branches,
    OosSourceDepartureHandoffResult? SourceDeparture,
    OosTransportDestinationScenario Destination,
    bool? PreservesContinuousControllerState,
    bool? NoUnmodeledMotionEffects);

public sealed record OosCrossGatePredeclaredProductResult(
    OosTransportPhaseStatus Status,
    OosCrossGateTripInput? Input,
    OosCrossGatePredeclaredScriptSchedule? ScriptSchedule,
    ulong? NextScriptRandomState,
    float? UnusedEntryRandomOffsetX,
    IReadOnlyList<string> UnknownReasons);

/// <summary>
/// 条件事前产品生成器。它在克隆的同态驱动器上按声明的周期相位在线推进；入口最后点进入
/// 原生 finish-on-approach 阈值并经发布器/AI 接受后停止，出口正常完成仍要求
/// 精确点物理弹出。随后返回另一份全新驱动器与生成的事件供正式执行。
/// </summary>
public static class OosCrossGatePredeclaredScenarioProducer
{
    private const double Epsilon = 1e-9;

    public static OosCrossGatePredeclaredProductResult Produce(
        OosCrossGatePredeclaredProductInput input,
        Action<OosGatePhase, OosGateSegmentDriver, OosGateWorkerResult>? observeWorker = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            var factory = input.DriverFactory ?? throw Unknown("a deterministic fresh-driver factory is required");
            var scheduler = ValidateScheduler(input.Scheduler);
            var branches = ValidateBranches(input.Branches);
            if (input.EntryGeometry is null || input.WarpGeometry is null || input.ExitGeometry is null)
                throw Unknown("entry, stop-time warp and exit product geometry are required");
            if (input.NoBoost is not { } noBoost)
                throw Unknown("the move.gate noboost branch is required");
            var route = RequireRoute(input.Route);
            if (input.ScriptRandomState is not { } randomState)
                throw Unknown("the predeclared VM RNG state is required");
            if (input.DestinationSpace == 0)
                throw Unknown("the destination navigation space is required");
            NonNegative(input.WarpDeliveryDelaySeconds, nameof(input.WarpDeliveryDelaySeconds));
            NonNegative(input.ContextSubmitDelaySeconds, nameof(input.ContextSubmitDelaySeconds));

            var probe = factory();
            if (probe.PhaseCount != 0 || probe.LastPhaseSequence != -1)
                throw Unknown("DriverFactory must return a fresh one-shot driver");
            var origin = probe.LastPhaseTime;
            if (!double.IsFinite(origin) || origin + Epsilon < probe.Flight.Now)
                throw Unknown("driver script clock must be finite and not precede its last worker state");
            var sequence = 0L;
            var events = new List<OosCrossGateTripEvent>();
            var clock = new PeriodicClock(origin, scheduler);
            var generated = 0;
            var exitActionsCreated = false;
            OosMoveActionHandoff? entryAction = null;
            OosMoveActionHandoff? intermediateAction = null;
            OosMoveActionHandoff? safeAction = null;
            OosNavigationPathQueueEntry? intermediatePoint = null;
            OosNavigationPathQueueEntry? safePoint = null;
            var intermediateReturned = false;
            var safeReturned = false;
            var entryReturned = false;
            var entryApproachTriggered = false;
            var entryCompletionDelivered = false;
            var intermediatePhysicallyCompleted = false;
            var safePhysicallyCompleted = false;
            var intermediateCompletionDelivered = false;
            var safeCompletionDelivered = false;
            double? intermediateReturnTime = null;
            double? safeReturnTime = null;

            var entryRandom = DrawEntry(randomState, input.EntryGeometry);
            randomState = entryRandom.NextState;
            var entryGeometry = input.EntryGeometry with
            {
                RandomOffsetY = entryRandom.RandomOffsetY,
                LateralSide = entryRandom.LateralSide
            };
            var entryPreparation = OosTransportGateEntryGeometry.PrepareCapitalEntry(
                entryGeometry.ShipInSector, entryGeometry.GateInSector,
                entryGeometry.ShipDistanceToGate, entryGeometry.ShipSize, entryGeometry.ShipSafeSize,
                entryGeometry.GateSize, entryGeometry.RandomOffsetY, entryGeometry.LateralSide,
                entryGeometry.SafePointConditions, entryGeometry.OrdinarySuccessfulSingleCapitalGate);
            OosNavigationPathQueueEntry? generatedApproach = null;
            if (entryPreparation.Target is { } entryTarget)
            {
                Add(new OosCrossGateAbortPathEvent(Phase(origin), ClearImmediately: true));
                probe.DeliverAbortPath(events[^1].Phase, clearImmediately: true);
                generatedApproach = OosGateScriptPointFactory.CreatePoint(
                    Vector(entryTarget.SafePosition), PointPredecessorAtNextMerge(probe, clock),
                    entryApproach: true, noBoost, input.EntryPointReferenceInSourceSpace);
            }

            if (generatedApproach is { } approach)
            {
                Add(new OosCrossGateDeliverEntryPointEvent(Phase(origin), entryGeometry, generatedApproach,
                    noBoost, input.EntryPointReferenceInSourceSpace));
                probe.DeliverPoints(events[^1].Phase, [approach]);
                entryAction = new(origin, null, false);
            }
            else
            {
                Add(new OosCrossGateDeliverEntryPointEvent(Phase(origin), entryGeometry, null,
                    noBoost, input.EntryPointReferenceInSourceSpace));
            }

            while (entryPreparation.RequiresApproach && !entryReturned)
                AdvanceOne(double.PositiveInfinity);

            var stopTime = entryPreparation.RequiresApproach
                ? entryAction!.Continued ? clock.LastTime : throw Unknown("entry action did not return")
                : origin;
            var warpPose = PrepareWarpPose(probe.Flight.Pose, route.DepartureTransform,
                route.ArrivalTransform, input.WarpGeometry);
            // zone835B80 的无方向 get_safe_pos 即使在完整未命中分支不改变候选值时，
            // 也恰好消耗两次 RNG 抽取。
            randomState = AdvanceRandom(randomState, 2);
            Add(new OosCrossGateStopMovingEvent(Phase(stopTime)));
            probe.DeliverStopMoving(events[^1].Phase);

            var waits = DrawWaits(randomState);
            randomState = waits.NextState;
            var warpSubmit = stopTime + waits.PreWarp + waits.WarpEffect;
            var warpDelivery = warpSubmit + input.WarpDeliveryDelaySeconds;
            var contextSubmit = warpDelivery + input.ContextSubmitDelaySeconds;
            var acrossEligible = warpSubmit + OosCrossGatePredeclaredScenario.PostWarpConnectionWaitSeconds +
                OosCrossGatePredeclaredScenario.PostWarpEffectWaitSeconds + waits.PostWarp;
            if (warpDelivery > acrossEligible + Epsilon || contextSubmit > acrossEligible + Epsilon)
                throw Unknown("declared warp/context delivery misses the scripted destination-context check");
            AdvanceBefore(warpSubmit);
            Add(new OosCrossGateWarpSubmittedEvent(Phase(warpSubmit)));
            AdvanceBefore(warpDelivery);
            Add(new OosCrossGateWarpEvent(Phase(warpDelivery), warpPose));
            probe.SubmitExplicitWarpPose(events[^1].Phase, warpPose);
            // EA1E80 独立于周期期限。父对象更改后，其取值方法可能读取即时的
            // Navigation 缓存而非运动 bank；ValidateBranches 要求将它们与此读 bank 相等
            // 作为显式输入。
            var parentRefresh = new OosCrossGateParameterRefreshEvent(Phase(warpDelivery),
                probe.ReadIndex, branches.RefreshControls, branches.ListenerEffectsAlreadyApplied);
            Add(parentRefresh);
            probe.ForceParameterRefresh(parentRefresh.Phase, parentRefresh.MotionBankIndex,
                parentRefresh.Controls, parentRefresh.ListenerEffectsAlreadyApplied);
            AdvanceBefore(contextSubmit);
            Add(new OosCrossGateContextEvent(Phase(contextSubmit), input.DestinationSpace,
                branches.OrdinaryContextWithoutOtherEffects));
            probe.SubmitContextChange(events[^1].Phase, input.DestinationSpace,
                branches.OrdinaryContextWithoutOtherEffects);
            AdvanceBefore(acrossEligible);
            if (probe.ContextSpace != input.DestinationSpace || probe.PendingOrderCount != 0)
                throw Unknown("declared manager phases did not accept warp/context FIFO before the scripted context check");
            var exitDraw = DrawExit(randomState);
            randomState = exitDraw.NextState;
            if (input.WarpGeometry.NextPosition is not { } exitNextPosition ||
                input.WarpGeometry.NextReferenceInDestination is not { } exitNextReference ||
                input.ExitGeometry.NextPositionInDestinationContext is not { } nextInContext)
                throw Unknown("exit next-position context and reference are required");
            var nextPosition = OosGateScriptPointFactory.EvaluateNextPositionDistances(
                probe.Banks[probe.ReadIndex].Pose, input.ExitGeometry.ShipParentInDestinationSpace,
                exitNextPosition, exitNextReference, nextInContext);
            var exitGeometry = new OosCrossGateExitGeometryInput(
                Transform(probe.Banks[probe.ReadIndex].Pose), route.ArrivalTransform,
                input.ExitGeometry.ShipLength, input.ExitGeometry.ShipSafeSize,
                input.ExitGeometry.ExitGateSize, exitDraw.Side,
                nextPosition, input.ExitGeometry.SafePointConditions,
                input.ExitGeometry.OrdinarySuccessfulSingleShip);
            var exitTargets = OosTransportGateGeometry.PrepareExit(
                exitGeometry.ShipInDestination, exitGeometry.ExitGateInDestination,
                exitGeometry.ShipLength, exitGeometry.ShipSafeSize, exitGeometry.ExitGateSize,
                exitGeometry.Side, exitGeometry.NextPosition, exitGeometry.SafePointConditions,
                exitGeometry.OrdinarySuccessfulSingleShip);
            intermediatePoint = OosGateScriptPointFactory.CreatePoint(
                Vector(exitTargets.IntermediatePosition), PointPredecessorAtNextMerge(probe, clock),
                entryApproach: false, noBoost, input.ExitGeometry.PointReferenceInDestinationSpace);
            Add(new OosCrossGateDeliverExitPointsEvent(Phase(acrossEligible), exitGeometry,
                intermediatePoint, noBoost, input.ExitGeometry.PointReferenceInDestinationSpace));
            probe.DeliverPoints(events[^1].Phase, [intermediatePoint]);
            intermediateAction = new(acrossEligible, acrossEligible, false);
            exitActionsCreated = true;

            while (!intermediateReturned)
                AdvanceOne(double.PositiveInfinity);

            var safePointSubmit = intermediateReturnTime!.Value;
            safePoint = OosGateScriptPointFactory.CreatePoint(
                Vector(exitTargets.SafePosition), PointPredecessorAtNextMerge(probe, clock),
                entryApproach: false, noBoost, input.ExitGeometry.PointReferenceInDestinationSpace);
            Add(new OosCrossGateDeliverExitSafePointEvent(Phase(safePointSubmit), safePoint,
                noBoost, input.ExitGeometry.PointReferenceInDestinationSpace));
            probe.DeliverPoints(events[^1].Phase, [safePoint]);
            safeAction = new(safePointSubmit, safePointSubmit + exitDraw.SafeTimer, false);
            var schedule = OosCrossGatePredeclaredScenario.CreateExplicitCapitalWarp(
                stopTime, waits.PreWarp, waits.WarpEffect, warpDelivery, contextSubmit,
                waits.PostWarp, acrossEligible, safePointSubmit, exitDraw.SafeTimer);

            while (!(intermediateReturned && safeReturned && safePhysicallyCompleted &&
                probe.Flight.Now + Epsilon >= Math.Max(intermediateReturnTime ?? 0, safeReturnTime ?? 0) &&
                ReadBankMatchesFlight(probe)))
                AdvanceOne(double.PositiveInfinity);

            var freshDriver = factory();
            var tripInput = new OosCrossGateTripInput(
                OosCrossGateScenarioBasis.PredeclaredScenario,
                input.Route,
                freshDriver,
                events,
                schedule,
                entryPreparation.RequiresApproach ? new(origin, null, false) : null,
                new(acrossEligible, schedule.IntermediateTimerDeadlineSeconds, false),
                new(safePointSubmit, schedule.SafePointTimerDeadlineSeconds, false),
                input.SourceDeparture,
                input.Destination,
                input.PreservesContinuousControllerState,
                input.NoUnmodeledMotionEffects);
            return new(OosTransportPhaseStatus.Conditional, tripInput, schedule, randomState,
                entryRandom.UnusedRandomOffsetX, []);

            void AdvanceBefore(double cutoff)
            {
                while (clock.NextTime < cutoff - Epsilon)
                    AdvanceOne(cutoff);
            }

            void AdvanceOne(double cutoff)
            {
                if (++generated > scheduler.MaximumGeneratedPhases)
                {
                    var position = probe.Flight.Pose.Position;
                    throw Unknown($"declared periodic schedule reached its phase bound before physical completion " +
                        $"(now={probe.Flight.Now:R}, pos=({position.X:R},{position.Y:R},{position.Z:R}), " +
                        $"active={probe.Path.Active.Count}, pending={probe.Path.Pending.Count}, " +
                        $"dirty={probe.IsDirty}, read={probe.ReadIndex}, write={probe.WriteIndex}, " +
                        $"entryReturned={entryReturned}, intermediateReturned={intermediateReturned}, " +
                        $"safeReturned={safeReturned}, safePhysical={safePhysicallyCompleted})");
                }
                var next = clock.Next();
                if (next.Time > cutoff + Epsilon)
                    throw new InvalidOperationException("Periodic phase advanced past a script boundary.");
                switch (next.Kind)
                {
                    case PeriodicKind.Refresh:
                    {
                        var e = new OosCrossGateParameterRefreshEvent(Phase(next.Time), probe.ReadIndex,
                            branches.RefreshControls, branches.ListenerEffectsAlreadyApplied);
                        Add(e);
                        probe.ForceParameterRefresh(e.Phase, e.MotionBankIndex, e.Controls,
                            e.ListenerEffectsAlreadyApplied);
                        break;
                    }
                    case PeriodicKind.Worker:
                    {
                        var head = probe.Path.Active.Count != 0 ? probe.Path.Active[0] :
                            probe.Path.Pending.Count != 0 ? probe.Path.Pending[0] : null;
                        var workerDelta = next.Time - probe.Flight.Now;
                        if (!double.IsFinite(workerDelta) || workerDelta < 0)
                            throw Unknown("declared worker phase precedes the continuous worker clock");
                        var worker = new OosCrossGateWorkerEvent(Phase(next.Time), workerDelta,
                            branches.OrdinaryCopyEligible, branches.DirtyConsumerEligible,
                            branches.RequireOrientation, branches.SuppressCompletion,
                            branches.EntryApproachingWaypointPublisherEligible,
                            branches.SuppressEntryApproachingWaypoint);
                        Add(worker);
                        var result = probe.StepWorker(worker.Phase, worker.DeltaSeconds,
                            worker.OrdinaryCopyEligible, worker.DirtyConsumerEligible,
                            worker.RequireOrientation, worker.SuppressCompletion);
                        // 研究诊断观察的是同一个连续 worker；回调不得修改驱动器、消耗 RNG
                        // 或交付额外相位。
                        observeWorker?.Invoke(worker.Phase, probe, result);
                        if (!entryApproachTriggered && head is not null &&
                            ReferenceEquals(head, generatedApproach) &&
                            OosTransportArrivalGeometry.IsLastWaypointApproached(
                                result.Linear.U, probe.Flight.Length, generatedApproach!.Point.Radius,
                                probe.Flight.Parameters.Proximity, result.Queue.RemainingPointCount,
                                branches.EntryApproachingWaypointPublisherEligible == true,
                                suppressApproach: branches.SuppressEntryApproachingWaypoint == true))
                            entryApproachTriggered = true;
                        if (result.Completion?.Popped == true && exitActionsCreated && head is not null &&
                            ReferenceEquals(head, intermediatePoint))
                            intermediatePhysicallyCompleted = true;
                        if (result.Completion?.Popped == true && exitActionsCreated && head is not null &&
                            ReferenceEquals(head, safePoint))
                            safePhysicallyCompleted = true;
                        // E88160 在 Linear 内同步弹出已完成的头。后续模式消费者读取的是
                        // 新的头，而不是 Linear 前的队列快照。
                        DeliverModeRequests(next.Time,
                            result.Completion?.HeadBoost ?? result.Queue.HeadBoost,
                            result.Completion?.HeadTravel ?? result.Queue.HeadTravel);
                        break;
                    }
                    case PeriodicKind.Manager:
                    {
                        var count = branches.ManagerAcceptsEntireVisibleFifo == true
                            ? probe.PendingOrderCount
                            : throw Unknown("manager FIFO prefix policy is unresolved");
                        var boundary = new OosGateManagerBoundary(branches.ManagerEligible,
                            branches.WorkersCompleteBeforeManager, count,
                            Enumerable.Repeat(branches.AllManagerContextLookupsSucceed == true, count).ToArray(),
                            (float)scheduler.ManagerPeriodSeconds);
                        var manager = new OosCrossGateManagerEvent(Phase(next.Time), boundary);
                        Add(manager);
                        probe.FinishManager(manager.Phase, manager.Boundary);
                        break;
                    }
                    case PeriodicKind.TravelStartDelivery:
                    {
                        if (probe.Dynamics.TravelQueue.Deadline is not { } deadline || deadline > next.Time)
                            break;
                        var travel = new OosCrossGateTravelStartEvent(Phase(next.Time), probe.ReadIndex);
                        Add(travel);
                        probe.DeliverTravelStart(travel.Phase, travel.MotionBankIndex);
                        break;
                    }
                    case PeriodicKind.CompletionPublisher:
                        if (entryApproachTriggered && !entryCompletionDelivered)
                        {
                            var completion = new OosCrossGateDeliverMoveCompletionEvent(
                                Phase(next.Time), OosCrossGateMoveActionRole.EntryApproach);
                            Add(completion);
                            entryAction!.DeliverNormalCompletion(completion.Phase.Sequence, completion.Phase.Now);
                            entryCompletionDelivered = true;
                        }
                        if (!exitActionsCreated) break;
                        if (intermediatePhysicallyCompleted && !intermediateCompletionDelivered &&
                            intermediateAction!.Continued == false)
                        {
                            var completion = new OosCrossGateDeliverMoveCompletionEvent(
                                Phase(next.Time), OosCrossGateMoveActionRole.ExitIntermediate);
                            Add(completion);
                            intermediateAction!.DeliverNormalCompletion(completion.Phase.Sequence, completion.Phase.Now);
                            intermediateCompletionDelivered = true;
                        }
                        if (safePhysicallyCompleted && !safeCompletionDelivered && safeAction!.Continued == false)
                        {
                            var completion = new OosCrossGateDeliverMoveCompletionEvent(
                                Phase(next.Time), OosCrossGateMoveActionRole.ExitSafePoint);
                            Add(completion);
                            safeAction!.DeliverNormalCompletion(completion.Phase.Sequence, completion.Phase.Now);
                            safeCompletionDelivered = true;
                        }
                        break;
                    case PeriodicKind.Ai:
                        if (entryAction is not null && !entryReturned && entryAction.NormalCompletionPending)
                        {
                            var accept = new OosCrossGateAcceptMoveActionEvent(Phase(next.Time),
                                OosCrossGateMoveActionRole.EntryApproach,
                                OosMoveActionCandidate.NormalCompletion, true);
                            Add(accept);
                            entryReturned = entryAction.Accept(accept.Phase.Sequence, accept.Phase.Now,
                                accept.Candidate, accept.IsDirectLeaf) == OosMoveActionAcceptance.Continued;
                        }
                        if (!exitActionsCreated) break;
                        if (!intermediateReturned)
                        {
                            var candidate = branches.IntermediateConsumerCandidate!.Value;
                            if (candidate == OosMoveActionCandidate.NormalCompletion &&
                                !intermediateAction!.NormalCompletionPending) break;
                            if (candidate == OosMoveActionCandidate.Timer &&
                                intermediateAction!.TimerDeadline > next.Time) break;
                            var accept = new OosCrossGateAcceptMoveActionEvent(Phase(next.Time),
                                OosCrossGateMoveActionRole.ExitIntermediate, candidate, true);
                            Add(accept);
                            intermediateReturned = intermediateAction!.Accept(accept.Phase.Sequence,
                                accept.Phase.Now, accept.Candidate, accept.IsDirectLeaf) ==
                                OosMoveActionAcceptance.Continued;
                            if (intermediateReturned) intermediateReturnTime = next.Time;
                        }
                        if (safeAction is not null && !safeReturned)
                        {
                            var candidate = branches.SafePointConsumerCandidate!.Value;
                            if (candidate == OosMoveActionCandidate.NormalCompletion &&
                                !safeAction!.NormalCompletionPending) break;
                            if (candidate == OosMoveActionCandidate.Timer &&
                                safeAction.TimerDeadline > next.Time) break;
                            var accept = new OosCrossGateAcceptMoveActionEvent(Phase(next.Time),
                                OosCrossGateMoveActionRole.ExitSafePoint, candidate, true);
                            Add(accept);
                            safeReturned = safeAction.Accept(accept.Phase.Sequence, accept.Phase.Now,
                                accept.Candidate, accept.IsDirectLeaf) == OosMoveActionAcceptance.Continued;
                            if (safeReturned) safeReturnTime = next.Time;
                        }
                        break;
                }
            }

            void DeliverModeRequests(double now, bool boost, bool travel)
            {
                var decision = probe.Dynamics.Gate.Evaluate(now, probe.Flight.OrientationError, boost, travel);
                foreach (var request in decision.Requests)
                {
                    var e = new OosCrossGateModeRequestEvent(Phase(now), probe.ReadIndex, request);
                    Add(e);
                    probe.DeliverModeRequest(e.Phase, e.MotionBankIndex, e.Request);
                }
            }

            OosGatePhase Phase(double now) => new(++sequence, now);
            void Add(OosCrossGateTripEvent e) => events.Add(e);
        }
        catch (NotSupportedException ex)
        {
            return new(OosTransportPhaseStatus.Unknown, null, null, null, null, [ex.Message]);
        }
    }

    private static RouteTransforms RequireRoute(OosCrossGateRouteContract? route)
    {
        if (route?.DepartureGate is not { } departure || route.ArrivalGate is not { } arrival ||
            departure.SaveGateSectorTransform is not { } departureTransform ||
            arrival.SaveGateSectorTransform is not { } arrivalTransform ||
            !departure.FoundInSave || !arrival.FoundInSave ||
            route.DepartureGateBelongsToSourceSector != true ||
            route.ArrivalGateBelongsToTargetSector != true ||
            string.IsNullOrWhiteSpace(departure.TargetGateId) ||
            !string.Equals(departure.TargetGateId, arrival.Id, StringComparison.OrdinalIgnoreCase))
            throw Unknown("both route gates need save-confirmed transforms, sector ownership and an explicit target link");
        return new(departureTransform, arrivalTransform);
    }

    private static OosLinearPose PrepareWarpPose(
        OosLinearPose stopPose,
        X4RigidTransform entryGate,
        X4RigidTransform exitGate,
        OosCrossGateWarpGeometryConditions conditions)
    {
        if (conditions.ExitGateIsAccelerator != false)
            throw Unknown("the accelerator-gate warp target branch is unresolved");
        if (conditions.DestinationSectorCorePosition is not { } sectorCore)
            throw Unknown("destination sector core position is required to choose the exit side");
        if (conditions.NextPosition is not { } nextPosition ||
            conditions.NextReferenceInDestination is not { } nextReference ||
            conditions.CompleteConstraintsDoNotChangeCandidate is null ||
            conditions.ReservationAndPreferredClearHaveNoEffect is null)
            throw Unknown("warp next-position and safe-position branch inputs are required");
        return OosGateScriptPointFactory.PrepareUnobstructedWarp(
            stopPose, entryGate, exitGate, conditions.ExitZoneInDestination,
            Vector(sectorCore), nextPosition, nextReference,
            conditions.CompleteConstraintsDoNotChangeCandidate.Value,
            conditions.ReservationAndPreferredClearHaveNoEffect.Value);
    }

    private static OosLinearPose PointPredecessorAtNextMerge(
        OosGateSegmentDriver driver,
        PeriodicClock clock)
    {
        if (driver.Path.Pending.Count != 0)
            return driver.Path.Pending[^1].Point.Target;
        // 下一个 worker 会在合并这个新 pending 点前，移除连续已标记的 active 前缀。
        // finish-on-approach 入口通常仍保持活动状态，但会被 Stop 标记。
        var retained = driver.Path.Active
            .SkipWhile(x => x.Deadline > OosNavigationPathQueue.NativeDeadlineSentinelLimit)
            .ToArray();
        if (retained.Length != 0)
            return retained[^1].Point.Target;
        return driver.Banks[clock.ReadIndexAtNextWorker(driver.ReadIndex)].Pose;
    }

    private static X4RigidTransform Transform(OosLinearPose pose) => new(
        new X4RotationMatrix(
            new(pose.XAxis.X, pose.YAxis.X, pose.ZAxis.X),
            new(pose.XAxis.Y, pose.YAxis.Y, pose.ZAxis.Y),
            new(pose.XAxis.Z, pose.YAxis.Z, pose.ZAxis.Z)),
        new(pose.Position.X, pose.Position.Y, pose.Position.Z));

    private static OosLinearVector4 Vector(Vec3 value) => new(
        (float)value.X, (float)value.Y, (float)value.Z);

    private static EntryDraw DrawEntry(ulong state, OosCrossGateEntryGeometryInput geometry)
    {
        var threshold = 10_000f + geometry.ShipSize / 2f;
        if (geometry.ShipDistanceToGate <= threshold)
            return new(state, null, float.NaN, OosGateEntryLateralSide.Positive);
        var max = MathF.Min(geometry.ShipSize * 2,
            (MathF.Max(geometry.GateSize, geometry.ShipSize) - geometry.ShipSize) / 2);
        var unused = X4NativeRandom.DrawFloat(state, max * 2);
        var y = X4NativeRandom.DrawFloat(unused.NextState, max * 2);
        var side = X4NativeRandom.DrawIndex(y.NextState, 2);
        return new(side.NextState, unused.Value - max, y.Value - max,
            side.Index == 0 ? OosGateEntryLateralSide.Negative : OosGateEntryLateralSide.Positive);
    }

    private static WaitDraw DrawWaits(ulong state)
    {
        var a = X4NativeRandom.DrawFloat(state, 1);
        var b = X4NativeRandom.DrawFloat(a.NextState, .5f);
        var c = X4NativeRandom.DrawFloat(b.NextState, .5f);
        return new(c.NextState, 6 + a.Value, 1 + b.Value, 1.5 + c.Value);
    }

    private static ulong AdvanceRandom(ulong state, int count)
    {
        for (var i = 0; i < count; i++)
            state = X4NativeRandom.DrawFloat(state, 1).NextState;
        return state;
    }

    private static ExitDraw DrawExit(ulong state)
    {
        var side = X4NativeRandom.DrawIndex(state, 4);
        var timer = X4NativeRandom.DrawFloat(side.NextState, 6);
        return new(timer.NextState, (OosGateExitSide)side.Index, 11 + timer.Value);
    }

    private static bool PointNoLongerQueued(OosGateSegmentDriver driver, OosNavigationPathQueueEntry point) =>
        !driver.Path.Active.Contains(point) && !driver.Path.Pending.Contains(point);

    private static bool ReadBankMatchesFlight(OosGateSegmentDriver driver)
    {
        var bank = driver.Banks[driver.ReadIndex];
        return Near(bank.Pose.Position, driver.Flight.Pose.Position) &&
            Near(bank.Pose.XAxis, driver.Flight.Pose.XAxis) &&
            Near(bank.Pose.YAxis, driver.Flight.Pose.YAxis) &&
            Near(bank.Pose.ZAxis, driver.Flight.Pose.ZAxis) &&
            Near(bank.Velocity, driver.Flight.Velocity);
    }

    private static bool Near(OosLinearVector4 left, OosLinearVector4 right) =>
        Math.Abs(left.X-right.X) <= .001f && Math.Abs(left.Y-right.Y) <= .001f &&
        Math.Abs(left.Z-right.Z) <= .001f && Math.Abs(left.W-right.W) <= .001f;

    private static OosCrossGatePeriodicSchedule ValidateScheduler(OosCrossGatePeriodicSchedule? value)
    {
        var result = value ?? throw Unknown("the periodic worker/manager/AI scheduler is required");
        Positive(result.WorkerPeriodSeconds, nameof(result.WorkerPeriodSeconds));
        Positive(result.ManagerPeriodSeconds, nameof(result.ManagerPeriodSeconds));
        Positive(result.AiPeriodSeconds, nameof(result.AiPeriodSeconds));
        NonNegative(result.WorkerPhaseOffsetSeconds, nameof(result.WorkerPhaseOffsetSeconds));
        NonNegative(result.ManagerPhaseOffsetSeconds, nameof(result.ManagerPhaseOffsetSeconds));
        NonNegative(result.AiPhaseOffsetSeconds, nameof(result.AiPhaseOffsetSeconds));
        if ((result.RefreshPeriodSeconds is null) != (result.RefreshPhaseOffsetSeconds is null))
            throw Unknown("refresh period and phase offset must be declared together");
        if (result.RefreshPeriodSeconds is { } refresh) Positive(refresh, nameof(result.RefreshPeriodSeconds));
        if (result.RefreshPhaseOffsetSeconds is { } phase) NonNegative(phase, nameof(result.RefreshPhaseOffsetSeconds));
        if ((result.TravelStartDeliveryPeriodSeconds is null) !=
            (result.TravelStartDeliveryPhaseOffsetSeconds is null))
            throw Unknown("travel-start delivery period and phase offset must be declared together");
        if (result.TravelStartDeliveryPeriodSeconds is { } travelPeriod)
            Positive(travelPeriod, nameof(result.TravelStartDeliveryPeriodSeconds));
        if (result.TravelStartDeliveryPhaseOffsetSeconds is { } travelPhase)
            NonNegative(travelPhase, nameof(result.TravelStartDeliveryPhaseOffsetSeconds));
        Positive(result.CompletionPublisherPeriodSeconds, nameof(result.CompletionPublisherPeriodSeconds));
        NonNegative(result.CompletionPublisherPhaseOffsetSeconds,
            nameof(result.CompletionPublisherPhaseOffsetSeconds));
        if (result.MaximumGeneratedPhases <= 0 || !result.RefreshTravelWorkerManagerPublisherAiAtEqualTime)
            throw Unknown("phase bound and same-time refresh/worker/manager/AI order are required");
        if (result.ScriptBeforePeriodicAtSameTime != true)
            throw Unknown("script-versus-periodic same-time ordering must declare the supported script-first condition");
        return result;
    }

    private static OosCrossGateOrdinaryBranchConditions ValidateBranches(
        OosCrossGateOrdinaryBranchConditions? value)
    {
        var result = value ?? throw Unknown("ordinary worker/manager/action branch conditions are required");
        if (result.OrdinaryCopyEligible != true || result.DirtyConsumerEligible != true ||
            result.RequireOrientation is null || result.SuppressCompletion is null ||
            result.ManagerEligible != true || result.WorkersCompleteBeforeManager != true ||
            result.ManagerAcceptsEntireVisibleFifo != true || result.AllManagerContextLookupsSucceed != true ||
            result.OrdinaryContextWithoutOtherEffects != true ||
            result.EntryApproachingWaypointPublisherEligible != true ||
            result.SuppressEntryApproachingWaypoint != false ||
            result.IntermediateConsumerCandidate is null ||
            result.SafePointConsumerCandidate is null)
            throw Unknown("ordinary worker, manager, context and completion branches are incomplete");
        if (result.ListenerEffectsAlreadyApplied != true || result.RefreshControls is null)
            throw Unknown("warp parent-change refresh needs resolved listener effects and engine controls, even without periodic refresh");
        if (result.ParentRefreshMotionMatchesReadBank != true)
            throw Unknown("warp parent-change refresh motion getters must explicitly match the selected read bank; immediate Navigation caches are otherwise unresolved");
        return result;
    }

    private static void Positive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(name);
    }

    private static void NonNegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(name);
    }

    private static NotSupportedException Unknown(string reason) => new($"UNKNOWN: {reason}.");

    private enum PeriodicKind { Refresh, TravelStartDelivery, Worker, Manager, CompletionPublisher, Ai }

    private sealed class PeriodicClock
    {
        private readonly OosCrossGatePeriodicSchedule _schedule;
        private readonly double[] _next;
        public double NextTime => _next.Min();
        public double LastTime { get; private set; }

        public PeriodicClock(double origin, OosCrossGatePeriodicSchedule schedule)
        {
            _schedule = schedule;
            _next =
            [
                schedule.RefreshPhaseOffsetSeconds is { } r ? origin + r : double.PositiveInfinity,
                schedule.TravelStartDeliveryPhaseOffsetSeconds is { } t ? origin + t : double.PositiveInfinity,
                origin + schedule.WorkerPhaseOffsetSeconds,
                origin + schedule.ManagerPhaseOffsetSeconds,
                origin + schedule.CompletionPublisherPhaseOffsetSeconds,
                origin + schedule.AiPhaseOffsetSeconds
            ];
            for (var i = 0; i < _next.Length; i++)
            {
                var period = Period(i);
                while (_next[i] <= origin + Epsilon) _next[i] += period;
            }
        }

        public (PeriodicKind Kind, double Time) Next()
        {
            var index = 0;
            for (var i = 1; i < _next.Length; i++)
                if (_next[i] < _next[index] - Epsilon) index = i;
            var time = _next[index];
            _next[index] += Period(index);
            LastTime = time;
            return ((PeriodicKind)index, time);
        }

        public int ReadIndexAtNextWorker(int currentReadIndex)
        {
            var worker = _next[(int)PeriodicKind.Worker];
            var manager = _next[(int)PeriodicKind.Manager];
            var swaps = 0;
            // 在声明的同一时刻顺序中，worker 先于 manager。脚本点提交时没有待处理的 gate FIFO，
            // 因此介入的 manager 只执行其基于 dt 的交换。
            while (manager < worker - Epsilon)
            {
                if (MathF.Abs((float)_schedule.ManagerPeriodSeconds) >= .0001f)
                    swaps++;
                if (swaps > _schedule.MaximumGeneratedPhases)
                    throw Unknown("manager phases before the next point merge exceed the declared phase bound");
                manager += _schedule.ManagerPeriodSeconds;
            }
            return (currentReadIndex + swaps) & 1;
        }

        private double Period(int index) => index switch
        {
            0 => _schedule.RefreshPeriodSeconds ?? double.PositiveInfinity,
            1 => _schedule.TravelStartDeliveryPeriodSeconds ?? double.PositiveInfinity,
            2 => _schedule.WorkerPeriodSeconds,
            3 => _schedule.ManagerPeriodSeconds,
            4 => _schedule.CompletionPublisherPeriodSeconds,
            5 => _schedule.AiPeriodSeconds,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    private sealed record EntryDraw(
        ulong NextState,
        float? UnusedRandomOffsetX,
        float RandomOffsetY,
        OosGateEntryLateralSide LateralSide);

    private sealed record WaitDraw(
        ulong NextState,
        double PreWarp,
        double WarpEffect,
        double PostWarp);

    private sealed record ExitDraw(
        ulong NextState,
        OosGateExitSide Side,
        double SafeTimer);

    private sealed record RouteTransforms(
        X4RigidTransform DepartureTransform,
        X4RigidTransform ArrivalTransform);
}
