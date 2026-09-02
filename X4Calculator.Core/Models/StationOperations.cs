namespace X4Calculator.Core.Models;

/// <summary>空间站下属舰船及其在指挥官编组中的职责。</summary>
public sealed class StationSubordinate
{
    public string ShipId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Macro { get; init; } = string.Empty;
    public int GroupIndex { get; init; }
    public string Assignment { get; init; } = string.Empty;
}

/// <summary>空间站管理员的存档原始技能。</summary>
public sealed class StationManager
{
    public string ComponentId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int ManagementSkill { get; init; }
    public int MoraleSkill { get; init; }

    /// <summary>单独的管理技能星级；存档每 3 点为一星。</summary>
    public decimal ManagementStars => ManagementSkill / 3m;
}

/// <summary>一项买入或卖出报价。</summary>
public sealed class StationTradeOffer
{
    /// <summary>存档中的原始价格，单位为百分之一 Cr。</summary>
    public long PriceHundredths { get; init; }
    public decimal PriceCredits => PriceHundredths / 100m;
    public long Amount { get; init; }
    public long? DesiredAmount { get; init; }

    /// <summary>
    /// 存档 trade@flags 的原始值。X4 使用竖线分隔多个标志，例如
    /// supplies|invertfactionrestriction。
    /// </summary>
    public string Flags { get; init; } = string.Empty;

    /// <summary>该报价是否由空间站补给机制产生。</summary>
    public bool IsStationSupply => Flags
        .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Contains("supplies", StringComparer.OrdinalIgnoreCase);
}

/// <summary>空间站直属 supplies/wares 中的一项当前补给材料。</summary>
public sealed class StationSupplyWare
{
    public string WareId { get; init; } = string.Empty;
    public long Amount { get; init; }
}

/// <summary>空间站直属 supplies/orders 中的一项设备或无人机补给订单。</summary>
public sealed class StationSupplyOrder
{
    public string WareId { get; init; } = string.Empty;
    public long Amount { get; init; }
}

/// <summary>某种商品的贸易、价格覆盖和当前库存状态。</summary>
public enum StationStorageAllocationStatus
{
    /// <summary>存档没有显式的手动额度，且尚未由用户在规划器中选择状态。</summary>
    Unavailable,
    Automatic,
    Manual
}

public sealed class StationWareSetting
{
    public string WareId { get; init; } = string.Empty;
    /// <summary>站点直属 trade@wares 明确声明该商品参与站点贸易。</summary>
    public bool IsTradeWare { get; set; }
    /// <summary>
    /// true/false 表示报价方向已由存档或用户操作明确；null 表示存档没有显式方向记录。
    /// 活动买单缺失不能单独证明游戏 UI 中没有劳动力物资购买报价。
    /// </summary>
    public bool? BuyEnabled { get; set; }
    public bool SellEnabled { get; set; }
    /// <summary>普通生产/贸易购买报价；空间站补给报价单独保存在 StationSupplyBuyOffer。</summary>
    public StationTradeOffer? BuyOffer { get; set; }
    /// <summary>由 trade@flags 中 supplies 标志识别的空间站补给购买报价。</summary>
    public StationTradeOffer? StationSupplyBuyOffer { get; set; }
    public StationTradeOffer? SellOffer { get; set; }

    /// <summary>手动价格覆盖，单位 Cr；null 表示自动。</summary>
    public decimal? BuyPriceOverride { get; set; }
    public decimal? SellPriceOverride { get; set; }

    /// <summary>所有已建仓储组件中该商品的当前库存。</summary>
    public long CurrentAmount { get; set; }

    /// <summary>
    /// 手动仓储数量上限。X4 9.00 序列化为站点直属 overrides/max/ware@amount；
    /// null 表示没有显式手动覆盖，不表示自动额度为零。
    /// </summary>
    public long? StorageAllocationOverride { get; set; }
    public StationStorageAllocationStatus StorageAllocationStatus { get; set; }
        = StationStorageAllocationStatus.Unavailable;

    /// <summary>没有货物级覆盖时使用空间站的买入贸易规则。</summary>
    public bool UseStationBuyTradeRule { get; set; } = true;
    /// <summary>货物级买入贸易规则 ID；仅在不使用空间站设置时有意义。</summary>
    public int? BuyTradeRuleId { get; set; }
    /// <summary>没有货物级覆盖时使用空间站的卖出贸易规则。</summary>
    public bool UseStationSellTradeRule { get; set; } = true;
    /// <summary>货物级卖出贸易规则 ID；仅在不使用空间站设置时有意义。</summary>
    public int? SellTradeRuleId { get; set; }
}

/// <summary>玩家在存档中定义的一项全局贸易规则。</summary>
public sealed class StationTradeRule
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> Factions { get; init; } = Array.Empty<string>();
    public bool IsWhitelist { get; init; }
}

/// <summary>空间站建设计划关联的独立建材仓库。</summary>
public sealed class StationBuildStorage
{
    public string ComponentId { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public IReadOnlyList<StationBuildStorageWare> Wares { get; init; }
        = Array.Empty<StationBuildStorageWare>();

    /// <summary>建材仓库没有直属 buy 规则时为 true，并使用玩家全局买入规则。</summary>
    public bool UsesGlobalBuyTradeRule { get; init; }
    /// <summary>建材仓库直属 buy 规则原值；继承全局时为 null，-1 表示显式无限制。</summary>
    public int? BuyTradeRuleId { get; init; }
    public int? EffectiveBuyTradeRuleId { get; init; }
    public StationTradeRule? EffectiveBuyTradeRule { get; init; }
}

/// <summary>建材仓库中一种建设货物的需求、库存、在途和报价状态。</summary>
public sealed class StationBuildStorageWare
{
    public string WareId { get; init; } = string.Empty;
    public string WareName { get; init; } = string.Empty;
    /// <summary>当前模块剩余资源与后续建设序列资源之和。</summary>
    public long RequiredAmount { get; init; }
    public long CurrentAmount { get; init; }
    public IReadOnlyList<StationBuildStorageIncomingOrder> IncomingOrders { get; init; }
        = Array.Empty<StationBuildStorageIncomingOrder>();
    public long IncomingAmount => IncomingOrders.Sum(item => item.Amount);
    public StationTradeOffer? BuyOffer { get; init; }
    public long AvailableAndIncomingAmount => CurrentAmount + IncomingAmount;
    public long ShortfallAmount => Math.Max(0, RequiredAmount - AvailableAndIncomingAmount);
    public bool IsSatisfied => AvailableAndIncomingAmount >= RequiredAmount;

    /// <summary>该货物没有独立 buy 规则时为 true，并使用建材仓库规则。</summary>
    public bool UsesBuildStorageBuyTradeRule { get; init; }
    /// <summary>货物级 buy 规则原值；继承建材仓库时为 null，-1 表示显式无限制。</summary>
    public int? BuyTradeRuleId { get; init; }
    public int? EffectiveBuyTradeRuleId { get; init; }
    public StationTradeRule? EffectiveBuyTradeRule { get; init; }
}

/// <summary>一笔运往建材仓库的预约；Amount 是尚未交付的剩余数量。</summary>
public sealed class StationBuildStorageIncomingOrder
{
    public string ReservationId { get; init; } = string.Empty;
    public string ReserverId { get; init; } = string.Empty;
    public string PartnerId { get; init; } = string.Empty;
    public string SellerId { get; init; } = string.Empty;
    public long Amount { get; init; }
    public long? DesiredAmount { get; init; }
    public long TransferredAmount { get; init; }
    public long PriceHundredths { get; init; }
}

public enum StationModuleConstructionState
{
    Planned,
    UnderConstruction,
    Operational,
    Wrecked,
    Unknown
}

/// <summary>construction sequence 中保持顺序和实例身份的一项模块。</summary>
public sealed class StationModuleConstruction
{
    public string EntryId { get; init; } = string.Empty;
    public int Index { get; init; }
    public string ModuleId { get; init; } = string.Empty;
    public string Connection { get; init; } = string.Empty;
    public string RuntimeComponentId { get; set; } = string.Empty;
    public StationModuleConstructionState State { get; set; } = StationModuleConstructionState.Planned;
    public bool IsInStationSequence { get; set; }
    public string BuildOrderId { get; set; } = string.Empty;
}

public enum StationBlacklistSelection
{
    Inherited,
    Disabled,
    Explicit
}

/// <summary>玩家定义的一项全局黑名单。</summary>
public sealed class StationBlacklistRule
{
    public int Id { get; init; }
    public string Type { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string OwnerRelation { get; init; } = string.Empty;
    public IReadOnlyList<string> Factions { get; init; } = Array.Empty<string>();
    public bool IsWhitelist { get; init; }
}

/// <summary>站点对一种黑名单类型的局部选择及玩家全局默认。</summary>
public sealed class StationBlacklistSetting
{
    public string Type { get; init; } = string.Empty;
    public StationBlacklistSelection Selection { get; init; }
    /// <summary>站点局部 ref 原值；继承时为 null，显式关闭时为 -1。</summary>
    public int? ReferenceId { get; init; }
    public StationBlacklistRule? ExplicitRule { get; init; }
    public int? CivilianDefaultRuleId { get; init; }
    public StationBlacklistRule? CivilianDefaultRule { get; init; }
    public int? MilitaryDefaultRuleId { get; init; }
    public StationBlacklistRule? MilitaryDefaultRule { get; init; }
}
