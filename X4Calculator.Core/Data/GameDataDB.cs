using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Data;

/// <summary>
/// 游戏基础数据容器，直接解析 GameData 根目录中的 X4 XML 文件。
/// </summary>
public class GameDataDB
{
    /// <summary>
    /// 所有商品数据，key 为 ware ID（如 "advancedcomposites"）。
    /// </summary>
    public Dictionary<string, Ware> Wares { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 按工厂名索引的商品（中文名）。
    /// </summary>
    public Dictionary<string, Ware> WaresByFactoryName { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 模块 macro ID → ware ID 映射（来自 modules.xml）。
    /// </summary>
    public Dictionary<string, string> ModuleWareMapping { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 商品 ID → 可生产该商品的模块定义列表（含生产种族/谱系）。
    /// </summary>
    public Dictionary<string, List<ProductionFacilityInfo>> ProductionFacilitiesByWare { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本地模块宏 ID 到其显示、劳动力和类别定义的索引。</summary>
    public Dictionary<string, StationModuleDefinition> StationModuleDefinitions { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本地 parameters.xml 中的劳动力增长参数。</summary>
    public WorkforceGrowthParameters WorkforceGrowthParameters { get; private set; } = new();

    /// <summary>
    /// 直接建造材料 ware ID → 使用该材料的玩家可建造舰船、装备或消耗品。
    /// 在加载 wares.xml 时一次性建立，页面切换不会再次读取 XML。
    /// </summary>
    public Dictionary<string, List<BuildableMaterialUse>> PlayerBuildUsesByMaterial { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 玩家可建造舰船、装备和消耗品的 ware ID → 原生建造方式及单位直接材料。
    /// 与反向材料用途索引在同一次 wares.xml 内存遍历中建立。
    /// </summary>
    public Dictionary<string, PlayerBuildableDefinition> PlayerBuildables { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 玩家可建造的空间站模块 ware ID → 原生建造方式及单位直接材料。
    /// 与舰船/装备定义分开保存，避免模块蓝图进入船厂仓储需求计算。
    /// </summary>
    public Dictionary<string, PlayerBuildableDefinition> StationModuleBuildables { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>直接建造材料 ware ID → 使用该材料的玩家空间站模块。</summary>
    public Dictionary<string, List<BuildableMaterialUse>> StationModuleBuildUsesByMaterial { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 可由存档蓝图直接解析的舰船、装备和消耗品。与 <see cref="PlayerBuildables"/> 不同，
    /// 这里保留玩家已经拥有的 limited 蓝图，供船厂仓储重建使用。
    /// </summary>
    public Dictionary<string, PlayerBuildableDefinition> BlueprintBuildables { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>parameters.xml 中 Wharf 的装备/消耗品预留类别倍率。</summary>
    public IReadOnlyDictionary<string, double> WharfUpgradeResourceFactors { get; private set; }
        = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    /// <summary>parameters.xml 中 Shipyard 的装备/消耗品预留类别倍率。</summary>
    public IReadOnlyDictionary<string, double> ShipyardUpgradeResourceFactors { get; private set; }
        = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 所有舰船信息，key 为 macro ID。
    /// </summary>
    public Dictionary<string, ShipInfo> Ships { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 所有引擎信息，key 为 macro ID。
    /// </summary>
    public Dictionary<string, EngineInfo> Engines { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 所有推进器信息，key 为 macro ID。
    /// </summary>
    public Dictionary<string, ThrusterInfo> Thrusters { get; } = new(StringComparer.OrdinalIgnoreCase);

    // 文本查找表：pageId → { textId → text }
    private readonly Dictionary<string, Dictionary<string, string>> _textPages = new();

    // 文本引用解析正则：{pageId,textId}（允许逗号前后有空格，如 "{20101, 32502}"）
    private static readonly Regex TextRefRegex = new(@"\{(\d+)\s*,\s*(\d+)\}", RegexOptions.Compiled);

    // wares 蓝图信息：宏 id → ware tags（基础+DLC 的 wares.xml，用于剔除 limited/noblueprint/noplayerblueprint 等不可获取舰船与引擎）
    private readonly Dictionary<string, HashSet<string>> _wareTagsByMacro = new(StringComparer.OrdinalIgnoreCase);

    // X4 官方 index：组件/macro 名 → 文件路径。DLC index 的 value 仍以 dataDir 为根。
    private readonly Dictionary<string, string> _componentPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _macroPaths = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? _componentFallbackPaths;
    private Dictionary<string, string>? _macroFallbackPaths;
    private string? _indexedDataDir;

    // 多个舰船/引擎宏会复用同一个组件或仓储宏，只解析一次。
    private readonly Dictionary<string, ShipComponentData> _shipComponentData = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> _engineSlotTagsByComponent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StorageCargoData> _storageCargoByMacro = new(StringComparer.OrdinalIgnoreCase);

    public GameDataDB() { }

    /// <summary>
    /// 从 GameData 根目录直接加载数据。
    /// </summary>
    /// <param name="gameDataPath">GameData 根目录路径。</param>
    public async Task LoadAsync(string gameDataPath)
    {
        var dataDir = Path.GetFullPath(gameDataPath);
        GameDataDirectory.EnsureUsable(dataDir);
        var libDir = Path.Combine(dataDir, "libraries");
        var tDir = Path.Combine(dataDir, "t");

        EnsureDataFileIndexes(dataDir);

        // 1. 加载中文文本（必须先加载，因为解析 wares.xml 时需要查表）
        var textFile = FindChineseTextFile(tDir);
        if (textFile != null)
            await LoadTextFileAsync(textFile);

        // 2. 加载商品/配方数据
        var waresPath = Path.Combine(libDir, "wares.xml");
        if (File.Exists(waresPath))
            LoadWares(waresPath);

        // 3. 加载模块映射
        var modulesPath = Path.Combine(libDir, "modules.xml");
        if (File.Exists(modulesPath))
            LoadModules(modulesPath);

        // 3.1 合并官方 DLC 对商品、配方和生产模块的增量定义。
        LoadExtensionProductionData(dataDir);
        LoadStationModuleDefinitions(dataDir);
        LoadWorkforceGrowthParameters(libDir);
        LoadShipBuildStorageParameters(libDir);

        // 3.5 加载 wares 蓝图信息（用于剔除 limited/noblueprint 等不可获取的舰船与引擎）
        LoadWareBlueprintTags(dataDir);

        // 3.6 从已合并的 ware 配方建立舰船/装备/消耗品直接材料反向索引，不扫描 macro 文件。
        BuildPlayerBuildMaterialIndex();

        // 4. 加载舰船数据
        await LoadShipsAsync(dataDir);

        // 5. 加载引擎与推进器数据
        await LoadEquipmentAsync(dataDir);
    }

    /// <summary>
    /// 在 t/ 目录下查找简体中文文本文件（0001-l086.xml）。
    /// </summary>
    private static string? FindChineseTextFile(string tDir)
    {
        if (!Directory.Exists(tDir)) return null;

        // 优先精确匹配 0001-l086.xml，否则扫描 -l086 后缀
        var exact = Path.Combine(tDir, "0001-l086.xml");
        if (File.Exists(exact)) return exact;

        return Directory.GetFiles(tDir, "*-l086.xml").FirstOrDefault();
    }

    /// <summary>
    /// 加载文本文件，构建 {pageId → {textId → text}} 查找表。
    /// </summary>
    private Task LoadTextFileAsync(string textFilePath)
    {
        var doc = XDocument.Load(textFilePath);
        var root = doc.Root;
        if (root == null) return Task.CompletedTask;

        foreach (var pageElem in root.Elements("page"))
        {
            var pageId = pageElem.Attribute("id")?.Value;
            if (string.IsNullOrEmpty(pageId)) continue;

            var pageDict = new Dictionary<string, string>();
            foreach (var tElem in pageElem.Elements("t"))
            {
                var tId = tElem.Attribute("id")?.Value;
                if (tId != null)
                {
                    pageDict[tId] = tElem.Value;
                }
            }
            _textPages[pageId] = pageDict;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 解析文本引用 "{pageId,textId}" 为实际文本。
    /// </summary>
    private string? ResolveTextRef(string? textRef)
    {
        if (string.IsNullOrEmpty(textRef)) return null;

        var match = TextRefRegex.Match(textRef);
        if (!match.Success) return textRef;

        var pageId = match.Groups[1].Value;
        var textId = match.Groups[2].Value;

        if (_textPages.TryGetValue(pageId, out var page) &&
            page.TryGetValue(textId, out var text))
        {
            return text;
        }

        return textRef; // fallback: 返回原始引用
    }

    /// <summary>
    /// 递归解析字符串中所有文本引用 "{pageId,textId}"，直到全部替换为实际文本。
    /// </summary>
    public string ResolveTextRefs(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        const int maxIterations = 20;
        for (int i = 0; i < maxIterations; i++)
        {
            var match = TextRefRegex.Match(text);
            if (!match.Success) break;

            var pageId = match.Groups[1].Value;
            var textId = match.Groups[2].Value;
            string replacement;

            if (_textPages.TryGetValue(pageId, out var page) &&
                page.TryGetValue(textId, out var resolved))
            {
                replacement = resolved;
            }
            else
            {
                replacement = match.Value; // 保留原样
            }

            text = text[..match.Index] + replacement + text[(match.Index + match.Length)..];
        }

        return text;
    }

    /// <summary>
    /// 解析 wares.xml，填充 Wares 和 WaresByFactoryName。
    /// </summary>
    private void LoadWares(string waresPath)
    {
        var doc = XDocument.Load(waresPath);
        var root = doc.Root;
        if (root == null) return;

        foreach (var wareElem in root.Elements("ware"))
        {
            var ware = ParseWare(wareElem);
            if (ware != null)
            {
                Wares[ware.Id] = ware;
                if (!string.IsNullOrEmpty(ware.FactoryName))
                {
                    WaresByFactoryName[ware.FactoryName] = ware;
                }
            }
        }
    }

    private void LoadWorkforceGrowthParameters(string libDir)
    {
        var path = Path.Combine(libDir, "parameters.xml");
        if (!File.Exists(path)) return;
        var growth = XDocument.Load(path).Root?.Element("workforce")?.Element("growth");
        if (growth == null) return;
        var capacity = growth.Element("capacity");
        var population = growth.Element("population");
        var defaults = new WorkforceGrowthParameters();
        WorkforceGrowthParameters = new WorkforceGrowthParameters(
            ParseDouble(growth.Attribute("rate")?.Value, defaults.BaseGrowth),
            ParseInt(growth.Attribute("interval")?.Value, defaults.CycleSeconds),
            ParseInt(capacity?.Attribute("limit")?.Value, defaults.CapacityBonusLimit),
            ParseInt(capacity?.Attribute("min")?.Value, defaults.CapacityBonusMinimum),
            ParseDouble(capacity?.Attribute("max")?.Value, defaults.MaximumCapacityBonus * 100) / 100,
            ParseLong(population?.Attribute("limit")?.Value, defaults.PopulationBonusLimit),
            ParseLong(population?.Attribute("step")?.Value, defaults.PopulationBonusStep),
            ParseDouble(population?.Attribute("max")?.Value, defaults.MaximumPopulationBonus * 100) / 100);
    }

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static long ParseLong(string? value, long fallback) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static double ParseDouble(string? value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    /// <summary>
    /// 合并 extensions/ego_dlc_*/libraries 下与生产链有关的商品和模块定义。
    /// X4 DLC 使用 diff：既会向 /wares 添加新商品，也会向既有商品追加配方。
    /// </summary>
    private void LoadExtensionProductionData(string dataDir)
    {
        var extensionsDir = Path.Combine(dataDir, "extensions");
        if (!Directory.Exists(extensionsDir)) return;

        foreach (var extensionDir in Directory.GetDirectories(extensionsDir, "ego_dlc_*"))
        {
            var librariesDir = Path.Combine(extensionDir, "libraries");
            var waresPath = Path.Combine(librariesDir, "wares.xml");
            if (File.Exists(waresPath))
                MergeExtensionWares(waresPath);

            var modulesPath = Path.Combine(librariesDir, "modules.xml");
            if (File.Exists(modulesPath))
                LoadModules(modulesPath);
        }
    }

    private void MergeExtensionWares(string waresPath)
    {
        var doc = XDocument.Load(waresPath);
        var root = doc.Root;
        if (root == null) return;

        if (root.Name.LocalName == "wares")
        {
            LoadWares(waresPath);
            return;
        }

        // 新商品：<add sel="/wares"><ware ... /></add>
        foreach (var add in root.Elements("add")
                     .Where(e => string.Equals(e.Attribute("sel")?.Value, "/wares", StringComparison.Ordinal)))
        {
            foreach (var wareElem in add.Elements("ware"))
            {
                var ware = ParseWare(wareElem);
                if (ware == null) continue;
                Wares[ware.Id] = ware;
                if (!string.IsNullOrEmpty(ware.FactoryName))
                    WaresByFactoryName[ware.FactoryName] = ware;
            }
        }

        // 既有商品追加配方：<add sel="/wares/ware[@id='...']"><production ... /></add>
        var wareSelector = new Regex(@"^/wares/ware\[@id='(?<id>[^']+)'\]$", RegexOptions.Compiled);
        foreach (var add in root.Elements("add"))
        {
            var match = wareSelector.Match(add.Attribute("sel")?.Value ?? string.Empty);
            if (!match.Success || !Wares.TryGetValue(match.Groups["id"].Value, out var ware))
                continue;

            foreach (var productionElem in add.Elements("production"))
            {
                var recipe = ParseRecipe(productionElem);
                if (recipe == null) continue;
                ware.Production ??= new List<ProductionRecipe>();
                if (!ware.Production.Any(existing =>
                        string.Equals(existing.Method, recipe.Method, StringComparison.OrdinalIgnoreCase) &&
                        Math.Abs(existing.Time - recipe.Time) < 0.0001 &&
                        Math.Abs(existing.Amount - recipe.Amount) < 0.0001))
                {
                    ware.Production.Add(recipe);
                }
            }
        }
    }

    /// <summary>
    /// 从 &lt;ware&gt; XML 元素解析商品。
    /// </summary>
    private Ware? ParseWare(XElement wareElem)
    {
        var id = wareElem.Attribute("id")?.Value;
        if (string.IsNullOrEmpty(id)) return null;

        var nameRef = wareElem.Attribute("name")?.Value;
        var factoryRef = wareElem.Attribute("factoryname")?.Value;
        var group = wareElem.Attribute("group")?.Value ?? "";
        var transport = wareElem.Attribute("transport")?.Value ?? "";
        var volumeStr = wareElem.Attribute("volume")?.Value ?? "1";
        var tagsStr = wareElem.Attribute("tags")?.Value ?? "";
        var componentRef = wareElem.Element("component")?.Attribute("ref")?.Value ?? "";

        var ware = new Ware
        {
            Id = id,
            Name = ResolveTextRefs(nameRef) ?? id,
            FactoryName = ResolveTextRefs(factoryRef) ?? "",
            Group = group,
            Transport = transport,
            Volume = int.TryParse(volumeStr, out var v) ? v : 1,
            Tags = tagsStr.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
            ComponentRef = componentRef
        };

        // 解析价格
        var priceElem = wareElem.Element("price");
        if (priceElem != null)
        {
            ware.PriceMin = int.TryParse(priceElem.Attribute("min")?.Value, out var min) ? min : 0;
            ware.PriceAvg = int.TryParse(priceElem.Attribute("average")?.Value, out var avg) ? avg : 0;
            ware.PriceMax = int.TryParse(priceElem.Attribute("max")?.Value, out var max) ? max : 0;
        }

        // 解析配方
        var recipes = new List<ProductionRecipe>();
        foreach (var prodElem in wareElem.Elements("production"))
        {
            var recipe = ParseRecipe(prodElem);
            if (recipe != null) recipes.Add(recipe);
        }
        if (recipes.Count > 0) ware.Production = recipes;

        return ware;
    }

    /// <summary>
    /// 从 &lt;production&gt; XML 元素解析配方。
    /// </summary>
    private static ProductionRecipe? ParseRecipe(XElement prodElem)
    {
        var timeStr = prodElem.Attribute("time")?.Value;
        var amountStr = prodElem.Attribute("amount")?.Value;
        var method = prodElem.Attribute("method")?.Value ?? "default";
        var name = prodElem.Attribute("name")?.Value ?? "";
        var tags = (prodElem.Attribute("tags")?.Value ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        if (timeStr == null || amountStr == null) return null;

        var time = double.TryParse(timeStr, out var t) ? t : 0;
        var amount = double.TryParse(amountStr, out var a) ? a : 0;
        var workforceProductBonus = double.TryParse(
            prodElem.Element("effects")?.Elements("effect")
                .FirstOrDefault(effect => string.Equals(
                    effect.Attribute("type")?.Value, "work", StringComparison.OrdinalIgnoreCase))?
                .Attribute("product")?.Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsedWorkforceProductBonus)
            ? parsedWorkforceProductBonus
            : 0;

        var consumption = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // 解析 primary 消耗
        var primary = prodElem.Element("primary");
        if (primary != null)
        {
            foreach (var wareElem in primary.Elements("ware"))
            {
                var wareId = wareElem.Attribute("ware")?.Value;
                var amountStr2 = wareElem.Attribute("amount")?.Value;
                if (wareId != null && amountStr2 != null &&
                    double.TryParse(amountStr2, out var amt))
                {
                    consumption[wareId] = amt;
                }
            }
        }

        // 解析 secondary 消耗（可选）
        var secondary = prodElem.Element("secondary");
        if (secondary != null)
        {
            foreach (var wareElem in secondary.Elements("ware"))
            {
                var wareId = wareElem.Attribute("ware")?.Value;
                var amountStr2 = wareElem.Attribute("amount")?.Value;
                if (wareId != null && amountStr2 != null &&
                    double.TryParse(amountStr2, out var amt))
                {
                    // 用已有键区分，不覆盖 primary
                    consumption[$"secondary:{wareId}"] = amt;
                }
            }
        }

        return new ProductionRecipe
        {
            Time = time,
            Amount = amount,
            Method = method,
            Name = name,
            Tags = tags,
            Consumption = consumption.Count > 0 ? consumption : null,
            WorkforceProductBonus = workforceProductBonus
        };
    }

    private void BuildPlayerBuildMaterialIndex()
    {
        PlayerBuildUsesByMaterial.Clear();
        PlayerBuildables.Clear();
        StationModuleBuildables.Clear();
        StationModuleBuildUsesByMaterial.Clear();
        BlueprintBuildables.Clear();

        foreach (var ware in Wares.Values)
        {
            if (!IsBlueprintBuildableShipPartOrConsumable(ware)) continue;

            var methods = new List<PlayerBuildMethod>();
            foreach (var recipe in GetPlayerBuildRecipes(ware))
            {
                var materials = (recipe.Consumption ??
                                 new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase))
                    .Where(item => !item.Key.StartsWith("secondary:", StringComparison.OrdinalIgnoreCase))
                    .Select(item => new PlayerBuildMaterial(item.Key, item.Value / recipe.Amount))
                    .OrderBy(item => item.WareId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                methods.Add(new PlayerBuildMethod(
                    recipe.Method,
                    recipe.Time / recipe.Amount,
                    materials));

                if (IsPlayerBuildableShipPartOrConsumable(ware))
                {
                    foreach (var material in materials)
                    {
                        if (!PlayerBuildUsesByMaterial.TryGetValue(material.WareId, out var uses))
                        {
                            uses = new List<BuildableMaterialUse>();
                            PlayerBuildUsesByMaterial[material.WareId] = uses;
                        }

                        uses.Add(new BuildableMaterialUse(
                            ware.Id,
                            ware.ComponentRef,
                            ResolveBuildableKind(ware),
                            recipe.Method,
                            material.Amount));
                    }
                }
            }

            if (methods.Count > 0)
            {
                var definition = new PlayerBuildableDefinition(
                    ware.Id,
                    ware.ComponentRef,
                    ware.Name,
                    ResolveBuildableKind(ware),
                    methods.OrderBy(item => item.Method, StringComparer.OrdinalIgnoreCase).ToArray(),
                    ResolveUpgradeResourceCategory(ware),
                    ResolveBuildClass(ware));
                BlueprintBuildables[ware.Id] = definition;
                if (IsPlayerBuildableShipPartOrConsumable(ware))
                    PlayerBuildables[ware.Id] = definition;
            }
        }

        foreach (var ware in Wares.Values.Where(ware =>
                     IsPlayerStationModuleWare(ware) &&
                     StationModuleDefinitions.ContainsKey(ware.ComponentRef)))
        {
            var definition = CreateBuildableDefinition(ware, "StationModule");
            if (definition == null) continue;

            StationModuleBuildables[ware.Id] = definition;
            AddBuildMaterialUses(StationModuleBuildUsesByMaterial, definition);
        }
    }

    private PlayerBuildableDefinition? CreateBuildableDefinition(Ware ware, string kind)
    {
        var methods = GetPlayerBuildRecipes(ware)
            .Select(recipe => new PlayerBuildMethod(
                recipe.Method,
                recipe.Time / recipe.Amount,
                (recipe.Consumption ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase))
                .Where(item => !item.Key.StartsWith("secondary:", StringComparison.OrdinalIgnoreCase))
                .Select(item => new PlayerBuildMaterial(item.Key, item.Value / recipe.Amount))
                .OrderBy(item => item.WareId, StringComparer.OrdinalIgnoreCase)
                .ToArray()))
            .OrderBy(item => item.Method, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return methods.Length == 0
            ? null
            : new PlayerBuildableDefinition(ware.Id, ware.ComponentRef, ware.Name, kind, methods);
    }

    private static void AddBuildMaterialUses(
        IDictionary<string, List<BuildableMaterialUse>> usesByMaterial,
        PlayerBuildableDefinition definition)
    {
        foreach (var method in definition.Methods)
        foreach (var material in method.Materials)
        {
            if (!usesByMaterial.TryGetValue(material.WareId, out var uses))
            {
                uses = new List<BuildableMaterialUse>();
                usesByMaterial[material.WareId] = uses;
            }

            uses.Add(new BuildableMaterialUse(
                definition.WareId,
                definition.MacroId,
                definition.Kind,
                method.Method,
                material.Amount));
        }
    }

    private static IEnumerable<ProductionRecipe> GetPlayerBuildRecipes(Ware ware) =>
        (ware.Production ?? Enumerable.Empty<ProductionRecipe>())
        .Where(recipe => recipe.Amount > 0 &&
                         !recipe.Method.Equals("xenon", StringComparison.OrdinalIgnoreCase) &&
                         !recipe.Tags.Contains("noplayerbuild", StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 取得指定建造方式的单位直接材料；没有精确方式时回退到 default，二者都不存在则返回 null。
    /// </summary>
    public ResolvedPlayerBuildMaterials? ResolvePlayerBuildMaterials(
        string buildableWareId,
        string requestedMethod)
        => ResolveBuildMaterials(PlayerBuildables, buildableWareId, requestedMethod);

    /// <summary>
    /// 按存档实际拥有的蓝图解析单位直接材料；保留 limited 条目，并采用精确 method、缺失回退 default。
    /// </summary>
    public ResolvedPlayerBuildMaterials? ResolveBlueprintBuildMaterials(
        string buildableWareId,
        string requestedMethod)
        => ResolveBuildMaterials(BlueprintBuildables, buildableWareId, requestedMethod);

    /// <summary>
    /// 解析玩家空间站模块的单位直接建造材料；精确方式不存在时回退 default。
    /// </summary>
    public ResolvedPlayerBuildMaterials? ResolveStationModuleBuildMaterials(
        string moduleWareId,
        string requestedMethod)
        => ResolveBuildMaterials(StationModuleBuildables, moduleWareId, requestedMethod);

    /// <summary>是否为舰船/装备/消耗品或空间站模块直接使用的建造材料。</summary>
    public bool IsDirectBuildMaterial(string wareId) =>
        PlayerBuildUsesByMaterial.ContainsKey(wareId) ||
        StationModuleBuildUsesByMaterial.ContainsKey(wareId);

    private static ResolvedPlayerBuildMaterials? ResolveBuildMaterials(
        IReadOnlyDictionary<string, PlayerBuildableDefinition> buildables,
        string buildableWareId,
        string requestedMethod)
    {
        if (!buildables.TryGetValue(buildableWareId, out var buildable)) return null;

        var normalizedMethod = string.IsNullOrWhiteSpace(requestedMethod) ? "default" : requestedMethod;
        var method = buildable.Methods.FirstOrDefault(item =>
                         item.Method.Equals(normalizedMethod, StringComparison.OrdinalIgnoreCase))
                     ?? buildable.Methods.FirstOrDefault(item =>
                         item.Method.Equals("default", StringComparison.OrdinalIgnoreCase));
        if (method == null) return null;

        return new ResolvedPlayerBuildMaterials(
            buildable.WareId,
            normalizedMethod,
            method.Method,
            method.BuildTimeSeconds,
            method.Materials);
    }

    private static bool IsPlayerBuildableShipPartOrConsumable(Ware ware)
    {
        if (!IsBlueprintBuildableShipPartOrConsumable(ware)) return false;

        return !ware.Tags.Any(tag => tag.Equals("limited", StringComparison.OrdinalIgnoreCase) ||
                                     tag.Equals("noblueprint", StringComparison.OrdinalIgnoreCase) ||
                                     tag.Equals("noplayerblueprint", StringComparison.OrdinalIgnoreCase) ||
                                     tag.Equals("noplayerbuild", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBlueprintBuildableShipPartOrConsumable(Ware ware) =>
        !string.IsNullOrWhiteSpace(ware.ComponentRef) &&
        (string.Equals(ware.Transport, "ship", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(ware.Transport, "equipment", StringComparison.OrdinalIgnoreCase));

    private static string ResolveUpgradeResourceCategory(Ware ware)
    {
        string[] categories = [
            "countermeasure", "drone", "missile", "mine", "satellite", "navbeacon",
            "resourceprobe", "lasertower", "scanner", "shield", "thruster", "turret", "weapon", "engine"
        ];
        return categories.FirstOrDefault(category =>
            ware.Tags.Contains(category, StringComparer.OrdinalIgnoreCase)) ?? string.Empty;
    }

    private static string ResolveBuildClass(Ware ware)
    {
        var prefix = string.Equals(ware.Transport, "ship", StringComparison.OrdinalIgnoreCase)
            ? @"^ship_[^_]+_(s|m|l|xl)_"
            : @"^(?:engine|shield|weapon|turret|thruster|scanner)_[^_]+_(s|m|l|xl)_";
        var match = Regex.Match(ware.Id, prefix, RegexOptions.IgnoreCase);
        return match.Success ? $"ship_{match.Groups[1].Value.ToLowerInvariant()}" : string.Empty;
    }

    private static string ResolveBuildableKind(Ware ware)
    {
        if (string.Equals(ware.Transport, "ship", StringComparison.OrdinalIgnoreCase)) return "Ship";
        if (ware.Tags.Contains("drone", StringComparer.OrdinalIgnoreCase)) return "Drone";
        if (ware.Tags.Contains("missile", StringComparer.OrdinalIgnoreCase)) return "Missile";
        if (ware.Tags.Contains("countermeasure", StringComparer.OrdinalIgnoreCase)) return "Countermeasure";
        if (ware.Tags.Any(tag => tag.Equals("satellite", StringComparison.OrdinalIgnoreCase) ||
                                 tag.Equals("resourceprobe", StringComparison.OrdinalIgnoreCase) ||
                                 tag.Equals("navbeacon", StringComparison.OrdinalIgnoreCase) ||
                                 tag.Equals("lasertower", StringComparison.OrdinalIgnoreCase) ||
                                 tag.Equals("mine", StringComparison.OrdinalIgnoreCase)))
            return "Deployable";
        return "Equipment";
    }

    /// <summary>
    /// 解析 modules.xml，填充 ModuleWareMapping。
    /// </summary>
    private void LoadModules(string modulesPath)
    {
        var doc = XDocument.Load(modulesPath);
        var root = doc.Root;
        if (root == null) return;

        var moduleElements = root.Name.LocalName == "modules"
            ? root.Elements("module")
            : root.Descendants("module");

        foreach (var moduleElem in moduleElements)
        {
            var moduleId = moduleElem.Attribute("id")?.Value;
            if (string.IsNullOrEmpty(moduleId)) continue;

            // 取第一个 category 元素的 ware 属性
            var category = moduleElem.Element("category");
            var wareId = category?.Attribute("ware")?.Value;
            if (!string.IsNullOrEmpty(wareId))
            {
                ModuleWareMapping[moduleId] = wareId;

                var facility = new ProductionFacilityInfo
                {
                    ModuleId = moduleId,
                    WareId = wareId,
                    Race = NormalizeProductionRace(category?.Attribute("race")?.Value)
                };
                if (!ProductionFacilitiesByWare.TryGetValue(wareId, out var facilities))
                {
                    facilities = new List<ProductionFacilityInfo>();
                    ProductionFacilitiesByWare[wareId] = facilities;
                }
                if (!facilities.Any(existing =>
                        string.Equals(existing.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase)))
                {
                    facilities.Add(facility);
                }
            }
        }
    }

    private static string NormalizeProductionRace(string? race)
    {
        if (string.IsNullOrWhiteSpace(race) || race.StartsWith("[", StringComparison.Ordinal))
            return "default";
        return race.Trim();
    }

    private void EnsureDataFileIndexes(string dataDir)
    {
        var normalizedDataDir = Path.GetFullPath(dataDir)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(_indexedDataDir, normalizedDataDir, StringComparison.OrdinalIgnoreCase)) return;

        _componentPaths.Clear();
        _macroPaths.Clear();
        _componentFallbackPaths = null;
        _macroFallbackPaths = null;
        _shipComponentData.Clear();
        _engineSlotTagsByComponent.Clear();
        _storageCargoByMacro.Clear();

        var indexFiles = new List<(string Components, string Macros)>
        {
            (Path.Combine(normalizedDataDir, "index", "components.xml"),
             Path.Combine(normalizedDataDir, "index", "macros.xml"))
        };

        var extensionsDir = Path.Combine(normalizedDataDir, "extensions");
        if (Directory.Exists(extensionsDir))
        {
            foreach (var extensionDir in Directory.GetDirectories(extensionsDir, "ego_dlc_*"))
            {
                indexFiles.Add((
                    Path.Combine(extensionDir, "index", "components.xml"),
                    Path.Combine(extensionDir, "index", "macros.xml")));
            }
        }

        foreach (var (components, macros) in indexFiles)
        {
            // 全树搜索原本优先命中基础目录；组件索引保持首次有效定义。
            LoadDataIndex(components, normalizedDataDir, _componentPaths, overwrite: false);
            // macro 的扩展定义按加载顺序覆盖基础定义，与原递归枚举后的字典赋值一致。
            LoadDataIndex(macros, normalizedDataDir, _macroPaths, overwrite: true);
        }

        _indexedDataDir = normalizedDataDir;
    }

    private static void LoadDataIndex(
        string indexPath,
        string dataDir,
        Dictionary<string, string> destination,
        bool overwrite)
    {
        if (!File.Exists(indexPath)) return;

        try
        {
            foreach (var entry in XDocument.Load(indexPath).Root?.Elements("entry") ?? [])
            {
                var name = entry.Attribute("name")?.Value;
                var value = entry.Attribute("value")?.Value;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value) ||
                    name.Contains('*')) continue;

                var relativePath = value
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar) + ".xml";
                var path = Path.Combine(dataDir, relativePath);

                if (overwrite || !destination.ContainsKey(name)) destination[name] = path;
            }
        }
        catch (System.Xml.XmlException)
        {
            // 单个损坏的 index 由一次性文件名兜底索引接管，不阻断其他原生数据。
        }
    }

    private string? LocateMacroFile(string macroRef, string dataDir)
    {
        if (_macroPaths.TryGetValue(macroRef, out var indexedPath) && File.Exists(indexedPath))
            return indexedPath;

        EnsureMacroFallbackIndex(dataDir);
        return _macroFallbackPaths!.GetValueOrDefault(macroRef);
    }

    private void EnsureComponentFallbackIndex(string dataDir)
    {
        if (_componentFallbackPaths != null) return;

        _componentFallbackPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dataDir, "*.xml", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!name.EndsWith("_macro", StringComparison.OrdinalIgnoreCase))
                _componentFallbackPaths.TryAdd(name, file);
        }
    }

    private void EnsureMacroFallbackIndex(string dataDir)
    {
        if (_macroFallbackPaths != null) return;

        _macroFallbackPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dataDir, "*_macro.xml", SearchOption.AllDirectories))
            _macroFallbackPaths[Path.GetFileNameWithoutExtension(file)] = file;
    }

    private void LoadStationModuleDefinitions(string dataDir)
    {
        StationModuleDefinitions.Clear();
        var playerModuleWares = Wares.Values
            .Where(IsPlayerStationModuleWare)
            .ToDictionary(ware => ware.ComponentRef, StringComparer.OrdinalIgnoreCase);

        foreach (var (moduleId, moduleWare) in playerModuleWares)
        {
            var file = LocateMacroFile(moduleId, dataDir);
            if (file == null) continue;
            try
            {
                var macro = XDocument.Load(file).Descendants("macro").FirstOrDefault();
                var properties = macro?.Element("properties");
                if (macro == null || properties == null) continue;
                var id = macro.Attribute("name")?.Value;
                if (string.IsNullOrWhiteSpace(id) ||
                    !string.Equals(id, moduleId, StringComparison.OrdinalIgnoreCase)) continue;
                var identification = properties.Element("identification");
                var workforce = properties.Element("workforce");
                var cargo = properties.Element("cargo");
                var builder = properties.Element("builder");
                var kind = macro.Attribute("class")?.Value ?? string.Empty;
                var race = ResolveStationModuleRace(identification, workforce, moduleWare.Id);
                var products = ReadStationModuleProducts(properties, race);
                var resolvedName = ResolveTextRefs(identification?.Attribute("name")?.Value);
                StationModuleDefinitions[id] = new StationModuleDefinition
                {
                    Id = id,
                    BuildableWareId = moduleWare.Id,
                    Name = FormatStationModuleName(resolvedName, id),
                    Kind = kind,
                    Race = race,
                    Products = products,
                    WareId = products.FirstOrDefault()?.WareId,
                    WorkforceRequired = int.TryParse(workforce?.Attribute("max")?.Value, out var required) ? required : 0,
                    WorkforceCapacity = int.TryParse(workforce?.Attribute("capacity")?.Value, out var capacity) ? capacity : 0,
                    WorkforceGrowthRate = double.TryParse(workforce?.Attribute("growthrate")?.Value,
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var growthRate) ? growthRate : 0,
                    StorageCapacity = long.TryParse(cargo?.Attribute("max")?.Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var storageCapacity) ? storageCapacity : 0,
                    StorageTypes = (cargo?.Attribute("tags")?.Value ?? string.Empty)
                        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    BuildProcessorCount = macro.Element("connections")?.Elements("connection").Count(connection =>
                        connection.Element("macro")?.Attribute("ref")?.Value.StartsWith(
                            "buildprocessor_", StringComparison.OrdinalIgnoreCase) == true) ?? 0,
                    BuildClasses = (builder?.Attribute("classes")?.Value ?? string.Empty)
                        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                };
            }
            catch (System.Xml.XmlException) { }
        }

        foreach (var group in StationModuleDefinitions.Values
                     .GroupBy(module => module.Name, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var hasGenericVariant = group.Any(module =>
                string.Equals(module.Race, "default", StringComparison.OrdinalIgnoreCase));
            foreach (var module in group)
            {
                if (hasGenericVariant &&
                    string.Equals(module.Race, "default", StringComparison.OrdinalIgnoreCase)) continue;

                module.Name = $"{StationModuleRaceName(module.Race)} {module.Name}";
            }
        }
    }

    private void LoadShipBuildStorageParameters(string libDir)
    {
        var path = Path.Combine(libDir, "parameters.xml");
        if (!File.Exists(path)) return;
        var root = XDocument.Load(path).Root;
        WharfUpgradeResourceFactors = ReadUpgradeResourceFactors(root?.Element("wharfupgraderesources"));
        ShipyardUpgradeResourceFactors = ReadUpgradeResourceFactors(root?.Element("shipyardupgraderesources"));
    }

    private static IReadOnlyDictionary<string, double> ReadUpgradeResourceFactors(XElement? element)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var factor = element?.Element("factor");
        if (factor == null) return result;
        foreach (var attribute in factor.Attributes())
        {
            if (double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                result[attribute.Name.LocalName] = value;
        }
        return result;
    }

    private static IReadOnlyList<StationModuleProductDefinition> ReadStationModuleProducts(
        XElement properties, string race)
    {
        var products = new List<StationModuleProductDefinition>();
        var production = properties.Element("production");
        if (production != null)
        {
            var queue = production.Element("queue");
            var queueItems = queue?.Elements("item").ToArray() ?? Array.Empty<XElement>();
            if (queueItems.Length > 0)
            {
                foreach (var item in queueItems)
                    AddProduct(products, item.Attribute("ware")?.Value,
                        item.Attribute("method")?.Value ?? race);
            }
            else if (queue != null && queue.Attribute("ware") != null)
            {
                AddProduct(products, queue.Attribute("ware")?.Value,
                    queue.Attribute("method")?.Value ?? race);
            }
            else
            {
                foreach (var wareId in (production.Attribute("wares")?.Value ?? string.Empty)
                             .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    AddProduct(products, wareId, race);
            }
        }

        // processingmodule 没有 production/queue，而是在 products 中声明处理结果。
        foreach (var product in properties.Element("products")?.Elements("ware") ?? Enumerable.Empty<XElement>())
        {
            var outputPerMinute = double.TryParse(product.Attribute("amount")?.Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) ? amount : (double?)null;
            AddProduct(products, product.Attribute("ware")?.Value, "processing", outputPerMinute);
        }

        return products;
    }

    private static void AddProduct(
        ICollection<StationModuleProductDefinition> products, string? wareId, string method,
        double? outputPerMinute = null)
    {
        if (string.IsNullOrWhiteSpace(wareId) ||
            products.Any(product => string.Equals(product.WareId, wareId, StringComparison.OrdinalIgnoreCase))) return;
        products.Add(new StationModuleProductDefinition(wareId, method, outputPerMinute));
    }

    private static string StationModuleRaceName(string race) => race.ToLowerInvariant() switch
    {
        "argon" => "Argon",
        "paranid" => "Paranid",
        "teladi" => "Teladi",
        "split" => "Split",
        "terran" => "Terran",
        "boron" => "Boron",
        "default" => "GEN",
        _ when string.IsNullOrWhiteSpace(race) => "GEN",
        _ => char.ToUpperInvariant(race[0]) + race[1..]
    };

    private static bool IsPlayerStationModuleWare(Ware ware)
    {
        if (string.IsNullOrWhiteSpace(ware.ComponentRef) ||
            !ware.Tags.Contains("module", StringComparer.OrdinalIgnoreCase)) return false;
        if (ware.Id.Contains("venture", StringComparison.OrdinalIgnoreCase) ||
            ware.ComponentRef.Contains("venture", StringComparison.OrdinalIgnoreCase)) return false;

        return !ware.Tags.Any(tag => tag.Equals("limited", StringComparison.OrdinalIgnoreCase) ||
                                     tag.Equals("noblueprint", StringComparison.OrdinalIgnoreCase) ||
                                     tag.Equals("noplayerblueprint", StringComparison.OrdinalIgnoreCase) ||
                                     tag.Equals("noplayerbuild", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveStationModuleRace(XElement? identification, XElement? workforce, string moduleWareId)
    {
        var race = identification?.Attribute("makerrace")?.Value
                       ?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                   ?? workforce?.Attribute("race")?.Value
                       ?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(race)) return race;

        foreach (var part in moduleWareId.Split('_', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var inferred = part.ToLowerInvariant() switch
            {
                "arg" or "pir" => "argon",
                "par" => "paranid",
                "tel" => "teladi",
                "bor" => "boron",
                "spl" => "split",
                "ter" => "terran",
                _ => null
            };
            if (inferred != null) return inferred;
        }
        return "default";
    }

    /// <summary>
    /// 将本地宏文本转换为模块列表使用的短显示名。
    /// 本地文本表中的模块名会同时包含英文检索注记和中文显示名；英文括号段不是游戏内展示名。
    /// </summary>
    private static string FormatStationModuleName(string? name, string fallbackId)
    {
        var compactName = Regex.Replace(name ?? string.Empty, @"\([^)]*[A-Za-z][^)]*\)", string.Empty).Trim();
        compactName = Regex.Replace(compactName, @"\s{2,}", " ");
        if (string.IsNullOrWhiteSpace(compactName)) compactName = fallbackId;

        return compactName;
    }

    /// <summary>
    /// 根据工厂中文名查找商品。
    /// </summary>
    public Ware? FindByFactoryName(string factoryName)
    {
        WaresByFactoryName.TryGetValue(factoryName, out var ware);
        return ware;
    }

    /// <summary>
    /// 根据 ware ID 查找商品。
    /// </summary>
    public Ware? FindByWareId(string wareId)
    {
        Wares.TryGetValue(wareId, out var ware);
        return ware;
    }

    /// <summary>
    /// 根据模块 macro ID 查找对应的 ware ID（来自 modules.xml）。
    /// </summary>
    public string? FindWareIdByModuleId(string moduleId)
    {
        ModuleWareMapping.TryGetValue(moduleId, out var wareId);
        return wareId;
    }

    /// <summary>
    /// 解析文本引用 "{pageId,textId}" 为实际文本（公开版本）。
    /// </summary>
    public string? ResolveText(string? textRef)
    {
        return ResolveTextRef(textRef);
    }

    /// <summary>
    /// 从解包目录加载所有舰船数据（包括 DLC 扩展中的舰船）。
    /// </summary>
    public async Task LoadShipsAsync(string dataDir)
    {
        Ships.Clear();
        EnsureDataFileIndexes(dataDir);
        _shipComponentData.Clear();
        _storageCargoByMacro.Clear();

        // 搜索所有 ship_*_macro.xml 文件
        var shipFiles = new List<string>();

        // 1. 基础目录
        var unitsDir = Path.Combine(dataDir, "assets", "units");
        if (Directory.Exists(unitsDir))
        {
            shipFiles.AddRange(Directory.GetFiles(
                unitsDir, "ship_*_macro.xml", SearchOption.AllDirectories));
        }

        // 2. DLC 扩展目录
        var extensionsDir = Path.Combine(dataDir, "extensions");
        if (Directory.Exists(extensionsDir))
        {
            foreach (var dlcDir in Directory.GetDirectories(extensionsDir, "ego_dlc_*"))
            {
                var dlcUnitsDir = Path.Combine(dlcDir, "assets", "units");
                if (Directory.Exists(dlcUnitsDir))
                {
                    shipFiles.AddRange(Directory.GetFiles(
                        dlcUnitsDir, "ship_*_macro.xml", SearchOption.AllDirectories));
                }
            }
        }

        foreach (var filePath in shipFiles)
        {
            try
            {
                var ship = ParseShipMacro(filePath, dataDir);
                if (ship != null)
                {
                    Ships[ship.Id] = ship;
                }
            }
            catch
            {
                // 忽略解析失败的单个文件
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 从 ship macro XML 文件解析舰船信息。
    /// </summary>
    private ShipInfo? ParseShipMacro(string filePath, string dataDir)
    {
        var doc = XDocument.Load(filePath);
        var macroElem = doc.Root?.Element("macro");
        if (macroElem == null) return null;

        var macroName = macroElem.Attribute("name")?.Value;
        var classAttr = macroElem.Attribute("class")?.Value;
        if (string.IsNullOrEmpty(macroName) || string.IsNullOrEmpty(classAttr))
            return null;

        var props = macroElem.Element("properties");
        if (props == null) return null;

        var ident = props.Element("identification");
        if (ident == null) return null;

        var nameRef = ident.Attribute("name")?.Value;
        var makerRace = ident.Attribute("makerrace")?.Value ?? "";
        // makerrace 可能为空格分隔的多个种族（如 "argon teladi"，特使船可在两族建造）
        var races = makerRace.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var primaryRace = races.FirstOrDefault() ?? "";

        // 乘员为 0 的舰船（竞速飞船/无人机/特殊船）玩家不可驾驶量产，直接剔除
        var peopleElem = props.Element("people");
        if (peopleElem != null &&
            int.TryParse(peopleElem.Attribute("capacity")?.Value, out var crewCapacity) &&
            crewCapacity == 0)
            return null;

        // 无玩家蓝图/限量的舰船（limited/noblueprint/noplayerblueprint）直接剔除
        if (_wareTagsByMacro.TryGetValue(macroName, out var shipWareTags) && IsUnavailableTags(shipWareTags))
            return null;

        // 从 class 提取尺寸（如 "ship_l" → "L"）
        var sizeCategory = ParseSizeFromClass(classAttr);

        // 舰船类型
        var shipTypeElem = props.Element("ship");
        var shipType = shipTypeElem?.Attribute("type")?.Value ?? "";

        // 主要用途
        var purposeElem = props.Element("purpose");
        var purpose = purposeElem?.Attribute("primary")?.Value ?? "";

        // 变体（从 macro 名提取 _a, _b 等）
        var variant = "";
        var variantMatch = System.Text.RegularExpressions.Regex.Match(macroName, @"_([a-z])_macro$");
        if (variantMatch.Success)
            variant = variantMatch.Groups[1].Value.ToUpperInvariant();

        // 推进器尺寸标签（<thruster tags="medium" />）
        var thrusterTag = props.Element("thruster")?.Attribute("tags")?.Value ?? "";

        // 物理参数（<physics mass="" > <inertia/> <drag/> </physics>）
        double mass = 0, inertiaPitch = 0, inertiaYaw = 0, inertiaRoll = 0,
               dragForward = 0, dragReverse = 0, dragHorizontal = 0,
               dragVertical = 0, dragPitch = 0, dragYaw = 0, dragRoll = 0;
        var physicsElem = props.Element("physics");
        if (physicsElem != null)
        {
            double.TryParse(physicsElem.Attribute("mass")?.Value,
                System.Globalization.NumberStyles.Float, null, out mass);
            var inertiaElem = physicsElem.Element("inertia");
            if (inertiaElem != null)
            {
                double.TryParse(inertiaElem.Attribute("pitch")?.Value,
                    System.Globalization.NumberStyles.Float, null, out inertiaPitch);
                double.TryParse(inertiaElem.Attribute("yaw")?.Value,
                    System.Globalization.NumberStyles.Float, null, out inertiaYaw);
                double.TryParse(inertiaElem.Attribute("roll")?.Value,
                    System.Globalization.NumberStyles.Float, null, out inertiaRoll);
            }
            var dragElem = physicsElem.Element("drag");
            if (dragElem != null)
            {
                double.TryParse(dragElem.Attribute("forward")?.Value,
                    System.Globalization.NumberStyles.Float, null, out dragForward);
                double.TryParse(dragElem.Attribute("reverse")?.Value,
                    System.Globalization.NumberStyles.Float, null, out dragReverse);
                double.TryParse(dragElem.Attribute("horizontal")?.Value,
                    System.Globalization.NumberStyles.Float, null, out dragHorizontal);
                double.TryParse(dragElem.Attribute("vertical")?.Value,
                    System.Globalization.NumberStyles.Float, null, out dragVertical);
                double.TryParse(dragElem.Attribute("pitch")?.Value,
                    System.Globalization.NumberStyles.Float, null, out dragPitch);
                double.TryParse(dragElem.Attribute("yaw")?.Value,
                    System.Globalization.NumberStyles.Float, null, out dragYaw);
                double.TryParse(dragElem.Attribute("roll")?.Value,
                    System.Globalization.NumberStyles.Float, null, out dragRoll);
            }
        }

        // 加速度系数（<accfactors> 位于 <physics> 内部：
        // <physics><inertia/><drag/><accfactors forward="" reverse="" horizontal="" vertical="" /></physics>，缺省 1.0）
        // ⚠️ 属性缺失必须保持缺省 1.0：double.TryParse(null, out x) 解析失败会把 x 置 0（不保持调用前值），
        //    不能直接对缺失属性 TryParse——accfactors 常只写 forward（如 Elite 仅 forward="1.2"），
        //    reverse/horizontal/vertical 缺失时须保留 1.0（2026-08-14 修复：此前缺 reverse 的船反向制动减速度为 0）。
        double accForward = 1.0, accReverse = 1.0, accHorizontal = 1.0, accVertical = 1.0;
        var accElem = physicsElem?.Element("accfactors");
        if (accElem != null)
        {
            if (accElem.Attribute("forward") is { } fAttr)
                double.TryParse(fAttr.Value, System.Globalization.NumberStyles.Float, null, out accForward);
            if (accElem.Attribute("reverse") is { } rAttr)
                double.TryParse(rAttr.Value, System.Globalization.NumberStyles.Float, null, out accReverse);
            if (accElem.Attribute("horizontal") is { } hAttr)
                double.TryParse(hAttr.Value, System.Globalization.NumberStyles.Float, null, out accHorizontal);
            if (accElem.Attribute("vertical") is { } vAttr)
                double.TryParse(vAttr.Value, System.Globalization.NumberStyles.Float, null, out accVertical);
        }

        var ship = new ShipInfo
        {
            Id = macroName,
            SourceDir = System.IO.Path.GetDirectoryName(filePath),
            Name = ResolveTextRefs(nameRef) ?? macroName,
            Race = primaryRace,
            Races = races,
            SizeCategory = sizeCategory,
            ShipType = shipType,
            Purpose = purpose,
            Variant = variant,
            ThrusterTag = thrusterTag,
            Mass = mass,
            InertiaPitch = inertiaPitch,
            InertiaYaw = inertiaYaw,
            InertiaRoll = inertiaRoll,
            DragForward = dragForward,
            DragReverse = dragReverse,
            DragHorizontal = dragHorizontal,
            DragVertical = dragVertical,
            DragPitch = dragPitch,
            DragYaw = dragYaw,
            DragRoll = dragRoll,
            AccFactorForward = accForward,
            AccFactorReverse = accReverse,
            AccFactorHorizontal = accHorizontal,
            AccFactorVertical = accVertical
        };

        // 舰船 macro 与组件各只加载一次；同一组件的连接点、尺寸结果由变体共享。
        var component = ReadShipComponentData(macroElem, filePath, dataDir);
        ship.EngineCount = component.EngineCount;
        ship.EngineSlotTags = component.EngineSlotTags.ToList();
        ship.Length = component.Length;
        ship.Width = component.Width;

        // 货舱信息：从 <connection ref="con_storage*"> 引用仓储宏，读取 <cargo max="" tags="">
        // ⚠️ 连接点在宏的 <connections> 元素下（不在 <properties>）；连接点名不统一
        // （con_storage01 / con_storage_01 / con_storage），按 con_storage 前缀匹配
        var storageConn = macroElem.Descendants("connection")
            .FirstOrDefault(c => c.Attribute("ref")?.Value.StartsWith("con_storage", StringComparison.OrdinalIgnoreCase) == true);
        var storageRef = storageConn?.Element("macro")?.Attribute("ref")?.Value;
        if (!string.IsNullOrEmpty(storageRef))
        {
            var cargo = ReadStorageCargo(storageRef, dataDir);
            if (cargo != null)
            {
                ship.CargoCapacity = cargo.Capacity;
                ship.CargoTypes = cargo.Types.ToList();
            }
        }

        return ship;
    }

    /// <summary>
    /// 从舰船组件文件的碰撞体 part（tags 含 "part" 且不含 "nocollision"）中，
    /// 取 &lt;size&gt;&lt;max z&gt; 最大的 part（即船主体 part_main），船长 = 2 × max.z、船宽 = 2 × max.x（米）。
    /// </summary>
    private ShipComponentData ReadShipComponentData(XElement macro, string shipMacroPath, string dataDir)
    {
        var componentRef = macro.Element("component")?.Attribute("ref")?.Value;
        if (string.IsNullOrEmpty(componentRef)) return ShipComponentData.Default;

        var componentPath = LocateComponentFile(shipMacroPath, componentRef, dataDir);
        if (componentPath == null) return ShipComponentData.Default;
        if (_shipComponentData.TryGetValue(componentPath, out var cached)) return cached;

        try
        {
            var compDoc = XDocument.Load(componentPath);
            var connections = compDoc.Root?.Descendants("connection").ToList() ?? [];

            var engineConnections = connections
                .Where(connection => connection.Attribute("name")?.Value.StartsWith(
                    "con_engine", StringComparison.OrdinalIgnoreCase) == true)
                .ToList();
            var engineTags = engineConnections
                .SelectMany(connection => ExtractOwnershipTags(connection.Attribute("tags")?.Value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            double maxZ = 0, maxX = 0;
            foreach (var conn in connections)
            {
                var tagList = (conn.Attribute("tags")?.Value ?? "")
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (!tagList.Contains("part", StringComparer.OrdinalIgnoreCase)) continue;
                if (tagList.Contains("nocollision", StringComparer.OrdinalIgnoreCase)) continue;
                foreach (var part in conn.Descendants("part"))
                {
                    var mx = part.Element("size")?.Element("max");
                    if (mx == null) continue;
                    if (double.TryParse(mx.Attribute("z")?.Value,
                        System.Globalization.NumberStyles.Float, null, out var z) && z > maxZ)
                    {
                        maxZ = z;
                        double.TryParse(mx.Attribute("x")?.Value,
                            System.Globalization.NumberStyles.Float, null, out maxX);
                    }
                }
            }

            var result = new ShipComponentData(
                engineConnections.Count > 0 ? engineConnections.Count : 1,
                engineTags,
                maxZ * 2,
                maxX * 2);
            _shipComponentData[componentPath] = result;
            return result;
        }
        catch
        {
            _shipComponentData[componentPath] = ShipComponentData.Default;
            return ShipComponentData.Default;
        }
    }

    private StorageCargoData? ReadStorageCargo(string storageRef, string dataDir)
    {
        if (_storageCargoByMacro.TryGetValue(storageRef, out var cached)) return cached;

        var storagePath = LocateMacroFile(storageRef, dataDir);
        if (storagePath == null) return null;
        try
        {
            var cargo = XDocument.Load(storagePath).Root?
                .Element("macro")?.Element("properties")?.Element("cargo");
            if (cargo == null) return null;

            double.TryParse(cargo.Attribute("max")?.Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var capacity);
            var result = new StorageCargoData(
                capacity,
                (cargo.Attribute("tags")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));
            _storageCargoByMacro[storageRef] = result;
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 定位组件文件：先查宏文件所在目录的上级（如 .../macros/ → .../），再查询 X4 官方组件索引；
    /// 索引缺失时只建立一次全局文件名兜底索引。
    /// </summary>
    private string? LocateComponentFile(string macroFilePath, string componentRef, string dataDir)
    {
        var macrosDir = Path.GetDirectoryName(macroFilePath);
        if (macrosDir != null)
        {
            var parentDir = Path.GetDirectoryName(macrosDir);
            if (parentDir != null)
            {
                var candidate = Path.Combine(parentDir, componentRef + ".xml");
                if (File.Exists(candidate)) return candidate;
            }
        }

        if (_componentPaths.TryGetValue(componentRef, out var indexedPath) && File.Exists(indexedPath))
            return indexedPath;

        EnsureComponentFallbackIndex(dataDir);
        return _componentFallbackPaths!.GetValueOrDefault(componentRef);
    }

    // 连接点 tags 中的类型/尺寸/渲染标签，解析归属标签时移除
    private static readonly HashSet<string> SlotTypeTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "engine", "shield", "weapon", "primaryweapon", "turret", "thruster"
    };

    private static readonly HashSet<string> SlotSizeTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "small", "medium", "large", "extralarge", "s", "m", "l", "xl"
    };

    private static readonly HashSet<string> IgnoredOwnershipTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "component", "platformcollision", "envmap_cockpit", "mandatory"
    };

    /// <summary>
    /// 从连接点 tags 提取"归属标签"：移除类型/尺寸/渲染/强制标签与 symmetry* 前缀标签。
    /// 剩余为决定"哪些舰船槽位能安装本装备"的标签（如 advanced/standard/ship_gen_m_corvette_01）。
    /// </summary>
    private static List<string> ExtractOwnershipTags(string? tagsAttr)
    {
        if (string.IsNullOrWhiteSpace(tagsAttr)) return new List<string>();
        return tagsAttr
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !SlotTypeTags.Contains(t))
            .Where(t => !SlotSizeTags.Contains(t))
            .Where(t => !IgnoredOwnershipTags.Contains(t))
            .Where(t => !t.StartsWith("symmetry", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 从 class 属性解析尺寸分类。
    /// </summary>
    private static string ParseSizeFromClass(string classAttr)
    {
        return classAttr switch
        {
            "ship_xl" => "XL",
            "ship_l" => "L",
            "ship_m" => "M",
            "ship_s" => "S",
            _ => classAttr
        };
    }

    /// <summary>
    /// 从解包目录加载所有引擎与推进器数据（包括 DLC 扩展）。
    /// </summary>
    public async Task LoadEquipmentAsync(string dataDir)
    {
        Engines.Clear();
        Thrusters.Clear();
        EnsureDataFileIndexes(dataDir);
        _engineSlotTagsByComponent.Clear();

        var engineFiles = new List<string>();
        var thrusterFiles = new List<string>();

        // 收集指定根目录下的引擎/推进器宏文件
        void CollectFrom(string root)
        {
            var enginesDir = Path.Combine(root, "assets", "props", "engines", "macros");
            if (Directory.Exists(enginesDir))
            {
                engineFiles.AddRange(Directory.GetFiles(enginesDir, "engine_*_macro.xml"));
                thrusterFiles.AddRange(Directory.GetFiles(enginesDir, "thruster_*_macro.xml"));
            }
        }

        // 1. 基础目录
        CollectFrom(dataDir);

        // 2. DLC 扩展目录
        var extensionsDir = Path.Combine(dataDir, "extensions");
        if (Directory.Exists(extensionsDir))
        {
            foreach (var dlcDir in Directory.GetDirectories(extensionsDir, "ego_dlc_*"))
                CollectFrom(dlcDir);
        }

        // 引擎身份去重：剔除 timelines 与标准版重复的同名引擎（同种族+尺寸+修正后风格+档位保留先加载的基础版）
        var seenEngineKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in engineFiles)
        {
            try
            {
                var engine = ParseEngineMacro(filePath, dataDir);
                if (engine == null) continue;
                // 用户要求剔除的引擎（Racer 赛车引擎，不加入列表）
                if (ExcludedEngineIds.Contains(engine.Id)) continue;
                // 同身份引擎（种族+尺寸+修正后风格+档位）只保留一个
                var key = $"{engine.Race}|{engine.SizeCategory}|{engine.Style}|{engine.Mk}";
                if (!seenEngineKeys.Add(key)) continue;
                Engines[engine.Id] = engine;
            }
            catch
            {
                // 忽略解析失败的单个文件
            }
        }

        foreach (var filePath in thrusterFiles)
        {
            try
            {
                var thruster = ParseThrusterMacro(filePath);
                if (thruster != null) Thrusters[thruster.Id] = thruster;
            }
            catch
            {
                // 忽略解析失败的单个文件
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 用户要求从引擎列表中剔除的引擎（Racer 赛车引擎 / Timelines 竞速教学引擎，不加入列表）。
    /// 其余特殊引擎（Envoy/Astrid/TER Frontier 等）均保留，其可安装性由归属标签（SlotTags）决定。
    /// </summary>
    private static readonly HashSet<string> ExcludedEngineIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "engine_arg_s_racer_01_mk1_macro",
        "engine_par_s_racer_01_mk1_macro",
        "engine_tel_s_racer_01_mk1_macro",
        "engine_gen_s_racer_01_mk1_macro",
        "engine_gen_s_racer_01_mk2_macro",
        // Timelines 竞速教学引擎（ARG traveltutorial，仅用于竞速型舰船，玩家不可量产）
        "engine_arg_s_traveltutorial_01_mk1_macro",
    };

    // 引擎宏名正则：engine_{race}_{size}_{style}_0x_mk{n}。
    // 风格不做白名单限制（含 allround/combat/travel/corvette/yacht/virtual/racer 等），
    // 可安装性完全由连接点归属标签（SlotTags）决定；尺寸必须为 s/m/l/xl 才接受。
    private static readonly Regex EngineMacroRegex =
        new(@"^engine_([a-z]{2,4})_([a-z]+)_([a-z]+)_(\d+)_mk(\d+)_macro$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 合法引擎尺寸（过滤 xs/spacesuit/mine 等非玩家引擎）
    private static readonly HashSet<string> ValidEquipSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        "s", "m", "l", "xl"
    };

    /// <summary>
    /// 从引擎宏 XML 文件解析引擎信息。
    /// </summary>
    private EngineInfo? ParseEngineMacro(string filePath, string dataDir)
    {
        var doc = XDocument.Load(filePath);
        var macroElem = doc.Root?.Element("macro");
        if (macroElem == null) return null;

        var macroName = macroElem.Attribute("name")?.Value;
        if (string.IsNullOrEmpty(macroName)) return null;

        var match = EngineMacroRegex.Match(macroName);
        if (!match.Success) return null;

        // 引擎宏名种族为缩写（"arg"），映射为全名（"argon"）以便与舰船 makerrace 及前端过滤一致
        var race = ResolveRaceName(match.Groups[1].Value);
        var size = match.Groups[2].Value.ToLowerInvariant();
        if (!ValidEquipSizes.Contains(size)) return null;

        // 无玩家蓝图的引擎剔除：无 ware 条目（如 Timelines 教学引擎 traveltutorial）或
        // limited/noblueprint/noplayerblueprint（无玩家蓝图/限量）
        if (!_wareTagsByMacro.TryGetValue(macroName, out var engWareTags) || IsUnavailableTags(engWareTags))
            return null;

        var style = match.Groups[3].Value.ToLowerInvariant();
        // 注意：mk 取 Groups[5]（Groups[4] 是序号 01/02，误用会导致 mk2/mk3 与 mk1 撞键被去重）
        var mk = int.TryParse(match.Groups[5].Value, out var m) ? m : 0;

        // 游戏内引擎风格显示修正（仅显示分类，不影响计算参数）：
        // - BOR 的 XL/L 引擎宏名为 travel，但游戏内文本为 All-round（均衡），修正为均衡
        if (race == "boron" && (size == "l" || size == "xl") && style == "travel")
            style = "allround";
        // - TER 尖端引擎（Frontier，timelines DLC）：L 级宏名 allround_02、M/S 级宏名 virtual，游戏内文本均为 Frontier（尖端）
        else if (race == "terran" &&
                 (string.Equals(macroName, "engine_ter_l_allround_02_mk1_macro", StringComparison.OrdinalIgnoreCase) ||
                  (style == "virtual" && (size == "m" || size == "s"))))
            style = "frontier";

        // 特殊舰船专属引擎的显示名（Envoy 特使引擎 / Astrid 阿斯特丽德引擎）
        string? displayNameOverride = null;
        if (macroName.StartsWith("engine_arg_m_corvette", StringComparison.OrdinalIgnoreCase))
            displayNameOverride = $"ARG Envoy Mk{mk}";
        else if (macroName.StartsWith("engine_tel_m_corvette", StringComparison.OrdinalIgnoreCase))
            displayNameOverride = $"TEL Envoy Mk{mk}";
        else if (macroName.StartsWith("engine_gen_m_yacht", StringComparison.OrdinalIgnoreCase))
            displayNameOverride = "Astrid";

        // 可安装性归属标签（SlotTags）：
        // 引擎宏 component ref → 组件文件 → 含 "component" 标签的连接点 tags → 提取归属标签。
        // 如普通 M 引擎 → ["advanced"]；Envoy 引擎 → ["ship_gen_m_corvette_01"]；Astrid → ["ship_gen_m_yacht_01"]。
        IReadOnlyList<string> slotTags = [];
        var componentRef = macroElem.Element("component")?.Attribute("ref")?.Value;
        if (!string.IsNullOrEmpty(componentRef))
            slotTags = ReadEngineSlotTags(filePath, componentRef, dataDir);

        var props = macroElem.Element("properties");
        if (props == null) return null;

        var ident = props.Element("identification");
        var nameRef = ident?.Attribute("name")?.Value;

        // 推力（<thrust forward reverse />）
        double thrustForward = 0, thrustReverse = 0;
        var thrustElem = props.Element("thrust");
        if (thrustElem != null)
        {
            ParseDouble(thrustElem.Attribute("forward")?.Value, ref thrustForward);
            ParseDouble(thrustElem.Attribute("reverse")?.Value, ref thrustReverse);
        }

        // 巡航（<travel charge thrust attack release />）
        double travelCharge = 0, travelThrust = 0, travelAttack = 0, travelRelease = 0;
        var travelElem = props.Element("travel");
        if (travelElem != null)
        {
            ParseDouble(travelElem.Attribute("charge")?.Value, ref travelCharge);
            ParseDouble(travelElem.Attribute("thrust")?.Value, ref travelThrust);
            ParseDouble(travelElem.Attribute("attack")?.Value, ref travelAttack);
            ParseDouble(travelElem.Attribute("release")?.Value, ref travelRelease);
        }

        return new EngineInfo
        {
            Id = macroName,
            Name = ResolveTextRefs(nameRef) ?? macroName,
            Race = race,
            SizeCategory = ParseSizeFromEquip(size),
            Style = style,
            Mk = mk,
            SlotTags = slotTags.ToList(),
            DisplayNameOverride = displayNameOverride,
            ThrustForward = thrustForward,
            ThrustReverse = thrustReverse,
            TravelCharge = travelCharge,
            TravelThrust = travelThrust,
            TravelAttack = travelAttack,
            TravelRelease = travelRelease
        };
    }

    private IReadOnlyList<string> ReadEngineSlotTags(
        string engineMacroPath, string componentRef, string dataDir)
    {
        var componentPath = LocateComponentFile(engineMacroPath, componentRef, dataDir);
        if (componentPath == null) return [];
        if (_engineSlotTagsByComponent.TryGetValue(componentPath, out var cached)) return cached;

        try
        {
            var result = XDocument.Load(componentPath).Root?.Descendants("connection")
                .Where(connection => (connection.Attribute("tags")?.Value ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("component", StringComparer.OrdinalIgnoreCase))
                .SelectMany(connection => ExtractOwnershipTags(connection.Attribute("tags")?.Value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [];
            _engineSlotTagsByComponent[componentPath] = result;
            return result;
        }
        catch
        {
            _engineSlotTagsByComponent[componentPath] = [];
            return [];
        }
    }

    // 推进器宏名正则：thruster_gen_{size}_{style}_0x_mk{n}
    private static readonly Regex ThrusterMacroRegex =
        new(@"^thruster_gen_([a-z]+)_(allround|combat)_\d+_mk(\d+)_macro$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 从推进器宏 XML 文件解析推进器信息。
    /// </summary>
    private ThrusterInfo? ParseThrusterMacro(string filePath)
    {
        var doc = XDocument.Load(filePath);
        var macroElem = doc.Root?.Element("macro");
        if (macroElem == null) return null;

        var macroName = macroElem.Attribute("name")?.Value;
        if (string.IsNullOrEmpty(macroName)) return null;

        var match = ThrusterMacroRegex.Match(macroName);
        if (!match.Success) return null;

        var size = match.Groups[1].Value.ToLowerInvariant();
        var style = match.Groups[2].Value.ToLowerInvariant();
        var mk = int.TryParse(match.Groups[3].Value, out var m) ? m : 0;

        var props = macroElem.Element("properties");
        if (props == null) return null;

        var ident = props.Element("identification");
        var nameRef = ident?.Attribute("name")?.Value;

        // 推力（<thrust strafe pitch yaw roll />）
        double strafe = 0, pitch = 0, yaw = 0, roll = 0;
        var thrustElem = props.Element("thrust");
        if (thrustElem != null)
        {
            ParseDouble(thrustElem.Attribute("strafe")?.Value, ref strafe);
            ParseDouble(thrustElem.Attribute("pitch")?.Value, ref pitch);
            ParseDouble(thrustElem.Attribute("yaw")?.Value, ref yaw);
            ParseDouble(thrustElem.Attribute("roll")?.Value, ref roll);
        }

        return new ThrusterInfo
        {
            Id = macroName,
            Name = ResolveTextRefs(nameRef) ?? macroName,
            SizeCategory = ParseSizeFromEquip(size),
            Style = style,
            Mk = mk,
            Strafe = strafe,
            Pitch = pitch,
            Yaw = yaw,
            Roll = roll
        };
    }

    /// <summary>
    /// 加载 wares.xml 的蓝图信息：宏 id → ware tags。
    /// 扫描基础目录与所有 DLC 扩展的 libraries/wares.xml，通过 &lt;ware&gt;&lt;component ref&gt; 映射到宏 id。
    /// 用于数据驱动剔除玩家不可获取的物品（limited/noblueprint/noplayerblueprint）。
    /// </summary>
    private void LoadWareBlueprintTags(string dataDir)
    {
        _wareTagsByMacro.Clear();

        void CollectFrom(string root)
        {
            var waresPath = Path.Combine(root, "libraries", "wares.xml");
            if (!File.Exists(waresPath)) return;
            try
            {
                var doc = XDocument.Load(waresPath);
                // 基础 wares.xml 根为 <wares>（ware 为直接子元素）；DLC 为 <diff>（ware 在 <add> 内）。
                // 用 Descendants 递归取所有 <ware>，仅处理含 <component> 子元素的舰船/装备 ware
                //（跳过 production 内的 <ware ware="..."> 原料引用）。
                foreach (var ware in doc.Descendants("ware"))
                {
                    if (ware.Element("component") == null) continue;
                    var tagSet = new HashSet<string>(
                        (ware.Attribute("tags")?.Value ?? "")
                            .Split(' ', StringSplitOptions.RemoveEmptyEntries),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var comp in ware.Elements("component"))
                    {
                        var refValue = comp.Attribute("ref")?.Value;
                        if (!string.IsNullOrEmpty(refValue))
                            _wareTagsByMacro[refValue] = tagSet;
                    }
                }
            }
            catch { /* 忽略单个 wares.xml 解析失败 */ }
        }

        CollectFrom(dataDir);
        var extDir = Path.Combine(dataDir, "extensions");
        if (Directory.Exists(extDir))
            foreach (var sub in Directory.EnumerateDirectories(extDir, "ego_dlc_*"))
                CollectFrom(sub);
    }

    /// <summary>
    /// ware tags 是否表示玩家不可获取（限量/无蓝图/玩家无蓝图）。
    /// </summary>
    private static bool IsUnavailableTags(HashSet<string> tags)
    {
        return tags.Contains("limited") || tags.Contains("noblueprint") || tags.Contains("noplayerblueprint");
    }

    /// <summary>
    /// 引擎宏种族缩写 → 全名（"arg"→"argon" 等），与舰船 makerrace 全名保持一致。
    /// </summary>
    private static string ResolveRaceName(string code)
    {
        return code.ToLowerInvariant() switch
        {
            "arg" => "argon",
            "par" => "paranid",
            "tel" => "teladi",
            "bor" => "boron",
            "spl" => "split",
            "ter" => "terran",
            _ => code
        };
    }

    /// <summary>
    /// 从装备宏名尺寸段解析尺寸分类（"xl"→"XL" 等）。
    /// </summary>
    private static string ParseSizeFromEquip(string size)
    {
        return size.ToLowerInvariant() switch
        {
            "xl" => "XL",
            "l" => "L",
            "m" => "M",
            "s" => "S",
            _ => size
        };
    }

    /// <summary>
    /// 尝试解析字符串为 double（文化无关）。
    /// </summary>
    private static void ParseDouble(string? value, ref double target)
    {
        if (double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var result))
        {
            target = result;
        }
    }

    private sealed record ShipComponentData(
        int EngineCount,
        IReadOnlyList<string> EngineSlotTags,
        double Length,
        double Width)
    {
        public static ShipComponentData Default { get; } = new(1, [], 0, 0);
    }

    private sealed record StorageCargoData(double Capacity, IReadOnlyList<string> Types);
}
