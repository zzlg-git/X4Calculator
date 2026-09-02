namespace X4Calculator.Core.Models;

/// <summary>
/// 空间站数据模型，包含所有已建造的生产模块。
/// </summary>
public class Station
{
    /// <summary>
    /// 空间站唯一标识（存档中的 connection id）。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 空间站名称。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>存档中空间站组件的独有标识名，例如 ABC-123；规划站为空。</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// 存档中的 nameindex，或规划器为自动名称分配的序号。
    /// 规划器按“扇区 + 自动生成类型”分配；导入时保留存档原值或缺失状态。
    /// </summary>
    public int? GeneratedNameIndex { get; set; }

    /// <summary>
    /// 空间站所属派系。
    /// </summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>
    /// 空间站类型 macro。
    /// </summary>
    public string Macro { get; set; } = string.Empty;

    /// <summary>空间站所在扇区的 macro ID。</summary>
    public string SectorId { get; set; } = string.Empty;

    /// <summary>空间站所在区域的 macro ID；动态区域通常为 tempzone。</summary>
    public string ZoneId { get; set; } = string.Empty;

    /// <summary>
    /// 光伏效率百分比。计划站默认 100%；导入站由调用方依据所在扇区初始化。
    /// </summary>
    public int SolarEfficiencyPercent { get; set; } = 100;

    /// <summary>存档站点直属 build@method；缺失时为空。</summary>
    public string BuildMethod { get; set; } = string.Empty;

    /// <summary>空间站相对扇区的坐标（区域偏移 + 站点在区域内的偏移）。</summary>
    public Vec3 SectorPosition { get; set; }

    /// <summary>原版星图对象图标键，例如 mapob_playerhq。</summary>
    public string IconKey { get; set; } = string.Empty;

    /// <summary>由星图布局计算出的显示坐标 X。</summary>
    public double DisplayX { get; set; }

    /// <summary>由星图布局计算出的显示坐标 Y。</summary>
    public double DisplayY { get; set; }

    /// <summary>
    /// 空间站上的生产模块列表。
    /// </summary>
    public List<ProductionModule> Modules { get; set; } = new();

    /// <summary>不直接产出商品的模块，例如居住区和仓储。</summary>
    public List<StationModule> AdditionalModules { get; set; } = new();

    /// <summary>保持实例 ID、当前建设序列和施工状态的模块列表。</summary>
    public List<StationModuleConstruction> ModuleConstructions { get; set; } = new();

    public List<StationSubordinate> Subordinates { get; set; } = new();

    public List<StationWareSetting> WareSettings { get; set; } = new();

    /// <summary>空间站直属 supplies/wares 中保存的补给材料状态。</summary>
    public List<StationSupplyWare> SupplyWares { get; set; } = new();

    /// <summary>空间站直属 supplies/orders 中保存的设备或无人机补给订单。</summary>
    public List<StationSupplyOrder> SupplyOrders { get; set; } = new();

    public List<StationTradeRule> TradeRules { get; set; } = new();
    public int? DefaultBuyTradeRuleId { get; set; }
    public int? DefaultSellTradeRuleId { get; set; }

    /// <summary>与当前空间站建设计划关联的建材仓库；没有已确认建设任务时为 null。</summary>
    public StationBuildStorage? BuildStorage { get; set; }

    public StationManager? Manager { get; set; }

    /// <summary>按种族保存的当前劳动力；总数见 <see cref="CurrentWorkforce"/>。</summary>
    public Dictionary<string, long> WorkforceByRace { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public long CurrentWorkforce => WorkforceByRace.Values.Sum();

    /// <summary>存档 workforces@lasttime；游戏内绝对秒数，缺失时为 null。</summary>
    public double? WorkforceLastUpdateTimeSeconds { get; set; }

    /// <summary>存档 workforces/bonus@endtime；下一次劳动力效率换班的绝对秒数。</summary>
    public double? WorkforceEfficiencyEndTimeSeconds { get; set; }

    /// <summary>存档 workforces/bonus@value；作为当前生产配方劳动力产品加成的覆盖比例。</summary>
    public double? WorkforceEfficiencyBonus { get; set; }

    public List<StationBlacklistSetting> Blacklists { get; set; } = new();
}
