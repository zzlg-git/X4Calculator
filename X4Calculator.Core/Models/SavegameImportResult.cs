namespace X4Calculator.Core.Models;

/// <summary>存档中某个扇区的实时控制权。</summary>
public sealed record SectorOwnership(string SectorId, string Owner, bool IsContested);

/// <summary>存档中某个 Cluster 星体的地表改造当前人口。</summary>
public sealed record TerraformingPopulation(string ClusterId, string WorldPart, long Population);

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
        IReadOnlyList<SaveMapObject>? mapObjects = null,
        IReadOnlyList<TerraformingPopulation>? terraformingPopulations = null,
        IReadOnlyList<SaveGateInstance>? gateInstances = null)
    {
        Stations = stations;
        SectorOwnerships = sectorOwnerships;
        GameTimeSeconds = gameTimeSeconds;
        PlayerBlueprintWareIds = (playerBlueprintWareIds ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        NpcStations = npcStations ?? [];
        MapObjects = mapObjects ?? [];
        TerraformingPopulations = terraformingPopulations ?? [];
        GateInstances = gateInstances ?? [];
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

    /// <summary>无主舰船、数据保险库、妖王数据保险库和信仰之跃异常点的轻量星图快照。</summary>
    public IReadOnlyList<SaveMapObject> MapObjects { get; }

    /// <summary>
    /// Cluster 的 terraforming/stats/stat[@id="population"] 当前值；
    /// WorldPart 对应 terraforming@part，由星图按 Sector 的 world@factor 投影。
    /// </summary>
    public IReadOnlyList<TerraformingPopulation> TerraformingPopulations { get; }
    /// <summary>同一存档的门实例；宏默认姿态只在有效 XML 连接闭合时补全。</summary>
    public IReadOnlyList<SaveGateInstance> GateInstances { get; }
}
