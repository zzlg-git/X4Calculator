namespace X4Calculator.Core.Models;

/// <summary>
/// 舰船信息，从 ship_*_macro.xml 解析。
/// </summary>
public class ShipInfo
{
    /// <summary>
    /// 舰船 macro ID（如 "ship_arg_l_trans_container_01_a_macro"）。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 宏文件来源目录（用于排除玩家不可用的舰船，如 Timelines DLC size_xl 目录）。
    /// </summary>
    public string? SourceDir { get; set; }

    /// <summary>
    /// 舰船中文名。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 制造种族（如 "argon", "teladi", "boron"）。
    /// </summary>
    public string Race { get; set; } = string.Empty;

    /// <summary>
    /// 制造种族列表（makerrace 可能为空格分隔的多个种族，如 "argon teladi"，特使船可在两族建造；Race 为第一个主种族）。
    /// </summary>
    public List<string> Races { get; set; } = new();

    /// <summary>
    /// 尺寸分类（"XL", "L", "M", "S"）。
    /// </summary>
    public string SizeCategory { get; set; } = string.Empty;

    /// <summary>
    /// 舰船类型（如 "freighter", "destroyer", "fighter"）。
    /// </summary>
    public string ShipType { get; set; } = string.Empty;

    /// <summary>
    /// 主要用途（如 "trade", "combat", "mining"）。
    /// </summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>
    /// 变体标识（如 "a", "b"）。
    /// </summary>
    public string Variant { get; set; } = string.Empty;

    // ===== 货舱信息 =====

    /// <summary>
    /// 货舱类型标签（来自 storage 宏 cargo@tags，空格分隔多值）。
    /// 如 ["container"]（集装）、["solid"]（固体）、["liquid"]（液体）、["container","condensate"]（集装+冷凝）。
    /// </summary>
    public List<string> CargoTypes { get; set; } = new();

    /// <summary>
    /// 货舱总容量（m³，来自 storage 宏 cargo@max）。
    /// </summary>
    public double CargoCapacity { get; set; }

    // ===== 飞行物理参数（用于速度/加速度计算）=====

    /// <summary>
    /// 引擎连接点数量（从组件文件 con_engine_N 统计，速度按此倍乘）。
    /// </summary>
    public int EngineCount { get; set; } = 1;

    /// <summary>
    /// 引擎槽位归属标签（从组件文件所有 con_engine_N 连接点的 tags 提取，如 "advanced"/"standard"/"ship_gen_m_corvette_01"）。
    /// 引擎可用于本舰船 ⇔ 尺寸匹配 且 引擎.SlotTags 全量包含于本列表（列表为空时不约束）。
    /// </summary>
    public List<string> EngineSlotTags { get; set; } = new();

    /// <summary>
    /// 推进器尺寸标签（"small"/"medium"/"large"/"extralarge"）。
    /// </summary>
    public string ThrusterTag { get; set; } = string.Empty;

    /// <summary>
    /// 舰船质量。
    /// </summary>
    public double Mass { get; set; }

    /// <summary>
    /// 俯仰轴转动惯量（physics/inertia@pitch）。
    /// </summary>
    public double InertiaPitch { get; set; }

    /// <summary>
    /// 偏航轴转动惯量（physics/inertia@yaw）。
    /// </summary>
    public double InertiaYaw { get; set; }

    /// <summary>
    /// 翻滚轴转动惯量（physics/inertia@roll）。
    /// </summary>
    public double InertiaRoll { get; set; }

    /// <summary>
    /// 前向阻力。
    /// </summary>
    public double DragForward { get; set; }

    /// <summary>
    /// 后向阻力。
    /// </summary>
    public double DragReverse { get; set; }

    /// <summary>
    /// 水平（侧移）阻力。
    /// </summary>
    public double DragHorizontal { get; set; }

    /// <summary>
    /// 垂直阻力。
    /// </summary>
    public double DragVertical { get; set; }

    /// <summary>
    /// 俯仰阻力。
    /// </summary>
    public double DragPitch { get; set; }

    /// <summary>
    /// 偏航阻力。
    /// </summary>
    public double DragYaw { get; set; }

    /// <summary>
    /// 翻滚阻力。
    /// </summary>
    public double DragRoll { get; set; }

    /// <summary>
    /// 船长（米，= 2 × 组件文件碰撞体 part_main 的 max.z）。
    /// </summary>
    public double Length { get; set; }

    /// <summary>
    /// 船宽（米，= 2 × 组件文件碰撞体 part_main 的 max.x）。
    /// </summary>
    public double Width { get; set; }

    /// <summary>
    /// 前向加速度系数（默认 1.0，仅影响加速度，不影响最高速度）。
    /// </summary>
    public double AccFactorForward { get; set; } = 1.0;

    /// <summary>
    /// 后向加速度系数。
    /// </summary>
    public double AccFactorReverse { get; set; } = 1.0;

    /// <summary>
    /// 水平加速度系数。
    /// </summary>
    public double AccFactorHorizontal { get; set; } = 1.0;

    /// <summary>
    /// 垂直加速度系数。
    /// </summary>
    public double AccFactorVertical { get; set; } = 1.0;
}
