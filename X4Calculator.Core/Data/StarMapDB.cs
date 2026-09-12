using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using X4Calculator.Core.Models;
using X4Calculator.Core.Parsing;

namespace X4Calculator.Core.Data;

/// <summary>
/// X4 星图数据加载器。
/// 从 GAMEDATA 解包目录解析星系（Galaxy → Cluster → Sector → Zone）层级、
/// 星门/轨道加速器位置与连接关系，并可结合存档扫描结果校验/补全动态门。
///
/// 数据源：
///   maps/xu_ep2_universe/                 —— 主星图（galaxy/clusters/sectors/zones/sechighways）
///   extensions/ego_dlc_*/maps/xu_ep2_universe/ —— 各 DLC 星图（*_clusters.xml 等 + galaxy.xml）
///   libraries/mapdefaults.xml             —— 星区/扇区名称文本引用（英文+中文）
/// </summary>
public class StarMapDB
{
    /// <summary>所有星区，key = Cluster macro ID（如 "Cluster_01_macro"）。</summary>
    public Dictionary<string, ClusterInfo> Clusters { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>所有扇区，key = Sector macro ID（如 "Cluster_01_Sector001_macro"）。</summary>
    public Dictionary<string, SectorInfo> Sectors { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>所有星门/轨道加速器，key = 门连接名（如 "connection_ClusterGate001To004"）。</summary>
    public Dictionary<string, GateInfo> Gates { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>星区内扇区间连接（超级高速路）。</summary>
    public List<SectorLink> SectorLinks { get; } = new();

    /// <summary>存档中扫描到的门实例（含动态门）。</summary>
    public List<SaveGateInstance> SaveGates { get; } = new();
    private readonly Dictionary<string, (string? Code, string? Target)> _staticGateIdentity =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前导入存档中的玩家空间站。</summary>
    public List<Station> PlayerStations { get; } = new();

    /// <summary>当前导入存档中的 NPC 空间站轻量星图快照。</summary>
    public List<NpcStation> NpcStations { get; } = new();

    /// <summary>当前导入存档中的无主舰船与保险库轻量星图快照。</summary>
    public List<SaveMapObject> SaveMapObjects { get; } = new();

    /// <summary>当前星图数据中最低的扇区光伏效率百分比。</summary>
    public int MinSunlightPercent { get; private set; } = 100;

    /// <summary>当前星图数据中最高的扇区光伏效率百分比。</summary>
    public int MaxSunlightPercent { get; private set; } = 100;

    /// <summary>导入或取消导入导致存档星图数据变化时触发。</summary>
    public event EventHandler? PlayerStationsChanged;

    // 英文文本查找表：pageId → textId → text
    private readonly Dictionary<string, Dictionary<string, string>> _enPages = new(StringComparer.OrdinalIgnoreCase);

    // 简体中文文本查找表：X4 的扇区名称通常已采用“英文｜中文”格式。
    private readonly Dictionary<string, Dictionary<string, string>> _zhPages = new(StringComparer.OrdinalIgnoreCase);

    // macro ID → 名称文本引用（来自 mapdefaults.xml identification@name）
    private readonly Dictionary<string, string> _nameIds = new(StringComparer.OrdinalIgnoreCase);

    // sector/cluster macro ID → 光照系数（来自 mapdefaults.xml area@sunlight）
    private readonly Dictionary<string, double> _sunlightByMacro = new(StringComparer.OrdinalIgnoreCase);

    // cluster macro → 物理恒星系统键；相同 identification@system 的多个 Cluster 共用星体定义。
    private readonly Dictionary<string, string> _systemByCluster = new(StringComparer.OrdinalIgnoreCase);

    // 恒星系统键 → 星体 part → 最大人口，以及 sector macro → world 引用。
    private readonly Dictionary<string, Dictionary<string, long>> _populationBySystemPart = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(string Part, double Factor)>> _worldsBySector = new(StringComparer.OrdinalIgnoreCase);

    // sectorId → zoneId → zone 在 sector 内的偏移（来自 sectors.xml）
    private readonly Dictionary<string, Dictionary<string, Vec3>> _zoneOffsets = new(StringComparer.OrdinalIgnoreCase);

    // 游戏官方 colors.xml 中的 faction_* RGB 映射。
    private readonly Dictionary<string, string> _factionColors = new(StringComparer.OrdinalIgnoreCase);

    // factions.xml 中具备领土宣称能力的势力；god.xml 的其他站点不能决定扇区归属。
    private readonly HashSet<string> _claimspaceFactions = new(StringComparer.OrdinalIgnoreCase);

    // god.xml 显式 constructionplan/station macro 是否包含 ownership claim="1" 的解析索引。
    private readonly Dictionary<string, string> _macroPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _constructionPlanModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _macroClaimCache = new(StringComparer.OrdinalIgnoreCase);

    // 未导入存档时的静态默认归属，用于取消存档导入后恢复。
    private readonly Dictionary<string, string> _defaultSectorOwners = new(StringComparer.OrdinalIgnoreCase);

    // 文本引用 {pageId,textId}（逗号前后允许空格）
    private static readonly Regex TextRefRegex = new(@"\{(\d+)\s*,\s*(\d+)\}", RegexOptions.Compiled);

    // 门连接名：connection_ClusterGate{所在Cluster}To{目标Cluster}
    private static readonly Regex GateNameRegex = new(
        @"connection_ClusterGate(?<src>\d+)To(?<dst>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 扇区 macro：Cluster_NN_SectorNNN_macro
    private static readonly Regex SectorMacroRegex = new(
        @"Cluster_(\d+)_Sector(\d+)_macro",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Zone macro 中的 cluster/sector 编号（Zone003_Cluster_01_Sector001_macro / tzoneCluster_01_Sector001SHCon5_GateZone_macro）
    private static readonly Regex ZoneMacroRegex = new(
        @"Cluster_(\d+)_Sector(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // sechighway 连接 path 中的 sector 引用：../../Cluster_01_Sector002_connection/...
    private static readonly Regex SectorPathRegex = new(
        @"Cluster_(\d+)_Sector(\d+)_connection",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // X4 英文文本中的发音/显示注释 (...)（如 "Grand Exchange(Grand Exchange) I(speak as 1)"），游戏内不显示
    private static readonly Regex PronounceNoteRegex = new(@"\([^)]*\)", RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>
    /// 加载星图数据（主星图 + 全部 DLC），可选结合存档校验动态门。
    /// </summary>
    /// <param name="gameDataPath">GameData 根目录路径。</param>
    /// <param name="saveFilePath">可选存档文件路径，提供后流式扫描门实例。</param>
    public Task LoadAsync(string gameDataPath, string? saveFilePath = null) =>
        LoadAsync(gameDataPath, saveFilePath, null);

    /// <summary>加载星图并使用调用方的有效 XML 解析器补全宏默认存档姿态。</summary>
    public async Task LoadAsync(string gameDataPath, string? saveFilePath,
        SavegameGateTransformResolver? gateTransformResolver)
    {
        var dataDir = ResolveDataDir(gameDataPath);

        // 1. 文本（英文+中文）
        LoadTexts(dataDir);

        // 2. 名称文本引用（mapdefaults，主 + DLC）
        LoadMapDefaults(dataDir);

        // 3. galaxy.xml → Cluster 银河坐标
        LoadGalaxy(dataDir);

        // 4. clusters.xml → Sector 位置 + 超级高速路连接
        LoadClusters(dataDir);

        // 4b. Sector 环境：光照优先自身、缺失时继承 Cluster；人口按 worlds 引用折算。
        ResolveSectorEnvironment();

        // 5. sectors.xml → Zone 位置
        LoadSectors(dataDir);

        // 6. zones.xml → 星门/轨道加速器
        LoadZones(dataDir);

        // 6b. 超级高速路端点银河坐标（依赖 zone 偏移表，须在 LoadSectors/LoadZones 之后）
        ComputeSectorLinkWorldPositions();

        // 7. 官方势力颜色、可宣称领土的势力，以及 god.xml 默认归属
        LoadFactionMetadata(dataDir);
        LoadOwners(dataDir);

        // 8. 名称解析（英文）
        ResolveNames();

        // 9. 星图显示布局（与游戏内星图一致的轴向网格 + 模板槽位 + 门归一化）
        ComputeLayout();

        // 10. 存档门扫描（可选）
        if (!string.IsNullOrEmpty(saveFilePath) && File.Exists(saveFilePath))
        {
            var scanner = new SavegameGateScanner();
            var saveGates = await scanner.ScanAsync(saveFilePath);
            if (gateTransformResolver is not null)
                foreach (var gate in saveGates)
                    gateTransformResolver.Resolve(gate).ApplyTo(gate);
            ReplaceSaveGates(saveGates);
        }
    }

    /// <summary>查找指定 Cluster 的显示名（"英文|中文"）。</summary>
    public ClusterInfo? FindCluster(string clusterId) => Clusters.GetValueOrDefault(clusterId);

    /// <summary>查找指定 Sector 的显示名。</summary>
    public SectorInfo? FindSector(string sectorId) => Sectors.GetValueOrDefault(sectorId);

    /// <summary>
    /// 把相对扇区的游戏坐标投影为星图显示坐标。
    /// 超出 flat-top 扇区六边形的位置会沿扇区中心方向限制到边界，
    /// 使存档中超出静态 zone 范围的动态空间站仍能安全显示。
    /// </summary>
    /// <remarks>显示坐标是 <see cref="SectorInfo.DisplayX"/> / <see cref="SectorInfo.DisplayY"/> 使用的星图单位，不是 WPF 屏幕坐标。</remarks>
    public bool TryProjectSectorPosition(
        string sectorId,
        Vec3 sectorPosition,
        out double displayX,
        out double displayY)
    {
        displayX = 0;
        displayY = 0;
        if (!double.IsFinite(sectorPosition.X) || !double.IsFinite(sectorPosition.Z) ||
            !TryGetSectorProjection(sectorId, out var sector, out var center, out var scalePerRadius))
            return false;

        var normalizedX = (sectorPosition.X - center.X) * scalePerRadius;
        var normalizedY = -(sectorPosition.Z - center.Z) * scalePerRadius;
        if (!double.IsFinite(normalizedX) || !double.IsFinite(normalizedY)) return false;
        ClampToNormalizedSectorHex(ref normalizedX, ref normalizedY);
        displayX = sector.DisplayX + normalizedX * sector.DisplayRadius;
        displayY = sector.DisplayY + normalizedY * sector.DisplayRadius;
        return true;
    }

    /// <summary>
    /// 把 flat-top 扇区六边形内的星图显示坐标反算为相对扇区的游戏坐标。
    /// 二维星图不表达垂直位置，因此返回值的 Y 固定为 0。
    /// </summary>
    /// <returns>坐标位于指定扇区六边形内时返回 true；扇区不存在或坐标在六边形外时返回 false。</returns>
    public bool TryUnprojectSectorPosition(
        string sectorId,
        double displayX,
        double displayY,
        out Vec3 sectorPosition)
    {
        sectorPosition = Vec3.Zero;
        if (!double.IsFinite(displayX) || !double.IsFinite(displayY) ||
            !TryGetSectorProjection(sectorId, out var sector, out var center, out var scalePerRadius))
            return false;

        var normalizedX = (displayX - sector.DisplayX) / sector.DisplayRadius;
        var normalizedY = (displayY - sector.DisplayY) / sector.DisplayRadius;
        if (!IsInsideNormalizedSectorHex(normalizedX, normalizedY)) return false;

        sectorPosition = new Vec3(
            center.X + normalizedX / scalePerRadius,
            0,
            center.Z - normalizedY / scalePerRadius);
        return true;
    }

    /// <summary>把存档 player@location 文本引用解析为游戏中文界面使用的完整位置名。</summary>
    public string? ResolveLocalizedLocationName(string? textReference)
    {
        var resolved = CleanName(ResolveText(_zhPages, textReference), string.Empty);
        return string.IsNullOrWhiteSpace(resolved) || TextRefRegex.IsMatch(resolved) ? null : resolved;
    }

    /// <summary>按门连接名（大小写不敏感）查找星门。</summary>
    public GateInfo? FindGate(string gateConnectionName) => Gates.GetValueOrDefault(gateConnectionName);

    /// <summary>替换存档空间站覆盖层，并计算各站点在星图中的显示坐标。</summary>
    public void SetPlayerStations(IEnumerable<Station> stations)
    {
        ReplacePlayerStations(stations);
        PlayerStationsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>取得游戏 colors.xml 中指定势力的星图颜色。</summary>
    public string ResolveFactionColorHex(string? owner) => ResolveFactionColor(owner);

    /// <summary>应用一次存档导入的空间站、实时扇区归属和地表改造人口；空结果恢复静态默认值。</summary>
    public void ApplySavegameData(SavegameImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        RestoreDefaultOwners();
        foreach (var ownership in result.SectorOwnerships)
        {
            if (!Sectors.TryGetValue(ownership.SectorId, out var sector)) continue;
            sector.Owner = string.IsNullOrWhiteSpace(ownership.Owner) ? "ownerless" : ownership.Owner;
            sector.IsContested = ownership.IsContested;
        }

        RefreshOwnershipDisplay();
        ApplyTerraformingPopulations(result.TerraformingPopulations);
        ReplacePlayerStations(result.Stations);
        ReplaceNpcStations(result.NpcStations);
        ReplaceSaveMapObjects(result.MapObjects);
        ReplaceSaveGates(result.GateInstances);
        PlayerStationsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReplacePlayerStations(IEnumerable<Station> stations)
    {
        PlayerStations.Clear();
        PlayerStations.AddRange(stations);
        foreach (var station in PlayerStations)
            ProjectStation(station);
    }

    private void ReplaceNpcStations(IEnumerable<NpcStation> stations)
    {
        NpcStations.Clear();
        NpcStations.AddRange(stations);
        foreach (var station in NpcStations)
        {
            station.HasValidProjection = false;
            if (!TryProjectSectorPosition(
                    station.SectorId, station.SectorPosition, out var displayX, out var displayY))
                continue;
            station.DisplayX = displayX;
            station.DisplayY = displayY;
            station.HasValidProjection = double.IsFinite(displayX) && double.IsFinite(displayY);
        }
    }

    private void ReplaceSaveMapObjects(IEnumerable<SaveMapObject> objects)
    {
        SaveMapObjects.Clear();
        SaveMapObjects.AddRange(objects);
        foreach (var mapObject in SaveMapObjects)
        {
            mapObject.HasValidProjection = false;
            if (!TryProjectSectorPosition(
                    mapObject.SectorId, mapObject.SectorPosition, out var displayX, out var displayY))
                continue;
            mapObject.DisplayX = displayX;
            mapObject.DisplayY = displayY;
            mapObject.HasValidProjection = double.IsFinite(displayX) && double.IsFinite(displayY);
        }
    }

    private void RestoreDefaultOwners()
    {
        foreach (var sector in Sectors.Values)
        {
            sector.Owner = _defaultSectorOwners.GetValueOrDefault(sector.Id, "ownerless");
            sector.IsContested = false;
        }
    }

    private void ApplyTerraformingPopulations(
        IReadOnlyList<TerraformingPopulation> terraformingPopulations)
    {
        var overrides = new Dictionary<string, Dictionary<string, long>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in terraformingPopulations)
        {
            if (string.IsNullOrWhiteSpace(item.ClusterId) ||
                string.IsNullOrWhiteSpace(item.WorldPart) || item.Population < 0)
                continue;
            if (!overrides.TryGetValue(item.ClusterId, out var parts))
                overrides[item.ClusterId] = parts = new(StringComparer.OrdinalIgnoreCase);
            parts[item.WorldPart] = item.Population;
        }

        foreach (var sector in Sectors.Values)
            sector.Population = CalculateSectorPopulation(sector, overrides);
    }

    private void ProjectStation(Station station)
    {
        if (!TryProjectSectorPosition(
                station.SectorId, station.SectorPosition, out var displayX, out var displayY))
            return;

        station.DisplayX = displayX;
        station.DisplayY = displayY;
    }

    private bool TryGetSectorProjection(
        string sectorId,
        out SectorInfo sector,
        out Vec3 center,
        out double scalePerRadius)
    {
        sector = null!;
        center = Vec3.Zero;
        scalePerRadius = 0;
        if (!Sectors.TryGetValue(sectorId, out var resolvedSector) ||
            !double.IsFinite(resolvedSector.DisplayX) || !double.IsFinite(resolvedSector.DisplayY) ||
            !double.IsFinite(resolvedSector.DisplayRadius) || resolvedSector.DisplayRadius <= 0)
            return false;

        sector = resolvedSector;
        center = ComputeSectorCenterLocal(sectorId);
        var maxExtent = ComputeSectorMaxExtent(sectorId, center);
        scalePerRadius = (Math.Sqrt(3.0) / 2.0 * 0.8) / Math.Max(1.0, maxExtent);
        return double.IsFinite(scalePerRadius) && scalePerRadius > 0;
    }

    private static bool IsInsideNormalizedSectorHex(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
        const double tolerance = 1e-9;
        var sqrt3 = Math.Sqrt(3.0);
        var absX = Math.Abs(x);
        var absY = Math.Abs(y);
        return absY <= sqrt3 / 2.0 + tolerance &&
               sqrt3 * absX + absY <= sqrt3 + tolerance;
    }

    private static void ClampToNormalizedSectorHex(ref double x, ref double y)
    {
        if (IsInsideNormalizedSectorHex(x, y)) return;

        var sqrt3 = Math.Sqrt(3.0);
        var absX = Math.Abs(x);
        var absY = Math.Abs(y);
        var factor = 1.0;
        if (absY > sqrt3 / 2.0)
            factor = Math.Min(factor, sqrt3 / 2.0 / absY);
        var diagonalExtent = sqrt3 * absX + absY;
        if (diagonalExtent > sqrt3)
            factor = Math.Min(factor, sqrt3 / diagonalExtent);
        x *= factor;
        y *= factor;
    }

    // ─── 目录与文本 ───────────────────────────────────────────────

    private static string ResolveDataDir(string gameDataPath)
    {
        var dataDir = Path.GetFullPath(gameDataPath);
        GameDataDirectory.EnsureUsable(dataDir);
        return dataDir;
    }

    private void LoadTexts(string dataDir)
    {
        var tDir = Path.Combine(dataDir, "t");
        LoadTextFile(Path.Combine(tDir, "0001-l044.xml"), _enPages);
        LoadTextFile(Path.Combine(tDir, "0001-l086.xml"), _zhPages);
    }

    private static void LoadTextFile(string path, Dictionary<string, Dictionary<string, string>> pages)
    {
        if (!File.Exists(path)) return;
        var doc = XDocument.Load(path);
        if (doc.Root == null) return;
        foreach (var pageElem in doc.Root.Elements("page"))
        {
            var pageId = pageElem.Attribute("id")?.Value;
            if (string.IsNullOrEmpty(pageId)) continue;
            var pageDict = new Dictionary<string, string>();
            foreach (var tElem in pageElem.Elements("t"))
            {
                var tId = tElem.Attribute("id")?.Value;
                if (tId != null) pageDict[tId] = tElem.Value;
            }
            pages[pageId] = pageDict;
        }
    }

    /// <summary>
    /// 收集主星图 + 全部 DLC 的同类地图文件。
    /// </summary>
    private static IEnumerable<string> CollectMapFiles(string dataDir, string fixedName, string dlcPattern)
    {
        var mapDir = Path.Combine(dataDir, "maps", "xu_ep2_universe");
        var fixedPath = Path.Combine(mapDir, fixedName);
        if (File.Exists(fixedPath)) yield return fixedPath;

        var extensionsDir = Path.Combine(dataDir, "extensions");
        if (!Directory.Exists(extensionsDir)) yield break;
        foreach (var ext in Directory.GetDirectories(extensionsDir, "ego_dlc_*"))
        {
            var extMapDir = Path.Combine(ext, "maps", "xu_ep2_universe");
            if (!Directory.Exists(extMapDir)) continue;
            foreach (var f in Directory.GetFiles(extMapDir, dlcPattern))
                yield return f;
        }
    }

    private static IEnumerable<string> CollectMapDefaultsFiles(string dataDir)
    {
        var main = Path.Combine(dataDir, "libraries", "mapdefaults.xml");
        if (File.Exists(main)) yield return main;

        var extensionsDir = Path.Combine(dataDir, "extensions");
        if (!Directory.Exists(extensionsDir)) yield break;
        foreach (var ext in Directory.GetDirectories(extensionsDir, "ego_dlc_*"))
        {
            var f = Path.Combine(ext, "libraries", "mapdefaults.xml");
            if (File.Exists(f)) yield return f;
        }
    }

    // ─── 名称解析 ─────────────────────────────────────────────────

    private void LoadMapDefaults(string dataDir)
    {
        foreach (var file in CollectMapDefaultsFiles(dataDir))
        {
            var doc = XDocument.Load(file);
            if (doc.Root == null) continue;
            foreach (var dataset in doc.Root.Elements("dataset"))
            {
                var macro = dataset.Attribute("macro")?.Value;
                if (string.IsNullOrEmpty(macro)) continue;
                var nameRef = dataset.Element("properties")?.Element("identification")?.Attribute("name")?.Value;
                if (!string.IsNullOrEmpty(nameRef)) _nameIds[macro] = nameRef;

                var properties = dataset.Element("properties");
                var identification = properties?.Element("identification");
                var system = properties?.Element("system");
                var systemId = identification?.Attribute("system")?.Value;
                if (system != null || !string.IsNullOrWhiteSpace(systemId))
                {
                    var systemKey = string.IsNullOrWhiteSpace(systemId) ? $"cluster:{macro}" : $"system:{systemId}";
                    _systemByCluster[macro] = systemKey;
                    if (!_populationBySystemPart.TryGetValue(systemKey, out var populations))
                        _populationBySystemPart[systemKey] = populations = new(StringComparer.OrdinalIgnoreCase);
                    foreach (var world in (system?.Descendants() ?? []).Where(element =>
                                 element.Name.LocalName is "planet" or "moon"))
                    {
                        var part = world.Attribute("part")?.Value;
                        if (string.IsNullOrWhiteSpace(part) ||
                            !long.TryParse(world.Attribute("maxpopulation")?.Value,
                                NumberStyles.Integer, CultureInfo.InvariantCulture, out var population)) continue;
                        populations[part] = population;
                    }
                }

                var worlds = properties?.Element("worlds")?.Elements("world")
                    .Select(world => (
                        Part: world.Attribute("part")?.Value ?? string.Empty,
                        Factor: double.TryParse(world.Attribute("factor")?.Value, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var factor) ? factor : 1d))
                    .Where(world => !string.IsNullOrWhiteSpace(world.Part))
                    .ToList();
                if (worlds is { Count: > 0 }) _worldsBySector[macro] = worlds;

                var sunlightText = properties?.Element("area")?.Attribute("sunlight")?.Value;
                if (double.TryParse(sunlightText, NumberStyles.Float, CultureInfo.InvariantCulture, out var sunlight) &&
                    sunlight > 0)
                {
                    _sunlightByMacro[macro] = sunlight;
                }
            }
        }
    }

    private void ResolveSectorEnvironment()
    {
        foreach (var sector in Sectors.Values)
        {
            if (_sunlightByMacro.TryGetValue(sector.Id, out var sectorSunlight))
                sector.SunlightFactor = sectorSunlight;
            else if (_sunlightByMacro.TryGetValue(sector.ClusterId, out var clusterSunlight))
                sector.SunlightFactor = clusterSunlight;
            else
                sector.SunlightFactor = 1.0;

            sector.Population = CalculateSectorPopulation(sector);
        }

        if (Sectors.Count == 0)
        {
            MinSunlightPercent = MaxSunlightPercent = 100;
            return;
        }

        MinSunlightPercent = Sectors.Values.Min(sector => sector.SunlightPercent);
        MaxSunlightPercent = Sectors.Values.Max(sector => sector.SunlightPercent);
    }

    private long CalculateSectorPopulation(
        SectorInfo sector,
        IReadOnlyDictionary<string, Dictionary<string, long>>? terraformingPopulations = null)
    {
        if (!_worldsBySector.TryGetValue(sector.Id, out var worlds)) return 0;

        Dictionary<string, long>? terraformingParts = null;
        terraformingPopulations?.TryGetValue(sector.ClusterId, out terraformingParts);
        Dictionary<string, long>? staticPopulations = null;
        if (_systemByCluster.TryGetValue(sector.ClusterId, out var systemKey))
            _populationBySystemPart.TryGetValue(systemKey, out staticPopulations);

        return (long)Math.Round(worlds.Sum(world =>
        {
            if (terraformingParts?.TryGetValue(world.Part, out var currentPopulation) == true)
                return currentPopulation * world.Factor;
            return staticPopulations?.TryGetValue(world.Part, out var maximumPopulation) == true
                ? maximumPopulation * world.Factor
                : 0;
        }), MidpointRounding.AwayFromZero);
    }

    private void ResolveNames()
    {
        foreach (var cluster in Clusters.Values)
        {
            var name = ResolveText(_enPages, _nameIds.GetValueOrDefault(cluster.Id));
            cluster.Name = CleanName(name, cluster.Id);
        }

        foreach (var sector in Sectors.Values)
        {
            var nameRef = _nameIds.GetValueOrDefault(sector.Id);
            var name = ResolveText(_enPages, nameRef);
            sector.Name = CleanName(name, sector.Id);
            sector.SearchName = BuildSearchName(sector.Name, ResolveText(_zhPages, nameRef));
        }
    }

    private static string BuildSearchName(string englishName, string localizedName)
    {
        var cleaned = CleanName(localizedName, string.Empty);
        if (string.IsNullOrWhiteSpace(cleaned) || TextRefRegex.IsMatch(cleaned)) return englishName;

        var parts = cleaned.Split(['｜', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return englishName;
        if (parts.Length == 1)
            return parts[0].Equals(englishName, StringComparison.OrdinalIgnoreCase)
                ? englishName
                : $"{englishName}｜{parts[0]}";

        // X4 的 l086 地名约定为“英文｜中文”；取最后一段可兼容半角/全角分隔符。
        var chineseName = parts[^1];
        return string.IsNullOrWhiteSpace(chineseName) ? englishName : $"{englishName}｜{chineseName}";
    }

    /// <summary>清理英文名称：去掉发音/显示注释括号，合并多余空白。</summary>
    private static string CleanName(string raw, string fallback)
    {
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        var cleaned = PronounceNoteRegex.Replace(raw, string.Empty);
        cleaned = WhitespaceRegex.Replace(cleaned, " ").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    // ─── 势力颜色与默认归属（colors.xml / factions.xml / god.xml）───

    private void LoadFactionMetadata(string dataDir)
    {
        _factionColors.Clear();
        _claimspaceFactions.Clear();

        var colorsPath = Path.Combine(dataDir, "libraries", "colors.xml");
        if (File.Exists(colorsPath))
        {
            var doc = XDocument.Load(colorsPath);
            var colors = doc.Descendants("color")
                .Where(element => element.Attribute("id") != null)
                .ToDictionary(
                    element => element.Attribute("id")!.Value,
                    element => element,
                    StringComparer.OrdinalIgnoreCase);

            foreach (var mapping in doc.Descendants("mapping"))
            {
                var id = mapping.Attribute("id")?.Value;
                var colorRef = mapping.Attribute("ref")?.Value;
                if (id == null || colorRef == null || !id.StartsWith("faction_", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!colors.TryGetValue(colorRef, out var color)) continue;
                if (!byte.TryParse(color.Attribute("r")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ||
                    !byte.TryParse(color.Attribute("g")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ||
                    !byte.TryParse(color.Attribute("b")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
                    continue;

                _factionColors[id["faction_".Length..]] = $"#{r:X2}{g:X2}{b:X2}";
            }
        }

        foreach (var file in CollectFactionFiles(dataDir))
        {
            var doc = XDocument.Load(file);
            foreach (var faction in EnumerateFactionDefinitions(doc))
            {
                var id = faction.Attribute("id")?.Value;
                var tags = faction.Attribute("tags")?.Value;
                if (!string.IsNullOrWhiteSpace(id) && HasTag(tags, "claimspace"))
                    _claimspaceFactions.Add(id);
            }
        }

        LoadOwnershipMetadata(dataDir);
    }

    /// <summary>
    /// 从 god.xml 的正式初始站点分布推断默认扇区所属势力。
    /// 只允许 factions.xml 带 claimspace 标签的势力参与，排除警察、剧情和教学场景站点。
    /// </summary>
    private void LoadOwners(string dataDir)
    {
        foreach (var sector in Sectors.Values)
        {
            sector.Owner = "ownerless";
            sector.IsContested = false;
        }

        var scores = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in CollectGodFiles(dataDir))
        {
            var doc = XDocument.Load(file);
            if (doc.Root == null) continue;
            foreach (var station in EnumerateDefaultGodStations(doc))
            {
                var owner = station.Attribute("owner")?.Value;
                if (string.IsNullOrEmpty(owner) ||
                    owner.Equals("player", StringComparison.OrdinalIgnoreCase) ||
                    !_claimspaceFactions.Contains(owner))
                    continue;
                var sectorId = ResolveSectorFromLocation(station.Element("location"));
                if (string.IsNullOrEmpty(sectorId)) continue;
                var stationDefinition = station.Element("station");
                var tags = stationDefinition?.Element("select")?.Attribute("tags")?.Value ?? string.Empty;
                if (!CanDefaultStationClaim(stationDefinition, tags, station.Attribute("type")?.Value))
                    continue;
                var weight = StationWeightFromTags(tags);
                if (weight <= 0) continue;
                if (!scores.TryGetValue(sectorId, out var dict))
                {
                    dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    scores[sectorId] = dict;
                }
                dict[owner] = dict.GetValueOrDefault(owner) + weight;
            }
        }
        ApplyOwnerScores(scores);

        RefreshOwnershipDisplay();

        _defaultSectorOwners.Clear();
        foreach (var sector in Sectors.Values)
            _defaultSectorOwners[sector.Id] = sector.Owner;
    }

    private void RefreshOwnershipDisplay()
    {
        foreach (var sector in Sectors.Values)
            sector.OwnerColor = ResolveFactionColor(sector.Owner);

        // Cluster 归属只用于外轮廓，由其非 ownerless 扇区多数决定。
        foreach (var cluster in Clusters.Values)
        {
            var owners = cluster.SectorIds
                .Select(s => Sectors.GetValueOrDefault(s)?.Owner)
                .Where(o => !string.IsNullOrEmpty(o) && !o.Equals("ownerless", StringComparison.OrdinalIgnoreCase))
                .GroupBy(o => o, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key)
                .FirstOrDefault();
            cluster.Owner = owners ?? "ownerless";
            cluster.OwnerColor = ResolveFactionColor(cluster.Owner);
        }
    }

    private string ResolveFactionColor(string? owner) =>
        _factionColors.GetValueOrDefault(owner ?? "ownerless",
            _factionColors.GetValueOrDefault("ownerless", "#666666"));

    private void ApplyOwnerScores(Dictionary<string, Dictionary<string, int>> scores)
    {
        foreach (var (sectorId, owners) in scores)
        {
            if (!Sectors.TryGetValue(sectorId, out var sector)) continue;
            var best = owners
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .First();
            sector.Owner = best.Key;
        }
    }

    /// <summary>从 god.xml station 的 location 元素解析所属 Sector macro ID。</summary>
    private string? ResolveSectorFromLocation(XElement? location)
    {
        if (location == null) return null;
        var macro = location.Attribute("macro")?.Value;
        if (string.IsNullOrEmpty(macro)) return null;
        var cls = location.Attribute("class")?.Value;
        if (string.Equals(cls, "sector", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var sectorId in Sectors.Keys)
            {
                if (string.Equals(sectorId, macro, StringComparison.OrdinalIgnoreCase))
                    return sectorId;
            }
            return null;
        }
        // zone 类：从 zone macro 提取 cluster/sector 编号
        var m = ZoneMacroRegex.Match(macro);
        if (m.Success)
        {
            var sectorId = $"Cluster_{m.Groups[1].Value}_Sector{m.Groups[2].Value}_macro";
            return Sectors.ContainsKey(sectorId) ? sectorId : null;
        }
        return null;
    }

    private static int StationWeightFromTags(string tags)
    {
        if (tags.Contains("shipyard")) return 100;
        if (tags.Contains("wharf")) return 90;
        if (tags.Contains("equipmentdock")) return 70;
        if (tags.Contains("tradestation")) return 60;
        if (tags.Contains("defence")) return 40;
        return 30;
    }

    private static bool HasTag(string? tags, string expected) =>
        !string.IsNullOrWhiteSpace(tags) && tags
            .Trim('[', ']')
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Contains(expected, StringComparer.OrdinalIgnoreCase);

    private bool CanDefaultStationClaim(XElement? stationDefinition, string tags, string? stationType)
    {
        if (stationDefinition == null) return false;

        var macro = stationDefinition.Attribute("macro")?.Value;
        if (!string.IsNullOrWhiteSpace(macro))
            return MacroCanClaim(macro);

        var constructionPlan = stationDefinition.Attribute("constructionplan")?.Value;
        if (!string.IsNullOrWhiteSpace(constructionPlan))
        {
            var planId = constructionPlan.Trim().Trim('\'', '"');
            if (_constructionPlanModules.TryGetValue(planId, out var modules) && modules.Any(MacroCanClaim))
                return true;
            // 少数剧情后启用的贸易站计划不含独立 ownership 模块，但 station 类型本身决定初始归属。
            return string.Equals(stationType, "tradingstation", StringComparison.OrdinalIgnoreCase);
        }

        // 标签选出的 defence/shipyard/wharf/tradestation 计划可能含行政或造船模块；piratebase 不含。
        return !string.IsNullOrWhiteSpace(tags) && !HasTag(tags, "piratebase");
    }

    private bool MacroCanClaim(string macro)
    {
        if (_macroClaimCache.TryGetValue(macro, out var cached)) return cached;
        var result = false;
        if (_macroPaths.TryGetValue(macro, out var path) && File.Exists(path))
        {
            var doc = XDocument.Load(path);
            result = doc.Descendants("ownership")
                .Any(element => string.Equals(element.Attribute("claim")?.Value, "1", StringComparison.Ordinal));
        }
        _macroClaimCache[macro] = result;
        return result;
    }

    private void LoadOwnershipMetadata(string dataDir)
    {
        _macroPaths.Clear();
        _constructionPlanModules.Clear();
        _macroClaimCache.Clear();

        LoadMacroIndex(dataDir, Path.Combine(dataDir, "index", "macros.xml"));
        LoadConstructionPlans(Path.Combine(dataDir, "libraries", "constructionplans.xml"));

        var extensionsDir = Path.Combine(dataDir, "extensions");
        if (!Directory.Exists(extensionsDir)) return;
        foreach (var extensionDir in Directory.GetDirectories(extensionsDir, "ego_dlc_*"))
        {
            LoadMacroIndex(dataDir, Path.Combine(extensionDir, "index", "macros.xml"));
            LoadConstructionPlans(Path.Combine(extensionDir, "libraries", "constructionplans.xml"));
        }
    }

    private void LoadMacroIndex(string rootDir, string indexPath)
    {
        if (!File.Exists(indexPath)) return;
        var doc = XDocument.Load(indexPath);
        foreach (var entry in doc.Descendants("entry"))
        {
            var name = entry.Attribute("name")?.Value;
            var value = entry.Attribute("value")?.Value;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value)) continue;
            _macroPaths[name] = Path.Combine(rootDir, value.Replace('\\', Path.DirectorySeparatorChar) + ".xml");
        }
    }

    private void LoadConstructionPlans(string path)
    {
        if (!File.Exists(path)) return;
        var doc = XDocument.Load(path);
        foreach (var plan in doc.Descendants("plan"))
        {
            var id = plan.Attribute("id")?.Value;
            if (string.IsNullOrWhiteSpace(id)) continue;
            _constructionPlanModules[id] = plan.Descendants("entry")
                .Select(entry => entry.Attribute("macro")?.Value)
                .Where(macro => !string.IsNullOrWhiteSpace(macro))
                .Cast<string>()
                .ToArray();
        }
    }

    private static IEnumerable<XElement> EnumerateFactionDefinitions(XDocument doc)
    {
        if (doc.Root?.Name.LocalName == "factions")
            return doc.Root.Elements("faction");

        return doc.Root?.Elements("add")
                   .Where(add => string.Equals(add.Attribute("sel")?.Value, "/factions", StringComparison.Ordinal))
                   .SelectMany(add => add.Elements("faction"))
               ?? Enumerable.Empty<XElement>();
    }

    private static IEnumerable<XElement> EnumerateDefaultGodStations(XDocument doc)
    {
        if (doc.Root?.Name.LocalName == "god")
            return doc.Root.Element("stations")?.Elements("station") ?? Enumerable.Empty<XElement>();

        return doc.Root?.Elements("add")
                   .Where(add => string.Equals(add.Attribute("sel")?.Value, "/god/stations", StringComparison.Ordinal))
                   .SelectMany(add => add.Elements("station"))
               ?? Enumerable.Empty<XElement>();
    }

    private static IEnumerable<string> CollectFactionFiles(string dataDir)
    {
        var main = Path.Combine(dataDir, "libraries", "factions.xml");
        if (File.Exists(main)) yield return main;

        var extensionsDir = Path.Combine(dataDir, "extensions");
        if (!Directory.Exists(extensionsDir)) yield break;
        foreach (var ext in Directory.GetDirectories(extensionsDir, "ego_dlc_*"))
        {
            var file = Path.Combine(ext, "libraries", "factions.xml");
            if (File.Exists(file)) yield return file;
        }
    }

    private static IEnumerable<string> CollectGodFiles(string dataDir)
    {
        var main = Path.Combine(dataDir, "libraries", "god.xml");
        if (File.Exists(main)) yield return main;

        var extensionsDir = Path.Combine(dataDir, "extensions");
        if (!Directory.Exists(extensionsDir)) yield break;
        foreach (var ext in Directory.GetDirectories(extensionsDir, "ego_dlc_*"))
        {
            var f = Path.Combine(ext, "libraries", "god.xml");
            if (File.Exists(f)) yield return f;
        }
    }

    /// <summary>递归解析字符串中的全部文本引用。</summary>
    private static string ResolveText(Dictionary<string, Dictionary<string, string>> pages, string? textRef)
    {
        if (string.IsNullOrEmpty(textRef)) return string.Empty;
        var text = textRef;
        const int maxIterations = 20;
        for (int i = 0; i < maxIterations; i++)
        {
            var m = TextRefRegex.Match(text);
            if (!m.Success) break;
            string replacement = m.Value;
            if (pages.TryGetValue(m.Groups[1].Value, out var page) &&
                page.TryGetValue(m.Groups[2].Value, out var resolved))
            {
                replacement = resolved;
            }
            text = text[..m.Index] + replacement + text[(m.Index + m.Length)..];
        }
        return text;
    }

    // ─── 星图显示布局（复刻游戏内星图）────────────────

    /// <summary>
    /// 二维比例坐标（屏幕空间，y 向下为正）。
    /// </summary>
    private readonly record struct Ratio2(double X, double Y);

    /// <summary>
    /// 计算星图显示布局，使星区/扇区/星门位置与游戏内星图一致。
    ///
    /// 1. Cluster：银河世界坐标 → 轴向坐标(q,r) → 网格显示坐标。
    ///    q = round(x / 15e6)，r = round((z - 8.66e6·q) / 17.32e6)
    ///    显示坐标：x = 1.5·q，y = -√3·(r + q/2)（屏幕 y 向下）。
    /// 2. Cluster 半径 = 所有 cluster 显示中心间最小距离 / √3。
    /// 3. Sector：按 cluster 内 sector 数量选择模板槽位（单/双/三扇区），
    ///    用实际偏移方向匹配槽位；sector 中心 = cluster 中心 + 槽位比例 × cluster 半径；
    ///    六边形外接半径 = sector_radius_ratio × cluster 半径。
    /// 4. 门：相对 sector 中心（zone 包围盒中心，吸附 64k 网格）归一化
    ///    sx=(x-cx)·scale，sy=-(z-cz)·scale，再投影到 sector 六边形内。
    /// </summary>
    public void ComputeLayout()
    {
        const double sqrt3 = 1.7320508075688772;

        // 1. Cluster 轴向网格显示坐标
        foreach (var cluster in Clusters.Values)
        {
            int q = (int)Math.Round(cluster.WorldPos.X / 15_000_000.0);
            int r = (int)Math.Round((cluster.WorldPos.Z - 8_660_000.0 * q) / 17_320_000.0);
            cluster.DisplayX = 1.5 * q;
            cluster.DisplayY = -sqrt3 * (r + q / 2.0);
        }

        // 2. Cluster 半径 = 最小中心距 / √3
        var clusterList = Clusters.Values.ToList();
        double minDist = double.MaxValue;
        for (int i = 0; i < clusterList.Count; i++)
        for (int j = i + 1; j < clusterList.Count; j++)
        {
            double dx = clusterList[i].DisplayX - clusterList[j].DisplayX;
            double dy = clusterList[i].DisplayY - clusterList[j].DisplayY;
            minDist = Math.Min(minDist, Math.Sqrt(dx * dx + dy * dy));
        }
        double clusterRadius = minDist >= double.MaxValue ? 1.0 : minDist / sqrt3;

        // 3. Sector 模板槽位
        foreach (var cluster in Clusters.Values)
        {
            var sectorIds = cluster.SectorIds
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (sectorIds.Count == 0) continue;

            var localPositions = new Dictionary<string, Vec3>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in sectorIds)
            {
                if (Sectors.TryGetValue(id, out var s))
                    localPositions[id] = new Vec3(s.WorldPos.X - cluster.WorldPos.X, 0, s.WorldPos.Z - cluster.WorldPos.Z);
            }

            var (slotMap, slotPositions) = ChooseSectorTemplate(localPositions);
            double radiusRatio = SectorRadiusRatio(sectorIds.Count);

            foreach (var id in sectorIds)
            {
                if (!Sectors.TryGetValue(id, out var sector)) continue;
                var slot = slotMap.TryGetValue(id, out var sn) ? sn : "single";
                var ratio = slotPositions.TryGetValue(slot, out var rv) ? rv : new Ratio2(0, 0);
                sector.Slot = slot;
                sector.DisplayX = cluster.DisplayX + ratio.X * clusterRadius;
                sector.DisplayY = cluster.DisplayY + ratio.Y * clusterRadius;
                sector.DisplayRadius = radiusRatio * clusterRadius;
            }
        }

        // 4. 门归一化投影到 sector 六边形内
        foreach (var gate in Gates.Values)
        {
            if (!Sectors.TryGetValue(gate.SectorId, out var sector)) continue;
            var center = ComputeSectorCenterLocal(gate.SectorId);
            double maxExtent = ComputeSectorMaxExtent(gate.SectorId, center);
            double scalePerRadius = (sqrt3 / 2.0 * 0.8) / Math.Max(1.0, maxExtent);
            double gx = gate.WorldPos.X - sector.WorldPos.X - center.X;
            double gz = gate.WorldPos.Z - sector.WorldPos.Z - center.Z;
            double sx = gx * scalePerRadius;
            double sy = -gz * scalePerRadius;
            double radiusRatio = sector.DisplayRadius / Math.Max(clusterRadius, 1e-9);
            gate.DisplayX = sector.DisplayX + sx * radiusRatio * clusterRadius;
            gate.DisplayY = sector.DisplayY + sy * radiusRatio * clusterRadius;
        }

        // 5. Cluster 大六边形外接半径（= 全局 clusterRadius）
        foreach (var cluster in Clusters.Values)
        {
            cluster.DisplayRadius = clusterRadius;
        }

        // 6. 超级高速路端点（入口/出口 zone）显示坐标 + 车道（单向/双向）
        ComputeSectorLinkLayout(clusterRadius);
    }

    /// <summary>吸附到最近的 64000 网格（X4 扇区中心的格子语义）。</summary>
    private static double SnapToCenterGrid(double value) => Math.Round(value / 64000.0) * 64000.0;

    /// <summary>
    /// 计算 sector 内部基准中心（相对 sector 的局部坐标，基于该 sector 所有 zone 的包围盒中心，
    /// x/z 吸附到 64k 网格）。
    /// </summary>
    private Vec3 ComputeSectorCenterLocal(string sectorId)
    {
        if (_zoneOffsets.TryGetValue(sectorId, out var dict) && dict.Count > 0)
        {
            var xs = dict.Values.Select(v => v.X).ToList();
            var zs = dict.Values.Select(v => v.Z).ToList();
            return new Vec3(
                SnapToCenterGrid((xs.Min() + xs.Max()) / 2.0),
                0,
                SnapToCenterGrid((zs.Min() + zs.Max()) / 2.0));
        }
        return Vec3.Zero;
    }

    /// <summary>计算 sector 内所有 zone 与门相对 sector 基准中心的最大半径（用于归一化缩放）。</summary>
    private double ComputeSectorMaxExtent(string sectorId, Vec3 center)
    {
        double maxExtent = 1.0;
        if (_zoneOffsets.TryGetValue(sectorId, out var zones))
        {
            foreach (var z in zones.Values)
            {
                double dz = Math.Sqrt((z.X - center.X) * (z.X - center.X) + (z.Z - center.Z) * (z.Z - center.Z));
                maxExtent = Math.Max(maxExtent, dz);
            }
        }
        if (Sectors.TryGetValue(sectorId, out var sector))
        {
            foreach (var gate in Gates.Values)
            {
                if (!string.Equals(gate.SectorId, sectorId, StringComparison.OrdinalIgnoreCase)) continue;
                double gx = gate.WorldPos.X - sector.WorldPos.X - center.X;
                double gz = gate.WorldPos.Z - sector.WorldPos.Z - center.Z;
                maxExtent = Math.Max(maxExtent, Math.Sqrt(gx * gx + gz * gz));
            }
        }
        return maxExtent;
    }

    /// <summary>
    /// 计算超级高速路端点显示坐标与车道信息。
    /// 按同 cluster 内相同扇区对（无序）分组：组内 2 条 = 双向成对，1 条 = 单向。
    /// 端点用入口/出口 zone 相对扇区中心的归一化投影（与星门同套算法）。
    /// </summary>
    private void ComputeSectorLinkLayout(double clusterRadius)
    {
        var groups = new Dictionary<(string Cluster, string A, string B), List<SectorLink>>();
        foreach (var link in SectorLinks)
        {
            var cmp = string.Compare(link.SectorAId, link.SectorBId, StringComparison.OrdinalIgnoreCase);
            var key = (link.ClusterId, cmp < 0 ? link.SectorAId : link.SectorBId, cmp < 0 ? link.SectorBId : link.SectorAId);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<SectorLink>();
                groups[key] = list;
            }
            list.Add(link);
        }

        foreach (var links in groups.Values)
        {
            var sorted = links.OrderBy(l => l.Id, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                var link = sorted[i];
                link.LaneCount = sorted.Count;
                link.LaneIndex = i;
                var (fx, fy) = ZoneDisplayPosition(link.SectorAId, link.FromZoneId, clusterRadius);
                var (tx, ty) = ZoneDisplayPosition(link.SectorBId, link.ToZoneId, clusterRadius);
                link.DisplayFromX = fx;
                link.DisplayFromY = fy;
                link.DisplayToX = tx;
                link.DisplayToY = ty;
            }
        }
    }

    /// <summary>
    /// 计算 zone 相对所属扇区中心的显示坐标（星图显示单位）。
    /// 归一化方式与星门一致：zone 偏移相对扇区基准中心 → scale_per_radius → 投影到扇区六边形内。
    /// zone 缺失时回退到扇区中心。
    /// </summary>
    private (double X, double Y) ZoneDisplayPosition(string sectorId, string zoneId, double clusterRadius)
    {
        if (!Sectors.TryGetValue(sectorId, out var sector))
            return (0, 0);

        if (!string.IsNullOrEmpty(zoneId) &&
            _zoneOffsets.TryGetValue(sectorId, out var zones) &&
            zones.TryGetValue(zoneId, out var offset))
        {
            var center = ComputeSectorCenterLocal(sectorId);
            double maxExtent = ComputeSectorMaxExtent(sectorId, center);
            double scalePerRadius = (Math.Sqrt(3.0) / 2.0 * 0.8) / Math.Max(1.0, maxExtent);
            double zx = offset.X - center.X;
            double zy = offset.Z - center.Z;
            double sx = zx * scalePerRadius;
            double sy = -zy * scalePerRadius;
            double radiusRatio = sector.DisplayRadius / Math.Max(clusterRadius, 1e-9);
            return (
                sector.DisplayX + sx * radiusRatio * clusterRadius,
                sector.DisplayY + sy * radiusRatio * clusterRadius);
        }

        return (sector.DisplayX, sector.DisplayY);
    }

    /// <summary>sector 六边形外接半径与 cluster 半径的比值。</summary>
    private static double SectorRadiusRatio(int count)
    {
        if (count <= 1) return 1.0;
        if (count == 2 || count == 3) return 0.5;
        return 0.36;
    }

    /// <summary>模板槽位比例（屏幕空间，y 向下为正；s = √3/4）。</summary>
    private static Dictionary<string, Ratio2> TemplatePositionsRatio(int count, int variant)
    {
        double s = Math.Sqrt(3.0) / 4.0;
        if (count == 1) return new() { ["single"] = new Ratio2(0, 0) };
        if (count == 2)
        {
            return variant == 0
                ? new() { ["upper"] = new Ratio2(-0.25, -s), ["lower"] = new Ratio2(0.25, s) }
                : new() { ["upper"] = new Ratio2(0.25, -s), ["lower"] = new Ratio2(-0.25, s) };
        }
        if (count == 3)
        {
            return variant == 0
                ? new() { ["left"] = new Ratio2(-0.5, 0), ["center"] = new Ratio2(0.25, s), ["right"] = new Ratio2(0.25, -s) }
                : new() { ["upper_left"] = new Ratio2(-0.25, -s), ["lower_left"] = new Ratio2(-0.25, s), ["right"] = new Ratio2(0.5, 0) };
        }
        return new();
    }

    /// <summary>按实际 sector 偏移方向为每个 sector 分配合适的模板槽位（枚举排列取最小方向差）。</summary>
    private static Dictionary<string, string> BestSlotAssignment(
        Dictionary<string, Vec3> localPositions, Dictionary<string, Ratio2> slots)
    {
        var slotNames = slots.Keys.ToList();
        var sectorNames = localPositions.Keys.ToList();
        double bestScore = double.MaxValue;
        Dictionary<string, string> best = new(StringComparer.OrdinalIgnoreCase);

        foreach (var perm in Permutations(sectorNames, slotNames.Count))
        {
            double score = 0;
            var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < perm.Count; i++)
            {
                var (ax, ay) = UnitVecCentered(localPositions, perm[i]);
                var (sx, sy) = UnitVec(slots[slotNames[i]].X, slots[slotNames[i]].Y);
                score += (ax - sx) * (ax - sx) + (ay - sy) * (ay - sy);
                mapping[perm[i]] = slotNames[i];
            }
            if (score < bestScore) { bestScore = score; best = mapping; }
        }
        return best;
    }

    /// <summary>选择 sector 模板：单/双/三扇区用模板，多扇区退回默认（槽位为空）。</summary>
    private static (Dictionary<string, string> SlotMap, Dictionary<string, Ratio2> Slots) ChooseSectorTemplate(
        Dictionary<string, Vec3> localPositions)
    {
        var names = localPositions.Keys.ToList();
        int count = names.Count;
        if (count == 1)
        {
            var slots = new Dictionary<string, Ratio2> { ["single"] = new Ratio2(0, 0) };
            return (new Dictionary<string, string> { [names[0]] = "single" }, slots);
        }
        if (count == 2 || count == 3)
        {
            double bestScore = double.MaxValue;
            Dictionary<string, string> bestMapping = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Ratio2> bestSlots = new(StringComparer.OrdinalIgnoreCase);
            for (int variant = 0; variant < 2; variant++)
            {
                var slots = TemplatePositionsRatio(count, variant);
                var mapping = BestSlotAssignment(localPositions, slots);
                double score = 0;
                foreach (var kv in mapping)
                {
                    var (ax, ay) = UnitVecCentered(localPositions, kv.Key);
                    var (sx, sy) = UnitVec(slots[kv.Value].X, slots[kv.Value].Y);
                    score += (ax - sx) * (ax - sx) + (ay - sy) * (ay - sy);
                }
                if (count == 2)
                {
                    // 两个 sector 几乎在同一水平线上时优先 variant 1
                    double avgX = localPositions.Values.Average(v => v.X);
                    double avgZ = localPositions.Values.Average(v => v.Z);
                    var xs = localPositions.Values.Select(v => v.X - avgX).ToList();
                    double xSpan = xs.Max() - xs.Min();
                    score += xSpan <= 1e-6 ? (variant == 1 ? 0 : 1e-3) : 0;
                }
                if (score < bestScore) { bestScore = score; bestMapping = mapping; bestSlots = slots; }
            }
            return (bestMapping, bestSlots);
        }
        return (new Dictionary<string, string>(), new Dictionary<string, Ratio2>());
    }

    /// <summary>sector 偏移（中心化后）在显示空间的单位方向向量：(x, -z)。</summary>
    private static (double X, double Y) UnitVecCentered(Dictionary<string, Vec3> positions, string key)
    {
        double avgX = positions.Values.Average(v => v.X);
        double avgZ = positions.Values.Average(v => v.Z);
        var v = positions[key];
        return UnitVec(v.X - avgX, -(v.Z - avgZ));
    }

    private static (double X, double Y) UnitVec(double x, double y)
    {
        double len = Math.Sqrt(x * x + y * y);
        if (len < 1e-9) return (0, 0);
        return (x / len, y / len);
    }

    /// <summary>枚举 items 取 k 个的所有排列。</summary>
    private static IEnumerable<List<string>> Permutations(List<string> items, int k)
    {
        if (k <= 0) { yield return new List<string>(); yield break; }
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var rest = new List<string>();
            for (int j = 0; j < items.Count; j++)
                if (j != i) rest.Add(items[j]);
            foreach (var perm in Permutations(rest, k - 1))
            {
                var result = new List<string> { item };
                result.AddRange(perm);
                yield return result;
            }
        }
    }

    // ─── galaxy.xml：Cluster 位置 ─────────────────────────────────

    private void LoadGalaxy(string dataDir)
    {
        foreach (var file in CollectMapFiles(dataDir, "galaxy.xml", "galaxy.xml"))
        {
            var doc = XDocument.Load(file);
            if (doc.Root == null) continue;
            // 兼容两种根格式：
            //   主文件  <macros><macro class="galaxy"><connections><connection ref="clusters">...
            //   DLC 文件 <diff><add sel="..."><connection ref="clusters">...
            foreach (var conn in doc.Descendants("connection"))
            {
                if (!string.Equals(conn.Attribute("ref")?.Value, "clusters", StringComparison.OrdinalIgnoreCase))
                    continue;
                var clusterMacro = conn.Element("macro")?.Attribute("ref")?.Value;
                if (string.IsNullOrEmpty(clusterMacro)) continue;
                Clusters[clusterMacro] = new ClusterInfo
                {
                    Id = clusterMacro,
                    WorldPos = ReadPosition(conn)
                };
            }
        }
    }

    // ─── clusters.xml：Sector 位置 + 超级高速路连接 ───────────────

    private void LoadClusters(string dataDir)
    {
        foreach (var file in CollectMapFiles(dataDir, "clusters.xml", "*_clusters.xml"))
        {
            var doc = XDocument.Load(file);
            if (doc.Root == null) continue;
            foreach (var macro in doc.Root.Elements("macro"))
            {
                if (!string.Equals(macro.Attribute("class")?.Value, "cluster", StringComparison.OrdinalIgnoreCase))
                    continue;
                var clusterId = macro.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(clusterId)) continue;

                if (!Clusters.TryGetValue(clusterId, out var cluster))
                {
                    cluster = new ClusterInfo { Id = clusterId };
                    Clusters[clusterId] = cluster;
                }
                var clusterPos = cluster.WorldPos;

                foreach (var conn in macro.Elements("connections").Elements("connection"))
                {
                    var refVal = conn.Attribute("ref")?.Value;
                    if (string.Equals(refVal, "sectors", StringComparison.OrdinalIgnoreCase))
                    {
                        var sectorMacro = conn.Element("macro")?.Attribute("ref")?.Value;
                        if (string.IsNullOrEmpty(sectorMacro)) continue;
                        var offset = ReadPosition(conn);
                        Sectors[sectorMacro] = new SectorInfo
                        {
                            Id = sectorMacro,
                            ClusterId = clusterId,
                            WorldPos = clusterPos + offset
                        };
                        if (!cluster.SectorIds.Contains(sectorMacro, StringComparer.OrdinalIgnoreCase))
                            cluster.SectorIds.Add(sectorMacro);
                    }
                    else if (string.Equals(refVal, "sechighways", StringComparison.OrdinalIgnoreCase))
                    {
                        ParseSectorLink(clusterId, conn);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 计算超级高速路入口/出口端点的银河绝对坐标（入口/出口 zone 偏移 + 所属扇区坐标）。
    /// zone 偏移缺失时回退到扇区中心。
    /// </summary>
    private void ComputeSectorLinkWorldPositions()
    {
        foreach (var link in SectorLinks)
        {
            link.EntranceWorldPos = ResolveZoneWorldPos(link.SectorAId, link.FromZoneId);
            link.ExitWorldPos = ResolveZoneWorldPos(link.SectorBId, link.ToZoneId);
        }
    }

    /// <summary>zone 的银河绝对坐标 = 所属扇区坐标 + zone 偏移；zone 缺失回退扇区中心。</summary>
    private Vec3 ResolveZoneWorldPos(string sectorId, string zoneId)
    {
        if (!string.IsNullOrEmpty(zoneId) &&
            _zoneOffsets.TryGetValue(sectorId, out var zones) &&
            zones.TryGetValue(zoneId, out var offset) &&
            Sectors.TryGetValue(sectorId, out var sector))
        {
            return sector.WorldPos + offset;
        }
        return Sectors.TryGetValue(sectorId, out var s) ? s.WorldPos : Vec3.Zero;
    }

    /// <summary>
    /// 从超级高速路连接解析星区内扇区连接（entrypoint/exitpoint 的 path 含 Cluster_NN_SectorNNN_connection），
    /// 并记录入口/出口 zone（sechighway 端点）。
    /// </summary>
    private void ParseSectorLink(string clusterId, XElement conn)
    {
        var linkName = conn.Attribute("name")?.Value;
        if (string.IsNullOrEmpty(linkName)) return;

        var macroNode = conn.Element("macro");
        string? sectorA = null;
        string? sectorB = null;
        string? zoneA = null;
        string? zoneB = null;
        foreach (var end in macroNode?.Elements("connections").Elements("connection") ?? Enumerable.Empty<XElement>())
        {
            var refVal = end.Attribute("ref")?.Value;
            var endMacro = end.Element("macro");
            var path = endMacro?.Attribute("path")?.Value ?? string.Empty;
            var m = SectorPathRegex.Match(path);
            if (!m.Success) continue;
            var sectorMacro = $"Cluster_{m.Groups[1].Value}_Sector{m.Groups[2].Value}_macro";
            var zoneMacro = endMacro?.Attribute("ref")?.Value ?? string.Empty;
            if (string.Equals(refVal, "entrypoint", StringComparison.OrdinalIgnoreCase))
            {
                sectorA = sectorMacro;
                zoneA = zoneMacro;
            }
            else if (string.Equals(refVal, "exitpoint", StringComparison.OrdinalIgnoreCase))
            {
                sectorB = sectorMacro;
                zoneB = zoneMacro;
            }
        }

        if (sectorA != null && sectorB != null)
        {
            SectorLinks.Add(new SectorLink
            {
                Id = linkName,
                ClusterId = clusterId,
                SectorAId = sectorA,
                SectorBId = sectorB,
                FromZoneId = zoneA ?? string.Empty,
                ToZoneId = zoneB ?? string.Empty,
                Kind = "sechighway"
            });
        }
    }

    // ─── sectors.xml：Zone 位置 ──────────────────────────────────

    private void LoadSectors(string dataDir)
    {
        foreach (var file in CollectMapFiles(dataDir, "sectors.xml", "*_sectors.xml"))
        {
            var doc = XDocument.Load(file);
            if (doc.Root == null) continue;
            foreach (var macro in doc.Root.Elements("macro"))
            {
                if (!string.Equals(macro.Attribute("class")?.Value, "sector", StringComparison.OrdinalIgnoreCase))
                    continue;
                var sectorId = macro.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(sectorId)) continue;

                foreach (var conn in macro.Elements("connections").Elements("connection"))
                {
                    if (!string.Equals(conn.Attribute("ref")?.Value, "zones", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var zoneMacro = conn.Element("macro")?.Attribute("ref")?.Value;
                    if (string.IsNullOrEmpty(zoneMacro)) continue;
                    var offset = ReadPosition(conn);
                    if (!_zoneOffsets.TryGetValue(sectorId, out var dict))
                    {
                        dict = new Dictionary<string, Vec3>(StringComparer.OrdinalIgnoreCase);
                        _zoneOffsets[sectorId] = dict;
                    }
                    dict[zoneMacro] = offset;
                }
            }
        }
    }

    // ─── zones.xml：星门/轨道加速器 ───────────────────────────────

    private void LoadZones(string dataDir)
    {
        foreach (var file in CollectMapFiles(dataDir, "zones.xml", "*_zones.xml"))
        {
            var doc = XDocument.Load(file);
            if (doc.Root == null) continue;
            foreach (var macro in doc.Root.Elements("macro"))
            {
                if (!string.Equals(macro.Attribute("class")?.Value, "zone", StringComparison.OrdinalIgnoreCase))
                    continue;
                var zoneId = macro.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(zoneId)) continue;

                var (sectorId, clusterId, zonePos) = ResolveZoneContext(zoneId);

                foreach (var conn in macro.Elements("connections").Elements("connection"))
                {
                    if (!string.Equals(conn.Attribute("ref")?.Value, "gates", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var gateName = conn.Attribute("name")?.Value;
                    if (string.IsNullOrEmpty(gateName)) continue;

                    var m = GateNameRegex.Match(gateName);
                    if (!m.Success) continue;
                    var targetClusterId = $"Cluster_{int.Parse(m.Groups["dst"].Value):D2}_macro";

                    var gateMacroRef = conn.Element("macro")?.Attribute("ref")?.Value ?? string.Empty;
                    var kind = gateMacroRef.Contains("orb_accelerator", StringComparison.OrdinalIgnoreCase)
                        ? GateKind.Accelerator
                        : GateKind.Gate;
                    var localPos = ReadPosition(conn);

                    var gate = new GateInfo
                    {
                        Id = gateName,
                        ClusterId = clusterId,
                        SectorId = sectorId,
                        ZoneId = zoneId,
                        TargetClusterId = targetClusterId,
                        Kind = kind,
                        LocalPos = localPos,
                        WorldPos = zonePos + localPos
                    };
                    Gates[gateName] = gate;

                    if (Sectors.TryGetValue(sectorId, out var sector))
                        sector.GateIds.Add(gateName);
                }
            }
        }
    }

    /// <summary>
    /// 通过 sectors.xml 的 zone 归属表反查 zone 所属 sector/cluster 及 zone 银河坐标。
    /// </summary>
    private (string SectorId, string ClusterId, Vec3 ZonePos) ResolveZoneContext(string zoneId)
    {
        foreach (var (sectorId, dict) in _zoneOffsets)
        {
            if (dict.TryGetValue(zoneId, out var offset) && Sectors.TryGetValue(sectorId, out var sector))
                return (sectorId, sector.ClusterId, sector.WorldPos + offset);
        }

        // fallback：按 zone 宏名中的 cluster/sector 编号推断
        var m = ZoneMacroRegex.Match(zoneId);
        if (m.Success)
        {
            var clusterNum = m.Groups[1].Value;
            var sectorNum = m.Groups[2].Value;
            var sectorId = $"Cluster_{clusterNum}_Sector{sectorNum}_macro";
            if (Sectors.TryGetValue(sectorId, out var sector))
                return (sectorId, sector.ClusterId, sector.WorldPos);
        }

        return (string.Empty, string.Empty, Vec3.Zero);
    }

    // ─── 存档门合并 ───────────────────────────────────────────────

    /// <summary>
    /// 将存档中扫描到的门实例合并回静态星门：标注 FoundInSave、回填 Code 与 TargetGateId。
    /// </summary>
    private void ReplaceSaveGates(IReadOnlyList<SaveGateInstance> saveGates)
    {
        foreach (var gate in Gates.Values)
        {
            if (!_staticGateIdentity.TryGetValue(gate.Id, out var original))
                _staticGateIdentity[gate.Id] = original = (gate.Code, gate.TargetGateId);
            gate.Code = original.Code;
            gate.TargetGateId = original.Target;
            gate.FoundInSave = false;
            gate.SaveGateZoneTransform = null;
            gate.SaveZoneSectorTransform = null;
            gate.SaveGateSectorTransform = null;
        }
        SaveGates.Clear();
        SaveGates.AddRange(saveGates);
        MergeSaveGates(saveGates);
    }

    private void MergeSaveGates(IReadOnlyList<SaveGateInstance> saveGates)
    {
        var gateByComponentId = saveGates
            .Where(g => !string.IsNullOrEmpty(g.ComponentId))
            .GroupBy(g => g.ComponentId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var sg in saveGates)
        {
            // 匹配静态门（存档 connection 名小写，忽略大小写比较）
            if (Gates.TryGetValue(sg.ConnectionName, out var gate))
            {
                gate.FoundInSave = true;
                if (string.IsNullOrEmpty(gate.Code)) gate.Code = sg.Code;
                gate.SaveGateZoneTransform = sg.GateZoneTransform;
                gate.SaveZoneSectorTransform = sg.ZoneSectorTransform;
                gate.SaveGateSectorTransform = sg.GateSectorTransform;
            }

            // 补全目标门 ID：destination 指向目标门组件 id
            if (!string.IsNullOrEmpty(sg.DestinationComponentId) &&
                gateByComponentId.TryGetValue(sg.DestinationComponentId, out var target) &&
                Gates.TryGetValue(sg.ConnectionName, out var srcGate) &&
                Gates.TryGetValue(target.ConnectionName, out var dstGate))
            {
                srcGate.TargetGateId = dstGate.Id;
            }
        }
    }

    /// <summary>读取 connection 下 offset/position 坐标。</summary>
    private static Vec3 ReadPosition(XElement conn)
    {
        var pos = conn.Element("offset")?.Element("position");
        if (pos == null) return Vec3.Zero;
        return new Vec3(ReadDouble(pos, "x"), ReadDouble(pos, "y"), ReadDouble(pos, "z"));
    }

    private static double ReadDouble(XElement elem, string name)
    {
        var v = elem.Attribute(name)?.Value;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;
    }
}
