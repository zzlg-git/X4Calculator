using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

/// <summary>
/// <c>move.gate.xml</c> 的 capital-gate 入口横向二选一。它表示脚本
/// <c>[-maxoffset, maxoffset].random</c> 已求值后的条件情景，不推断 TLS seed 或情景权重。
/// 脚本在此之前还会求值一个随后未被 capital 分支使用的 <c>$randomoffsetx</c>；需要延续原生
/// RNG 状态的调用方仍须在外部保留那次 draw，本几何 API 不擅自推进未知 VM 随机流。
/// </summary>
public enum OosGateEntryLateralSide { Negative, Positive }

/// <summary>
/// 入口 <c>get_safe_pos</c> 的完整性声明。当前可计算范围是没有命中其它约束，或仅命中一个
/// 已确认 OBB；旧 reservation 的取消和新 reservation 的生成/后处理都必须已解析。
/// </summary>
public sealed record OosGateEntrySafePointConditions(
    bool ConstraintSetComplete,
    bool PreviousReservationCancellationResolved,
    bool NewReservationSucceeded,
    bool PreferredClearHasNoEffect,
    bool ReservationHasNoEffect,
    X4RigidTransform? ObstacleTransform,
    X4AxisAlignedBounds? ObstacleBounds);

/// <summary>
/// capital ship 距门不超过脚本严格阈值时不创建入口点；否则 <see cref="Target"/> 非空。
/// </summary>
public sealed record OosGateEntryPreparation(
    bool RequiresApproach,
    float ApproachThreshold,
    float MaximumLateralOffset,
    OosGateEntryTarget? Target);

/// <summary>
/// 已求值的门入口目标。BasePosition/SafePosition 位于调用方提供的同一扇区坐标系；
/// ReservationLookDirection 保留 spacereservation 的 <c>look_at gate</c> 几何输入，
/// 不把 reservation 姿态冒充为 <c>move_to</c> 的 Linear FCM 目标姿态。
/// </summary>
public sealed record OosGateEntryTarget(
    Vec3 BasePosition,
    Vec3 SafePosition,
    Vec3 SafeDirection,
    float SafeRadius,
    float RandomOffsetY,
    OosGateEntryLateralSide LateralSide,
    bool Collided,
    Vec3 ReservationLookDirection);

/// <summary>
/// X4 9.00 <c>move.gate.xml:151-200</c> 的普通单船 capital-gate 入口几何。
/// 本类型只生成条件点和预约朝向输入，不加载存档/XML、不分配 reservation，也不添加脚本或运动时长。
/// </summary>
public static class OosTransportGateEntryGeometry
{
    public static OosGateEntryPreparation PrepareCapitalEntry(
        X4RigidTransform shipInSector,
        X4RigidTransform gateInSector,
        float shipDistanceToGate,
        float shipSize,
        float shipSafeSize,
        float gateSize,
        float randomOffsetY,
        OosGateEntryLateralSide lateralSide,
        OosGateEntrySafePointConditions conditions,
        bool ordinarySuccessfulSingleCapitalGate)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        if (!ordinarySuccessfulSingleCapitalGate)
            throw new NotSupportedException("Only successful ordinary single-capital-ship gate approaches are supported.");

        Positive(shipSize, nameof(shipSize));
        Positive(shipSafeSize, nameof(shipSafeSize));
        Positive(gateSize, nameof(gateSize));
        NonNegative(shipDistanceToGate, nameof(shipDistanceToGate));
        ValidateTransform(shipInSector, nameof(shipInSector));
        ValidateTransform(gateInSector, nameof(gateInSector));

        var threshold = F(10_000f + F(shipSize / 2f));
        var maximumOffset = MathF.Min(F(shipSize * 2f),
            F(F(MathF.Max(gateSize, shipSize) / 2f) - F(shipSize / 2f)));
        if (!float.IsFinite(threshold) || !float.IsFinite(maximumOffset) || maximumOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(shipSize));

        // XML 使用严格大于条件 'gt'；相等时舰船直接穿过，不创建入口点或预约。
        if (shipDistanceToGate <= threshold)
            return new(false, threshold, maximumOffset, null);

        ValidateConditions(conditions);
        if (!float.IsFinite(randomOffsetY) || randomOffsetY < -maximumOffset || randomOffsetY > maximumOffset)
            throw new ArgumentOutOfRangeException(nameof(randomOffsetY),
                "The evaluated XML random Y offset must be inside [-maxoffset, maxoffset].");

        var localX = lateralSide switch
        {
            OosGateEntryLateralSide.Negative => -maximumOffset,
            OosGateEntryLateralSide.Positive => maximumOffset,
            _ => throw new ArgumentOutOfRangeException(nameof(lateralSide))
        };
        // 参数为 object=gate、x=[-maxoffset,maxoffset].random、y=$randomoffsety；省略的 z 保持为零。
        var basePosition = Round(gateInSector.TransformPoint(new Vec3(localX, randomOffsetY, 0)));
        // directionobject=ship 且未显式指定 direction 时，方向为基准点指向舰船（遵循 common.xsd 的 directionobject 契约）。
        var safeDirection = Sub(Round(shipInSector.Position), basePosition);
        var safeRadius = F(shipSafeSize / 2f);
        var safe = new OosSafeTarget(basePosition, false);

        if (conditions.ObstacleTransform is { } obstacle && conditions.ObstacleBounds is { } bounds)
        {
            ValidateTransform(obstacle, nameof(conditions));
            if (IsZero(safeDirection))
                throw new NotSupportedException("The directionobject fallback is zero while constraint correction is required.");
            safe = OosTransportArrivalGeometry.SafePoint(
                basePosition,
                obstacle.TransformPoint(bounds.Center),
                bounds.HalfExtents,
                safeDirection,
                safeRadius,
                obstacle.Rotation,
                noOtherConstraints: true);
        }

        var reservationLookDirection = Sub(Round(gateInSector.Position), safe.Position);
        if (IsZero(reservationLookDirection))
            throw new NotSupportedException("The reservation look-at orientation is undefined at the gate origin.");
        return new(true, threshold, maximumOffset,
            new(basePosition, safe.Position, safeDirection, safeRadius, randomOffsetY,
                lateralSide, safe.Collided, reservationLookDirection));
    }

    private static void ValidateConditions(OosGateEntrySafePointConditions conditions)
    {
        if (!conditions.ConstraintSetComplete ||
            !conditions.PreviousReservationCancellationResolved ||
            !conditions.NewReservationSucceeded ||
            !conditions.PreferredClearHasNoEffect ||
            !conditions.ReservationHasNoEffect)
            throw new NotSupportedException(
                "Gate-entry constraints, previous/new reservation effects and preferred-clear behavior must be resolved.");
        if ((conditions.ObstacleTransform is null) != (conditions.ObstacleBounds is null))
            throw new ArgumentException("Obstacle transform and bounds must be supplied together.", nameof(conditions));
        if (conditions.ObstacleBounds is { } bounds)
        {
            var half = Round(bounds.HalfExtents);
            if (half.X < 0 || half.Y < 0 || half.Z < 0)
                throw new ArgumentOutOfRangeException(nameof(conditions), "Obstacle half extents must be non-negative.");
        }
    }

    private static Vec3 Sub(Vec3 left, Vec3 right) => Round(new(
        F((float)left.X - (float)right.X),
        F((float)left.Y - (float)right.Y),
        F((float)left.Z - (float)right.Z)));

    private static bool IsZero(Vec3 value) => value.X == 0 && value.Y == 0 && value.Z == 0;

    private static float F(float value) => value;

    private static void Positive(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void NonNegative(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static Vec3 Round(Vec3 value)
    {
        var result = new Vec3((float)value.X, (float)value.Y, (float)value.Z);
        if (!double.IsFinite(result.X) || !double.IsFinite(result.Y) || !double.IsFinite(result.Z))
            throw new ArgumentOutOfRangeException(nameof(value));
        return result;
    }

    private static void ValidateTransform(X4RigidTransform transform, string parameterName)
    {
        Round(transform.Position);
        var r = transform.Rotation;
        Round(r.Row0);
        Round(r.Row1);
        Round(r.Row2);
        var product = r * r.Transpose();
        var rows = new[] { product.Row0, product.Row1, product.Row2 };
        var expected = new[] { new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1) };
        if (rows.Where((row, index) => row.DistanceTo(expected[index]) > 1e-5).Any())
            throw new ArgumentException("Rotation must be orthonormal.", parameterName);
    }
}
