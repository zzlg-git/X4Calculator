namespace X4Calculator.Core.Models;

/// <summary>
/// 推进器信息，从 thruster_*_macro.xml 解析。
/// 推进器不区分种族（均为 gen），只分风格（均衡/战斗）和 MK 档位。
/// 提供侧移/俯仰/偏航/翻滚推力，不提供前向推力。
/// </summary>
public class ThrusterInfo
{
    /// <summary>
    /// 推进器 macro ID（如 "thruster_gen_m_allround_01_mk1_macro"）。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 推进器中文名。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 尺寸分类（"S", "M", "L", "XL"）。
    /// </summary>
    public string SizeCategory { get; set; } = string.Empty;

    /// <summary>
    /// 推进器风格（"allround" 均衡 / "combat" 战斗）。
    /// </summary>
    public string Style { get; set; } = string.Empty;

    /// <summary>
    /// MK 档位（1~3）。
    /// </summary>
    public int Mk { get; set; }

    /// <summary>
    /// 侧移推力。
    /// </summary>
    public double Strafe { get; set; }

    /// <summary>
    /// 俯仰推力。
    /// </summary>
    public double Pitch { get; set; }

    /// <summary>
    /// 偏航推力。
    /// </summary>
    public double Yaw { get; set; }

    /// <summary>
    /// 翻滚推力。
    /// </summary>
    public double Roll { get; set; }

    /// <summary>
    /// 显示名，如 "均衡 Mk1"。
    /// </summary>
    public string DisplayName => $"{EquipmentDisplay.StyleName(Style)} Mk{Mk}";
}
