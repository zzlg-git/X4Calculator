using System.Globalization;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

/// <summary>按 X4 的默认显示风格为空间站生成名称。</summary>
public static class StationNameGenerator
{
    /// <summary>
    /// 使用“扇区 + 自动生成类型”的序号空间为空间站命名。
    /// 存档已有序号原样保留；仅在调用方明确要求时为缺失序号的新计划站分配值。
    /// </summary>
    public static void AssignGeneratedName(
        Station station,
        string? sectorName,
        IEnumerable<Station> stations,
        bool allocateIndexWhenMissing = false)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentNullException.ThrowIfNull(stations);

        if (station.GeneratedNameIndex is not > 0 && allocateIndexWhenMissing)
        {
            var typeName = ResolveGeneratedStationType(station);
            var usedIndexes = stations
                .Where(other => !ReferenceEquals(other, station)
                                && string.Equals(other.SectorId, station.SectorId,
                                    StringComparison.OrdinalIgnoreCase)
                                && string.Equals(ResolveGeneratedStationType(other), typeName,
                                    StringComparison.Ordinal))
                .Select(other => other.GeneratedNameIndex)
                .Where(index => index is > 0)
                .Select(index => index!.Value)
                .ToHashSet();
            station.GeneratedNameIndex = Enumerable.Range(1, int.MaxValue)
                .First(candidate => !usedIndexes.Contains(candidate));
        }

        station.Name = BuildGeneratedStationName(station, sectorName, station.GeneratedNameIndex);
    }

    /// <summary>
    /// 生成“扇区名 工厂类型 [罗马序号]”形式的空间站名称。
    /// 扇区显示名缺失时依次回退到空间站的扇区 ID 和“未知扇区”。
    /// </summary>
    public static string BuildGeneratedStationName(Station station, string? sectorName, int? nameIndex)
    {
        ArgumentNullException.ThrowIfNull(station);

        if (string.IsNullOrWhiteSpace(sectorName)) sectorName = station.SectorId;
        if (string.IsNullOrWhiteSpace(sectorName)) sectorName = "未知扇区";

        var typeName = ResolveGeneratedStationType(station);
        return nameIndex is > 0
            ? $"{sectorName} {typeName} {ToRoman(nameIndex.Value)}"
            : $"{sectorName} {typeName}";
    }

    private static string ResolveGeneratedStationType(Station station)
    {
        var facilityType = StationIconClassifier.Classify(station) switch
        {
            "mapob_playerhq" => "总部",
            "mapob_piratestation" => "海盗基地",
            "mapob_shipyard" => "船厂",
            "mapob_wharf" => "码头",
            "mapob_equipmentdock" => "装备坞",
            _ => null
        };
        if (facilityType != null) return facilityType;
        if (StationIconClassifier.IsScrapProcessingStation(station)) return "废料处理站";

        var productionType = StationIconClassifier.ResolveProductionGroup(station)?.ToLowerInvariant() switch
        {
            "energy" => "太阳能发电厂",
            "hightech" => "高科技产品工厂",
            "pharmaceutical" => "制药厂",
            "food" => "农场",
            "agricultural" => "农产品工厂",
            "refined" => "精炼产品复合工厂",
            "shiptech" => "飞船技术工厂",
            "weapontech" => "武器技术工厂",
            "water" => "净化水厂",
            "gases" => "气体精炼厂",
            "minerals" => "矿物精炼厂",
            "ice" => "冰精炼厂",
            _ => null
        };
        if (productionType != null) return productionType;

        return StationIconClassifier.Classify(station) switch
        {
            "mapob_tradestation" => "贸易空间站",
            "mapob_defensestation" => "防御平台",
            _ => "工厂"
        };
    }

    private static string ToRoman(int value)
    {
        if (value is <= 0 or > 3999) return value.ToString(CultureInfo.InvariantCulture);
        (int Value, string Symbol)[] symbols =
        [
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
            (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
        ];
        var result = new System.Text.StringBuilder();
        foreach (var (number, symbol) in symbols)
        {
            while (value >= number)
            {
                result.Append(symbol);
                value -= number;
            }
        }
        return result.ToString();
    }
}
