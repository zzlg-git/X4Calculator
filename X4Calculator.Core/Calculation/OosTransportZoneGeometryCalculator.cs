using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

/// <summary>当前可由静态证据描述的 zone 形状种类。</summary>
public enum OosTransportZoneShapeKind
{
    /// <summary>原生默认构造出的、原点居中的逐轴半边长盒。</summary>
    DefaultAxisAlignedBox,

    /// <summary>显式 shape 或尚未支持的复合 shape；不能降级为默认盒。</summary>
    Unsupported
}

/// <summary>一个位置相对于单个 zone 几何的成员关系；它不表示运行时 zone 身份或事件时刻。</summary>
public enum OosTransportZoneMembership
{
    Inside,
    Outside,
    Unknown
}

/// <summary>
/// 将扇区坐标转换为 zone 局部坐标的刚性变换。
/// 三个 axis 是局部 X/Y/Z 轴在扇区坐标中的单位向量，局部坐标等于
/// 点积 dot(sectorPosition - Origin, axis)。
/// </summary>
public sealed record OosTransportZoneTransform(
    Vec3 Origin,
    Vec3 LocalXAxis,
    Vec3 LocalYAxis,
    Vec3 LocalZAxis)
{
    public static OosTransportZoneTransform Identity { get; } = new(
        Vec3.Zero,
        new Vec3(1, 0, 0),
        new Vec3(0, 1, 0),
        new Vec3(0, 0, 1));
}

/// <summary>
/// 单个 zone 的静态几何输入。半边长必须由已解析/验证的 shape 显式提供；本类型不内置 50 km 默认值。
/// </summary>
public sealed record OosTransportZoneGeometryInput(
    OosTransportZoneShapeKind ShapeKind,
    OosTransportZoneTransform Transform,
    Vec3 HalfExtents);

/// <summary>静态几何判定及已转换的局部坐标（未知判定没有局部坐标）。</summary>
public sealed record OosTransportZoneMembershipResult(
    OosTransportZoneMembership Membership,
    Vec3? LocalPosition,
    string Reason);

/// <summary>
/// 已证实的普通默认 zone 盒判定。它只回答一个指定位置是否落在一个指定盒内，
/// 不选择重叠 zone，也不推断 worker tick、relocation event 或舰船何时改变 zone 身份。
/// </summary>
public static class OosTransportZoneGeometryCalculator
{
    private const float RigidTransformTolerance = 1e-5f;

    public static OosTransportZoneMembershipResult Evaluate(
        OosTransportZoneGeometryInput geometry,
        Vec3 sectorPosition)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (geometry.ShapeKind != OosTransportZoneShapeKind.DefaultAxisAlignedBox)
        {
            return new(
                OosTransportZoneMembership.Unknown,
                LocalPosition: null,
                "only the native default axis-aligned box is supported");
        }

        ArgumentNullException.ThrowIfNull(geometry.Transform);
        var position = ToFloat32(sectorPosition, nameof(sectorPosition));
        var origin = ToFloat32(geometry.Transform.Origin, nameof(geometry.Transform.Origin));
        var localXAxis = ToFloat32(geometry.Transform.LocalXAxis, nameof(geometry.Transform.LocalXAxis));
        var localYAxis = ToFloat32(geometry.Transform.LocalYAxis, nameof(geometry.Transform.LocalYAxis));
        var localZAxis = ToFloat32(geometry.Transform.LocalZAxis, nameof(geometry.Transform.LocalZAxis));
        var halfExtents = ToFloat32(geometry.HalfExtents, nameof(geometry.HalfExtents));
        ValidateRigidTransform(localXAxis, localYAxis, localZAxis);
        ValidateHalfExtents(halfExtents, nameof(geometry.HalfExtents));

        var offset = new FloatVec3(
            position.X - origin.X,
            position.Y - origin.Y,
            position.Z - origin.Z);
        // 970170 的盒内判定使用 float32 局部坐标；此转换保留该边界的精度，
        // 不代表完整的指令级矩阵运算重放。
        var local = new FloatVec3(
            Dot(offset, localXAxis),
            Dot(offset, localYAxis),
            Dot(offset, localZAxis));
        var inside = MathF.Abs(local.X) <= halfExtents.X &&
                     MathF.Abs(local.Y) <= halfExtents.Y &&
                     MathF.Abs(local.Z) <= halfExtents.Z;

        return new(
            inside ? OosTransportZoneMembership.Inside : OosTransportZoneMembership.Outside,
            new Vec3(local.X, local.Y, local.Z),
            inside
                ? "position is inside the explicit default-box geometry"
                : "position is outside the explicit default-box geometry");
    }

    private static FloatVec3 ToFloat32(Vec3 value, string parameterName)
    {
        if (!double.IsFinite(value.X) || !double.IsFinite(value.Y) || !double.IsFinite(value.Z))
            throw new ArgumentOutOfRangeException(parameterName);

        var result = new FloatVec3((float)value.X, (float)value.Y, (float)value.Z);
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) || !float.IsFinite(result.Z))
            throw new ArgumentOutOfRangeException(parameterName, "Value cannot be represented as a finite float32.");
        return result;
    }

    private static void ValidateHalfExtents(FloatVec3 value, string parameterName)
    {
        if (value.X < 0 || value.Y < 0 || value.Z < 0)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void ValidateRigidTransform(FloatVec3 localXAxis, FloatVec3 localYAxis, FloatVec3 localZAxis)
    {
        if (MathF.Abs(Dot(localXAxis, localXAxis) - 1) > RigidTransformTolerance ||
            MathF.Abs(Dot(localYAxis, localYAxis) - 1) > RigidTransformTolerance ||
            MathF.Abs(Dot(localZAxis, localZAxis) - 1) > RigidTransformTolerance ||
            MathF.Abs(Dot(localXAxis, localYAxis)) > RigidTransformTolerance ||
            MathF.Abs(Dot(localXAxis, localZAxis)) > RigidTransformTolerance ||
            MathF.Abs(Dot(localYAxis, localZAxis)) > RigidTransformTolerance)
        {
            throw new ArgumentException("Zone transform axes must be orthonormal.");
        }
    }

    private static float Dot(FloatVec3 left, FloatVec3 right) =>
        left.Y * right.Y + left.X * right.X + left.Z * right.Z;

    private readonly record struct FloatVec3(float X, float Y, float Z);
}
