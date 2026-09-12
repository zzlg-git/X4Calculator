using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

/// <summary>把普通 L 船与存档 XL pier 的唯一 +Z clscloselink 组合为两点静态模板。</summary>
public static class OosTransportBerthGeometryCalculator
{
    public static OosTransportTwoPointTemplate CreateTwoPointTemplate(
        OosTransportBerth berth,
        X4MacroGeometryResult shipGeometry,
        double shipBoundingRadius)
    {
        ArgumentNullException.ThrowIfNull(berth);
        ArgumentNullException.ThrowIfNull(shipGeometry);
        if (!double.IsFinite(shipBoundingRadius) || shipBoundingRadius <= 0)
            throw new ArgumentOutOfRangeException(nameof(shipBoundingRadius));
        if (!berth.SupportsCapitalTwoPointTemplate)
            throw new InvalidOperationException(
                $"泊位 {berth.SaveId} 没有受支持的唯一 +Z capital-pier closelink 模板：" +
                string.Join("；", berth.UnsupportedReasons));

        var shipGroups = shipGeometry.CloseLinkGroups;
        if (shipGroups.Count != 1 || shipGroups[0].Side != 1)
            throw new InvalidOperationException(
                $"舰船 {shipGeometry.MacroId} 的 clscloselink 必须只有 +Z 一组；实际为 " +
                $"[{string.Join(",", shipGroups.Select(group => group.Side))}]");

        var dockMean = berth.CloseLinkGroups[0].Mean;
        var shipMean = shipGroups[0].Mean;
        // C_dock - R_yaw180(C_ship)，其中 R_yaw180(x,y,z)=(-x,y,-z)。
        var finalDockLocal = new Vec3(
            dockMean.X + shipMean.X,
            dockMean.Y - shipMean.Y,
            dockMean.Z + shipMean.Z);
        var firstDockLocal = new Vec3(
            finalDockLocal.X,
            finalDockLocal.Y,
            finalDockLocal.Z + 2 * shipBoundingRadius);
        var yaw180 = new X4RotationMatrix(
            new Vec3(-1, 0, 0),
            new Vec3(0, 1, 0),
            new Vec3(0, 0, -1));

        return new OosTransportTwoPointTemplate(
            berth.StationTransform.TransformPoint(firstDockLocal),
            berth.StationTransform.TransformPoint(finalDockLocal),
            berth.SectorTransform.TransformPoint(firstDockLocal),
            berth.SectorTransform.TransformPoint(finalDockLocal),
            berth.SectorTransform.Rotation * yaw180);
    }
}
