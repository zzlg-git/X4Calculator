using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

public sealed record StationBalanceItem(string WareId, string WareName, double PerMinute, ProductionCatalogRole Role);
public sealed record StationModuleContribution(
    string ModuleId,
    string ModuleName,
    string WareId,
    string WareName,
    double PerMinute,
    ProductionCatalogRole Role);
public sealed record StationWorkforceSummary(int Required, int Capacity, long Current)
{
    public IReadOnlyList<StationWorkforceCapacityByRace> CapacityByRace { get; init; } = [];
    public long AvailableForProduction => Math.Min(Required, Current);
    public double Coverage => Required == 0 ? 1 : Math.Min(1, (double)Current / Required);
}
public sealed record StationWorkforceCapacityByRace(string Race, int Capacity);
public sealed record StationWorkforceRequirements(
    int AllBuiltRequired,
    int CurrentRequired,
    bool HasIncompleteProductionModules);
public sealed record StationAutomaticOfferProductionProfile(
    int ConsumingModuleCount,
    long? ProductionBatchAmount);

/// <summary>纯计算的空间站产能、分类和劳动力汇总；不依赖 WPF 或存档解析。</summary>
public sealed class StationProductionPlanner
{
    private readonly GameDataDB _gameData;
    private readonly ProductionChainPlanner _catalogPlanner;
    public StationProductionPlanner(GameDataDB gameData)
    {
        _gameData = gameData;
        _catalogPlanner = new ProductionChainPlanner(gameData);
    }

    public IReadOnlyList<StationBalanceItem> CalculateBalance(Station station, long? currentWorkforce = null)
    {
        return CalculateModuleContributions(station, currentWorkforce)
            .GroupBy(item => item.WareId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new StationBalanceItem(group.Key, group.First().WareName,
                group.Sum(item => item.PerMinute), group.Min(item => item.Role)))
            .OrderBy(item => item.Role)
            .ThenBy(item => item.WareName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>按空间站中的模块实例返回产出和 primary 消耗贡献，供产能视图展开来源。</summary>
    public IReadOnlyList<StationModuleContribution> CalculateModuleContributions(
        Station station,
        long? currentWorkforce = null,
        bool useSavedWorkforceEfficiency = true)
    {
        var workforce = CalculateWorkforce(station, currentWorkforce);
        var workforceCoverage = Math.Clamp(
            useSavedWorkforceEfficiency
                ? station.WorkforceEfficiencyBonus ?? workforce.Coverage
                : workforce.Coverage,
            0d,
            1d);
        return station.Modules.SelectMany(module => CalculateModuleContributions(
            module, station.SolarEfficiencyPercent / 100d, workforceCoverage)).ToList();
    }

    /// <summary>
    /// 计算一个模块实例的产能。多产品队列在“全部”模式下等分运行时间；选中单个产品时，
    /// 该产品使用完整运行时间，模拟另一产品库存已满时模块持续生产剩余产品。
    /// </summary>
    public IReadOnlyList<StationModuleContribution> CalculateModuleContributions(ProductionModule module)
        => CalculateModuleContributions(module, 1.0, 0);

    /// <summary>
    /// 汇总原生自动报价生产分支所需的消费者模块数与单批产量。
    /// 多种有效配方给出不同批量时返回 null，避免把未验证的合并顺序伪装成精确值。
    /// </summary>
    public IReadOnlyDictionary<string, StationAutomaticOfferProductionProfile>
        CalculateAutomaticOfferProductionProfiles(Station station)
    {
        var consumingModuleCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var productionBatchAmounts = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in station.Modules)
        {
            var moduleId = string.IsNullOrWhiteSpace(module.ModuleId)
                ? $"prod_gen_{module.WareId}_macro"
                : module.ModuleId;
            _gameData.StationModuleDefinitions.TryGetValue(moduleId, out var definition);
            var products = definition?.Products.Count > 0 ? definition.Products : CreateLegacyProduct(module);
            var activeProducts = string.IsNullOrWhiteSpace(module.SelectedProductWareId)
                ? products
                : products.Where(product => product.WareId.Equals(
                    module.SelectedProductWareId, StringComparison.OrdinalIgnoreCase)).ToArray();
            var consumedByModule = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var product in activeProducts)
            {
                var recipe = SelectRecipe(_gameData.FindByWareId(product.WareId), product.Method);
                if (recipe == null) continue;

                if (!productionBatchAmounts.TryGetValue(product.WareId, out var batches))
                {
                    batches = new HashSet<long>();
                    productionBatchAmounts[product.WareId] = batches;
                }
                batches.Add((long)recipe.Amount);

                foreach (var wareId in (recipe.Consumption ?? []).Keys)
                {
                    if (!wareId.StartsWith("secondary:", StringComparison.OrdinalIgnoreCase))
                        consumedByModule.Add(wareId);
                }
            }

            foreach (var wareId in consumedByModule)
            {
                consumingModuleCounts.TryGetValue(wareId, out var count);
                consumingModuleCounts[wareId] = checked(count + module.Count);
            }
        }

        return consumingModuleCounts.Keys.Concat(productionBatchAmounts.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                wareId => wareId,
                wareId =>
                {
                    productionBatchAmounts.TryGetValue(wareId, out var batches);
                    return new StationAutomaticOfferProductionProfile(
                        consumingModuleCounts.GetValueOrDefault(wareId),
                        batches?.Count == 1 ? batches.Single() : null);
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private IReadOnlyList<StationModuleContribution> CalculateModuleContributions(
        ProductionModule module,
        double sunlightFactor,
        double workforceCoverage)
    {
        var moduleId = string.IsNullOrWhiteSpace(module.ModuleId)
            ? $"prod_gen_{module.WareId}_macro"
            : module.ModuleId;
        _gameData.StationModuleDefinitions.TryGetValue(moduleId, out var definition);
        var moduleName = definition?.Name ?? module.Ware?.FactoryName ?? module.WareId;

        var configuredProducts = definition?.Products.Count > 0
            ? definition.Products
            : CreateLegacyProduct(module);
        if (configuredProducts.Count == 0) return Array.Empty<StationModuleContribution>();

        var selected = string.IsNullOrWhiteSpace(module.SelectedProductWareId)
            ? null
            : configuredProducts.FirstOrDefault(product => string.Equals(
                product.WareId, module.SelectedProductWareId, StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<StationModuleProductDefinition> activeProducts = selected == null
            ? configuredProducts
            : new[] { selected };
        var lineage = ResolveModuleLineage(module, activeProducts);
        var timeShare = selected == null ? 1d / configuredProducts.Count : 1d;
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var product in activeProducts)
        {
            var ware = _gameData.FindByWareId(product.WareId);
            var recipe = SelectRecipe(ware, product.Method);
            if (ware == null || recipe == null || recipe.Time <= 0 || recipe.Amount <= 0) continue;

            var operationPerMinute = product.OutputPerMinute ?? recipe.OutputPerMinute;
            if (IsScrapProcessor(moduleId) &&
                !string.Equals(module.OperatingMode, "full", StringComparison.OrdinalIgnoreCase))
            {
                // 两种废料处理设施的标准模式均按相同占空机制折算为全速的 20/21。
                operationPerMinute *= 20d / 21d;
            }
            operationPerMinute *= module.Count * timeShare;
            // X4 truncates the workforce bonus to whole ware units for every recipe batch.
            // Applying the percentage to the aggregated hourly output overstates production.
            var workforceBonusPerBatch = Math.Floor(
                recipe.Amount * workforceCoverage * recipe.WorkforceProductBonus);
            var outputPerMinute = operationPerMinute *
                                  (1 + workforceBonusPerBatch / recipe.Amount);
            if (string.Equals(product.WareId, "energycells", StringComparison.OrdinalIgnoreCase))
                outputPerMinute *= sunlightFactor;
            Add(totals, ware.Id, outputPerMinute);
            foreach (var (id, amount) in recipe.Consumption ??
                     new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase))
            {
                if (id.StartsWith("secondary:", StringComparison.OrdinalIgnoreCase)) continue;
                Add(totals, id, -operationPerMinute * amount / recipe.Amount);
            }
        }

        return totals.Select(pair =>
            {
                var ware = _gameData.FindByWareId(pair.Key);
                return new StationModuleContribution(
                    moduleId, moduleName, pair.Key, ware?.Name ?? pair.Key, pair.Value,
                    GetRole(ware, lineage));
            })
            .OrderBy(item => item.Role)
            .ThenBy(item => item.WareName, StringComparer.Ordinal)
            .ToList();
    }

    public StationWorkforceSummary CalculateWorkforce(Station station, long? currentWorkforce = null)
    {
        var required = CalculateWorkforceRequirements(station).AllBuiltRequired;
        var capacity = station.AdditionalModules.Sum(module =>
            _gameData.StationModuleDefinitions.TryGetValue(module.ModuleId, out var definition)
                ? definition.WorkforceCapacity * module.Count : 0);
        var capacityByRace = station.AdditionalModules
            .Select(module => _gameData.StationModuleDefinitions.TryGetValue(module.ModuleId, out var definition)
                ? new StationWorkforceCapacityByRace(definition.Race, definition.WorkforceCapacity * module.Count)
                : new StationWorkforceCapacityByRace(string.Empty, 0))
            .Where(item => item.Capacity > 0)
            .GroupBy(item => item.Race, StringComparer.OrdinalIgnoreCase)
            .Select(group => new StationWorkforceCapacityByRace(group.Key, group.Sum(item => item.Capacity)))
            .OrderBy(item => item.Race, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new StationWorkforceSummary(required, capacity, currentWorkforce ?? capacity)
            { CapacityByRace = capacityByRace };
    }

    public StationWorkforceRequirements CalculateWorkforceRequirements(Station station)
    {
        var allBuiltRequired = station.Modules.Sum(module =>
        {
            var id = string.IsNullOrWhiteSpace(module.ModuleId)
                ? $"prod_gen_{module.WareId}_macro"
                : module.ModuleId;
            return _gameData.StationModuleDefinitions.TryGetValue(id, out var definition)
                ? definition.WorkforceRequired * module.Count
                : 0;
        }) + station.AdditionalModules.Sum(module =>
            _gameData.StationModuleDefinitions.TryGetValue(module.ModuleId, out var definition)
                ? definition.WorkforceRequired * module.Count
                : 0);

        if (station.ModuleConstructions.Count == 0)
            return new StationWorkforceRequirements(allBuiltRequired, allBuiltRequired, false);

        var operationalCounts = station.ModuleConstructions
            .Where(module => module.State == StationModuleConstructionState.Operational)
            .GroupBy(module => module.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var currentRequired = station.Modules.Sum(module =>
        {
            var id = string.IsNullOrWhiteSpace(module.ModuleId)
                ? $"prod_gen_{module.WareId}_macro"
                : module.ModuleId;
            return _gameData.StationModuleDefinitions.TryGetValue(id, out var definition)
                ? definition.WorkforceRequired * Math.Min(module.Count, operationalCounts.GetValueOrDefault(id))
                : 0;
        }) + station.AdditionalModules.Sum(module =>
            _gameData.StationModuleDefinitions.TryGetValue(module.ModuleId, out var definition)
                ? definition.WorkforceRequired * Math.Min(module.Count, operationalCounts.GetValueOrDefault(module.ModuleId))
                : 0);
        var hasIncompleteProductionModules = station.Modules.Any(module =>
            module.Count > operationalCounts.GetValueOrDefault(module.ModuleId));

        return new StationWorkforceRequirements(
            allBuiltRequired, currentRequired, hasIncompleteProductionModules);
    }

    /// <summary>返回模块在当前全站劳动力覆盖率下的加权额外产出比例。</summary>
    public double CalculateModuleWorkforceBonus(ProductionModule module, double workforceCoverage)
    {
        var moduleId = string.IsNullOrWhiteSpace(module.ModuleId)
            ? $"prod_gen_{module.WareId}_macro"
            : module.ModuleId;
        _gameData.StationModuleDefinitions.TryGetValue(moduleId, out var definition);
        var products = definition?.Products.Count > 0 ? definition.Products : CreateLegacyProduct(module);
        var activeProducts = string.IsNullOrWhiteSpace(module.SelectedProductWareId)
            ? products
            : products.Where(product => product.WareId.Equals(
                module.SelectedProductWareId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (activeProducts.Count == 0) return 0;

        return activeProducts
            .Select(product => SelectRecipe(_gameData.FindByWareId(product.WareId), product.Method)?
                .WorkforceProductBonus ?? 0)
            .Average() * Math.Clamp(workforceCoverage, 0, 1);
    }

    public ProductionCatalogRole GetRole(Ware? ware, string lineage = "default") =>
        _catalogPlanner.GetCatalogRole(ware, NormalizeLineage(lineage));

    /// <summary>
    /// 为仓储等没有模块来源的货物复用生产链路角色：农业按站点当前劳动力种族，
    /// 工业使用通用谱系。多种族中任一种族的终端商品均保持终端角色。
    /// </summary>
    public ProductionCatalogRole GetStationWareRole(
        Station station,
        Ware? ware)
    {
        if (!_catalogPlanner.IsAgriculturalWare(ware))
            return GetRole(ware);

        var workforceRaces = station.WorkforceByRace
            .Where(item => item.Value > 0)
            .Select(item => item.Key)
            .Where(race => !string.IsNullOrWhiteSpace(race))
            .Select(NormalizeWorkforceLineage)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return workforceRaces.Length == 0
            ? GetRole(ware, "default")
            : workforceRaces.Min(race => GetRole(ware, race));
    }

    /// <summary>按模块当前实际启用的产品方法解析角色，回收和废料处理模块统一使用回收谱系。</summary>
    public ProductionCatalogRole GetModuleRole(ProductionModule module, Ware? ware)
    {
        var moduleId = string.IsNullOrWhiteSpace(module.ModuleId)
            ? $"prod_gen_{module.WareId}_macro"
            : module.ModuleId;
        _gameData.StationModuleDefinitions.TryGetValue(moduleId, out var definition);
        var products = definition?.Products.Count > 0 ? definition.Products : CreateLegacyProduct(module);
        var activeProducts = string.IsNullOrWhiteSpace(module.SelectedProductWareId)
            ? products
            : products.Where(product => product.WareId.Equals(module.SelectedProductWareId, StringComparison.OrdinalIgnoreCase)).ToArray();
        return GetRole(ware, ResolveModuleLineage(module, activeProducts));
    }

    private string ResolveModuleLineage(
        ProductionModule module,
        IReadOnlyList<StationModuleProductDefinition> activeProducts)
    {
        if (activeProducts.Any(product => product.Method.Equals("recycling", StringComparison.OrdinalIgnoreCase)
                                          || product.Method.Equals("processing", StringComparison.OrdinalIgnoreCase)))
            return "recycling";
        var moduleId = string.IsNullOrWhiteSpace(module.ModuleId)
            ? $"prod_gen_{module.WareId}_macro"
            : module.ModuleId;
        return _gameData.StationModuleDefinitions.TryGetValue(moduleId, out var definition)
            ? definition.Race
            : module.Method;
    }

    private static string NormalizeLineage(string lineage) =>
        string.Equals(lineage, "default", StringComparison.OrdinalIgnoreCase) ? "argon" : lineage;

    private static string NormalizeWorkforceLineage(string race) => race.ToLowerInvariant() switch
    {
        "argon" or "default" => "default",
        "boron" or "paranid" or "split" or "teladi" or "terran" => race.ToLowerInvariant(),
        _ => "default"
    };

    private static void Add(IDictionary<string, double> values, string id, double value)
    {
        values.TryGetValue(id, out var current);
        values[id] = current + value;
    }

    private static bool IsScrapProcessor(string moduleId) =>
        moduleId.Equals("proc_gen_scrapworks_macro", StringComparison.OrdinalIgnoreCase)
        || moduleId.Equals("proc_gen_scrapworkskhaak_macro", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<StationModuleProductDefinition> CreateLegacyProduct(ProductionModule module)
    {
        if (module.Ware == null) return Array.Empty<StationModuleProductDefinition>();
        return new[] { new StationModuleProductDefinition(module.Ware.Id, module.Method) };
    }

    private static ProductionRecipe? SelectRecipe(Ware? ware, string method) => ware?.Production?
        .FirstOrDefault(recipe => string.Equals(recipe.Method, method, StringComparison.OrdinalIgnoreCase))
        ?? ware?.Production?.FirstOrDefault(recipe => string.Equals(recipe.Method, "default", StringComparison.OrdinalIgnoreCase))
        ?? ware?.Production?.FirstOrDefault();
}
