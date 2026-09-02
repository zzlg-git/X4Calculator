using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

/// <summary>
/// 产线计算引擎，协调配方数据与产线编排逻辑。
/// </summary>
public class ProductionCalculator
{
    private readonly GameDataDB _gameData;

    public ProductionCalculator(GameDataDB gameData)
    {
        _gameData = gameData;
    }

    /// <summary>
    /// 计算单个模块的每分钟投入/产出明细。
    /// </summary>
    /// <param name="wareId">商品 ID（如 "advancedcomposites"）。</param>
    /// <param name="method">配方方法（null 表示默认）。</param>
    /// <param name="count">模块数量。</param>
    /// <returns>每分钟投入/产出明细。</returns>
    public ProductionSummary CalculateSingle(string wareId, string? method = null, int count = 1)
    {
        var ware = _gameData.FindByWareId(wareId);
        if (ware == null)
            throw new ArgumentException($"Ware '{wareId}' not found.", nameof(wareId));

        var recipe = SelectRecipe(ware, method);
        if (recipe == null)
            throw new InvalidOperationException($"No production recipe found for '{wareId}'.");

        var summary = new ProductionSummary
        {
            WareName = ware.Name,
            FactoryName = ware.FactoryName,
            Method = recipe.Name,
            Count = count,
            OutputPerMinute = recipe.OutputPerMinute * count,
            CycleTimeSeconds = recipe.Time
        };

        if (recipe.Consumption != null)
        {
            foreach (var (inputId, amount) in recipe.Consumption)
            {
                var inputWare = _gameData.FindByWareId(inputId);
                summary.Inputs[inputId] = new InputDetail
                {
                    WareId = inputId,
                    WareName = inputWare?.Name ?? inputId,
                    AmountPerMinute = amount / recipe.Time * 60 * count,
                    AmountPerCycle = amount * count
                };
            }
        }

        return summary;
    }

    /// <summary>
    /// 从多个模块构建完整产线链并计算供需平衡。
    /// </summary>
    public ProductionChain BuildChain(IEnumerable<ProductionModule> modules)
    {
        var chain = new ProductionChain();

        foreach (var module in modules)
        {
            chain.AddModule(module);
        }

        chain.AutoMatch();
        return chain;
    }

    private static ProductionRecipe? SelectRecipe(Ware ware, string? method)
    {
        if (ware.Production == null || ware.Production.Count == 0)
            return null;

        if (method != null)
            return ware.Production.FirstOrDefault(p =>
                string.Equals(p.Method, method, StringComparison.OrdinalIgnoreCase));

        return ware.Production.FirstOrDefault(p => p.Method == "default")
               ?? ware.Production[0];
    }
}

/// <summary>
/// 单个模块的生产摘要。
/// </summary>
public class ProductionSummary
{
    public string WareName { get; set; } = string.Empty;
    public string FactoryName { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public int Count { get; set; }
    public double OutputPerMinute { get; set; }
    public double CycleTimeSeconds { get; set; }
    public Dictionary<string, InputDetail> Inputs { get; set; } = new();
}

/// <summary>
/// 原料消耗明细。
/// </summary>
public class InputDetail
{
    public string WareId { get; set; } = string.Empty;
    public string WareName { get; set; } = string.Empty;
    public double AmountPerMinute { get; set; }
    public double AmountPerCycle { get; set; }
}
