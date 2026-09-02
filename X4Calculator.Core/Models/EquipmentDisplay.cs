namespace X4Calculator.Core.Models;

/// <summary>
/// 引擎/推进器显示名辅助（风格中文名、种族缩写）。
/// </summary>
public static class EquipmentDisplay
{
    /// <summary>
    /// 风格中文名映射。
    /// </summary>
    public static string StyleName(string style)
    {
        return style.ToLowerInvariant() switch
        {
            "allround" => "均衡",
            "combat" => "战斗",
            "travel" => "巡航",
            "frontier" => "尖端",
            _ => style
        };
    }

    /// <summary>
    /// 种族缩写（argon→ARG 等）。
    /// </summary>
    public static string RaceCode(string race)
    {
        return race.ToLowerInvariant() switch
        {
            "argon" => "ARG",
            "paranid" => "PAR",
            "teladi" => "TEL",
            "split" => "SPL",
            "terran" => "TER",
            "boron" => "BOR",
            _ => race.ToUpperInvariant()
        };
    }
}
