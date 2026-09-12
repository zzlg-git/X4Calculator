namespace X4Calculator.Core.Models;

/// <summary>存档中可投影到星图的特殊对象类别。</summary>
public enum SaveMapObjectKind
{
    OwnerlessShip,
    DataVault,
    ErlkingDataVault,
    LeapOfFaithAnomaly
}

/// <summary>存档中的特殊星图对象轻量快照。</summary>
public sealed class SaveMapObject
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string Macro { get; init; } = string.Empty;
    public string ObjectClass { get; init; } = string.Empty;
    public SaveMapObjectKind Kind { get; init; }
    public string SectorId { get; init; } = string.Empty;
    public string ZoneId { get; init; } = string.Empty;
    public Vec3 SectorPosition { get; init; }

    /// <summary>仅异常点使用；表示存档对象子树中存在 wormhole_active 特效。</summary>
    public bool IsActive { get; init; }

    /// <summary>由星图布局根据 SectorPosition 投影出的显示坐标。</summary>
    public double DisplayX { get; set; }
    public double DisplayY { get; set; }
    public bool HasValidProjection { get; set; }
}
