namespace X4Calculator.Core.Models;

/// <summary>
/// 可生产某种商品的空间站模块定义。用于区分通用、种族专用生产谱系。
/// </summary>
public sealed class ProductionFacilityInfo
{
    public string ModuleId { get; init; } = string.Empty;
    public string WareId { get; init; } = string.Empty;
    public string Race { get; init; } = "default";
}
