namespace X4Calculator.Core.Models;

/// <summary>
/// 存档中的 NPC 空间站星图快照。它只保留呈现和黑市筛选所需字段，
/// 不参与玩家空间站的生产、仓储或运输计算。
/// </summary>
public sealed class NpcStation
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string Owner { get; init; } = string.Empty;
    public string Macro { get; init; } = string.Empty;
    public string IconKey { get; init; } = string.Empty;
    public string SectorId { get; init; } = string.Empty;
    public string ZoneId { get; init; } = string.Empty;
    public Vec3 SectorPosition { get; init; }

    /// <summary>由星图布局根据 SectorPosition 投影出的显示坐标。</summary>
    public double DisplayX { get; set; }
    public double DisplayY { get; set; }
    public bool HasValidProjection { get; set; }

    /// <summary>control/post[@id='shadyguy']/@component 原值。</summary>
    public string BlackMarketTraderComponentId { get; init; } = string.Empty;

    /// <summary>岗位引用目标 NPC 的存档名称；引用无法解析时为空。</summary>
    public string BlackMarketTraderName { get; init; } = string.Empty;

    public bool HasBlackMarketTrader => !string.IsNullOrWhiteSpace(BlackMarketTraderComponentId);
    public bool IsBlackMarketTraderResolved { get; init; }

    /// <summary>目标 NPC traits@flags 含独立 tradesvisible 标志。</summary>
    public bool IsBlackMarketTraderUnlocked { get; init; }

    /// <summary>保存时该站子树内 class='signalleak' 且 type='voice' 的实例数。</summary>
    public int CurrentVoiceSignalLeakCount { get; init; }

    public bool IsWreck { get; init; }

    /// <summary>Kha'ak 高亮只包含仍有效的巢穴/虫巢，不包含武器平台。</summary>
    public bool IsKhaakHighlightTarget =>
        Owner.Equals("khaak", StringComparison.OrdinalIgnoreCase) &&
        !Macro.Contains("weaponplatform", StringComparison.OrdinalIgnoreCase) &&
        !IsWreck;
}
