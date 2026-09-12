using X4Calculator.Core.Calculation;

namespace X4Calculator.Core.Models;

/// <summary>三乘三行主序旋转矩阵；矩阵左乘列向量。</summary>
public readonly record struct X4RotationMatrix(Vec3 Row0, Vec3 Row1, Vec3 Row2)
{
    public static X4RotationMatrix Identity { get; } = new(
        new Vec3(1, 0, 0),
        new Vec3(0, 1, 0),
        new Vec3(0, 0, 1));

    public Vec3 Transform(Vec3 value) => new(
        Dot(Row0, value),
        Dot(Row1, value),
        Dot(Row2, value));

    public X4RotationMatrix Transpose() => new(
        new Vec3(Row0.X, Row1.X, Row2.X),
        new Vec3(Row0.Y, Row1.Y, Row2.Y),
        new Vec3(Row0.Z, Row1.Z, Row2.Z));

    public static X4RotationMatrix operator *(X4RotationMatrix left, X4RotationMatrix right)
    {
        var columns = right.Transpose();
        return new X4RotationMatrix(
            new Vec3(Dot(left.Row0, columns.Row0), Dot(left.Row0, columns.Row1), Dot(left.Row0, columns.Row2)),
            new Vec3(Dot(left.Row1, columns.Row0), Dot(left.Row1, columns.Row1), Dot(left.Row1, columns.Row2)),
            new Vec3(Dot(left.Row2, columns.Row0), Dot(left.Row2, columns.Row1), Dot(left.Row2, columns.Row2)));
    }

    private static double Dot(Vec3 left, Vec3 right) =>
        left.X * right.X + left.Y * right.Y + left.Z * right.Z;
}

/// <summary>局部坐标到父坐标的刚性变换。</summary>
public readonly record struct X4RigidTransform(X4RotationMatrix Rotation, Vec3 Position)
{
    public static X4RigidTransform Identity { get; } = new(X4RotationMatrix.Identity, Vec3.Zero);

    public Vec3 TransformPoint(Vec3 point) => Rotation.Transform(point) + Position;

    public X4RigidTransform Inverse()
    {
        var inverseRotation = Rotation.Transpose();
        var translated = inverseRotation.Transform(new Vec3(-Position.X, -Position.Y, -Position.Z));
        return new X4RigidTransform(inverseRotation, translated);
    }

    public static X4RigidTransform Compose(X4RigidTransform left, X4RigidTransform right) => new(
        left.Rotation * right.Rotation,
        left.TransformPoint(right.Position));
}

/// <summary>由已确认碰撞几何得到的逐轴包围盒。</summary>
public sealed record X4AxisAlignedBounds(
    Vec3 Minimum,
    Vec3 Maximum,
    Vec3 Center,
    Vec3 HalfExtents);

/// <summary>按 clscloselink 方位分组的原始 waypoint 与均值。</summary>
public sealed record X4CloseLinkGroup(
    int Side,
    IReadOnlyList<Vec3> Points,
    Vec3 Mean);

/// <summary>一个 macro 的有效 XML 几何。缺少确认几何时 Bounds 为 null，原因保留在 UnsupportedReasons。</summary>
public sealed record X4MacroGeometryResult(
    string MacroId,
    X4AxisAlignedBounds? Bounds,
    IReadOnlyList<X4CloseLinkGroup> CloseLinkGroups,
    IReadOnlyList<string> CargoTags,
    IReadOnlyList<string> UnsupportedReasons);

/// <summary>不参与候选集合的存档泊位及明确原因。</summary>
public sealed record OosTransportExcludedBerth(
    string SaveId,
    string Macro,
    string Reason);

/// <summary>
/// 一个运行中的存档 dockingbay 实例。静态候选不表示当前空闲、被分配或队列优先级。
/// </summary>
public sealed record OosTransportBerth(
    string SaveId,
    string Macro,
    X4RigidTransform StationTransform,
    X4RigidTransform SectorTransform,
    IReadOnlyList<string> DockSizeTags,
    IReadOnlyList<X4CloseLinkGroup> CloseLinkGroups,
    IReadOnlyList<string> UnsupportedReasons)
{
    public bool SupportsCapitalTwoPointTemplate =>
        DockSizeTags.Contains("dock_xl", StringComparer.OrdinalIgnoreCase) &&
        CloseLinkGroups.Count == 1 && CloseLinkGroups[0].Side == 1 &&
        UnsupportedReasons.Count == 0;
}

/// <summary>
/// 普通 L 船靠泊的两点静态模板。位置同时保留站点局部和扇区坐标；它不包含 FCM 或飞行积分状态。
/// </summary>
public sealed record OosTransportTwoPointTemplate(
    Vec3 FirstStationPosition,
    Vec3 FinalStationPosition,
    Vec3 FirstSectorPosition,
    Vec3 FinalSectorPosition,
    X4RotationMatrix SectorOrientation);

/// <summary>单个存档空间站的静态运输拓扑。</summary>
public sealed record StationTransportTopology(
    string StationSaveId,
    string StationCode,
    string StationName,
    string Owner,
    string StationMacro,
    string SectorSaveId,
    string SectorMacro,
    string? ZoneSaveId,
    string? ZoneMacro,
    X4RigidTransform? ZoneSectorTransform,
    OosTransportZoneGeometryInput? ZoneGeometry,
    string? ZoneGeometryUnsupportedReason,
    X4RigidTransform? StationSectorTransform,
    X4AxisAlignedBounds? StationLocalBounds,
    IReadOnlyList<OosTransportBerth> Berths,
    IReadOnlyList<OosTransportExcludedBerth> ExcludedBerths,
    int OperationalModuleCount,
    int ExcludedModuleCount,
    IReadOnlyList<string> StorageTypes,
    int? InstalledCargoDroneCount,
    IReadOnlyList<string> UnsupportedReasons)
{
    /// <summary>同时保留在总原因中；显式独立 carrier 合同可不依赖这部分库存输入。</summary>
    public IReadOnlyList<string> CargoDroneInventoryUnsupportedReasons { get; init; } = [];
}
