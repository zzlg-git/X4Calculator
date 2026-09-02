namespace X4Calculator.Core.Models;

/// <summary>建造一个单位的舰船、装备、消耗品或空间站模块所需的直接材料。</summary>
public sealed record PlayerBuildMaterial(string WareId, double Amount);

/// <summary>某个建造方式下的单位建造时间与直接材料。</summary>
public sealed record PlayerBuildMethod(
    string Method,
    double BuildTimeSeconds,
    IReadOnlyList<PlayerBuildMaterial> Materials);

/// <summary>玩家可建造对象及其原生建造方式。</summary>
public sealed record PlayerBuildableDefinition(
    string WareId,
    string MacroId,
    string Name,
    string Kind,
    IReadOnlyList<PlayerBuildMethod> Methods,
    string UpgradeCategory = "",
    string BuildClass = "");

/// <summary>
/// 对请求建造方式解析后的材料；原生对象没有该方式时可回退到 default。
/// </summary>
public sealed record ResolvedPlayerBuildMaterials(
    string WareId,
    string RequestedMethod,
    string EffectiveMethod,
    double BuildTimeSeconds,
    IReadOnlyList<PlayerBuildMaterial> Materials)
{
    public bool UsesDefaultFallback =>
        !RequestedMethod.Equals(EffectiveMethod, StringComparison.OrdinalIgnoreCase);
}
