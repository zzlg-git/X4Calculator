using System.Collections.ObjectModel;
using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

/// <summary>
/// 空间站劳动力物资的当前与全部建成需求，单位为货物数量/分钟。
/// 当前需求按存档人口拆分 busy/idle；全部建成需求按居住容量种族占比分配，且全部视为 busy。
/// </summary>
public sealed record StationWorkforceConsumptionResult(
    IReadOnlyDictionary<string, double> CurrentPerMinute,
    IReadOnlyDictionary<string, double> AllBuiltPerMinute);

/// <summary>从 workunit_busy/workunit_idle 配方计算站点级劳动力物资消耗。</summary>
public sealed class StationWorkforceConsumptionCalculator
{
    private const double Epsilon = 0.0000001;
    private readonly GameDataDB _gameData;

    public StationWorkforceConsumptionCalculator(GameDataDB gameData)
    {
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
    }

    /// <param name="station">
    /// 当前人口来自 <see cref="Station.WorkforceByRace"/>；附属模块应包含全部建成方案，
    /// 以便按最终居住容量的种族占比分配全部建成岗位。
    /// </param>
    /// <param name="currentRequired">当前已建模块的岗位需求。</param>
    /// <param name="allBuiltRequired">全部模块建成后的岗位需求；不受居住容量封顶。</param>
    public StationWorkforceConsumptionResult Calculate(
        Station station,
        long currentRequired,
        long allBuiltRequired)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentOutOfRangeException.ThrowIfNegative(currentRequired);
        ArgumentOutOfRangeException.ThrowIfNegative(allBuiltRequired);

        var currentPopulationByMethod = AggregatePopulationByMethod(station.WorkforceByRace);
        var currentPopulation = currentPopulationByMethod.Values.Sum();
        var currentBusy = Math.Min(currentPopulation, currentRequired);
        var currentBusyByMethod = ScalePopulation(currentPopulationByMethod, currentBusy);
        var currentIdleByMethod = currentPopulationByMethod.ToDictionary(
            pair => pair.Key,
            pair => pair.Value - currentBusyByMethod.GetValueOrDefault(pair.Key),
            StringComparer.OrdinalIgnoreCase);

        var current = CalculateConsumption(currentBusyByMethod, currentIdleByMethod);

        var allBuiltWeights = GetAllBuiltRaceWeights(station);
        if (allBuiltWeights.Count == 0)
            allBuiltWeights = currentPopulationByMethod;
        var allBuiltBusyByMethod = ScalePopulation(allBuiltWeights, allBuiltRequired);
        var allBuilt = CalculateConsumption(
            allBuiltBusyByMethod,
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase));

        return new StationWorkforceConsumptionResult(
            AsReadOnly(current),
            AsReadOnly(allBuilt));
    }

    private Dictionary<string, double> GetAllBuiltRaceWeights(Station station)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in station.AdditionalModules)
        {
            if (module.Count <= 0 ||
                !_gameData.StationModuleDefinitions.TryGetValue(module.ModuleId, out var definition) ||
                definition.WorkforceCapacity <= 0)
                continue;

            var method = NormalizeRace(definition.Race);
            Add(result, method, (double)definition.WorkforceCapacity * module.Count);
        }
        return result;
    }

    private static Dictionary<string, double> AggregatePopulationByMethod(
        IReadOnlyDictionary<string, long> populationByRace)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (race, population) in populationByRace)
        {
            if (population <= 0) continue;
            Add(result, NormalizeRace(race), population);
        }
        return result;
    }

    private Dictionary<string, double> CalculateConsumption(
        IReadOnlyDictionary<string, double> busyPopulationByMethod,
        IReadOnlyDictionary<string, double> idlePopulationByMethod)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (method, population) in busyPopulationByMethod)
        {
            if (population <= Epsilon) continue;
            AddRecipeConsumption(result, "workunit_busy", method, population);
        }
        foreach (var (method, population) in idlePopulationByMethod)
        {
            if (population <= Epsilon) continue;
            AddRecipeConsumption(result, "workunit_idle", method, population);
        }
        return result;
    }

    private void AddRecipeConsumption(
        IDictionary<string, double> result,
        string workunitId,
        string method,
        double population)
    {
        var workunit = _gameData.FindByWareId(workunitId)
            ?? throw new InvalidOperationException($"缺少劳动力虚拟货物 {workunitId}。");
        var recipe = workunit.Production?.FirstOrDefault(item =>
            item.Method.Equals(method, StringComparison.OrdinalIgnoreCase));
        if (recipe == null)
            throw new InvalidOperationException(
                $"劳动力虚拟货物 {workunitId} 缺少种族方法 {method} 的精确配方。");
        if (recipe is not { Amount: > 0, Time: > 0 })
            throw new InvalidOperationException(
                $"劳动力虚拟货物 {workunitId} 的 {method} 配方具有无效的 amount/time。");

        foreach (var (wareId, amount) in recipe.Consumption ?? [])
        {
            Add(result, wareId, amount / recipe.Amount / recipe.Time * 60 * population);
        }
    }

    private static Dictionary<string, double> ScalePopulation(
        IReadOnlyDictionary<string, double> weights,
        double totalPopulation)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (totalPopulation <= Epsilon) return result;
        var totalWeight = weights.Values.Where(value => value > 0).Sum();
        if (totalWeight <= Epsilon) return result;
        foreach (var (method, weight) in weights)
        {
            if (weight > 0)
                result[method] = totalPopulation * weight / totalWeight;
        }
        return result;
    }

    private static string NormalizeRace(string? race)
    {
        if (string.IsNullOrWhiteSpace(race)) return "default";
        return race.Trim().ToLowerInvariant() switch
        {
            "argon" or "default" => "default",
            "boron" => "boron",
            "paranid" => "paranid",
            "split" => "split",
            "teladi" => "teladi",
            "terran" => "terran",
            _ => throw new InvalidOperationException($"不支持的劳动力种族 {race}，不能静默回退到 default。")
        };
    }

    private static IReadOnlyDictionary<string, double> AsReadOnly(
        IReadOnlyDictionary<string, double> values)
    {
        var ordered = values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        return new ReadOnlyDictionary<string, double>(ordered);
    }

    private static void Add(IDictionary<string, double> values, string key, double amount)
    {
        values.TryGetValue(key, out var current);
        values[key] = current + amount;
    }
}
