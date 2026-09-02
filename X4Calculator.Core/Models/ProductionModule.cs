namespace X4Calculator.Core.Models;

/// <summary>
/// 生产模块实例，表示空间站中的一个已建造的生产模块。
/// </summary>
public class ProductionModule
{
    /// <summary>实际建造的模块宏 ID；用于保留种族变体及其劳动力需求。</summary>
    public string ModuleId { get; set; } = string.Empty;

    /// <summary>
    /// 模块对应的 warehouse key（如 "advancedcomposites"）。
    /// </summary>
    public string WareId { get; set; } = string.Empty;

    /// <summary>
    /// 使用的配方方法（"default", "teladi" 等）。
    /// </summary>
    public string Method { get; set; } = "default";

    /// <summary>
    /// 模块数量（同一类型多个）。
    /// </summary>
    public int Count { get; set; } = 1;

    /// <summary>由产线编排的中间产品补齐功能生成；与用户手动添加的同类模块保持独立。</summary>
    public bool IsAutoAdded { get; set; }

    /// <summary>
    /// 多产品模块当前专注生产的商品。null 或空表示运行完整队列（各产品等分生产时间）；
    /// 指定 ware ID 表示库存阻塞后专注该产品并使用完整产能。
    /// </summary>
    public string? SelectedProductWareId { get; set; }

    /// <summary>特殊处理模块的运行模式。默认使用更接近实际吞吐的标准模式。</summary>
    public string OperatingMode { get; set; } = "full";

    /// <summary>
    /// 关联的配方数据（从 GameDataDB 匹配后填充）。
    /// </summary>
    public ProductionRecipe? Recipe { get; set; }

    /// <summary>
    /// 关联的商品数据。
    /// </summary>
    public Ware? Ware { get; set; }
}
