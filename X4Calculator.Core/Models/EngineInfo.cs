namespace X4Calculator.Core.Models;

/// <summary>
/// 引擎信息，从 engine_*_macro.xml 解析。
/// 引擎区分种族、尺寸、风格（均衡/巡航/战斗）和 MK 档位。
/// </summary>
public class EngineInfo
{
    /// <summary>
    /// 引擎 macro ID（如 "engine_arg_m_allround_01_mk3_macro"）。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 引擎中文名。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 制造种族（如 "argon", "terran"）。推进器为 "gen"。
    /// </summary>
    public string Race { get; set; } = string.Empty;

    /// <summary>
    /// 尺寸分类（"S", "M", "L", "XL"）。
    /// </summary>
    public string SizeCategory { get; set; } = string.Empty;

    /// <summary>
    /// 引擎风格（"allround" 均衡 / "combat" 战斗 / "travel" 巡航）。
    /// </summary>
    public string Style { get; set; } = string.Empty;

    /// <summary>
    /// MK 档位（1~4）。
    /// </summary>
    public int Mk { get; set; }

    /// <summary>
    /// 前向推力。
    /// </summary>
    public double ThrustForward { get; set; }

    /// <summary>
    /// 后向推力。
    /// </summary>
    public double ThrustReverse { get; set; }

    /// <summary>
    /// 巡航引擎充电/启动时间（秒）。
    /// </summary>
    public double TravelCharge { get; set; }

    /// <summary>
    /// 巡航推力倍率（巡航速度 = 常规速度 × 此值）。
    /// </summary>
    public double TravelThrust { get; set; }

    /// <summary>
    /// 巡航加速时间（秒，从常规加速到巡航速度的时间）。
    /// </summary>
    public double TravelAttack { get; set; }

    /// <summary>
    /// 巡航减速时间（秒，引擎 travel.release；从巡航速度减速到常规的时间）。
    /// </summary>
    public double TravelRelease { get; set; }

    /// <summary>
    /// 可安装性归属标签（从引擎组件连接点 tags 提取，如 "advanced"/"standard"/"ship_gen_m_corvette_01"）。
    /// 引擎可用于某舰船 ⇔ 尺寸匹配 且 本列表全量包含于舰船.EngineSlotTags（舰船列表为空时不约束）。
    /// </summary>
    public List<string> SlotTags { get; set; } = new();

    /// <summary>
    /// 显示名覆盖（特殊舰船专属引擎用，如 "ARG Envoy Mk1"/"Astrid"；null 时用默认 "种族 风格 MkN"）。
    /// </summary>
    public string? DisplayNameOverride { get; set; }

    /// <summary>
    /// 显示名，如 "ARG 均衡 Mk3"。特殊引擎用 DisplayNameOverride。
    /// </summary>
    public string DisplayName =>
        !string.IsNullOrEmpty(DisplayNameOverride)
            ? DisplayNameOverride!
            : $"{EquipmentDisplay.RaceCode(Race)} {EquipmentDisplay.StyleName(Style)} Mk{Mk}";
}
