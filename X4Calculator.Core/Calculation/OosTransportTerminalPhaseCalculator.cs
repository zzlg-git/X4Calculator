using X4Calculator.Core.Models;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace X4Calculator.Core.Calculation;

/// <summary>源代码已经闭合、但仍依赖显式情景前提的 OOS 阶段状态。</summary>
public enum OosTransportPhaseStatus
{
    Empty,
    Conditional,
    Unknown
}

/// <summary>一个必需阶段的耗时。Unknown 不用零值代替。</summary>
public sealed record OosTransportPhaseDuration(
    OosTransportPhaseStatus Status,
    double? MinimumSeconds,
    double? MaximumSeconds,
    double? ReferenceSeconds,
    bool IsReferenceStatisticalMean,
    string Reason)
{
    public static OosTransportPhaseDuration Empty(string reason = "no work requested") =>
        new(OosTransportPhaseStatus.Empty, 0, 0, 0, true, reason);

    public static OosTransportPhaseDuration Exact(double seconds, string reason) =>
        Range(seconds, seconds, seconds, isReferenceStatisticalMean: false, reason);

    public static OosTransportPhaseDuration Range(
        double minimumSeconds,
        double maximumSeconds,
        double referenceSeconds,
        bool isReferenceStatisticalMean,
        string reason)
    {
        ValidateSeconds(minimumSeconds, nameof(minimumSeconds));
        ValidateSeconds(maximumSeconds, nameof(maximumSeconds));
        ValidateSeconds(referenceSeconds, nameof(referenceSeconds));
        if (maximumSeconds < minimumSeconds)
            throw new ArgumentOutOfRangeException(nameof(maximumSeconds));
        if (referenceSeconds < minimumSeconds || referenceSeconds > maximumSeconds)
            throw new ArgumentOutOfRangeException(nameof(referenceSeconds));

        return new(
            OosTransportPhaseStatus.Conditional,
            minimumSeconds,
            maximumSeconds,
            referenceSeconds,
            isReferenceStatisticalMean,
            reason);
    }

    public static OosTransportPhaseDuration Unknown(string reason) =>
        new(OosTransportPhaseStatus.Unknown, null, null, null, false, reason);

    private static void ValidateSeconds(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}

/// <summary>
/// 由游戏 XML/source profile 解析出的停靠配置。调用者必须明确提供配置，不能按全部尺寸猜测。
/// </summary>
public sealed record OosTransportDockingSourceProfile(
    string Id,
    double LandingSeconds,
    double TakeoffSeconds)
{
    /// <summary>原版 normal L/XL profile：landing 20 秒，缺少 takeoff 条目故为 0 秒。</summary>
    public static OosTransportDockingSourceProfile NormalCapitalXml20 { get; } =
        new("normal-capital-xml-20", LandingSeconds: 20, TakeoffSeconds: 0);
}

/// <summary>同一 holder 上一次或多次并发 transfer 的单并行波容量输入。</summary>
public sealed record OosTransportSingleWaveCapacityInput(
    IReadOnlyList<long> TransferCargoUnits,
    long WareVolume,
    long CarrierCapacityVolume,
    long HolderTotal,
    long? ReservedUpperBound);

/// <summary>单并行波容量的充分条件结果；不满足时不外推多波耗时。</summary>
public sealed record OosTransportSingleWaveCapacityResult(
    OosTransportPhaseStatus Status,
    long UnitsPerQuota,
    long? RequiredCarriers,
    long? AvailableLowerBound,
    bool OneWaveSufficient,
    string Reason);

/// <summary>单并行波交易阶段结果。</summary>
public sealed record OosTransportSingleWaveTransferResult(
    OosTransportPhaseDuration Duration,
    double? TransactionDistance,
    float? SingleLegSecondsFloat32,
    string Endpoint);

/// <summary>选中 cargo carrier holder 相对本笔交易货源和货物目的的位置。</summary>
public enum OosTransportCargoCarrierHolderRole
{
    CargoSource,
    CargoDestination
}

/// <summary>
/// 同一 holder 的同质 cargo carrier 多波 trade-complete 输入。
/// 每个布尔条件均由调用方的静态/运行时证据明确提供，不能由总 cargo drone 数量推断。
/// </summary>
public sealed record OosTransportHomogeneousCarrierWaveInput(
    long CargoUnits,
    long WareVolume,
    long CarrierCapacityVolume,
    long HolderTotal,
    long? ReservedCarrierCount,
    OosTransportCargoCarrierHolderRole CarrierHolderRole,
    double RawSurfaceSeparation,
    bool OrdinaryOosGeometry,
    bool EndpointsAreStationary,
    bool CarriersShareSelectedHolder,
    bool CarriersAreHomogeneous,
    bool CarrierStartsAreSynchronized,
    bool NoCarrierCompetition);

/// <summary>
/// 同质并行 cargo carrier 的 trade-complete 时长。它止于交易完成事件，不包含最终 ReturnUnitEvent、
/// Ready、detach 或调度 slip。
/// </summary>
public sealed record OosTransportHomogeneousCarrierWaveTradeResult(
    OosTransportPhaseDuration TradeCompleteDuration,
    OosTransportCargoCarrierHolderRole CarrierHolderRole,
    long UnitsPerQuota,
    long? RequiredCarrierQuotas,
    long? AvailableCarrierCount,
    long? WaveCount,
    double? TransactionDistance,
    float? SingleLegSecondsFloat32,
    string Conditions);

/// <summary>
/// 两个显式 carrier holder 的同质初始单波输入。两侧 reservation 必须是当前交易开始前的精确值；
/// 仅给总数不足以证明可用 carrier 数。
/// </summary>
public sealed record OosTransportHomogeneousMixedHolderSingleWaveInput(
    long CargoUnits,
    long WareVolume,
    long CarrierCapacityVolume,
    long SourceHolderTotal,
    long? SourceReservedCarrierCount,
    long DestinationHolderTotal,
    long? DestinationReservedCarrierCount,
    double RawSurfaceSeparation,
    bool OrdinaryOosGeometry,
    bool EndpointsAreStationary,
    bool CarriersAreHomogeneous,
    bool CarrierStartsAreSynchronized,
    bool NoCarrierCompetition);

/// <summary>
/// 两个 holder 混供的同质初始单波 trade-complete 结果。它只覆盖初始同步 quota，
/// 不包含 ReturnUnitEvent、可用计数恢复、Ready、detach 或调度 slip。
/// </summary>
public sealed record OosTransportHomogeneousMixedHolderSingleWaveTradeResult(
    OosTransportPhaseDuration TradeCompleteDuration,
    long UnitsPerQuota,
    long? RequiredCarrierQuotas,
    long? SourceAvailableCarrierCount,
    long? DestinationAvailableCarrierCount,
    long? SourceInitialQuotaCount,
    long? DestinationInitialQuotaCount,
    double? TransactionDistance,
    float? SingleLegSecondsFloat32,
    string Conditions);

/// <summary>
/// station 提供本波全部 cargo drone 时的装货及空返恢复阶段。
/// 两段持续时间均是 native 理想调度参考，不包含实际调度 slip。
/// </summary>
public sealed record OosTransportStationProvidedSingleWaveLoadingResult(
    OosTransportPhaseDuration LoadingDuration,
    OosTransportPhaseDuration RemainingRecoveryDuration,
    float? SingleLegSecondsFloat32,
    double? TransactionDistance);

/// <summary>普通对象 vfunc14B0 bbox 对应的脚本 size/safesize。</summary>
public sealed record OosTransportSourceBoundingSizes(float Size, float SafeSize, float Radius);

public enum OosTransportDepartureStartBoundary
{
    ExecuteTradeComplete,
    MoveUndockEntryNetworkDetached,
    ClearanceGranted
}

/// <summary>
/// execute_trade 完成后 detach_from_masstraffic 的匹配网络状态。
/// MatchingNetwork 只证明 action 会等待 0x57F；完整剩余等待仍须由调用者提供。
/// </summary>
public enum OosTransportDetachNetworkCondition
{
    MatchingNetwork,
    NoMatchingNetwork,
    Unknown
}

public sealed record OosTransportWaitTerm(string Name, double MinimumSeconds, double MaximumSeconds);

/// <summary>从一个明确起点边界开始的源站脚本等待合同。</summary>
public sealed record OosTransportDepartureWaitResult(
    OosTransportDepartureStartBoundary StartBoundary,
    OosTransportDockingSourceProfile SourceProfile,
    OosTransportPhaseDuration Duration,
    IReadOnlyList<OosTransportWaitTerm> Terms);

public sealed record OosTransportDirectHandoffResult(
    OosTransportPhaseStatus Status,
    bool NoLaunchPosition,
    bool CloselinkFinalPose,
    bool NoExitPath,
    bool ExplicitNonblockingOrder,
    string? LaunchPositionRule,
    double? LaunchGeometricDistance,
    double? SchedulerSeconds,
    string SafePositionRule,
    string HandoffPoseRule,
    string StationaryIdealization);

public enum OosTransportClearanceAction
{
    None,
    MoveToReverse,
    MoveStrafe,
    MoveToZeroTimeHandoff
}

/// <summary>capital trade-pier 在 get_safe_pos 返回后的源分支选择。</summary>
public sealed record OosTransportClearanceMovementResult(
    OosTransportPhaseStatus Status,
    string Direction,
    Vec3 LocalEvaluationPosition,
    double DistanceToSafePosition,
    bool MovementRequired,
    bool BlockingOrder,
    OosTransportClearanceAction Action,
    double? InterruptAfterSeconds,
    bool? ForcePosition,
    bool ForceRotation,
    bool AbortPath,
    bool? AvoidBigAndSmallObjects,
    string Destination,
    double? MovementSeconds,
    string EndStateRule);

/// <summary>
/// 已由 native/source 闭合的 OOS 运输终端规则。这里不包含旧进离港拟合，也不补齐调度未知量。
/// </summary>
public static class OosTransportTerminalPhaseCalculator
{
    /// <summary>
    /// MassTrafficNetworkRemovedEvent 在删除完成后才加入的 native 通知延迟。
    /// 这不是从 execute_trade 完成开始的完整 detach 等待。
    /// </summary>
    public const double MassTrafficNetworkRemovedEventNotificationSeconds = 2d;

    private static readonly HashSet<string> BlockingOrders = new(StringComparer.Ordinal)
    {
        "Wait",
        "Escort",
        "TradeRoutine",
        "TradeRoutine_Basic",
        "TradeRoutine_Advanced",
        "Middleman",
        "MiningRoutine",
        "MiningRoutine_Basic",
        "MiningRoutine_Advanced",
        "MiningRoutine_Expert"
    };

    public static OosTransportSingleWaveCapacityResult CalculateSingleWaveCapacity(
        OosTransportSingleWaveCapacityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.TransferCargoUnits);
        if (input.TransferCargoUnits.Count == 0)
            throw new ArgumentException("At least one transfer is required.", nameof(input));
        if (input.TransferCargoUnits.Any(value => value < 0) ||
            input.WareVolume < 0 ||
            input.CarrierCapacityVolume < 0 ||
            input.HolderTotal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input));
        }
        if (input.ReservedUpperBound < 0)
            throw new ArgumentOutOfRangeException(nameof(input));

        var unitsPerQuota = input.CarrierCapacityVolume / Math.Max(input.WareVolume, 1);
        if (input.TransferCargoUnits.All(value => value == 0))
        {
            return new(
                OosTransportPhaseStatus.Empty,
                unitsPerQuota,
                RequiredCarriers: 0,
                AvailableLowerBound: null,
                OneWaveSufficient: true,
                "no transfer requested");
        }

        if (unitsPerQuota == 0)
        {
            return new(
                OosTransportPhaseStatus.Unknown,
                unitsPerQuota,
                RequiredCarriers: null,
                AvailableLowerBound: null,
                OneWaveSufficient: false,
                "selected carrier cannot claim one ware unit");
        }

        long requiredCarriers = 0;
        foreach (var cargoUnits in input.TransferCargoUnits)
        {
            var requiredForTransfer = cargoUnits / unitsPerQuota;
            if (cargoUnits % unitsPerQuota != 0)
                requiredForTransfer = checked(requiredForTransfer + 1);
            requiredCarriers = checked(requiredCarriers + requiredForTransfer);
        }

        if (!input.ReservedUpperBound.HasValue)
        {
            return new(
                OosTransportPhaseStatus.Unknown,
                unitsPerQuota,
                requiredCarriers,
                AvailableLowerBound: null,
                OneWaveSufficient: false,
                "transport reservation upper bound is unresolved");
        }

        var available = Math.Max(0, input.HolderTotal - input.ReservedUpperBound.Value);
        var sufficient = requiredCarriers <= available;
        return new(
            sufficient ? OosTransportPhaseStatus.Conditional : OosTransportPhaseStatus.Unknown,
            unitsPerQuota,
            requiredCarriers,
            available,
            sufficient,
            sufficient
                ? "single parallel wave capacity proved under reservation bound"
                : "insufficient first-wave carriers; multiwave duration unresolved");
    }

    /// <summary>
    /// rawD = length(max(abs(shipLocal-center)-stationHalf, 0)) - max(shipHalf).
    /// 所有向量必须位于同一个 station-local 坐标系。
    /// </summary>
    public static double CalculateRawSurfaceSeparation(
        Vec3 shipLocalPosition,
        Vec3 stationCenter,
        Vec3 stationHalfExtents,
        Vec3 shipHalfExtents)
    {
        ValidateFinite(shipLocalPosition, nameof(shipLocalPosition));
        ValidateFinite(stationCenter, nameof(stationCenter));
        ValidateHalfExtents(stationHalfExtents, nameof(stationHalfExtents));
        ValidateHalfExtents(shipHalfExtents, nameof(shipHalfExtents));

        var outsideX = Math.Max(Math.Abs(shipLocalPosition.X - stationCenter.X) - stationHalfExtents.X, 0);
        var outsideY = Math.Max(Math.Abs(shipLocalPosition.Y - stationCenter.Y) - stationHalfExtents.Y, 0);
        var outsideZ = Math.Max(Math.Abs(shipLocalPosition.Z - stationCenter.Z) - stationHalfExtents.Z, 0);
        var centerToSurface = Math.Sqrt(outsideX * outsideX + outsideY * outsideY + outsideZ * outsideZ);
        var shipRadius = Math.Max(shipHalfExtents.X, Math.Max(shipHalfExtents.Y, shipHalfExtents.Z));
        return centerToSurface - shipRadius;
    }

    /// <summary>
    /// 按 native float32 RSQRTSS/RCPSS 算普通对象的 size 与 safesize。
    /// safesize 使用离原点最远的 bbox 角，即 abs(center)+halfExtents；center 不可丢弃。
    /// </summary>
    public static OosTransportSourceBoundingSizes CalculateSourceBoundingSizes(
        Vec3 center,
        Vec3 halfExtents)
    {
        ValidateFinite(center, nameof(center));
        ValidateHalfExtents(halfExtents, nameof(halfExtents));
        if (halfExtents.X <= 0 || halfExtents.Y <= 0 || halfExtents.Z <= 0)
            throw new ArgumentOutOfRangeException(nameof(halfExtents));
        if (!Sse.IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Native source size semantics require SSE RSQRTSS/RCPSS.");
        }

        var centerX = ToFiniteSingle(center.X, nameof(center));
        var centerY = ToFiniteSingle(center.Y, nameof(center));
        var centerZ = ToFiniteSingle(center.Z, nameof(center));
        var halfX = ToFiniteSingle(halfExtents.X, nameof(halfExtents));
        var halfY = ToFiniteSingle(halfExtents.Y, nameof(halfExtents));
        var halfZ = ToFiniteSingle(halfExtents.Z, nameof(halfExtents));

        var radius = CalculateNativeBoundingRadius(halfX, halfY, halfZ);
        var farX = MathF.Abs(centerX) + halfX;
        var farY = MathF.Abs(centerY) + halfY;
        var farZ = MathF.Abs(centerZ) + halfZ;
        var safeRadius = CalculateNativeBoundingRadius(farX, farY, farZ);
        return new(
            Size: 2f * radius,
            SafeSize: 2f * safeRadius,
            Radius: radius);
    }

    public static OosTransportSingleWaveTransferResult CalculateSingleWaveTransfer(
        double rawSurfaceSeparation,
        OosTransportSingleWaveCapacityResult capacity,
        bool ordinaryOosGeometry)
    {
        if (!double.IsFinite(rawSurfaceSeparation))
            throw new ArgumentOutOfRangeException(nameof(rawSurfaceSeparation));
        ArgumentNullException.ThrowIfNull(capacity);

        if (capacity.Status == OosTransportPhaseStatus.Empty)
        {
            return new(
                OosTransportPhaseDuration.Empty("no transfer requested"),
                TransactionDistance: null,
                SingleLegSecondsFloat32: null,
                "execute_trade completion event; scheduler slip excluded");
        }

        if (!ordinaryOosGeometry || !capacity.OneWaveSufficient)
        {
            return new(
                OosTransportPhaseDuration.Unknown(
                    "ordinary OOS geometry and single-wave eligibility required"),
                TransactionDistance: null,
                SingleLegSecondsFloat32: null,
                "execute_trade completion event; scheduler slip excluded");
        }

        var (distance, singleLeg) = CalculateTransferGeometry(rawSurfaceSeparation);
        var seconds = 2d * singleLeg + 3d;
        return new(
            OosTransportPhaseDuration.Exact(
                seconds,
                "two single-wave legs plus 2 s turnaround and 1 s completion"),
            distance,
            singleLeg,
            "execute_trade completion event; scheduler slip excluded");
    }

    /// <summary>
    /// 计算同一 holder 上 N 个同质、同时可用 cargo carrier 的 W 波交易完成时长。
    /// 该简式只适用于无混合 holder、端点静止、同容量同起点且没有其他 carrier 竞争的情景。
    /// </summary>
    public static OosTransportHomogeneousCarrierWaveTradeResult CalculateHomogeneousCarrierWaveTradeComplete(
        OosTransportHomogeneousCarrierWaveInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!Enum.IsDefined(input.CarrierHolderRole))
            throw new ArgumentOutOfRangeException(nameof(input));
        if (!double.IsFinite(input.RawSurfaceSeparation))
            throw new ArgumentOutOfRangeException(nameof(input));

        const string conditions =
            "requires ordinary OOS geometry, stationary endpoints, carriers from one selected holder only, homogeneous capacity, synchronized starts, a fixed reservation count, and no competing carrier demand; scheduler slip excluded";
        var capacity = CalculateSingleWaveCapacity(new(
            [input.CargoUnits],
            input.WareVolume,
            input.CarrierCapacityVolume,
            input.HolderTotal,
            input.ReservedCarrierCount));

        if (capacity.Status == OosTransportPhaseStatus.Empty)
        {
            return new(
                OosTransportPhaseDuration.Empty("no transfer requested"),
                input.CarrierHolderRole,
                capacity.UnitsPerQuota,
                RequiredCarrierQuotas: 0,
                AvailableCarrierCount: null,
                WaveCount: 0,
                TransactionDistance: null,
                SingleLegSecondsFloat32: null,
                conditions);
        }

        if (!input.ReservedCarrierCount.HasValue)
            return Unknown(capacity, input.CarrierHolderRole, "transport reservation count is unresolved");
        if (capacity.UnitsPerQuota == 0)
            return Unknown(capacity, input.CarrierHolderRole, "selected carrier cannot claim one ware unit");
        if (capacity.AvailableLowerBound is not long available || available == 0)
            return Unknown(capacity, input.CarrierHolderRole, "no cargo carriers are available after reservations");

        var unmetConditions = new List<string>();
        if (!input.OrdinaryOosGeometry)
            unmetConditions.Add("ordinary OOS geometry is unresolved");
        if (!input.EndpointsAreStationary)
            unmetConditions.Add("endpoint motion is unresolved");
        if (!input.CarriersShareSelectedHolder)
            unmetConditions.Add("mixed carrier holders are unresolved");
        if (!input.CarriersAreHomogeneous)
            unmetConditions.Add("carrier capacity or leg-time homogeneity is unresolved");
        if (!input.CarrierStartsAreSynchronized)
            unmetConditions.Add("carrier start synchronization is unresolved");
        if (!input.NoCarrierCompetition)
            unmetConditions.Add("other carrier demand is unresolved");
        if (unmetConditions.Count > 0)
            return Unknown(capacity, input.CarrierHolderRole, string.Join("; ", unmetConditions));

        var requiredQuotas = capacity.RequiredCarriers!.Value;
        var waveCount = DivideRoundUp(requiredQuotas, available);
        var (distance, singleLeg) = CalculateTransferGeometry(input.RawSurfaceSeparation);
        var lastWaveStart = (waveCount - 1d) * (2d * singleLeg + 4d);
        var seconds = input.CarrierHolderRole switch
        {
            OosTransportCargoCarrierHolderRole.CargoSource => lastWaveStart + singleLeg + 1d,
            OosTransportCargoCarrierHolderRole.CargoDestination => lastWaveStart + 2d * singleLeg + 3d,
            _ => throw new ArgumentOutOfRangeException(nameof(input))
        };
        if (!double.IsFinite(seconds))
            return Unknown(capacity, input.CarrierHolderRole, "trade-complete duration exceeds finite numeric range", available, waveCount);

        var formula = input.CarrierHolderRole == OosTransportCargoCarrierHolderRole.CargoSource
            ? "(W-1)*(2L+4)+L+1"
            : "(W-1)*(2L+4)+2L+3";
        return new(
            OosTransportPhaseDuration.Exact(
                seconds,
                $"native ideal homogeneous carrier trade-complete duration {formula}; final ReturnUnitEvent +0.1, Ready, detach, and scheduler slip excluded"),
            input.CarrierHolderRole,
            capacity.UnitsPerQuota,
            requiredQuotas,
            available,
            waveCount,
            distance,
            singleLeg,
            conditions);
    }

    /// <summary>
    /// 计算货源与货物目的各自 holder 混供的同质初始单波交易完成时长。
    /// 初始只按可用数选择较大侧（相等时货源优先），之后在另一侧仍有候选时严格交替。
    /// K 超出两侧可用数之和时，后续多波的精确 holder/尾批归属不在本 API 范围内。
    /// </summary>
    public static OosTransportHomogeneousMixedHolderSingleWaveTradeResult
        CalculateHomogeneousMixedHolderSingleWaveTradeComplete(
            OosTransportHomogeneousMixedHolderSingleWaveInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!double.IsFinite(input.RawSurfaceSeparation))
            throw new ArgumentOutOfRangeException(nameof(input));
        if (input.CargoUnits < 0 || input.WareVolume < 0 || input.CarrierCapacityVolume < 0 ||
            input.SourceHolderTotal < 0 || input.DestinationHolderTotal < 0 ||
            input.SourceReservedCarrierCount < 0 || input.DestinationReservedCarrierCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input));
        }

        const string conditions =
            "requires ordinary OOS geometry, stationary endpoints, homogeneous carrier capacity and leg times, synchronized initial starts, exact reservation counts for both holders, and no competing carrier demand; ReturnUnitEvent, Ready, detach, and scheduler slip excluded";

        var sourceCapacity = CalculateSingleWaveCapacity(new(
            [input.CargoUnits],
            input.WareVolume,
            input.CarrierCapacityVolume,
            input.SourceHolderTotal,
            input.SourceReservedCarrierCount));
        var unitsPerQuota = sourceCapacity.UnitsPerQuota;

        if (input.CargoUnits == 0)
        {
            return new(
                OosTransportPhaseDuration.Empty("no transfer requested"),
                unitsPerQuota,
                RequiredCarrierQuotas: 0,
                SourceAvailableCarrierCount: null,
                DestinationAvailableCarrierCount: null,
                SourceInitialQuotaCount: 0,
                DestinationInitialQuotaCount: 0,
                TransactionDistance: null,
                SingleLegSecondsFloat32: null,
                conditions);
        }

        long? requiredQuotas = unitsPerQuota == 0
            ? null
            : DivideRoundUp(input.CargoUnits, unitsPerQuota);
        var sourceAvailable = GetExactAvailableCarrierCount(
            input.SourceHolderTotal,
            input.SourceReservedCarrierCount);
        var destinationAvailable = GetExactAvailableCarrierCount(
            input.DestinationHolderTotal,
            input.DestinationReservedCarrierCount);

        if (unitsPerQuota == 0)
        {
            return UnknownMixedHolderSingleWave(
                unitsPerQuota, requiredQuotas, sourceAvailable, destinationAvailable,
                "selected carrier cannot claim one ware unit");
        }

        if (!input.SourceReservedCarrierCount.HasValue || !input.DestinationReservedCarrierCount.HasValue)
        {
            return UnknownMixedHolderSingleWave(
                unitsPerQuota, requiredQuotas, sourceAvailable, destinationAvailable,
                "exact reservation count is unresolved for one or both holders");
        }

        if (sourceAvailable is null || destinationAvailable is null)
        {
            return UnknownMixedHolderSingleWave(
                unitsPerQuota, requiredQuotas, sourceAvailable, destinationAvailable,
                "reserved carrier count exceeds its holder total");
        }

        var unmetConditions = new List<string>();
        if (!input.OrdinaryOosGeometry)
            unmetConditions.Add("ordinary OOS geometry is unresolved");
        if (!input.EndpointsAreStationary)
            unmetConditions.Add("endpoint motion is unresolved");
        if (!input.CarriersAreHomogeneous)
            unmetConditions.Add("carrier capacity or leg-time homogeneity is unresolved");
        if (!input.CarrierStartsAreSynchronized)
            unmetConditions.Add("initial carrier start synchronization is unresolved");
        if (!input.NoCarrierCompetition)
            unmetConditions.Add("other carrier demand is unresolved");
        if (unmetConditions.Count > 0)
        {
            return UnknownMixedHolderSingleWave(
                unitsPerQuota, requiredQuotas, sourceAvailable, destinationAvailable,
                string.Join("; ", unmetConditions));
        }

        var totalAvailable = checked(sourceAvailable.Value + destinationAvailable.Value);
        if (requiredQuotas!.Value > totalAvailable)
        {
            return UnknownMixedHolderSingleWave(
                unitsPerQuota, requiredQuotas, sourceAvailable, destinationAvailable,
                "initial carrier quotas exceed the combined available holders; mixed-holder multiwave scheduling is unresolved");
        }

        var sourceFirst = sourceAvailable.Value >= destinationAvailable.Value;
        var firstAvailable = sourceFirst ? sourceAvailable.Value : destinationAvailable.Value;
        var otherAvailable = sourceFirst ? destinationAvailable.Value : sourceAvailable.Value;
        var otherQuotaCount = Math.Min(otherAvailable, requiredQuotas.Value / 2);
        var firstQuotaCount = requiredQuotas.Value - otherQuotaCount;
        if (firstQuotaCount > firstAvailable)
        {
            return UnknownMixedHolderSingleWave(
                unitsPerQuota, requiredQuotas, sourceAvailable, destinationAvailable,
                "initial alternating allocation cannot be satisfied by the selected holder counts");
        }

        var sourceQuotaCount = sourceFirst ? firstQuotaCount : otherQuotaCount;
        var destinationQuotaCount = sourceFirst ? otherQuotaCount : firstQuotaCount;
        var (distance, singleLeg) = CalculateTransferGeometry(input.RawSurfaceSeparation);
        var seconds = destinationQuotaCount > 0
            ? 2d * singleLeg + 3d
            : singleLeg + 1d;
        if (!double.IsFinite(seconds))
        {
            return UnknownMixedHolderSingleWave(
                unitsPerQuota, requiredQuotas, sourceAvailable, destinationAvailable,
                "trade-complete duration exceeds finite numeric range");
        }

        var formula = destinationQuotaCount > 0 ? "2L+3" : "L+1";
        return new(
            OosTransportPhaseDuration.Exact(
                seconds,
                $"native ideal homogeneous mixed-holder initial single-wave trade-complete duration {formula}; ReturnUnitEvent +0.1, Ready, detach, and scheduler slip excluded"),
            unitsPerQuota,
            requiredQuotas,
            sourceAvailable,
            destinationAvailable,
            sourceQuotaCount,
            destinationQuotaCount,
            distance,
            singleLeg,
            conditions);
    }

    /// <summary>
    /// 计算 station 提供全部 cargo drone 的单波装货阶段。
    /// LoadingDuration 从 load execute 开始至其完成，为 L + 1；
    /// RemainingRecoveryDuration 从该完成事件至 0x57F ready 通知，为 L + 3。
    /// 两段不重叠，不能把空返重复计入两次；数值只是排除调度 slip 的 native 理想参考。
    /// </summary>
    public static OosTransportStationProvidedSingleWaveLoadingResult CalculateStationProvidedSingleWaveLoading(
        double rawSurfaceSeparation,
        OosTransportSingleWaveCapacityResult capacity,
        bool ordinaryOosGeometry,
        bool stationProvidesAllCarriers)
    {
        ArgumentNullException.ThrowIfNull(capacity);

        var transfer = CalculateSingleWaveTransfer(
            rawSurfaceSeparation,
            capacity,
            ordinaryOosGeometry);
        if (transfer.Duration.Status == OosTransportPhaseStatus.Empty)
        {
            return new(
                OosTransportPhaseDuration.Empty("no loading requested"),
                OosTransportPhaseDuration.Empty("no loading recovery requested"),
                SingleLegSecondsFloat32: null,
                TransactionDistance: null);
        }

        if (!stationProvidesAllCarriers || transfer.Duration.Status == OosTransportPhaseStatus.Unknown)
        {
            const string reason = "ordinary OOS geometry, single-wave eligibility, and station-provided carriers required";
            return new(
                OosTransportPhaseDuration.Unknown(reason),
                OosTransportPhaseDuration.Unknown(reason),
                SingleLegSecondsFloat32: null,
                TransactionDistance: null);
        }

        var singleLeg = transfer.SingleLegSecondsFloat32!.Value;
        return new(
            OosTransportPhaseDuration.Exact(
                singleLeg + 1d,
                "native ideal load execute duration L + 1; scheduler slip excluded"),
            OosTransportPhaseDuration.Exact(
                singleLeg + 3d,
                "native ideal empty-return recovery from load completion to 0x57F L + 3; scheduler slip excluded"),
            singleLeg,
            transfer.TransactionDistance);
    }

    public static OosTransportPhaseDuration CreateLandingPhase(OosTransportDockingSourceProfile sourceProfile)
    {
        ValidateProfile(sourceProfile);
        return OosTransportPhaseDuration.Exact(
            sourceProfile.LandingSeconds,
            $"landing source profile {sourceProfile.Id}");
    }

    public static OosTransportDepartureWaitResult CalculateDepartureWait(
        OosTransportDepartureStartBoundary startBoundary,
        OosTransportDockingSourceProfile sourceProfile,
        OosTransportDetachNetworkCondition detachNetworkCondition = OosTransportDetachNetworkCondition.Unknown,
        OosTransportPhaseDuration? matchingNetworkWait = null)
    {
        ValidateProfile(sourceProfile);
        var terms = new List<OosTransportWaitTerm>();
        OosTransportPhaseDuration? requiredMatchingNetworkWait = null;
        switch (startBoundary)
        {
            case OosTransportDepartureStartBoundary.ExecuteTradeComplete:
                switch (detachNetworkCondition)
                {
                    case OosTransportDetachNetworkCondition.MatchingNetwork:
                        requiredMatchingNetworkWait = matchingNetworkWait;
                        break;
                    case OosTransportDetachNetworkCondition.NoMatchingNetwork:
                        break;
                    case OosTransportDetachNetworkCondition.Unknown:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(detachNetworkCondition));
                }

                terms.Add(new("tradePostWait", 1, 3));
                break;
            case OosTransportDepartureStartBoundary.MoveUndockEntryNetworkDetached:
            case OosTransportDepartureStartBoundary.ClearanceGranted:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(startBoundary));
        }

        terms.Add(new("undockPreWait", 0.001, 0.5));
        terms.Add(new("takeoffProfile", sourceProfile.TakeoffSeconds, sourceProfile.TakeoffSeconds));
        terms.Add(new("undockPostWait", 1, 3));

        if (startBoundary == OosTransportDepartureStartBoundary.ExecuteTradeComplete &&
            detachNetworkCondition == OosTransportDetachNetworkCondition.Unknown)
        {
            return new(
                startBoundary,
                sourceProfile,
                OosTransportPhaseDuration.Unknown(
                    "matching mass-traffic detach network is unresolved; native event wait is unknown"),
                terms);
        }

        var minimum = terms.Sum(term => term.MinimumSeconds);
        var maximum = terms.Sum(term => term.MaximumSeconds);
        var midpoint = (minimum + maximum) / 2d;
        var scriptWait = OosTransportPhaseDuration.Range(
            minimum,
            maximum,
            midpoint,
            isReferenceStatisticalMean: false,
            "XML wait bounds; midpoint is only a reference choice");

        if (startBoundary == OosTransportDepartureStartBoundary.ExecuteTradeComplete &&
            detachNetworkCondition == OosTransportDetachNetworkCondition.MatchingNetwork)
        {
            if (requiredMatchingNetworkWait is null)
            {
                return new(
                    startBoundary,
                    sourceProfile,
                    OosTransportPhaseDuration.Unknown(
                        "matching mass-traffic network requires its complete remaining wait from this boundary"),
                    terms);
            }

            if (requiredMatchingNetworkWait.MinimumSeconds.HasValue &&
                requiredMatchingNetworkWait.MaximumSeconds.HasValue)
            {
                terms.Insert(0, new(
                    "matchingNetworkWait",
                    requiredMatchingNetworkWait.MinimumSeconds.Value,
                    requiredMatchingNetworkWait.MaximumSeconds.Value));
            }

            return new(
                startBoundary,
                sourceProfile,
                ComposeRequiredPhases(new[] { requiredMatchingNetworkWait!, scriptWait }),
                terms);
        }

        return new(
            startBoundary,
            sourceProfile,
            scriptWait,
            terms);
    }

    public static OosTransportDirectHandoffResult EvaluateDirectHandoff(
        bool launchPositionConnectionPresent,
        bool shipAndDockHaveCloselinks,
        bool exitPathPresent,
        string? nextOrder,
        string? defaultOrder)
    {
        var order = nextOrder ?? defaultOrder;
        var noLaunchPosition = !launchPositionConnectionPresent;
        var noExitPath = !exitPathPresent;
        var nonblockingOrder = !string.IsNullOrEmpty(order) && !BlockingOrders.Contains(order);
        var eligible = noLaunchPosition && shipAndDockHaveCloselinks && noExitPath && nonblockingOrder;

        return new(
            eligible ? OosTransportPhaseStatus.Conditional : OosTransportPhaseStatus.Unknown,
            noLaunchPosition,
            shipAndDockHaveCloselinks,
            noExitPath,
            nonblockingOrder,
            eligible ? "source dock final position" : null,
            eligible ? 0 : null,
            SchedulerSeconds: null,
            "unresolved; zero-time interrupted target is not reached",
            "continuous state after launch/controller update",
            "P_handoff=P_final, v=0 only under zero-dispatch-motion scenario");
    }

    public static OosTransportClearanceMovementResult CalculateClearanceMovement(
        Vec3 shipPosition,
        Vec3 shipAxisX,
        Vec3 shipAxisY,
        Vec3 shipAxisZ,
        Vec3 oldDockPosition,
        Vec3 safePosition,
        double shipSize,
        string? nextOrder = null,
        string? defaultOrder = null,
        bool exitPathPresent = false,
        bool inHighway = false)
    {
        ValidateFinite(shipPosition, nameof(shipPosition));
        ValidateFinite(shipAxisX, nameof(shipAxisX));
        ValidateFinite(shipAxisY, nameof(shipAxisY));
        ValidateFinite(shipAxisZ, nameof(shipAxisZ));
        ValidateFinite(oldDockPosition, nameof(oldDockPosition));
        ValidateFinite(safePosition, nameof(safePosition));
        if (!double.IsFinite(shipSize) || shipSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(shipSize));

        var safeMinusDock = new Vec3(
            safePosition.X - oldDockPosition.X,
            safePosition.Y - oldDockPosition.Y,
            safePosition.Z - oldDockPosition.Z);
        var local = new Vec3(
            Dot(safeMinusDock, shipAxisX),
            Dot(safeMinusDock, shipAxisY),
            Dot(safeMinusDock, shipAxisZ));
        var reverse = local.Z < local.X && local.Z < -local.X;
        var distance = shipPosition.DistanceTo(safePosition);
        var order = nextOrder ?? defaultOrder;
        var blocking = exitPathPresent || (order is not null && BlockingOrders.Contains(order));
        var movementRequired = distance > shipSize / 2d && !inHighway;
        var action = !movementRequired
            ? OosTransportClearanceAction.None
            : !blocking
                ? OosTransportClearanceAction.MoveToZeroTimeHandoff
                : reverse
                    ? OosTransportClearanceAction.MoveToReverse
                    : OosTransportClearanceAction.MoveStrafe;
        double? interruptAfterSeconds = action switch
        {
            OosTransportClearanceAction.MoveToZeroTimeHandoff => 0,
            OosTransportClearanceAction.MoveToReverse => 30,
            _ => null
        };

        return new(
            OosTransportPhaseStatus.Conditional,
            reverse ? "reverse" : "side",
            local,
            distance,
            movementRequired,
            blocking,
            action,
            interruptAfterSeconds,
            action == OosTransportClearanceAction.MoveStrafe ? null : false,
            action == OosTransportClearanceAction.MoveStrafe,
            AbortPath: false,
            action == OosTransportClearanceAction.MoveToReverse ? false : null,
            action == OosTransportClearanceAction.MoveToReverse ? "old dock container" : "zone",
            movementRequired ? null : 0,
            "handoff actual continuous pose and velocity, including timeout state");
    }

    /// <summary>
    /// 组合明确分界后的源站、途中和目的站阶段。调用者负责保证阶段的几何范围不重叠。
    /// </summary>
    public static OosTransportPhaseDuration ComposeOneWay(
        OosTransportPhaseDuration sourceTerminalPhase,
        OosTransportPhaseDuration routeFlightPhase,
        OosTransportPhaseDuration targetTerminalPhase)
    {
        ArgumentNullException.ThrowIfNull(sourceTerminalPhase);
        ArgumentNullException.ThrowIfNull(routeFlightPhase);
        ArgumentNullException.ThrowIfNull(targetTerminalPhase);
        return ComposeRequiredPhases(
            new[] { sourceTerminalPhase, routeFlightPhase, targetTerminalPhase });
    }

    /// <summary>组合所有必需阶段；任意 Unknown 都使总耗时 Unknown。</summary>
    public static OosTransportPhaseDuration ComposeRequiredPhases(
        IEnumerable<OosTransportPhaseDuration> requiredPhases)
    {
        ArgumentNullException.ThrowIfNull(requiredPhases);
        var phases = requiredPhases.ToArray();
        if (phases.Length == 0)
            return OosTransportPhaseDuration.Empty("no required phases");
        if (phases.Any(phase => phase is null))
            throw new ArgumentException("Required phases cannot contain null.", nameof(requiredPhases));
        ValidateDurations(phases!);

        var unknownReasons = phases
            .Where(phase => phase.Status == OosTransportPhaseStatus.Unknown)
            .Select(phase => phase.Reason)
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .ToArray();
        if (unknownReasons.Length > 0)
            return OosTransportPhaseDuration.Unknown(string.Join("; ", unknownReasons));

        var minimum = phases.Sum(phase => phase.MinimumSeconds!.Value);
        var maximum = phases.Sum(phase => phase.MaximumSeconds!.Value);
        var reference = phases.All(phase => phase.ReferenceSeconds.HasValue)
            ? phases.Sum(phase => phase.ReferenceSeconds!.Value)
            : (double?)null;
        if (!reference.HasValue)
            return OosTransportPhaseDuration.Unknown("a required phase has no reference duration");

        var allEmpty = phases.All(phase => phase.Status == OosTransportPhaseStatus.Empty);
        if (allEmpty)
            return OosTransportPhaseDuration.Empty("all required phases are empty");

        var isStatisticalMean = phases.All(phase => phase.IsReferenceStatisticalMean);
        return OosTransportPhaseDuration.Range(
            minimum,
            maximum,
            reference.Value,
            isStatisticalMean,
            "sum of all required phases without intermediate rounding");
    }

    private static void ValidateDurations(IEnumerable<OosTransportPhaseDuration> phases)
    {
        foreach (var phase in phases)
        {
            if (phase.Status == OosTransportPhaseStatus.Unknown)
            {
                if (phase.MinimumSeconds.HasValue || phase.MaximumSeconds.HasValue || phase.ReferenceSeconds.HasValue)
                    throw new ArgumentException("Unknown phases cannot carry duration values.");
                continue;
            }

            if (!phase.MinimumSeconds.HasValue || !phase.MaximumSeconds.HasValue || !phase.ReferenceSeconds.HasValue ||
                !double.IsFinite(phase.MinimumSeconds.Value) || phase.MinimumSeconds.Value < 0 ||
                !double.IsFinite(phase.MaximumSeconds.Value) || phase.MaximumSeconds.Value < phase.MinimumSeconds.Value ||
                !double.IsFinite(phase.ReferenceSeconds.Value) ||
                phase.ReferenceSeconds.Value < phase.MinimumSeconds.Value ||
                phase.ReferenceSeconds.Value > phase.MaximumSeconds.Value)
            {
                throw new ArgumentException("Known phase durations must be finite, nonnegative, and ordered.");
            }
        }
    }

    private static void ValidateProfile(OosTransportDockingSourceProfile sourceProfile)
    {
        ArgumentNullException.ThrowIfNull(sourceProfile);
        if (string.IsNullOrWhiteSpace(sourceProfile.Id))
            throw new ArgumentException("A source profile id is required.", nameof(sourceProfile));
        if (!double.IsFinite(sourceProfile.LandingSeconds) || sourceProfile.LandingSeconds < 0 ||
            !double.IsFinite(sourceProfile.TakeoffSeconds) || sourceProfile.TakeoffSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceProfile));
        }
    }

    private static void ValidateFinite(Vec3 value, string parameterName)
    {
        if (!double.IsFinite(value.X) || !double.IsFinite(value.Y) || !double.IsFinite(value.Z))
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void ValidateHalfExtents(Vec3 value, string parameterName)
    {
        ValidateFinite(value, parameterName);
        if (value.X < 0 || value.Y < 0 || value.Z < 0)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static double Dot(Vec3 left, Vec3 right) =>
        left.X * right.X + left.Y * right.Y + left.Z * right.Z;

    private static OosTransportHomogeneousCarrierWaveTradeResult Unknown(
        OosTransportSingleWaveCapacityResult capacity,
        OosTransportCargoCarrierHolderRole carrierHolderRole,
        string reason,
        long? availableCarrierCount = null,
        long? waveCount = null) =>
        new(
            OosTransportPhaseDuration.Unknown(reason),
            carrierHolderRole,
            capacity.UnitsPerQuota,
            capacity.RequiredCarriers,
            availableCarrierCount ?? capacity.AvailableLowerBound,
            waveCount,
            null,
            null,
            "requires ordinary OOS geometry, stationary endpoints, carriers from one selected holder only, homogeneous capacity, synchronized starts, a fixed reservation count, and no competing carrier demand; scheduler slip excluded");

    private static OosTransportHomogeneousMixedHolderSingleWaveTradeResult UnknownMixedHolderSingleWave(
        long unitsPerQuota,
        long? requiredQuotas,
        long? sourceAvailable,
        long? destinationAvailable,
        string reason) =>
        new(
            OosTransportPhaseDuration.Unknown(reason),
            unitsPerQuota,
            requiredQuotas,
            sourceAvailable,
            destinationAvailable,
            SourceInitialQuotaCount: null,
            DestinationInitialQuotaCount: null,
            TransactionDistance: null,
            SingleLegSecondsFloat32: null,
            "requires ordinary OOS geometry, stationary endpoints, homogeneous carrier capacity and leg times, synchronized initial starts, exact reservation counts for both holders, and no competing carrier demand; ReturnUnitEvent, Ready, detach, and scheduler slip excluded");

    private static long? GetExactAvailableCarrierCount(long holderTotal, long? reservedCarrierCount)
    {
        if (!reservedCarrierCount.HasValue || reservedCarrierCount.Value > holderTotal)
            return null;
        return holderTotal - reservedCarrierCount.Value;
    }

    private static (double Distance, float SingleLeg) CalculateTransferGeometry(double rawSurfaceSeparation)
    {
        var distance = rawSurfaceSeparation <= 0 ? 420d : Math.Min(rawSurfaceSeparation, 20_000d);
        return (distance, (float)(distance / 20d));
    }

    private static long DivideRoundUp(long dividend, long divisor)
    {
        if (dividend < 0 || divisor <= 0)
            throw new ArgumentOutOfRangeException(dividend < 0 ? nameof(dividend) : nameof(divisor));
        return dividend == 0 ? 0 : checked(1 + (dividend - 1) / divisor);
    }

    private static float CalculateNativeBoundingRadius(float x, float y, float z)
    {
        var xSquared = x * x;
        var ySquared = y * y;
        var zSquared = z * z;
        var yzSquared = ySquared + zSquared;
        var sum = xSquared + yzSquared;
        return Sse.ReciprocalScalar(
            Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(sum))).ToScalar();
    }

    private static float ToFiniteSingle(double value, string parameterName)
    {
        var result = (float)value;
        if (!float.IsFinite(result))
            throw new ArgumentOutOfRangeException(parameterName);
        return result;
    }
}
