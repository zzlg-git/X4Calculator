namespace X4Calculator.Core.Models;

/// <summary>存档中某个扇区的实时控制权。</summary>
public sealed record SectorOwnership(string SectorId, string Owner, bool IsContested);

/// <summary>一次存档导入产生的星图与空间站数据。</summary>
public sealed class SavegameImportResult
{
    public static SavegameImportResult Empty { get; } = new([], [], null, [], [], []);

    public SavegameImportResult(
        IReadOnlyList<Station> stations,
        IReadOnlyList<SectorOwnership> sectorOwnerships,
        double? gameTimeSeconds = null,
        IEnumerable<string>? playerBlueprintWareIds = null,
        IReadOnlyList<NpcStation>? npcStations = null,
        IReadOnlyList<SaveMapObject>? mapObjects = null)
    {
        Stations = stations;
        SectorOwnerships = sectorOwnerships;
        GameTimeSeconds = gameTimeSeconds;
        PlayerBlueprintWareIds = (playerBlueprintWareIds ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        NpcStations = npcStations ?? [];
        MapObjects = mapObjects ?? [];
    }

    public IReadOnlyList<Station> Stations { get; }
    public IReadOnlyList<SectorOwnership> SectorOwnerships { get; }
    /// <summary>存档 info/game@time；游戏内绝对秒数，缺失时为 null。</summary>
    public double? GameTimeSeconds { get; }

    /// <summary>
    /// 存档 blueprints/blueprint@ware 中的全部玩家蓝图 ware ID。
    /// 保留模块、舰船、装备、消耗品、改装和涂装等原始条目，由使用方按领域筛选。
    /// </summary>
    public IReadOnlySet<string> PlayerBlueprintWareIds { get; }

    /// <summary>全部非玩家空间站的轻量星图快照；不进入玩家站生产与运输模型。</summary>
    public IReadOnlyList<NpcStation> NpcStations { get; }

    /// <summary>无主舰船、数据保险库和妖王数据保险库的轻量星图快照。</summary>
    public IReadOnlyList<SaveMapObject> MapObjects { get; }
}
