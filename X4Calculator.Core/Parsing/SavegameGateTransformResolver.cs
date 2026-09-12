using System.Xml.Linq;
using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Parsing;

/// <summary>一次存档门姿态补全的结果；失败原因保留为文本，不以恒等变换替代。</summary>
public sealed record SaveGateTransformResolution(
    X4RigidTransform? GateZoneTransform,
    X4RigidTransform? ZoneSectorTransform,
    X4RigidTransform? GateSectorTransform,
    string? GateZoneUnsupportedReason,
    string? ZoneSectorUnsupportedReason)
{
    public bool HasCompleteGateSectorTransform => GateSectorTransform != null;

    /// <summary>将本结果应用到同一存档实例；调用者必须显式决定何时使用有效 XML 补全。</summary>
    public void ApplyTo(SaveGateInstance gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        gate.GateZoneTransform = GateZoneTransform;
        gate.ZoneSectorTransform = ZoneSectorTransform;
        gate.GateSectorTransform = GateSectorTransform;
    }
}

/// <summary>
/// 以调用者提供的最终有效 XML 补全 <c>offset default="1"</c> 的存档门层级变换。
/// 此类不读取存档，也不绑定实验场宏；缺少连接或宏定义时保持 unknown。
/// </summary>
public sealed class SavegameGateTransformResolver
{
    private readonly X4GeometryDefinitionReader _geometry;

    public SavegameGateTransformResolver(GameDataDB gameData, IEnumerable<XDocument>? supplementalMacroXml = null)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        if (!gameData.HasEffectiveXmlSource)
            throw new InvalidOperationException("GameDataDB 尚未通过 LoadAsync 建立有效 XML 数据源。");
        _geometry = new X4GeometryDefinitionReader(gameData, supplementalMacroXml ?? []);
    }

    public SaveGateTransformResolution Resolve(SaveGateInstance gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        var gateZone = ResolveLocalTransform(
            gate.GateZoneTransform,
            gate.GateZoneTransformSource,
            gate.ZoneId,
            gate.ConnectionName,
            gate.Macro,
            gate.GateConnectionName,
            "gate-to-zone");
        var zoneSector = ResolveLocalTransform(
            gate.ZoneSectorTransform,
            gate.ZoneSectorTransformSource,
            gate.SectorMacro,
            gate.ZoneParentConnectionName,
            gate.ZoneId,
            gate.ZoneConnectionName,
            "zone-to-sector");
        X4RigidTransform? gateSector = gateZone.Transform is { } gateZoneTransform && zoneSector.Transform is { } zoneSectorTransform
            ? X4RigidTransform.Compose(zoneSectorTransform, gateZoneTransform)
            : null;
        return new SaveGateTransformResolution(
            gateZone.Transform,
            zoneSector.Transform,
            gateSector,
            gateZone.UnsupportedReason,
            zoneSector.UnsupportedReason);
    }

    private (X4RigidTransform? Transform, string? UnsupportedReason) ResolveLocalTransform(
        X4RigidTransform? saveTransform,
        SaveGateTransformSource source,
        string parentMacro,
        string parentConnection,
        string childMacro,
        string childConnection,
        string label)
    {
        if (saveTransform != null) return (saveTransform, null);
        if (source != SaveGateTransformSource.MacroDefault)
            return (null, $"{label}: save offset source is {source}");
        if (string.IsNullOrWhiteSpace(parentMacro) || string.IsNullOrWhiteSpace(parentConnection) ||
            string.IsNullOrWhiteSpace(childMacro) || string.IsNullOrWhiteSpace(childConnection))
        {
            return (null, $"{label}: macro-default placement lacks parent/child macro or connection name");
        }

        try
        {
            // 每次查找都要求恰好存在一个匹配的宏/组件连接。已定义的连接若
            // offset 为空，则确认为恒等变换；连接不存在时抛出异常。
            var parent = _geometry.ResolveConnectionTransform(parentMacro, parentConnection);
            var child = _geometry.ResolveConnectionTransform(childMacro, childConnection);
            return (X4RigidTransform.Compose(parent, child.Inverse()), null);
        }
        catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException or FormatException)
        {
            return (null, $"{label}: {exception.Message}");
        }
    }
}
