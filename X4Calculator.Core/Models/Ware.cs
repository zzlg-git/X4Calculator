namespace X4Calculator.Core.Models;

/// <summary>
/// 游戏商品/物资定义，从 wares.xml 解析。
/// </summary>
public class Ware
{
    /// <summary>
    /// 商品 ID（如 "advancedcomposites"）。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 商品中文名（从 t/0001-l086.xml 解析）。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 工厂中文名（从 t/0001-l086.xml 解析）。
    /// </summary>
    public string FactoryName { get; set; } = string.Empty;

    /// <summary>
    /// 商品分组（如 "hightech", "shiptech"）。
    /// </summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>
    /// 运输类型（如 "container", "solid", "liquid"）。
    /// </summary>
    public string Transport { get; set; } = string.Empty;

    /// <summary>
    /// 单个体积。
    /// </summary>
    public int Volume { get; set; }

    /// <summary>
    /// 均价。
    /// </summary>
    public int PriceAvg { get; set; }

    /// <summary>
    /// 最低价。
    /// </summary>
    public int PriceMin { get; set; }

    /// <summary>
    /// 最高价。
    /// </summary>
    public int PriceMax { get; set; }

    /// <summary>
    /// 生产配方列表（多种族变体）。
    /// </summary>
    public List<ProductionRecipe>? Production { get; set; }

    /// <summary>
    /// 标签列表。
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// 舰船、装备或消耗品对应的 macro ID；普通经济商品为空。
    /// </summary>
    public string ComponentRef { get; set; } = string.Empty;
}

/// <summary>
/// 生产配方，包含消耗品、产出量和周期时间。
/// </summary>
public class ProductionRecipe
{
    /// <summary>
    /// 每周期产出量。
    /// </summary>
    public double Amount { get; set; }

    /// <summary>
    /// 周期时间（秒）。
    /// </summary>
    public double Time { get; set; }

    /// <summary>
    /// 配方方法（"default", "teladi", "paranid" 等）。
    /// </summary>
    public string Method { get; set; } = "default";

    /// <summary>
    /// 配方名称（如 "{20206,101}"）。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 配方标签，例如 noplayerbuild、recycling。
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// 消耗品列表：wareId → 每周期消耗量。
    /// </summary>
    public Dictionary<string, double>? Consumption { get; set; }

    /// <summary>
    /// 满额劳动力带来的额外产品比例，来自 effects/effect[@type='work']/@product。
    /// 该比例只增加产品产出，不增加 primary 原料消耗。
    /// </summary>
    public double WorkforceProductBonus { get; set; }

    /// <summary>
    /// 每分钟产出量 = amount / time * 60
    /// </summary>
    public double OutputPerMinute => Time > 0 ? Amount / Time * 60 : 0;
}

/// <summary>
/// 某种经济商品作为舰船、装备或可装载消耗品直接建造材料的来源记录。
/// </summary>
public sealed record BuildableMaterialUse(
    string BuildableWareId,
    string MacroId,
    string Kind,
    string Method,
    double PerUnitAmount);
