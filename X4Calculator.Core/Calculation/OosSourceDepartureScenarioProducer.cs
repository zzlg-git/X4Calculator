using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

/// <summary>
/// worker/manager/completion-publisher/AI/refresh/travel-delivery 从源泊位 execute_trade complete
/// （elapsed=0）起算；
/// trade-complete/carrier/network consumer 的 phase offset 从本次 unloading network start 起算，从而允许
/// CargoDestination 的最终返航在 trade complete 前被消费。相同时刻严格按
/// 以下顺序执行：refresh → travel-delivery → worker → manager → trade-complete → carrier → network →
/// completion-publisher → AI；clearance 脚本提交与这些周期阶段同刻时，脚本提交先行。
/// 这是调用方声明的事前情景。
/// </summary>
public sealed record OosSourceDeparturePeriodicSchedule(
    double WorkerPeriodSeconds,
    double WorkerPhaseOffsetSeconds,
    double TravelStartDeliveryPeriodSeconds,
    double TravelStartDeliveryPhaseOffsetSeconds,
    double ManagerPeriodSeconds,
    double ManagerPhaseOffsetSeconds,
    double AiPeriodSeconds,
    double AiPhaseOffsetSeconds,
    double TradeCompleteConsumerPeriodSeconds,
    double TradeCompleteConsumerPhaseOffsetSeconds,
    double CarrierConsumerPeriodSeconds,
    double CarrierConsumerPhaseOffsetSeconds,
    double NetworkConsumerPeriodSeconds,
    double NetworkConsumerPhaseOffsetSeconds,
    double CompletionPublisherPeriodSeconds,
    double CompletionPublisherPhaseOffsetSeconds,
    double? RefreshPeriodSeconds,
    double? RefreshPhaseOffsetSeconds,
    int MaximumGeneratedPhases,
    bool RefreshTravelWorkerManagerTradeCompleteCarrierNetworkPublisherAiAtEqualTime);

/// <summary>CalculateClearanceMovement 所需的静态泊位/安全点输入。</summary>
public sealed record OosSourceDepartureClearanceGeometry(
    Vec3 ShipPosition,
    Vec3 ShipAxisX,
    Vec3 ShipAxisY,
    Vec3 ShipAxisZ,
    Vec3 OldDockPosition,
    Vec3 SafePosition,
    OosLinearPose? NativeClearanceTargetPose,
    double ShipSize,
    string? NextOrder,
    string? DefaultOrder,
    bool ExitPathPresent,
    bool InHighway);

/// <summary>
/// 无法从存档恢复的分支、缓存和调度责任。true 表示调用方选择一个条件情景，不表示已经观察到未来事件。
/// </summary>
public sealed record OosSourceDepartureScenarioConditions(
    bool? InitialDockedStationaryStateAndCompleteCachesDeclared,
    bool? OrdinaryCopyEligible,
    bool? DirtyConsumerEligible,
    bool? ManagerEligible,
    bool? WorkersCompleteBeforeManager,
    bool? ManagerAcceptsEntireVisibleFifo,
    bool? AllManagerContextLookupsSucceed,
    bool? ListenerEffectsAlreadyApplied,
    OosEngineModeUpdateInputs? RefreshControls,
    bool? SelectedCarrierHolderIsDepartingShip,
    bool? ReceiverUsesProvenShipReturnHandler,
    bool? EventDestinationMatchesReceiver,
    bool? ReturnEventsAreExactTransactionSet,
    bool? EarlierReturnEventsAcceptedByTradeComplete,
    bool? TradeCompleteAcceptedAtFirstEligibleConsumerPhase,
    bool? LastReturnEventsAcceptedAtFirstEligibleConsumerPhase,
    bool? NetworkRemovedSynchronouslyAfterFinalReturnAcceptance,
    bool? NetworkNotificationAcceptedAtFirstEligibleConsumerPhase,
    bool? DepartureWaitDrawsAreConsecutiveInXmlOrder,
    bool? ClearanceSubmissionPrecedesPeriodicWorkAtEqualTime,
    bool? CompletionPublishedAtFirstEligiblePublisherPhase,
    OosMoveActionCandidate? ClearanceConsumerCandidate,
    bool? ClearanceActionIsDirectLeaf,
    bool? ClearancePointUsesProvenNativeUndockConstruction,
    bool? NewRouteActionGenerationEstablished,
    bool? OrdinaryLinearStateRemainsContinuous,
    bool? NoUnmodeledDepartureMotionEffects);

public sealed record OosSourceDepartureScenarioInput(
    Func<OosGateSegmentDriver>? InitialDriverFactory,
    OosTransportHomogeneousCarrierWaveTradeResult? ShipProvidedUnloading,
    OosTransportStationProvidedSingleWaveLoadingResult? StationProvidedLoading,
    OosTransportDockingSourceProfile? SourceProfile,
    OosSourceDepartureClearanceGeometry? ClearanceGeometry,
    OosNavigationPathQueueEntry? ClearancePoint,
    ulong? ScriptRandomState,
    OosSourceDeparturePeriodicSchedule? Scheduler,
    OosSourceDepartureScenarioConditions? Conditions,
    int OutputDriverMaximumPhases);

public sealed record OosSourceDepartureRandomWaits(
    double TradePostWaitSeconds,
    double UndockPreWaitSeconds,
    double TakeoffProfileSeconds,
    double UndockPostWaitSeconds);

/// <summary>事前生成的相位轨迹。Kind 是稳定的产品事件名，不表示运行时探针观察。</summary>
public sealed record OosSourceDepartureScenarioPhase(long Sequence, double Now, string Kind, string Detail);

public sealed record OosSourceDepartureScenarioResult(
    OosTransportPhaseStatus Status,
    OosSourceDepartureHandoffResult? Handoff,
    OosShipCarrierFinalReturnSchedule? FinalReturnSchedule,
    double? ActualTradeCompleteFromNetworkStartSeconds,
    long? ExpectedShipReturnEventCount,
    long? LastWaveShipReturnEventCount,
    OosTransportPhaseDuration RecoveryFromTradeComplete,
    OosTransportPhaseDuration MatchingNetworkWaitFromTradeComplete,
    OosTransportDepartureWaitResult? DepartureWaitContract,
    OosTransportPhaseDuration ExactDepartureWait,
    OosTransportClearanceMovementResult? ClearanceMovement,
    OosMoveActionCandidate? ClearanceWinner,
    OosSourceDepartureRandomWaits? RandomWaits,
    ulong? NextScriptRandomState,
    OosGateSegmentDriver? PreviewDriver,
    Func<OosGateSegmentDriver>? DriverFactory,
    IReadOnlyList<OosSourceDepartureScenarioPhase> Phases,
    IReadOnlyList<string> UnknownReasons);

/// <summary>
/// 从源泊位装货完成边界生成可重复的 source departure 条件情景。它只消费静态几何、native RNG 初态、
/// 完整 driver 初态和显式周期/仲裁，不读取未来 native pose，也不调用只接 observation/replay 的
/// ResolvePrimaryShipDispatchRecovery 或 ResolveDefaultTradeRoutineClearance。
/// </summary>
public static class OosSourceDepartureScenarioProducer
{
    private const double Epsilon = 1e-9;

    public static OosSourceDepartureScenarioResult Produce(OosSourceDepartureScenarioInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        try
        {
            var preview = Run(input);
            OosGateSegmentDriver Factory() => Run(input).Driver;
            return new(
                OosTransportPhaseStatus.Conditional,
                preview.Handoff,
                preview.FinalReturnSchedule,
                preview.ActualTradeCompleteFromNetworkStartSeconds,
                preview.ExpectedShipReturnEventCount,
                preview.LastWaveShipReturnEventCount,
                preview.Recovery,
                preview.MatchingNetworkWait,
                preview.DepartureWaitContract,
                preview.ExactDepartureWait,
                preview.ClearanceMovement,
                preview.ClearanceWinner,
                preview.RandomWaits,
                preview.NextScriptRandomState,
                preview.Driver,
                Factory,
                preview.Phases,
                []);
        }
        catch (NotSupportedException ex)
        {
            var reason = ex.Message;
            return new(
                OosTransportPhaseStatus.Unknown,
                null,
                null,
                null,
                null,
                null,
                OosTransportPhaseDuration.Unknown(reason),
                OosTransportPhaseDuration.Unknown(reason),
                null,
                OosTransportPhaseDuration.Unknown(reason),
                null,
                null,
                null,
                null,
                null,
                null,
                [],
                [reason]);
        }
    }

    private static RunResult Run(OosSourceDepartureScenarioInput input)
    {
        var factory = input.InitialDriverFactory ?? throw Unknown("a deterministic fresh initial-driver factory is required");
        var sourceProfile = input.SourceProfile ?? throw Unknown("the XML/source takeoff profile is required");
        var geometry = input.ClearanceGeometry ?? throw Unknown("source clearance geometry is required");
        var clearancePoint = input.ClearancePoint;
        var scheduler = ValidateScheduler(input.Scheduler);
        var conditions = ValidateConditions(input.Conditions, scheduler);
        if (input.ScriptRandomState is not { } randomState)
            throw Unknown("the predeclared VM RNG state is required; an unavailable TLS seed cannot be reconstructed");
        if (input.OutputDriverMaximumPhases <= 0)
            throw new ArgumentOutOfRangeException(nameof(input.OutputDriverMaximumPhases));

        var driver = factory();
        ValidateInitialDriver(driver, geometry, conditions);
        var sequence = 0L;
        var generated = 0;
        var phases = new List<OosSourceDepartureScenarioPhase>();

        if ((input.ShipProvidedUnloading is null) == (input.StationProvidedLoading is null))
            throw Unknown("exactly one ship-provided unloading or station-provided loading contract is required");

        OosShipCarrierFinalReturnSchedule? finalReturn = null;
        long expectedReturns = 0;
        long lastReturnCount = 0;
        double idealTradeCompleteFromNetworkStart;
        double? returnScheduledFromNetworkStart = null;
        double? stationRemainingRecoverySeconds = null;
        if (input.ShipProvidedUnloading is { } trade)
        {
            if (conditions.SelectedCarrierHolderIsDepartingShip != true ||
                conditions.ReceiverUsesProvenShipReturnHandler != true ||
                conditions.EventDestinationMatchesReceiver != true ||
                conditions.ReturnEventsAreExactTransactionSet != true ||
                conditions.LastReturnEventsAcceptedAtFirstEligibleConsumerPhase != true ||
                conditions.NetworkRemovedSynchronouslyAfterFinalReturnAcceptance != true)
            {
                throw Unknown("ship-provided unloading requires the proven Ship receiver, exact transaction set, earlier-return boundary, last-return consumer and synchronous removal conditions");
            }
            finalReturn = OosSourceDepartureHandoffCalculator.CalculateFinalReturnSchedule(
                trade, carriersBelongToDepartingShip: true);
            if (finalReturn.Status != OosTransportPhaseStatus.Conditional ||
                finalReturn.TradeCompleteFromNetworkStartSeconds is not { } idealTradeComplete ||
                finalReturn.ReturnUnitEventScheduledFromNetworkStartSeconds is not { } returnScheduled ||
                trade.RequiredCarrierQuotas is not { } required ||
                trade.AvailableCarrierCount is not { } available ||
                trade.WaveCount is not { } waves || required <= 0 || available <= 0 || waves <= 0)
            {
                throw Unknown("an exact homogeneous departing-holder final return schedule is required");
            }
            idealTradeCompleteFromNetworkStart = idealTradeComplete;
            returnScheduledFromNetworkStart = returnScheduled;
            expectedReturns = Math.Min(required, available);
            lastReturnCount = required - checked((waves - 1) * available);
            if (lastReturnCount <= 0 || lastReturnCount > expectedReturns)
                throw Unknown("the homogeneous final wave does not yield a bounded positive last-return event count");
            if (expectedReturns > lastReturnCount &&
                conditions.EarlierReturnEventsAcceptedByTradeComplete != true)
                throw Unknown("earlier-wave Ship return events must be declared accepted by trade complete");
        }
        else
        {
            var loading = input.StationProvidedLoading!;
            if (loading.LoadingDuration.Status != OosTransportPhaseStatus.Conditional ||
                loading.LoadingDuration.ReferenceSeconds is not { } idealLoadingComplete ||
                loading.LoadingDuration.MinimumSeconds != idealLoadingComplete ||
                loading.LoadingDuration.MaximumSeconds != idealLoadingComplete ||
                loading.RemainingRecoveryDuration.Status != OosTransportPhaseStatus.Conditional ||
                loading.RemainingRecoveryDuration.ReferenceSeconds is not { } remainingRecovery ||
                loading.RemainingRecoveryDuration.MinimumSeconds != remainingRecovery ||
                loading.RemainingRecoveryDuration.MaximumSeconds != remainingRecovery)
            {
                throw Unknown("station-provided loading requires exact native L+1 loading and L+3 remaining-recovery contracts");
            }
            idealTradeCompleteFromNetworkStart = idealLoadingComplete;
            stationRemainingRecoverySeconds = remainingRecovery;
        }

        var actualTradeCompleteFromNetworkStart = FirstTickAtOrAfter(
            idealTradeCompleteFromNetworkStart,
            scheduler.TradeCompleteConsumerPeriodSeconds,
            scheduler.TradeCompleteConsumerPhaseOffsetSeconds);
        var returnAcceptedFromNetworkStart = returnScheduledFromNetworkStart is { } scheduledReturnEvent
            ? FirstTickAtOrAfter(
                scheduledReturnEvent,
                scheduler.CarrierConsumerPeriodSeconds,
                scheduler.CarrierConsumerPhaseOffsetSeconds)
            : actualTradeCompleteFromNetworkStart;
        var returnAcceptedAt = Math.Max(0, returnAcceptedFromNetworkStart - actualTradeCompleteFromNetworkStart);
        var networkNotificationEligibleFromNetworkStart = stationRemainingRecoverySeconds is { } stationRecovery
            ? actualTradeCompleteFromNetworkStart + stationRecovery
            : returnAcceptedFromNetworkStart +
                OosTransportTerminalPhaseCalculator.MassTrafficNetworkRemovedEventNotificationSeconds;
        var networkAcceptedFromNetworkStart = FirstTickAtOrAfter(
            networkNotificationEligibleFromNetworkStart,
            scheduler.NetworkConsumerPeriodSeconds,
            scheduler.NetworkConsumerPhaseOffsetSeconds);
        var networkAcceptedAt = Math.Max(0, networkAcceptedFromNetworkStart - actualTradeCompleteFromNetworkStart);
        var clock = new PeriodicClock(scheduler, actualTradeCompleteFromNetworkStart);

        var waits = DrawDepartureWaits(randomState, sourceProfile.TakeoffSeconds);
        randomState = waits.NextState;
        var matchingNetworkWait = OosTransportPhaseDuration.Exact(
            networkAcceptedAt,
            input.StationProvidedLoading is not null
                ? "predeclared first eligible network consumer after native station-carrier L+3 ready notification"
                : "predeclared first eligible network consumer after synchronous removal and native +2 notification");
        var departureContract = OosTransportTerminalPhaseCalculator.CalculateDepartureWait(
            OosTransportDepartureStartBoundary.ExecuteTradeComplete,
            sourceProfile,
            OosTransportDetachNetworkCondition.MatchingNetwork,
            matchingNetworkWait);
        if (departureContract.Duration.Status != OosTransportPhaseStatus.Conditional)
            throw Unknown(departureContract.Duration.Reason);

        var exactDepartureSeconds = networkAcceptedAt + waits.Waits.TradePostWaitSeconds +
            waits.Waits.UndockPreWaitSeconds + waits.Waits.TakeoffProfileSeconds +
            waits.Waits.UndockPostWaitSeconds;
        var exactDeparture = OosTransportPhaseDuration.Exact(
            exactDepartureSeconds,
            "predeclared native flat wait draws after the complete matching-network wait");
        var recovery = input.StationProvidedLoading is not null
            ? OosTransportPhaseDuration.Empty(
                "station-provided carriers do not consume the departing Ship reserved pool")
            : returnAcceptedFromNetworkStart <= actualTradeCompleteFromNetworkStart + Epsilon
            ? OosTransportPhaseDuration.Empty(
                $"predeclared earlier plus last-wave carrier consumers restore all {expectedReturns} homogeneous Ship reservations by trade complete")
            : OosTransportPhaseDuration.Exact(
                returnAcceptedAt,
                $"predeclared first eligible carrier consumer accepts the last {lastReturnCount} event(s) after {expectedReturns - lastReturnCount} earlier return(s); fixed Ship handler releases the exact reserved set");

        var movement = OosTransportTerminalPhaseCalculator.CalculateClearanceMovement(
            geometry.ShipPosition,
            geometry.ShipAxisX,
            geometry.ShipAxisY,
            geometry.ShipAxisZ,
            geometry.OldDockPosition,
            geometry.SafePosition,
            geometry.ShipSize,
            geometry.NextOrder,
            geometry.DefaultOrder,
            geometry.ExitPathPresent,
            geometry.InHighway);
        var noClearanceMovement = movement.Action == OosTransportClearanceAction.None &&
                                  !movement.MovementRequired && movement.MovementSeconds == 0;
        var blockingClearanceMovement = movement.BlockingOrder &&
            movement.Action is OosTransportClearanceAction.MoveToReverse or OosTransportClearanceAction.MoveStrafe;
        var zeroTimeHandoff = !movement.BlockingOrder && movement.MovementRequired &&
            movement.Action == OosTransportClearanceAction.MoveToZeroTimeHandoff &&
            movement.InterruptAfterSeconds == 0 && !movement.AbortPath;
        if (!noClearanceMovement && !blockingClearanceMovement && !zeroTimeHandoff)
            throw Unknown("the source scenario requires CalculateClearanceMovement None, nonblocking zero-time handoff, or the verified blocking reverse/strafe branch");
        if (noClearanceMovement && clearancePoint is not null)
            throw Unknown("a no-movement source branch must not submit a synthetic clearance FlightPoint");
        if (!noClearanceMovement && clearancePoint is null)
            throw Unknown("a movement-required source branch needs the complete ordinary Linear clearance FlightPoint");
        if (!noClearanceMovement && geometry.NativeClearanceTargetPose is null)
            throw Unknown("a movement-required source branch needs the complete target pose from the proven native point construction");
        if (clearancePoint is not null && !Near(clearancePoint.Point.Target.Position, geometry.SafePosition))
            throw Unknown("the declared clearance FlightPoint does not target CalculateClearanceMovement safePosition");
        if (!noClearanceMovement &&
            (conditions.ClearanceConsumerCandidate is null ||
             conditions.ClearanceActionIsDirectLeaf != true ||
             conditions.ClearancePointUsesProvenNativeUndockConstruction != true ||
             (blockingClearanceMovement && conditions.CompletionPublishedAtFirstEligiblePublisherPhase != true)))
            throw Unknown("movement clearance requires proven native point construction, consumer candidate, direct-leaf arbitration and the blocking branch publisher");
        if (zeroTimeHandoff && conditions.ClearanceConsumerCandidate != OosMoveActionCandidate.Timer)
            throw Unknown("the nonblocking move_to requires the declared zero-time Timer consumer");
        if (clearancePoint is not null &&
            (clearancePoint.Deadline > OosNavigationPathQueue.NativeDeadlineSentinelLimit ||
             clearancePoint.Point.Radius != -1f ||
             clearancePoint.Point.Boost || clearancePoint.Point.Travel ||
             clearancePoint.Point.Parameters is not null ||
             !Near(clearancePoint.Point.Target, geometry.NativeClearanceTargetPose!.Value)))
        {
            throw Unknown("the clearance FlightPoint conflicts with the proven ordinary undock point: unmarked deadline, radius=-1, boost/travel=false, no point-local parameters, and the complete native target pose are required");
        }

        var returnAccepted = returnAcceptedFromNetworkStart <= actualTradeCompleteFromNetworkStart + Epsilon;
        var networkAccepted = networkAcceptedFromNetworkStart <= actualTradeCompleteFromNetworkStart + Epsilon;
        var clearanceDelivered = false;
        var physicalCompletionAwaitingPublish = false;
        var normalCompletionDelivered = false;
        var action = blockingClearanceMovement || zeroTimeHandoff
            ? new OosMoveActionHandoff(
                exactDepartureSeconds,
                movement.InterruptAfterSeconds is { } timer ? exactDepartureSeconds + timer : null,
                normalCompletionPending: false)
            : null;
        double? actionAcceptedAt = null;

        if (input.StationProvidedLoading is not null)
            Add(0, "station-carriers", "no departing Ship reservation recovery");
        else if (returnAccepted)
            Add(0, "return-events-accepted-by-origin", $"count={expectedReturns}; networkTime={returnAcceptedFromNetworkStart:R}");
        if (networkAccepted)
            Add(0, "network-notification-accepted-by-origin", $"networkTime={networkAcceptedFromNetworkStart:R}");

        AdvanceBefore(exactDepartureSeconds);
        if (!returnAccepted || !networkAccepted)
            throw Unknown("the declared carrier/network consumers did not satisfy the generated departure boundary");

        Add(exactDepartureSeconds, noClearanceMovement ? "clearance-not-required" : "clearance-delivered",
            movement.Action.ToString());
        if (clearancePoint is not null)
            driver.DeliverPoints(new(sequence, exactDepartureSeconds), [clearancePoint]);
        clearanceDelivered = true;

        while (true)
        {
            if (actionAcceptedAt is not null)
                break;
            AdvanceOne();
            if (!zeroTimeHandoff && actionAcceptedAt is not null && action?.Winner == OosMoveActionCandidate.Timer &&
                (driver.Path.Active.Count != 0 || driver.Path.Pending.Count != 0))
            {
                throw Unknown("timer continued before physical clearance completed; the current kernel cannot materialize the next action's complete angular banks without future native state");
            }
        }

        var handoffTime = actionAcceptedAt ?? throw new InvalidOperationException("The route action was not accepted.");
        if (driver.Flight.Now > handoffTime + Epsilon)
            throw new InvalidOperationException("The route handoff preceded the declared action continuation.");
        var clearanceDuration = OosTransportPhaseDuration.Exact(
            handoffTime - exactDepartureSeconds,
            noClearanceMovement
                ? "CalculateClearanceMovement requires zero physical movement; duration is only the predeclared AI route-handoff phase"
                : zeroTimeHandoff
                    ? "nonblocking move_to continues at the declared zero-time Timer consumer while preserving its ordinary Linear point and state"
                : "predeclared ordinary Linear motion, declared publisher/winner consumption, and phase-driven full-bank route handoff");
        var parallelReadySeconds = Math.Max(returnAcceptedAt, exactDepartureSeconds);
        var parallelReady = OosTransportPhaseDuration.Exact(
            parallelReadySeconds,
            "predeclared recovery and complete detach/undock readiness from the same loading-complete boundary; max, not sum");
        var total = OosTransportTerminalPhaseCalculator.ComposeRequiredPhases([parallelReady, clearanceDuration]);
        if (total.Status != OosTransportPhaseStatus.Conditional ||
            total.ReferenceSeconds is not { } totalSeconds || Math.Abs(totalSeconds - handoffTime) > Epsilon)
            throw Unknown("source elapsed time and the declared script handoff clock did not close at the same phase");

        var handoff = new OosSourceDepartureHandoffResult(
            parallelReady,
            total,
            driver.Flight.Pose,
            driver.Flight.Velocity,
            "predeclared prospective source recovery/detach/clearance scenario with continuous phase-zero state");
        var banks = driver.Banks;
        var phaseZero = new OosGateSegmentDriver(
            driver.Flight,
            driver.Path,
            driver.Dynamics,
            new(
                banks[0],
                banks[1],
                driver.ReadIndex,
                driver.WriteIndex,
                driver.ContextSpace,
                driver.Reference,
                driver.CompletionLatched,
                OrdinarySpaceAndSharedParameterCacheConfirmed: true),
            input.OutputDriverMaximumPhases,
            requireCompleteAngularState: false,
            initialPhaseTime: handoffTime,
            allowUnknownInitialAngularVelocity: true);

        return new(
            phaseZero,
            handoff,
            finalReturn,
            actualTradeCompleteFromNetworkStart,
            input.ShipProvidedUnloading is null ? null : expectedReturns,
            input.ShipProvidedUnloading is null ? null : lastReturnCount,
            recovery,
            matchingNetworkWait,
            departureContract,
            exactDeparture,
            movement,
            action?.Winner,
            waits.Waits,
            randomState,
            phases);

        void AdvanceBefore(double cutoff)
        {
            while (clock.NextTime < cutoff - Epsilon)
                AdvanceOne();
        }

        void AdvanceOne()
        {
            if (++generated > scheduler.MaximumGeneratedPhases)
                throw Unknown("declared periodic schedule reached its phase bound before route handoff");
            var next = clock.Next();
            switch (next.Kind)
            {
                case PeriodicKind.Refresh:
                {
                    Add(next.Time, "parameter-refresh", $"bank={driver.ReadIndex}");
                    driver.ForceParameterRefresh(
                        new(sequence, next.Time),
                        driver.ReadIndex,
                        conditions.RefreshControls,
                        conditions.ListenerEffectsAlreadyApplied);
                    break;
                }
                case PeriodicKind.TravelStartDelivery:
                    if (driver.Dynamics.TravelQueue.Deadline is { } travelDeadline &&
                        travelDeadline <= next.Time)
                    {
                        Add(next.Time, "travel-start-delivered", $"bank={driver.ReadIndex}");
                        driver.DeliverTravelStart(new(sequence, next.Time), driver.ReadIndex);
                    }
                    break;
                case PeriodicKind.Worker:
                {
                    var workerDelta = next.Time - driver.Flight.Now;
                    if (!double.IsFinite(workerDelta) || workerDelta < -Epsilon)
                        throw Unknown("the declared worker phase precedes the saved Flight worker clock");
                    workerDelta = Math.Max(0, workerDelta);
                    Add(next.Time, "worker", $"dt={workerDelta:R}");
                    var worker = driver.StepWorker(
                        new(sequence, next.Time),
                        workerDelta,
                        conditions.OrdinaryCopyEligible,
                        conditions.DirtyConsumerEligible,
                        movement.ForceRotation,
                        suppressCompletion: false);
                    if (worker.Completion?.Popped == true)
                    {
                        if (clearanceDelivered)
                            physicalCompletionAwaitingPublish = true;
                    }

                    DeliverModeRequests(next.Time, worker.Queue.HeadBoost, worker.Queue.HeadTravel);
                    break;
                }
                case PeriodicKind.Manager:
                {
                    var acceptedPrefix = conditions.ManagerAcceptsEntireVisibleFifo == true
                        ? driver.PendingOrderCount
                        : throw Unknown("manager FIFO prefix policy is unresolved");
                    Add(next.Time, "manager", $"accepted={acceptedPrefix}");
                    driver.FinishManager(
                        new(sequence, next.Time),
                        new(
                            conditions.ManagerEligible,
                            conditions.WorkersCompleteBeforeManager,
                            acceptedPrefix,
                            Enumerable.Repeat(conditions.AllManagerContextLookupsSucceed == true, acceptedPrefix).ToArray(),
                            (float)scheduler.ManagerPeriodSeconds));
                    break;
                }
                case PeriodicKind.Carrier:
                    if (!returnAccepted && next.Time + Epsilon >= returnAcceptedAt)
                    {
                        returnAccepted = true;
                        Add(next.Time, "return-events-accepted", $"lastCount={lastReturnCount}; earlierCount={expectedReturns - lastReturnCount}");
                        if (Math.Abs(next.Time - returnAcceptedAt) > Epsilon)
                            throw new InvalidOperationException("Carrier consumer phase drifted from its predeclared time.");
                    }
                    break;
                case PeriodicKind.Network:
                    if (!networkAccepted && returnAccepted && next.Time + Epsilon >= networkAcceptedAt)
                    {
                        networkAccepted = true;
                        Add(next.Time, "network-notification-accepted",
                            input.StationProvidedLoading is not null
                                ? "station-carrier L+3 ready notification"
                                : "native +2 notification after declared synchronous removal");
                        if (Math.Abs(next.Time - networkAcceptedAt) > Epsilon)
                            throw new InvalidOperationException("Network consumer phase drifted from its predeclared time.");
                    }
                    break;
                case PeriodicKind.CompletionPublisher:
                    if (physicalCompletionAwaitingPublish && !normalCompletionDelivered)
                    {
                        if (action!.Continued)
                        {
                            Add(next.Time, "move-completion-loser-ignored", "clearance generation already continued");
                        }
                        else
                        {
                            Add(next.Time, "move-completion-published", "clearance");
                            action.DeliverNormalCompletion(sequence, next.Time);
                            normalCompletionDelivered = true;
                        }
                        physicalCompletionAwaitingPublish = false;
                    }
                    break;
                case PeriodicKind.Ai:
                    if (!clearanceDelivered || actionAcceptedAt is not null)
                        break;
                    if (noClearanceMovement)
                    {
                        Add(next.Time, "route-action-handoff", "no clearance movement required");
                        actionAcceptedAt = next.Time;
                        break;
                    }
                    var candidate = conditions.ClearanceConsumerCandidate!.Value;
                    if (candidate == OosMoveActionCandidate.NormalCompletion && !normalCompletionDelivered)
                        break;
                    if (candidate == OosMoveActionCandidate.Timer && action!.TimerDeadline > next.Time)
                        break;
                    Add(next.Time, "clearance-consumer", candidate.ToString());
                    if (action!.Accept(
                            sequence,
                            next.Time,
                            candidate,
                            conditions.ClearanceActionIsDirectLeaf) == OosMoveActionAcceptance.Continued)
                    {
                        actionAcceptedAt = next.Time;
                    }
                    break;
            }
        }

        void DeliverModeRequests(double now, bool boost, bool travel)
        {
            var decision = driver.Dynamics.Gate.Evaluate(now, driver.Flight.OrientationError, boost, travel);
            foreach (var request in decision.Requests)
            {
                Add(now, "mode-request", $"{request.Mode}:{request.Action}");
                driver.DeliverModeRequest(new(sequence, now), driver.ReadIndex, request);
            }
        }

        void Add(double now, string kind, string detail) =>
            phases.Add(new(++sequence, now, kind, detail));
    }

    private static void ValidateInitialDriver(
        OosGateSegmentDriver driver,
        OosSourceDepartureClearanceGeometry geometry,
        OosSourceDepartureScenarioConditions conditions)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (conditions.InitialDockedStationaryStateAndCompleteCachesDeclared != true)
            throw Unknown("the docked stationary motion banks, engine state and shared caches must be explicitly declared");
        if (driver.IsObservedParameterProjection)
            throw Unknown("a prospective source scenario requires real Dynamics state, not observed parameter projection");
        if (driver.PhaseCount != 0 || driver.LastPhaseSequence != -1 || Math.Abs(driver.Flight.Now) > Epsilon)
            throw Unknown("InitialDriverFactory must return a fresh driver at loading-complete elapsed=0");
        if (driver.PendingOrderCount != 0 || driver.Path.Active.Count != 0 ||
            driver.Path.Pending.Count != 0 || driver.Path.IsDirty)
            throw Unknown("the loading-complete initial path/FIFO must be explicitly empty and clean");
        if (!Near(driver.Flight.Pose.Position, geometry.ShipPosition) ||
            !Near(driver.Flight.Pose.XAxis, geometry.ShipAxisX) ||
            !Near(driver.Flight.Pose.YAxis, geometry.ShipAxisY) ||
            !Near(driver.Flight.Pose.ZAxis, geometry.ShipAxisZ) ||
            !Near(driver.Flight.Velocity, OosLinearVector4.Zero) ||
            !ReadBankMatchesFlight(driver) || driver.Banks.Any(bank => bank.AngularVelocity is null))
            throw Unknown("the complete stationary banks must match the source ship pose and continuous Flight state");
    }

    private static OosSourceDepartureScenarioConditions ValidateConditions(
        OosSourceDepartureScenarioConditions? value,
        OosSourceDeparturePeriodicSchedule scheduler)
    {
        var result = value ?? throw Unknown("source branch and arbitration conditions are required");
        if (result.OrdinaryCopyEligible != true || result.DirtyConsumerEligible != true ||
            result.ManagerEligible != true || result.WorkersCompleteBeforeManager != true ||
            result.ManagerAcceptsEntireVisibleFifo != true ||
            result.AllManagerContextLookupsSucceed != true ||
            result.TradeCompleteAcceptedAtFirstEligibleConsumerPhase != true ||
            result.NetworkNotificationAcceptedAtFirstEligibleConsumerPhase != true ||
            result.DepartureWaitDrawsAreConsecutiveInXmlOrder != true ||
            result.ClearanceSubmissionPrecedesPeriodicWorkAtEqualTime != true ||
            result.NewRouteActionGenerationEstablished != true ||
            result.OrdinaryLinearStateRemainsContinuous != true ||
            result.NoUnmodeledDepartureMotionEffects != true)
        {
            throw Unknown("ordinary source, network notification, RNG order, script/periodic order and action arbitration conditions are incomplete");
        }
        if (scheduler.RefreshPeriodSeconds is not null &&
            (result.ListenerEffectsAlreadyApplied != true || result.RefreshControls is null))
            throw Unknown("periodic refresh requires resolved controls and already-applied listener effects");
        return result;
    }

    private static OosSourceDeparturePeriodicSchedule ValidateScheduler(OosSourceDeparturePeriodicSchedule? value)
    {
        var result = value ?? throw Unknown("the source worker/manager/carrier/network/AI schedule is required");
        Positive(result.WorkerPeriodSeconds, nameof(result.WorkerPeriodSeconds));
        Positive(result.TravelStartDeliveryPeriodSeconds, nameof(result.TravelStartDeliveryPeriodSeconds));
        Positive(result.ManagerPeriodSeconds, nameof(result.ManagerPeriodSeconds));
        Positive(result.AiPeriodSeconds, nameof(result.AiPeriodSeconds));
        Positive(result.TradeCompleteConsumerPeriodSeconds, nameof(result.TradeCompleteConsumerPeriodSeconds));
        Positive(result.CarrierConsumerPeriodSeconds, nameof(result.CarrierConsumerPeriodSeconds));
        Positive(result.NetworkConsumerPeriodSeconds, nameof(result.NetworkConsumerPeriodSeconds));
        Positive(result.CompletionPublisherPeriodSeconds, nameof(result.CompletionPublisherPeriodSeconds));
        NonNegative(result.WorkerPhaseOffsetSeconds, nameof(result.WorkerPhaseOffsetSeconds));
        NonNegative(result.TravelStartDeliveryPhaseOffsetSeconds, nameof(result.TravelStartDeliveryPhaseOffsetSeconds));
        NonNegative(result.ManagerPhaseOffsetSeconds, nameof(result.ManagerPhaseOffsetSeconds));
        NonNegative(result.AiPhaseOffsetSeconds, nameof(result.AiPhaseOffsetSeconds));
        NonNegative(result.TradeCompleteConsumerPhaseOffsetSeconds, nameof(result.TradeCompleteConsumerPhaseOffsetSeconds));
        NonNegative(result.CarrierConsumerPhaseOffsetSeconds, nameof(result.CarrierConsumerPhaseOffsetSeconds));
        NonNegative(result.NetworkConsumerPhaseOffsetSeconds, nameof(result.NetworkConsumerPhaseOffsetSeconds));
        NonNegative(result.CompletionPublisherPhaseOffsetSeconds, nameof(result.CompletionPublisherPhaseOffsetSeconds));
        if ((result.RefreshPeriodSeconds is null) != (result.RefreshPhaseOffsetSeconds is null))
            throw Unknown("refresh period and phase offset must be declared together");
        if (result.RefreshPeriodSeconds is { } refresh) Positive(refresh, nameof(result.RefreshPeriodSeconds));
        if (result.RefreshPhaseOffsetSeconds is { } refreshPhase) NonNegative(refreshPhase, nameof(result.RefreshPhaseOffsetSeconds));
        if (result.MaximumGeneratedPhases <= 0 ||
            !result.RefreshTravelWorkerManagerTradeCompleteCarrierNetworkPublisherAiAtEqualTime)
            throw Unknown("a positive phase bound and the declared same-time total order are required");
        return result;
    }

    private static WaitDraw DrawDepartureWaits(ulong state, double takeoffSeconds)
    {
        if (!double.IsFinite(takeoffSeconds) || takeoffSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(takeoffSeconds));
        var postTrade = X4NativeRandom.DrawFloat(state, 2f);
        var preUndock = X4NativeRandom.DrawFloat(postTrade.NextState, .499f);
        var postUndock = X4NativeRandom.DrawFloat(preUndock.NextState, 2f);
        return new(
            postUndock.NextState,
            new(
                1d + postTrade.Value,
                .001d + preUndock.Value,
                takeoffSeconds,
                1d + postUndock.Value));
    }

    private static double FirstTickAtOrAfter(double eligible, double period, double offset)
    {
        if (!double.IsFinite(eligible) || eligible < 0)
            throw new ArgumentOutOfRangeException(nameof(eligible));
        var first = offset <= Epsilon ? period : offset;
        if (eligible <= first + Epsilon)
            return first;
        var steps = Math.Ceiling((eligible - first - Epsilon) / period);
        return first + steps * period;
    }

    private static bool ReadBankMatchesFlight(OosGateSegmentDriver driver)
    {
        var bank = driver.Banks[driver.ReadIndex];
        return Near(bank.Pose.Position, driver.Flight.Pose.Position) &&
            Near(bank.Pose.XAxis, driver.Flight.Pose.XAxis) &&
            Near(bank.Pose.YAxis, driver.Flight.Pose.YAxis) &&
            Near(bank.Pose.ZAxis, driver.Flight.Pose.ZAxis) &&
            Near(bank.Velocity, driver.Flight.Velocity);
    }

    private static bool Near(OosLinearVector4 left, Vec3 right) =>
        Math.Abs(left.X - right.X) <= .001 && Math.Abs(left.Y - right.Y) <= .001 &&
        Math.Abs(left.Z - right.Z) <= .001 && Math.Abs(left.W) <= .001f;

    private static bool Near(OosLinearVector4 left, OosLinearVector4 right) =>
        Math.Abs(left.X - right.X) <= .001f && Math.Abs(left.Y - right.Y) <= .001f &&
        Math.Abs(left.Z - right.Z) <= .001f && Math.Abs(left.W - right.W) <= .001f;

    private static bool Near(OosLinearPose left, OosLinearPose right) =>
        Near(left.Position, right.Position) && Near(left.XAxis, right.XAxis) &&
        Near(left.YAxis, right.YAxis) && Near(left.ZAxis, right.ZAxis);

    private static void Positive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(name);
    }

    private static void NonNegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(name);
    }

    private static NotSupportedException Unknown(string reason) => new($"UNKNOWN: {reason}.");

    private enum PeriodicKind
    {
        Refresh,
        TravelStartDelivery,
        Worker,
        Manager,
        Carrier,
        Network,
        CompletionPublisher,
        Ai
    }

    private sealed class PeriodicClock
    {
        private readonly OosSourceDeparturePeriodicSchedule _schedule;
        private readonly double[] _next;
        public double NextTime => _next.Min();

        public PeriodicClock(OosSourceDeparturePeriodicSchedule schedule, double tradeCompleteFromNetworkStart)
        {
            _schedule = schedule;
            _next =
            [
                schedule.RefreshPhaseOffsetSeconds ?? double.PositiveInfinity,
                schedule.TravelStartDeliveryPhaseOffsetSeconds,
                schedule.WorkerPhaseOffsetSeconds,
                schedule.ManagerPhaseOffsetSeconds,
                FirstTickStrictlyAfter(
                    tradeCompleteFromNetworkStart,
                    schedule.CarrierConsumerPeriodSeconds,
                    schedule.CarrierConsumerPhaseOffsetSeconds) - tradeCompleteFromNetworkStart,
                FirstTickStrictlyAfter(
                    tradeCompleteFromNetworkStart,
                    schedule.NetworkConsumerPeriodSeconds,
                    schedule.NetworkConsumerPhaseOffsetSeconds) - tradeCompleteFromNetworkStart,
                schedule.CompletionPublisherPhaseOffsetSeconds,
                schedule.AiPhaseOffsetSeconds
            ];
            for (var index = 0; index < _next.Length; index++)
            {
                var period = Period(index);
                while (_next[index] <= Epsilon)
                    _next[index] += period;
            }
        }

        private static double FirstTickStrictlyAfter(double origin, double period, double offset)
        {
            var atOrAfter = FirstTickAtOrAfter(origin, period, offset);
            return atOrAfter <= origin + Epsilon ? atOrAfter + period : atOrAfter;
        }

        public (PeriodicKind Kind, double Time) Next()
        {
            var index = 0;
            for (var candidate = 1; candidate < _next.Length; candidate++)
                if (_next[candidate] < _next[index] - Epsilon)
                    index = candidate;
            var time = _next[index];
            _next[index] += Period(index);
            return ((PeriodicKind)index, time);
        }

        private double Period(int index) => index switch
        {
            0 => _schedule.RefreshPeriodSeconds ?? double.PositiveInfinity,
            1 => _schedule.TravelStartDeliveryPeriodSeconds,
            2 => _schedule.WorkerPeriodSeconds,
            3 => _schedule.ManagerPeriodSeconds,
            4 => _schedule.CarrierConsumerPeriodSeconds,
            5 => _schedule.NetworkConsumerPeriodSeconds,
            6 => _schedule.CompletionPublisherPeriodSeconds,
            7 => _schedule.AiPeriodSeconds,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    private sealed record WaitDraw(ulong NextState, OosSourceDepartureRandomWaits Waits);

    private sealed record RunResult(
        OosGateSegmentDriver Driver,
        OosSourceDepartureHandoffResult Handoff,
        OosShipCarrierFinalReturnSchedule? FinalReturnSchedule,
        double ActualTradeCompleteFromNetworkStartSeconds,
        long? ExpectedShipReturnEventCount,
        long? LastWaveShipReturnEventCount,
        OosTransportPhaseDuration Recovery,
        OosTransportPhaseDuration MatchingNetworkWait,
        OosTransportDepartureWaitResult DepartureWaitContract,
        OosTransportPhaseDuration ExactDepartureWait,
        OosTransportClearanceMovementResult ClearanceMovement,
        OosMoveActionCandidate? ClearanceWinner,
        OosSourceDepartureRandomWaits RandomWaits,
        ulong NextScriptRandomState,
        IReadOnlyList<OosSourceDepartureScenarioPhase> Phases);
}
