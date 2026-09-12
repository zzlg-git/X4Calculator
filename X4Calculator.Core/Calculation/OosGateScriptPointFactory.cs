using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

/// <summary>
/// move.gate 的无 rotation、普通 type-2 点。姿态在待处理点合并时生成：前驱是当时最后一个
/// 活动点；活动点为空时取 worker 位姿。只覆盖稳定、无 Navigation context 的点参考空间。
/// </summary>
public static class OosGateScriptPointFactory
{
    /// <summary>
    /// XML 578 的裸 position 使用 ship 的直接父空间；580 的二项列表使用显式 reference。
    /// 两者即使来自同一个 nextpos 也可能不同。输入位姿取 across 创建出口目标的当时预测状态。
    /// </summary>
    public static OosGateNextPositionCondition EvaluateNextPositionDistances(
        OosLinearPose shipPose,
        X4RigidTransform shipParentInCommonSpace,
        OosLinearVector4 nextPosition,
        X4RigidTransform nextReferenceInCommonSpace,
        bool inDestinationContext)
    {
        if (!inDestinationContext) return new(true, false);
        Validate(shipPose.Position); Validate(nextPosition);
        ValidateTransform(shipParentInCommonSpace); ValidateTransform(nextReferenceInCommonSpace);
        var shipLocal = Vector(shipParentInCommonSpace.Inverse().TransformPoint(Vec(shipPose.Position)));
        var nextCommon = Vector(nextReferenceInCommonSpace.TransformPoint(Vec(nextPosition)));
        return new(true, true, NativeDistance(shipLocal, nextPosition), NativeDistance(shipPose.Position, nextCommon));
    }

    /// <summary>
    /// 普通非 accelerator、静止门、有效 nextpos 的显式 warp 位姿，必须在 stop 之前求值。
    /// 这里只支持已声明完整约束均不命中，且预约/优先空区不改变位置的情景；无方向 zone 求解器
    /// 的两次 RNG 抽取仍由调用者在 stop 前推进，不能因落点未变化而省略。
    /// </summary>
    public static OosLinearPose PrepareUnobstructedWarp(
        OosLinearPose preStopShipPose,
        X4RigidTransform entryGateInOrigin,
        X4RigidTransform exitGateInDestination,
        X4RigidTransform exitZoneInDestination,
        OosLinearVector4 destinationSectorCorePosition,
        OosLinearVector4 nextPosition,
        X4RigidTransform nextReferenceInDestination,
        bool completeConstraintsDoNotChangeCandidate,
        bool reservationAndPreferredClearHaveNoEffect)
    {
        if (!completeConstraintsDoNotChangeCandidate || !reservationAndPreferredClearHaveNoEffect)
            throw new NotSupportedException("The explicit warp candidate requires complete no-hit and reservation conditions.");
        Validate(preStopShipPose.Position); Validate(destinationSectorCorePosition); Validate(nextPosition);
        ValidateTransform(entryGateInOrigin); ValidateTransform(exitGateInDestination);
        ValidateTransform(exitZoneInDestination); ValidateTransform(nextReferenceInDestination);
        var start = Vector(entryGateInOrigin.Inverse().TransformPoint(Vec(preStopShipPose.Position)));
        var front = Vector(exitGateInDestination.TransformPoint(new(start.X, start.Y, MathF.Abs(start.Z))));
        var back = Vector(exitGateInDestination.TransformPoint(new(start.X, start.Y, -MathF.Abs(start.Z))));
        // 严格比较：距离相等时保留前方候选值。
        var position = NativeDistance(destinationSectorCorePosition, back) < NativeDistance(destinationSectorCorePosition, front)
            ? back : front;
        var zoneInverse = exitZoneInDestination.Inverse();
        var localPosition = Vector(zoneInverse.TransformPoint(Vec(position)));
        var next = Vector(zoneInverse.TransformPoint(nextReferenceInDestination.TransformPoint(Vec(nextPosition))));
        var direction = new OosLinearVector4(next.X - localPosition.X, next.Y - localPosition.Y, next.Z - localPosition.Z);
        if (direction.X == 0 && direction.Y == 0 && direction.Z == 0)
            throw new NotSupportedException("Warp look_at is undefined when the next target equals the warp position.");
        var localPose = OosLinearFlightController.CreateLookPose(localPosition, direction);
        return new(position,
            Vector(exitZoneInDestination.Rotation.Transform(Vec(localPose.XAxis))),
            Vector(exitZoneInDestination.Rotation.Transform(Vec(localPose.YAxis))),
            Vector(exitZoneInDestination.Rotation.Transform(Vec(localPose.ZAxis))));
    }

    public static OosNavigationPathQueueEntry CreatePoint(
        OosLinearVector4 position,
        OosLinearPose predecessorPose,
        bool entryApproach,
        bool noBoost,
        X4RigidTransform pointReferenceInCommonSpace)
    {
        Validate(position);
        Validate(predecessorPose.Position);
        var rotation = pointReferenceInCommonSpace.Rotation;
        var inverse = pointReferenceInCommonSpace.Inverse();
        ValidateTransform(pointReferenceInCommonSpace);
        var localTarget = Vector(inverse.TransformPoint(Vec(position)));
        var localPrevious = Vector(inverse.TransformPoint(Vec(predecessorPose.Position)));
        var dx = localTarget.X - localPrevious.X;
        var dy = localTarget.Y - localPrevious.Y;
        var dz = localTarget.Z - localPrevious.Z;
        // E8E592..E8E606 具有独立的近零/垂直分支。普通过门行程不需要它们；
        // 应拒绝而不是赋予虚构的朝向。
        if ((MathF.Abs(dx) < 1e-4f && MathF.Abs(dy) < 1e-4f && MathF.Abs(dz) < 1e-4f) ||
            (MathF.Abs(dx) < 1e-5f && MathF.Abs(dz) < 1e-5f))
            throw new NotSupportedException("Degenerate default-point heading requires its separate native branch.");
        var length = MathF.Sqrt(dx * dx + (dy * dy + dz * dz));
        if (!float.IsFinite(length) || length <= 0)
            throw new ArgumentOutOfRangeException(nameof(position));
        var nx = dx / (length + 1e-27f);
        var nz = dz / (length + 1e-27f);
        // E8E638..E8E942 对应运算：-atan2(-x,z)、sin/cos、type 2 -> E8E936。
        // 即使位移包含 Y 分量，默认点也只有偏航角。
        var yaw = (float)(-Math.Atan2(-nx, nz));
        var sin = (float)Math.Sin(yaw);
        var cos = (float)Math.Cos(yaw);
        var target = new OosLinearPose(position,
            Vector(rotation.Transform(new(cos, 0, -sin))),
            Vector(rotation.Transform(new(0, 1, 0))),
            Vector(rotation.Transform(new(sin, 0, cos))));
        return new(new(target, -1f, Boost: !entryApproach && !noBoost,
            Travel: entryApproach && !noBoost));
    }

    private static Vec3 Vec(OosLinearVector4 v) => new(v.X, v.Y, v.Z);
    private static float NativeDistance(OosLinearVector4 a, OosLinearVector4 b)
    {
        if (!Sse.IsSupported)
            throw new PlatformNotSupportedException("Native position distance requires SSE RSQRTSS/RCPSS.");
        var x = a.X - b.X;
        var y = a.Y - b.Y;
        var z = a.Z - b.Z;
        var sum = x * x + (y * y + z * z);
        return Sse.ReciprocalScalar(
            Sse.ReciprocalSqrtScalar(Vector128.CreateScalar(sum))).ToScalar();
    }
    private static OosLinearVector4 Vector(Vec3 v)
    {
        var result = new OosLinearVector4((float)v.X, (float)v.Y, (float)v.Z);
        Validate(result);
        return result;
    }
    private static void Validate(OosLinearVector4 v)
    {
        if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z) || !float.IsFinite(v.W))
            throw new ArgumentOutOfRangeException(nameof(v));
    }
    private static void ValidateTransform(X4RigidTransform t)
    {
        Vector(t.Position);
        Vector(t.Rotation.Row0); Vector(t.Rotation.Row1); Vector(t.Rotation.Row2);
        var product = t.Rotation * t.Rotation.Transpose();
        if (product.Row0.DistanceTo(new(1, 0, 0)) > 1e-5 ||
            product.Row1.DistanceTo(new(0, 1, 0)) > 1e-5 ||
            product.Row2.DistanceTo(new(0, 0, 1)) > 1e-5)
            throw new ArgumentException("Point reference rotation must be orthonormal.");
    }
}
