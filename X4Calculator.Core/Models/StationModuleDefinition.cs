namespace X4Calculator.Core.Models;

/// <summary>由本地解包模块宏定义读取的可建造空间站模块。</summary>
public sealed class StationModuleDefinition
{
    public string Id { get; init; } = string.Empty;
    /// <summary>建造该模块的 module ware ID。</summary>
    public string BuildableWareId { get; init; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Race { get; init; } = "default";
    /// <summary>
    /// 模块可运行的产品队列。大多数生产模块只有一个产品；废料再生设施有两个
    /// 分时复用的产品，废料处理设施的产品来自 macro 的 products 节点。
    /// </summary>
    public IReadOnlyList<StationModuleProductDefinition> Products { get; init; }
        = Array.Empty<StationModuleProductDefinition>();

    /// <summary>首个产品的兼容入口；新代码应优先使用 <see cref="Products"/>。</summary>
    public string? WareId { get; init; }
    public int WorkforceRequired { get; init; }
    public int WorkforceCapacity { get; init; }
    /// <summary>福利模块提供的劳动力增长加成比例，例如 0.2 表示 +20%。</summary>
    public double WorkforceGrowthRate { get; init; }
    /// <summary>仓储模块的货舱容量；非仓储模块为 0。</summary>
    public long StorageCapacity { get; init; }
    /// <summary>仓储支持的运输类型（container / solid / liquid / condensate）。</summary>
    public IReadOnlyList<string> StorageTypes { get; init; } = Array.Empty<string>();
    /// <summary>建造模块宏中实际连接的 buildprocessor 数量；非建造模块为 0。</summary>
    public int BuildProcessorCount { get; init; }
    /// <summary>builder@classes 声明的可建舰船 class（ship_s / ship_m / ship_l / ship_xl）。</summary>
    public IReadOnlyList<string> BuildClasses { get; init; } = Array.Empty<string>();
}

/// <summary>由 libraries/parameters.xml 读取的劳动力增长参数。</summary>
public sealed record WorkforceGrowthParameters(
    double BaseGrowth = 20,
    int CycleSeconds = 600,
    int CapacityBonusLimit = 30_000,
    int CapacityBonusMinimum = 1_000,
    double MaximumCapacityBonus = 10,
    long PopulationBonusLimit = 10_000_000_000,
    long PopulationBonusStep = 1_000_000,
    double MaximumPopulationBonus = 10);

/// <summary>空间站模块的一个可生产商品及其配方方法。</summary>
public sealed record StationModuleProductDefinition(string WareId, string Method, double? OutputPerMinute = null);

/// <summary>空间站上非生产模块的实例，例如居住区和仓储。</summary>
public sealed class StationModule
{
    public string ModuleId { get; set; } = string.Empty;
    /// <summary>模块宏的 class，例如 storage、dockarea 或 defencemodule。</summary>
    public string Kind { get; set; } = string.Empty;
    public int Count { get; set; } = 1;
}
