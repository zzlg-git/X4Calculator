namespace X4Calculator.Core.Calculation;

/// <summary>
/// 同质单 holder cargo carrier 的最后返航计划边界。
/// Scheduled 不等于 Accepted，也不证明 holder 的可用计数已经恢复。
/// </summary>
public sealed record OosShipCarrierFinalReturnSchedule(
    OosTransportPhaseStatus Status,
    double? TradeCompleteFromNetworkStartSeconds,
    double? FinalReturnLegEndFromNetworkStartSeconds,
    double? ReturnUnitEventScheduledFromNetworkStartSeconds,
    double? ReturnUnitEventScheduledRelativeToTradeCompleteSeconds,
    string Reason);

/// <summary>
/// 从 execute_trade complete 起观察到的最终返航事件边界。
/// 通用 holder 的可用计数效果未统一闭合，故该入口必须显式提供恢复事实。
/// </summary>
public sealed record OosShipCarrierRecoveryObservation(
    bool? ReturnUnitEventAcceptedByTradeComplete,
    double? ReturnUnitEventAcceptedAfterTradeCompleteSeconds,
    bool? DepartingHolderAvailableCountRestored);

public sealed record OosShipCarrierRecoveryResult(
    OosShipCarrierFinalReturnSchedule Schedule,
    OosTransportPhaseDuration RecoveryFromTradeComplete);

/// <summary>
/// 固定 Ship vtable 的 ReturnUnitEvent 接受计数。AcceptedEventsAreExactTransactionSet 要求 holder、
/// unit model 和交易窗口均已过滤，不能把同 model 的其他交易归还混入。
/// </summary>
public sealed record OosShipCarrierReturnAcceptanceInput(
    /// <summary>
    /// 仅当 receiver 的精确 vtable +0xB68 已静态证明指向相同 Ship ReturnUnit handler 时为 true；
    /// 包括主 Ship 表和逐个核对的派生 Ship 表，不表示派生对象使用主表。
    /// </summary>
    bool? ReceiverUsesProvenShipReturnHandler,
    bool? EventDestinationMatchesReceiver,
    bool? AcceptedEventsAreExactTransactionSet,
    long? AcceptedByTradeCompleteCount,
    long? AcceptedAfterTradeCompleteCount,
    double? LastAcceptedAfterTradeCompleteSeconds);

public sealed record OosShipCarrierDispatchRecoveryResult(
    OosShipCarrierFinalReturnSchedule Schedule,
    OosTransportPhaseDuration RecoveryFromTradeComplete,
    long? ExpectedReturnEventCount,
    long? AcceptedReturnEventCount,
    bool ShipReservedReleaseEffectProven,
    string Reason);

/// <summary>
/// run07/run08 已验证的默认贸易普通 Linear 清离交接输入。
/// 耗时必须来自相同控制器状态和真实接受相位的条件重放，不能使用 19 秒样本或 30 秒 timer 常数。
/// </summary>
public sealed record OosDefaultTradeRoutineClearanceInput(
    string? DefaultOrderId,
    OosTransportClearanceMovementResult? Movement,
    OosTransportPhaseDuration? ReplayToAcceptedHandoff,
    OosMoveActionCandidate? AcceptedWinner,
    bool? ActualConsumerOrderObserved,
    bool? OrdinaryLinearStateContinuous,
    bool? NewActionGenerationEstablished,
    OosLinearPose? RouteStartPose,
    OosLinearVector4? RouteStartVelocity);

public sealed record OosDefaultTradeRoutineClearanceResult(
    OosTransportPhaseDuration Duration,
    OosMoveActionCandidate? AcceptedWinner,
    OosLinearPose? RouteStartPose,
    OosLinearVector4? RouteStartVelocity,
    string Reason);

/// <summary>
/// execute_trade complete 到下一路线动作连续状态的源端交接。
/// Recovery 与 detach/undock 从同一边界并行推进，先取 max，再串联清离。
/// </summary>
public sealed record OosSourceDepartureHandoffResult(
    OosTransportPhaseDuration ParallelReadyDuration,
    OosTransportPhaseDuration TotalDuration,
    OosLinearPose? RouteStartPose,
    OosLinearVector4? RouteStartVelocity,
    string Reason);

public static class OosSourceDepartureHandoffCalculator
{
    public const double ReturnUnitEventScheduleOffsetSeconds = 0.1;

    private static readonly HashSet<string> DefaultTradeRoutineOrders = new(StringComparer.Ordinal)
    {
        "TradeRoutine",
        "TradeRoutine_Basic",
        "TradeRoutine_Advanced"
    };

    /// <summary>
    /// 复用同质单 holder 多波结果，计算最终 quota 返航 leg end 与 ReturnUnitEvent 的计划时刻。
    /// 该结果不把 trade complete 当成 ReturnUnitEvent，也不声称事件已被接受。
    /// </summary>
    public static OosShipCarrierFinalReturnSchedule CalculateFinalReturnSchedule(
        OosTransportHomogeneousCarrierWaveTradeResult trade,
        bool carriersBelongToDepartingShip)
    {
        ArgumentNullException.ThrowIfNull(trade);

        if (trade.TradeCompleteDuration.Status == OosTransportPhaseStatus.Empty ||
            trade.RequiredCarrierQuotas == 0)
        {
            return new(
                OosTransportPhaseStatus.Empty,
                0,
                0,
                null,
                null,
                "no cargo carrier quota requires a final return event");
        }

        if (!carriersBelongToDepartingShip)
            return UnknownSchedule("selected carriers do not belong to the departing ship");

        if (trade.TradeCompleteDuration.Status == OosTransportPhaseStatus.Unknown ||
            trade.TradeCompleteDuration.MinimumSeconds != trade.TradeCompleteDuration.MaximumSeconds ||
            trade.TradeCompleteDuration.ReferenceSeconds is not { } tradeComplete ||
            trade.WaveCount is not { } waveCount || waveCount <= 0 ||
            trade.SingleLegSecondsFloat32 is not { } singleLeg || !float.IsFinite(singleLeg) || singleLeg < 0)
        {
            return UnknownSchedule(
                "an exact homogeneous single-holder trade result, positive wave count, and native float32 leg are required");
        }

        var finalReturnLegEnd = checked((waveCount - 1) * (2d * singleLeg + 4d)) + 2d * singleLeg + 2d;
        var returnEventScheduled = finalReturnLegEnd + ReturnUnitEventScheduleOffsetSeconds;
        return new(
            OosTransportPhaseStatus.Conditional,
            tradeComplete,
            finalReturnLegEnd,
            returnEventScheduled,
            returnEventScheduled - tradeComplete,
            "native ideal final return leg and ReturnUnitEvent scheduled +0.1; acceptance, holder count recovery, and dispatch slip excluded");
    }

    /// <summary>
    /// 将实际事件接受与 holder 可用计数恢复接到 trade-complete 边界。
    /// 计划时刻只用于审计，不能替代 observation。
    /// </summary>
    public static OosShipCarrierRecoveryResult ResolveObservedRecovery(
        OosShipCarrierFinalReturnSchedule schedule,
        OosShipCarrierRecoveryObservation observation)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(observation);

        if (schedule.Status == OosTransportPhaseStatus.Empty)
            return new(schedule, OosTransportPhaseDuration.Empty("no ship carrier recovery required"));
        if (schedule.Status == OosTransportPhaseStatus.Unknown)
            return new(schedule, OosTransportPhaseDuration.Unknown(schedule.Reason));

        if (observation.ReturnUnitEventAcceptedByTradeComplete is null)
            return UnknownRecovery(schedule, "actual ReturnUnitEvent acceptance relative to trade complete is missing");
        if (observation.DepartingHolderAvailableCountRestored != true)
            return UnknownRecovery(schedule, "ReturnUnitEvent acceptance is not proof that the departing holder available count was restored");

        if (observation.ReturnUnitEventAcceptedByTradeComplete.Value)
        {
            if (observation.ReturnUnitEventAcceptedAfterTradeCompleteSeconds is not null)
                throw new ArgumentException("An event already accepted by trade complete cannot also have a later acceptance offset.", nameof(observation));
            return new(schedule, OosTransportPhaseDuration.Empty(
                "ReturnUnitEvent accepted and departing holder availability restored by trade complete"));
        }

        if (observation.ReturnUnitEventAcceptedAfterTradeCompleteSeconds is not { } acceptedAfter ||
            !double.IsFinite(acceptedAfter) || acceptedAfter < 0)
        {
            return UnknownRecovery(schedule,
                "a finite observed ReturnUnitEvent acceptance offset after trade complete is required");
        }

        return new(schedule, OosTransportPhaseDuration.Exact(
            acceptedAfter,
            "observed ReturnUnitEvent acceptance with departing holder availability restored; no modeled dispatch constant"));
    }

    /// <summary>
    /// 固定 X4 9.00 Ship 主 vtable 的 +0xB68 经 0x4CFD30 跳到 0x4D9B50；每次正常命中
    /// unit model 的正 reserved(+0x550) 计数即减一，归零删除节点，总数(+0x510)不变。
    /// 因此同一交易的全部目标事件被接受后，可用数恢复到交易前；实际接受时间仍必须显式提供。
    /// </summary>
    public static OosShipCarrierDispatchRecoveryResult ResolvePrimaryShipDispatchRecovery(
        OosTransportHomogeneousCarrierWaveTradeResult trade,
        OosShipCarrierReturnAcceptanceInput acceptance)
    {
        ArgumentNullException.ThrowIfNull(trade);
        ArgumentNullException.ThrowIfNull(acceptance);

        var identityConfirmed = acceptance.ReceiverUsesProvenShipReturnHandler == true &&
                                acceptance.EventDestinationMatchesReceiver == true;
        var schedule = CalculateFinalReturnSchedule(trade, identityConfirmed);
        if (schedule.Status == OosTransportPhaseStatus.Empty)
        {
            return new(
                schedule,
                OosTransportPhaseDuration.Empty("no ship carrier return events required"),
                0,
                0,
                ShipReservedReleaseEffectProven: identityConfirmed,
                "no selected ship carriers");
        }
        if (schedule.Status == OosTransportPhaseStatus.Unknown || !identityConfirmed)
            return UnknownDispatchRecovery(
                schedule,
                null,
                "a receiver with a proven Ship ReturnUnit handler and matching event destination is required",
                effectProven: identityConfirmed);
        if (acceptance.AcceptedEventsAreExactTransactionSet != true)
            return UnknownDispatchRecovery(schedule, null, "accepted ReturnUnitEvents must be the exact holder/model/transaction set", effectProven: true);
        if (trade.RequiredCarrierQuotas is not { } required || required <= 0 ||
            trade.AvailableCarrierCount is not { } available || available <= 0)
        {
            return UnknownDispatchRecovery(schedule, null, "required quotas and available selected carriers are required", effectProven: true);
        }

        var expected = Math.Min(required, available);
        if (acceptance.AcceptedByTradeCompleteCount is not { } acceptedBy ||
            acceptance.AcceptedAfterTradeCompleteCount is not { } acceptedAfter ||
            acceptedBy < 0 || acceptedAfter < 0 || acceptedBy > expected || acceptedAfter > expected - acceptedBy)
        {
            return UnknownDispatchRecovery(schedule, expected, "bounded accepted event counts are required", effectProven: true);
        }

        var accepted = acceptedBy + acceptedAfter;
        if (accepted < expected)
        {
            return new(
                schedule,
                OosTransportPhaseDuration.Unknown(
                    $"{expected - accepted} target ReturnUnitEvent acceptance(s) are still missing"),
                expected,
                accepted,
                ShipReservedReleaseEffectProven: true,
                "each accepted event releases one matching Ship reserved count; the transaction set is incomplete");
        }

        if (acceptedAfter == 0)
        {
            if (acceptance.LastAcceptedAfterTradeCompleteSeconds is not null)
            {
                return UnknownDispatchRecovery(schedule, expected,
                    "a post-trade-complete acceptance offset is invalid when no event was accepted after trade complete",
                    effectProven: true);
            }

            return new(
                schedule,
                OosTransportPhaseDuration.Empty(
                    "all target ReturnUnitEvents accepted by trade complete; Ship reserved counts restored by the fixed receiver"),
                expected,
                accepted,
                ShipReservedReleaseEffectProven: true,
                "Ship +0xB68 fixed receiver decrements reserved(+0x550) once per accepted target event");
        }

        if (acceptance.LastAcceptedAfterTradeCompleteSeconds is not { } lastAccepted ||
            !double.IsFinite(lastAccepted) || lastAccepted < 0)
        {
            return UnknownDispatchRecovery(schedule, expected,
                "a finite non-negative last target acceptance offset is required",
                effectProven: true);
        }

        return new(
            schedule,
            OosTransportPhaseDuration.Exact(
                lastAccepted,
                "last target ReturnUnitEvent accepted; fixed Ship receiver restored all transaction reserved counts"),
            expected,
            accepted,
            ShipReservedReleaseEffectProven: true,
            "actual event acceptance supplies timing; Ship +0xB68 supplies the reserved-count effect without a fitted slip");
    }

    /// <summary>
    /// 接受普通默认贸易的条件 Linear 重放结果，并保留 MoveTo 实际赢家后的 pose/velocity。
    /// </summary>
    public static OosDefaultTradeRoutineClearanceResult ResolveDefaultTradeRoutineClearance(
        OosDefaultTradeRoutineClearanceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.DefaultOrderId is null || !DefaultTradeRoutineOrders.Contains(input.DefaultOrderId))
            return UnknownClearance("an explicit TradeRoutine default order is required");
        if (input.Movement is not { } movement || movement.Status == OosTransportPhaseStatus.Unknown ||
            !movement.BlockingOrder || movement.Action is not (OosTransportClearanceAction.MoveToReverse or OosTransportClearanceAction.MoveStrafe))
        {
            return UnknownClearance("the verified default TradeRoutine blocking reverse/strafe clearance branch is required");
        }
        if (input.ActualConsumerOrderObserved != true || input.AcceptedWinner is not { } acceptedWinner ||
            !Enum.IsDefined(acceptedWinner))
            return UnknownClearance("the actual MoveTo completion/timer consumer order is required");
        if (input.OrdinaryLinearStateContinuous != true)
            return UnknownClearance("the ordinary Linear controller state must remain continuous from clearance submission");
        if (input.NewActionGenerationEstablished != true || input.RouteStartPose is null || input.RouteStartVelocity is null)
            return UnknownClearance("the next action generation and its continuous route-start pose/velocity are required");
        if (input.ReplayToAcceptedHandoff is not { } replay || replay.Status != OosTransportPhaseStatus.Conditional)
            return UnknownClearance("a condition replay through the accepted handoff is required; sample and timer constants are unsupported");

        ValidateKnownDuration(replay, nameof(input.ReplayToAcceptedHandoff));
        ValidatePose(input.RouteStartPose.Value);
        ValidateVector(input.RouteStartVelocity.Value);

        return new(
            replay,
            input.AcceptedWinner,
            input.RouteStartPose,
            input.RouteStartVelocity,
            "run07/run08 ordinary Linear clearance replay accepted in actual consumer order; continuous state handed to a new action generation");
    }

    /// <summary>
    /// Recovery 与完整 departure wait 都从 execute_trade complete 起算，因此取并行 max；
    /// clearance 再从两者都满足后的边界串联。任何必要输入未知均保持 Unknown。
    /// </summary>
    public static OosSourceDepartureHandoffResult Compose(
        OosTransportPhaseDuration recoveryFromTradeComplete,
        OosTransportPhaseDuration departureWaitFromTradeComplete,
        OosDefaultTradeRoutineClearanceResult clearance)
    {
        ArgumentNullException.ThrowIfNull(recoveryFromTradeComplete);
        ArgumentNullException.ThrowIfNull(departureWaitFromTradeComplete);
        ArgumentNullException.ThrowIfNull(clearance);

        var parallelReady = MaxParallel(recoveryFromTradeComplete, departureWaitFromTradeComplete);
        var total = OosTransportTerminalPhaseCalculator.ComposeRequiredPhases(
            new[] { parallelReady, clearance.Duration });
        OosLinearPose? routeStartPose = null;
        OosLinearVector4? routeStartVelocity = null;
        if (total.Status != OosTransportPhaseStatus.Unknown &&
            clearance.RouteStartPose is not null && clearance.RouteStartVelocity is not null)
        {
            routeStartPose = clearance.RouteStartPose;
            routeStartVelocity = clearance.RouteStartVelocity;
        }

        return new(
            parallelReady,
            total,
            routeStartPose,
            routeStartVelocity,
            total.Status == OosTransportPhaseStatus.Unknown
                ? total.Reason
                : "parallel recovery/departure readiness followed by condition-replayed default TradeRoutine clearance");
    }

    private static OosTransportPhaseDuration MaxParallel(
        OosTransportPhaseDuration left,
        OosTransportPhaseDuration right)
    {
        if (left.Status == OosTransportPhaseStatus.Unknown || right.Status == OosTransportPhaseStatus.Unknown)
        {
            var reasons = new[] { left, right }
                .Where(value => value.Status == OosTransportPhaseStatus.Unknown)
                .Select(value => value.Reason);
            return OosTransportPhaseDuration.Unknown(string.Join("; ", reasons));
        }

        if (left.Status == OosTransportPhaseStatus.Empty && right.Status == OosTransportPhaseStatus.Empty)
            return OosTransportPhaseDuration.Empty("both parallel requirements are empty");

        ValidateKnownDuration(left, nameof(left));
        ValidateKnownDuration(right, nameof(right));
        return OosTransportPhaseDuration.Range(
            Math.Max(left.MinimumSeconds!.Value, right.MinimumSeconds!.Value),
            Math.Max(left.MaximumSeconds!.Value, right.MaximumSeconds!.Value),
            Math.Max(left.ReferenceSeconds!.Value, right.ReferenceSeconds!.Value),
            isReferenceStatisticalMean: false,
            "parallel requirements from the same execute-trade-complete boundary; max, not sum");
    }

    private static OosShipCarrierFinalReturnSchedule UnknownSchedule(string reason) =>
        new(OosTransportPhaseStatus.Unknown, null, null, null, null, reason);

    private static OosShipCarrierRecoveryResult UnknownRecovery(
        OosShipCarrierFinalReturnSchedule schedule,
        string reason) =>
        new(schedule, OosTransportPhaseDuration.Unknown(reason));

    private static OosDefaultTradeRoutineClearanceResult UnknownClearance(string reason) =>
        new(OosTransportPhaseDuration.Unknown(reason), null, null, null, reason);

    private static OosShipCarrierDispatchRecoveryResult UnknownDispatchRecovery(
        OosShipCarrierFinalReturnSchedule schedule,
        long? expected,
        string reason,
        bool effectProven = false) =>
        new(
            schedule,
            OosTransportPhaseDuration.Unknown(reason),
            expected,
            null,
            ShipReservedReleaseEffectProven: effectProven,
            reason);

    private static void ValidateKnownDuration(OosTransportPhaseDuration duration, string parameterName)
    {
        if (duration.MinimumSeconds is not { } minimum || duration.MaximumSeconds is not { } maximum ||
            duration.ReferenceSeconds is not { } reference || !double.IsFinite(minimum) || minimum < 0 ||
            !double.IsFinite(maximum) || maximum < minimum || !double.IsFinite(reference) ||
            reference < minimum || reference > maximum)
        {
            throw new ArgumentException("Known durations must be finite, nonnegative, and ordered.", parameterName);
        }
    }

    private static void ValidatePose(OosLinearPose pose)
    {
        ValidateVector(pose.Position);
        ValidateVector(pose.XAxis);
        ValidateVector(pose.YAxis);
        ValidateVector(pose.ZAxis);
    }

    private static void ValidateVector(OosLinearVector4 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z) || !float.IsFinite(value.W))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }
}
