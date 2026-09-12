using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

public enum OosGateExitSide { Up, Down, Left, Right }

/// <summary>完整约束已排除，或仅一个已确认 OBB；不是视觉门盒的自动替代。</summary>
public sealed record OosGateSafePointConditions(bool ConstraintSetComplete,
    bool PreferredClearHasNoEffect, bool ReservationHasNoEffect,
    X4RigidTransform? ObstacleTransform, X4AxisAlignedBounds? ObstacleBounds);

/// <summary>保留 XML 两个不同 distanceto 表达式的实际结果，不把 list 求值猜成坐标距离。</summary>
public sealed record OosGateNextPositionCondition(bool ContextKnown, bool InDestinationContext,
    float? DistanceToPositionExpression = null, float? DistanceToListExpression = null);

public sealed record OosGateExitTargets(Vec3 IntermediatePosition, Vec3 BasePosition,
    Vec3 SafePosition, Vec3 SafeDirection, bool Collided, OosGateExitSide Side);

/// <summary>成功普通单船过门的出口目标几何；不选择随机权重，不提供脚本时长或运动姿态。</summary>
public static class OosTransportGateGeometry
{
    public static OosGateExitTargets PrepareExit(X4RigidTransform shipInDestination,
        X4RigidTransform exitGateInDestination, float shipLength, float shipSafeSize,
        float exitGateSize, OosGateExitSide side, OosGateNextPositionCondition nextPosition,
        OosGateSafePointConditions conditions, bool ordinarySuccessfulSingleShip)
    {
        if (!ordinarySuccessfulSingleShip)
            throw new NotSupportedException("Only successful ordinary single-ship gate exits are supported.");
        if (!conditions.ConstraintSetComplete || !conditions.PreferredClearHasNoEffect || !conditions.ReservationHasNoEffect)
            throw new NotSupportedException("Gate constraints, preferred-clear and reservation effects must be resolved.");
        if ((conditions.ObstacleTransform is null) != (conditions.ObstacleBounds is null))
            throw new ArgumentException("Obstacle transform and bounds must be supplied together.", nameof(conditions));
        if (!nextPosition.ContextKnown)
            throw new NotSupportedException("Next position context is unresolved.");
        Positive(shipLength); Positive(shipSafeSize); Positive(exitGateSize);
        ValidateTransform(shipInDestination); ValidateTransform(exitGateInDestination);
        var distance = MathF.Max(7f * shipLength, exitGateSize / 2f);
        if (!float.IsFinite(distance)) throw new ArgumentOutOfRangeException(nameof(shipLength));
        if (nextPosition.InDestinationContext)
        {
            var comparison = RequireDistance(nextPosition.DistanceToPositionExpression);
            if (comparison < distance)
                distance = RequireDistance(nextPosition.DistanceToListExpression) / 2f;
        }
        var basePosition = F(shipInDestination.TransformPoint(new(0, 0, distance)));
        var intermediate = F(shipInDestination.TransformPoint(new(0, 0, 2f * shipLength)));
        var diagonal = side switch
        {
            OosGateExitSide.Up => new Vec3(0, 1, 1),
            OosGateExitSide.Down => new Vec3(0, -1, 1),
            OosGateExitSide.Left => new Vec3(-1, 0, 1),
            OosGateExitSide.Right => new Vec3(1, 0, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(side))
        };
        var direction = F(exitGateInDestination.Rotation.Transform(diagonal));
        var safe = new OosSafeTarget(basePosition, false);
        if (conditions.ObstacleTransform is { } obstacle && conditions.ObstacleBounds is { } bounds)
        {
            ValidateTransform(obstacle);
            safe = OosTransportArrivalGeometry.SafePoint(basePosition,
                obstacle.TransformPoint(bounds.Center), bounds.HalfExtents, direction, shipSafeSize,
                obstacle.Rotation, noOtherConstraints: true);
        }
        return new(intermediate, basePosition, safe.Position, direction, safe.Collided, side);
    }

    private static float RequireDistance(float? value) => value is { } d && float.IsFinite(d) && d >= 0
        ? d : throw new NotSupportedException("Native next-position distance expression is unresolved.");
    private static void Positive(float value)
    {
        if (!float.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
    }
    private static Vec3 F(Vec3 value)
    {
        var result = new Vec3((float)value.X, (float)value.Y, (float)value.Z);
        if (!double.IsFinite(result.X) || !double.IsFinite(result.Y) || !double.IsFinite(result.Z))
            throw new ArgumentOutOfRangeException(nameof(value));
        return result;
    }
    private static void ValidateTransform(X4RigidTransform transform)
    {
        F(transform.Position);
        var r = transform.Rotation;
        F(r.Row0); F(r.Row1); F(r.Row2);
        var product = r * r.Transpose();
        var rows = new[] { product.Row0, product.Row1, product.Row2 };
        var expected = new[] { new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1) };
        if (rows.Where((row, i) => row.DistanceTo(expected[i]) > 1e-5).Any())
            throw new ArgumentException("Rotation must be orthonormal.", nameof(transform));
    }
}
