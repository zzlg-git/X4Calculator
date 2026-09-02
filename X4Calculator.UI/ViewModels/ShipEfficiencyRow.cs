namespace X4Calculator.UI.ViewModels;

/// <summary>
/// 运输效率排序栏的一行：序号 / 舰船名 / 尺寸 / 推进器 / 引擎 / 容量 / 运输效率。
/// EfficiencyValue 为内部排序值，Efficiency 为显示文本（如 "123.4 m³/s"）。
/// </summary>
public class ShipEfficiencyRow
{
    public int Rank { get; set; }

    /// <summary>舰船宏 ID，仅用于稳定识别当前选中舰船的置顶配置组，不直接显示。</summary>
    public string ShipId { get; set; } = string.Empty;

    public string ShipName { get; set; } = string.Empty;

    public string Size { get; set; } = string.Empty;

    public string Thruster { get; set; } = string.Empty;

    public string Engine { get; set; } = string.Empty;

    public string Capacity { get; set; } = string.Empty;

    public string Efficiency { get; set; } = string.Empty;

    /// <summary>内部排序值（m³/s），不直接显示。</summary>
    public double EfficiencyValue { get; set; }
}
