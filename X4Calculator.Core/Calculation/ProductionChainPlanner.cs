using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

public enum ProductionCategory
{
    Industrial,
    Agricultural
}

public sealed record ProductionLineageOption(string Id, string DisplayName);

public enum ProductionCatalogRole
{
    Terminal,
    Intermediate,
    Resource
}

public sealed record ProductionCatalogItem(Ware Ware, int Tier, ProductionCatalogRole Role);

public sealed class ProductionDependencyNode
{
    public required Ware Ware { get; init; }
    public required int Tier { get; init; }
    public ProductionRecipe? Recipe { get; init; }
    public string FacilityRace { get; init; } = "resource";
    public bool IsTarget { get; init; }
    public bool IsResource => Recipe == null;
}

public sealed record ProductionDependencyEdge(string SourceWareId, string TargetWareId);

public sealed class ProductionDependencyPlan
{
    public required string TargetWareId { get; init; }
    public List<ProductionDependencyNode> Nodes { get; } = new();
    public List<ProductionDependencyEdge> Edges { get; } = new();
}

/// <summary>
/// 按工业/农业生产谱系筛选商品，并从目标商品递归生成四层依赖图。
/// </summary>
public sealed class ProductionChainPlanner
{
    private static readonly HashSet<string> IndustrialGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "minerals", "gases", "refined", "hightech", "shiptech", "energy"
    };

    private static readonly HashSet<string> AgriculturalGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "agricultural", "food", "pharmaceutical", "water", "ice", "energy"
    };

    private static readonly IReadOnlyList<ProductionLineageOption> IndustrialLineages =
    [
        new("default", "通用"),
        new("terran", "TER"),
        new("teladi", "TEL"),
        new("recycling", "回收")
    ];

    private static readonly IReadOnlyList<ProductionLineageOption> AgriculturalLineages =
    [
        new("argon", "ARG"),
        new("boron", "BOR"),
        new("paranid", "PAR"),
        new("split", "SPL"),
        new("teladi", "TEL"),
        new("terran", "TER"),
        new("contraband", "走私品")
    ];

    private readonly GameDataDB _gameData;

    public ProductionChainPlanner(GameDataDB gameData)
    {
        _gameData = gameData;
    }

    public static IReadOnlyList<ProductionLineageOption> GetLineages(ProductionCategory category) =>
        category == ProductionCategory.Industrial ? IndustrialLineages : AgriculturalLineages;

    public IReadOnlyList<ProductionCatalogItem> GetCandidateWares(
        ProductionCategory category,
        string lineage)
    {
        var categoryGroups = category == ProductionCategory.Industrial
            ? IndustrialGroups
            : AgriculturalGroups;
        var seedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (category == ProductionCategory.Agricultural)
        {
            var terminalIds = string.Equals(lineage, "contraband", StringComparison.OrdinalIgnoreCase)
                ? GetContrabandTerminalIds()
                : GetPopulationTerminalIds(lineage);
            seedIds.UnionWith(terminalIds);
        }
        else if (category == ProductionCategory.Industrial &&
            string.Equals(lineage, "recycling", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var ware in _gameData.Wares.Values)
            {
                if (ware.Production?.Any(recipe =>
                        string.Equals(recipe.Method, "recycling", StringComparison.OrdinalIgnoreCase)) == true)
                {
                    seedIds.Add(ware.Id);
                }
            }
        }
        else
        {
            foreach (var (wareId, facilities) in _gameData.ProductionFacilitiesByWare)
            {
                var ware = _gameData.FindByWareId(wareId);
                if (ware == null || !categoryGroups.Contains(ware.Group) || SelectRecipe(ware, lineage) == null)
                    continue;

                if (facilities.Any(f => string.Equals(f.Race, lineage, StringComparison.OrdinalIgnoreCase)))
                    seedIds.Add(wareId);

                // 泰拉迪工业体系与通用终端产品共享，仅替换存在专用配方的中间材料。
                if (category == ProductionCategory.Industrial &&
                    string.Equals(lineage, "teladi", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ware.Group, "shiptech", StringComparison.OrdinalIgnoreCase) &&
                    facilities.Any(f => string.Equals(f.Race, "default", StringComparison.OrdinalIgnoreCase)))
                {
                    seedIds.Add(wareId);
                }
            }
        }

        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seedId in seedIds)
            TraceDependencies(seedId, lineage, included, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var tiers = CalculateTierMap(included, lineage);

        return included
            .Select(id => _gameData.FindByWareId(id))
            .Where(ware => ware != null)
            .Select(ware => new ProductionCatalogItem(
                ware!,
                tiers[ware!.Id],
                ResolveCatalogRole(category, ware, tiers[ware.Id], seedIds)))
            .OrderBy(item => item.Tier)
            .ThenBy(item => item.Ware.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private ProductionCatalogRole ResolveCatalogRole(
        ProductionCategory category,
        Ware ware,
        int tier,
        IReadOnlySet<string> agriculturalTerminalIds)
    {
        if (category == ProductionCategory.Agricultural)
        {
            // 水即使是 Boron 人口配方的直接消耗品，也按可生产的中间商品显示为黄色。
            if (string.Equals(ware.Id, "water", StringComparison.OrdinalIgnoreCase))
                return ProductionCatalogRole.Intermediate;
            if (agriculturalTerminalIds.Contains(ware.Id)) return ProductionCatalogRole.Terminal;
            return tier == 0 ? ProductionCatalogRole.Resource : ProductionCatalogRole.Intermediate;
        }

        // 能量电池即使被部分建造方法直接使用，也保持 Tier 0 基础品语义。
        if (string.Equals(ware.Id, "energycells", StringComparison.OrdinalIgnoreCase))
            return ProductionCatalogRole.Resource;
        // 异构石墨炔可通过特殊机制替代常规造船材料，按终端建造材料显示。
        if (string.Equals(ware.Id, "khaakalloy", StringComparison.OrdinalIgnoreCase))
            return ProductionCatalogRole.Terminal;
        if (_gameData.IsDirectBuildMaterial(ware.Id))
            return ProductionCatalogRole.Terminal;
        return tier == 0 ? ProductionCatalogRole.Resource : ProductionCatalogRole.Intermediate;
    }

    /// <summary>按生产链路页面的同一套规则，为空间站模块商品解析三色角色。</summary>
    public ProductionCatalogRole GetCatalogRole(Ware? ware, string lineage)
    {
        if (ware == null) return ProductionCatalogRole.Resource;
        if (AgriculturalGroups.Contains(ware.Group))
        {
            if (string.Equals(ware.Id, "water", StringComparison.OrdinalIgnoreCase))
                return ProductionCatalogRole.Intermediate;
            var terminalIds = string.Equals(lineage, "contraband", StringComparison.OrdinalIgnoreCase)
                ? GetContrabandTerminalIds()
                : GetPopulationTerminalIds(lineage);
            if (terminalIds.Contains(ware.Id)) return ProductionCatalogRole.Terminal;
            var recipe = SelectRecipe(ware, lineage);
            return recipe == null || !GetPrimaryInputIds(recipe).Any()
                ? ProductionCatalogRole.Resource
                : ProductionCatalogRole.Intermediate;
        }

        if (string.Equals(ware.Id, "energycells", StringComparison.OrdinalIgnoreCase))
            return ProductionCatalogRole.Resource;
        if (string.Equals(ware.Id, "khaakalloy", StringComparison.OrdinalIgnoreCase))
            return ProductionCatalogRole.Terminal;
        if (_gameData.IsDirectBuildMaterial(ware.Id))
            return ProductionCatalogRole.Terminal;
        var industrialRecipe = SelectRecipe(ware, lineage);
        return industrialRecipe == null || !GetPrimaryInputIds(industrialRecipe).Any()
            ? ProductionCatalogRole.Resource
            : ProductionCatalogRole.Intermediate;
    }

    public bool IsAgriculturalWare(Ware? ware) =>
        ware != null && AgriculturalGroups.Contains(ware.Group);

    private HashSet<string> GetPopulationTerminalIds(string lineage)
    {
        var method = string.Equals(lineage, "argon", StringComparison.OrdinalIgnoreCase)
            ? "default"
            : lineage;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var workunitId in new[] { "workunit_busy", "workunit_idle" })
        {
            var workunit = _gameData.FindByWareId(workunitId);
            var recipe = workunit?.Production?.FirstOrDefault(item =>
                string.Equals(item.Method, method, StringComparison.OrdinalIgnoreCase));
            if (recipe == null) continue;
            result.UnionWith(GetPrimaryInputIds(recipe));
        }

        return result;
    }

    private HashSet<string> GetContrabandTerminalIds()
    {
        var populationClosure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lineage in AgriculturalLineages.Where(option => option.Id != "contraband"))
        {
            var lineageClosure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var terminalId in GetPopulationTerminalIds(lineage.Id))
            {
                TraceDependencies(terminalId, lineage.Id, lineageClosure,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
            populationClosure.UnionWith(lineageClosure);
        }

        var residual = _gameData.ProductionFacilitiesByWare.Keys
            .Select(_gameData.FindByWareId)
            .Where(ware => ware != null && AgriculturalGroups.Contains(ware.Group) &&
                           !populationClosure.Contains(ware.Id) && SelectRecipe(ware, "contraband") != null)
            .Select(ware => ware!.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedByAnotherResidual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var wareId in residual)
        {
            var ware = _gameData.FindByWareId(wareId);
            var recipe = ware == null ? null : SelectRecipe(ware, "contraband");
            if (recipe == null) continue;
            usedByAnotherResidual.UnionWith(GetPrimaryInputIds(recipe).Where(residual.Contains));
        }

        residual.ExceptWith(usedByAnotherResidual);
        return residual;
    }

    public ProductionDependencyPlan BuildChain(
        string targetWareId,
        ProductionCategory category,
        string lineage)
    {
        var plan = new ProductionDependencyPlan { TargetWareId = targetWareId };
        var nodes = new Dictionary<string, ProductionDependencyNode>(StringComparer.OrdinalIgnoreCase);
        var edgeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var included = GetCandidateWares(category, lineage)
            .Select(item => item.Ware.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        TraceDependencies(targetWareId, lineage, included, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var tiers = CalculateTierMap(included, lineage);

        BuildNode(targetWareId, lineage, targetWareId, tiers, nodes, plan.Edges, edgeKeys,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        plan.Nodes.AddRange(nodes.Values
            .OrderBy(node => node.Tier)
            .ThenBy(node => node.Ware.Name, StringComparer.CurrentCulture));
        return plan;
    }

    private void TraceDependencies(
        string wareId,
        string lineage,
        HashSet<string> included,
        HashSet<string> recursionStack)
    {
        if (!included.Add(wareId)) return;
        if (!recursionStack.Add(wareId)) return;

        var ware = _gameData.FindByWareId(wareId);
        var recipe = ware == null ? null : SelectRecipe(ware, lineage);
        if (recipe?.Consumption != null)
        {
            foreach (var inputId in GetPrimaryInputIds(recipe))
                TraceDependencies(inputId, lineage, included, recursionStack);
        }

        recursionStack.Remove(wareId);
    }

    private void BuildNode(
        string wareId,
        string lineage,
        string targetWareId,
        IReadOnlyDictionary<string, int> tiers,
        Dictionary<string, ProductionDependencyNode> nodes,
        List<ProductionDependencyEdge> edges,
        HashSet<string> edgeKeys,
        HashSet<string> recursionStack)
    {
        if (!recursionStack.Add(wareId))
            throw new InvalidOperationException($"生产配方存在循环依赖：{wareId}");

        var ware = _gameData.FindByWareId(wareId)
                   ?? throw new InvalidOperationException($"找不到生产链商品：{wareId}");
        var recipe = SelectRecipe(ware, lineage);
        var targetTier = tiers[wareId];

        nodes[wareId] = new ProductionDependencyNode
        {
            Ware = ware,
            Tier = targetTier,
            Recipe = recipe,
            FacilityRace = recipe == null ? "resource" : ResolveFacilityRace(wareId, lineage),
            IsTarget = string.Equals(wareId, targetWareId, StringComparison.OrdinalIgnoreCase)
        };

        if (recipe?.Consumption != null)
        {
            foreach (var inputId in GetPrimaryInputIds(recipe))
            {
                var inputWare = _gameData.FindByWareId(inputId)
                                ?? throw new InvalidOperationException($"配方 {wareId} 引用了未知商品：{inputId}");
                var inputTier = tiers[inputWare.Id];
                if (inputTier >= targetTier)
                {
                    throw new InvalidOperationException(
                        $"生产层级无法前向排列：{inputId} (Tier {inputTier}) → {wareId} (Tier {targetTier})");
                }

                var edgeKey = $"{inputId}>{wareId}";
                if (edgeKeys.Add(edgeKey))
                    edges.Add(new ProductionDependencyEdge(inputId, wareId));

                if (!nodes.ContainsKey(inputId))
                    BuildNode(inputId, lineage, targetWareId, tiers, nodes, edges, edgeKeys, recursionStack);
            }
        }

        recursionStack.Remove(wareId);
    }

    private ProductionRecipe? SelectRecipe(Ware ware, string lineage)
    {
        var allRecipes = ware.Production?.ToList();
        if (allRecipes == null || allRecipes.Count == 0) return null;

        if (string.Equals(lineage, "recycling", StringComparison.OrdinalIgnoreCase))
        {
            return allRecipes.FirstOrDefault(recipe =>
                       string.Equals(recipe.Method, "recycling", StringComparison.OrdinalIgnoreCase))
                   ?? allRecipes.FirstOrDefault(recipe =>
                       string.Equals(recipe.Method, "processing", StringComparison.OrdinalIgnoreCase))
                   ?? allRecipes.FirstOrDefault(recipe =>
                       string.Equals(recipe.Method, "default", StringComparison.OrdinalIgnoreCase));
        }

        var recipes = allRecipes
            .Where(recipe => !string.Equals(recipe.Method, "recycling", StringComparison.OrdinalIgnoreCase) &&
                             !string.Equals(recipe.Method, "processing", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (recipes == null || recipes.Count == 0) return null;

        var exact = recipes.FirstOrDefault(recipe =>
            string.Equals(recipe.Method, lineage, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        return recipes.FirstOrDefault(recipe =>
                   string.Equals(recipe.Method, "default", StringComparison.OrdinalIgnoreCase))
               ?? recipes[0];
    }

    private string ResolveFacilityRace(string wareId, string lineage)
    {
        if (!_gameData.ProductionFacilitiesByWare.TryGetValue(wareId, out var facilities))
            return "default";
        if (facilities.Any(f => string.Equals(f.Race, lineage, StringComparison.OrdinalIgnoreCase)))
            return lineage;
        if (facilities.Any(f => string.Equals(f.Race, "default", StringComparison.OrdinalIgnoreCase)))
            return "default";
        return facilities[0].Race;
    }

    private static IEnumerable<string> GetPrimaryInputIds(ProductionRecipe recipe) =>
        recipe.Consumption?.Keys ?? Enumerable.Empty<string>();

    /// <summary>
    /// 以当前谱系实际采用的配方计算最长上游距离。原料为 Tier 0，
    /// 每个产品位于其最深输入之后一列，因此所有边天然只会向右。
    /// </summary>
    private Dictionary<string, int> CalculateTierMap(IEnumerable<string> wareIds, string lineage)
    {
        var ids = wareIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tiers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int Calculate(string wareId)
        {
            if (tiers.TryGetValue(wareId, out var cached)) return cached;
            if (!visiting.Add(wareId))
                throw new InvalidOperationException($"生产配方存在循环依赖：{wareId}");

            var ware = _gameData.FindByWareId(wareId);
            var recipe = ware == null ? null : SelectRecipe(ware, lineage);
            var inputIds = recipe == null
                ? Array.Empty<string>()
                : GetPrimaryInputIds(recipe).Where(ids.Contains).ToArray();
            var tier = inputIds.Length == 0 ? 0 : inputIds.Max(Calculate) + 1;
            if (tier > 3)
                throw new InvalidOperationException($"生产链超过四列可表达范围：{wareId} 需要 Tier {tier}");

            visiting.Remove(wareId);
            tiers[wareId] = tier;
            return tier;
        }

        foreach (var id in ids)
            Calculate(id);

        return tiers;
    }
}
