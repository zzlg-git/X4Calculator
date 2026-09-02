using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

/// <summary>
/// 为无法查询游戏引擎最终图标的空间站选择原版图标键。
/// 工厂生产组按 X4 9.00 waregroups.xml/@priority 的降序选择；该规则已通过
/// GetComponentName/GetComponentIcon 运行时样本验证。设施类仍保留明确的项目回退顺序。
/// </summary>
public static class StationIconClassifier
{
    public static IReadOnlyList<string> NativeProductionGroupPriority { get; } =
    [
        "hightech", "shiptech", "minerals", "refined", "pharmaceutical", "food",
        "agricultural", "water", "gases", "ice", "energy"
    ];

    public static string Classify(Station station)
    {
        ArgumentNullException.ThrowIfNull(station);

        var moduleIds = station.Modules.Select(item => item.ModuleId)
            .Concat(station.AdditionalModules.Select(item => item.ModuleId))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
        var allMacroIds = moduleIds.Prepend(station.Macro).ToArray();

        if (station.Modules.Count == 0 && station.AdditionalModules.Count == 0)
            return "mapob_constructionsite";
        if (allMacroIds.Any(id => id.Contains("player_hq_", StringComparison.OrdinalIgnoreCase)) ||
            station.Macro.Contains("headquarters", StringComparison.OrdinalIgnoreCase))
            return "mapob_playerhq";
        if (station.Macro.Contains("_piratebase", StringComparison.OrdinalIgnoreCase))
            return "mapob_piratestation";
        if (moduleIds.Any(id => id.Contains("_ships_xl_", StringComparison.OrdinalIgnoreCase) ||
                                id.Contains("_ships_l_", StringComparison.OrdinalIgnoreCase)))
            return "mapob_shipyard";
        if (moduleIds.Any(id => id.Contains("_ships_m_", StringComparison.OrdinalIgnoreCase)))
            return "mapob_wharf";
        if (moduleIds.Any(id => id.Contains("_equip_", StringComparison.OrdinalIgnoreCase)))
            return "mapob_equipmentdock";
        if (IsScrapProcessingStation(station))
            return "mapob_factory";
        if (station.Modules.Count > 0)
            return ResolveProductionGroup(station) switch
            {
                "shiptech" => "mapob_shiptech",
                "hightech" => "mapob_hightech",
                "refined" or "minerals" or "gases" => "mapob_refined",
                "pharmaceutical" => "mapob_pharmaceutical",
                "food" => "mapob_food",
                "agricultural" => "mapob_agricultural",
                "water" or "ice" => "mapob_water",
                "energy" => "mapob_energy",
                _ => "mapob_factory"
            };
        if (IsTradeStation(station, allMacroIds))
            return "mapob_tradestation";
        if (station.AdditionalModules.Any(item =>
                item.Kind.Equals("defencemodule", StringComparison.OrdinalIgnoreCase)))
            return "mapob_defensestation";
        return "mapob_factory";
    }

    public static string? ResolveProductionGroup(Station station)
    {
        ArgumentNullException.ThrowIfNull(station);

        var groups = station.Modules.Select(ResolveModuleGroup)
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return NativeProductionGroupPriority.FirstOrDefault(groups.Contains);
    }

    public static bool IsScrapProcessingStation(Station station)
    {
        ArgumentNullException.ThrowIfNull(station);
        return station.Modules.Select(item => item.ModuleId)
            .Concat(station.AdditionalModules.Select(item => item.ModuleId))
            .Any(id => id.Contains("scrap_recycler", StringComparison.OrdinalIgnoreCase)
                       || id.Contains("scrapworks", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResolveModuleGroup(ProductionModule module) => module.Ware?.Group;

    private static bool IsTradeStation(Station station, IReadOnlyCollection<string> allMacroIds)
    {
        if (allMacroIds.Any(id => id.Contains("tradestation", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (station.Modules.Count > 0) return false;

        var hasStorage = station.AdditionalModules.Any(item =>
            item.Kind.Equals("storage", StringComparison.OrdinalIgnoreCase));
        var hasDocking = station.AdditionalModules.Any(item =>
            item.Kind.Equals("dockarea", StringComparison.OrdinalIgnoreCase)
            || item.Kind.Equals("pier", StringComparison.OrdinalIgnoreCase));
        var hasTradeWare = station.WareSettings.Any(setting =>
            setting.BuyEnabled == true
            || setting.SellEnabled
            || setting.BuyOffer != null
            || setting.SellOffer != null);
        return hasStorage && hasDocking && hasTradeWare;
    }
}
