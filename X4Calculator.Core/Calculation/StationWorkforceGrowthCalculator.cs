using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

public enum StationWorkforceGrowthConstraint
{
    Overcrowding,
    LimitedVacancies,
    Layoff
}

public sealed record StationWorkforceGrowthSummary(
    int Target,
    long Current,
    double BaseGrowth,
    int GrowthPerCycle,
    int CycleSeconds,
    double PopulationBonus,
    double CapacityBonus,
    double WelfareBonus,
    double OvercrowdingPenalty,
    StationWorkforceGrowthConstraint ActiveConstraint,
    double ActiveConstraintPenalty,
    long SecondsUntilNextGrowth,
    long SecondsToTarget);

/// <summary>
/// 根据 libraries/parameters.xml 的 workforce/growth 参数计算空间站劳动力增长。
/// 周期读取 growth@interval；update@variation 属于运行时调度抖动，不计入规划值。
/// </summary>
public sealed class StationWorkforceGrowthCalculator
{
    private readonly GameDataDB _gameData;
    private readonly StationProductionPlanner _planner;
    private readonly WorkforceGrowthParameters _parameters;

    public StationWorkforceGrowthCalculator(GameDataDB gameData)
    {
        _gameData = gameData;
        _planner = new StationProductionPlanner(gameData);
        _parameters = gameData.WorkforceGrowthParameters;
    }

    public StationWorkforceGrowthSummary Calculate(
        Station station,
        long sectorPopulation,
        long currentWorkforce,
        bool fillCapacity,
        double? gameTimeSeconds = null,
        double? lastUpdateTimeSeconds = null)
    {
        var workforce = _planner.CalculateWorkforce(station);
        var target = fillCapacity ? workforce.Capacity : Math.Min(workforce.Required, workforce.Capacity);
        var current = Math.Clamp(currentWorkforce, 0, workforce.Capacity);
        var populationBonus = CalculatePopulationBonus(sectorPopulation, _parameters);
        var capacityBonus = CalculateCapacityBonus(workforce.Capacity, _parameters);
        var welfareBonus = station.AdditionalModules.Sum(module =>
            _gameData.StationModuleDefinitions.TryGetValue(module.ModuleId, out var definition)
                ? definition.WorkforceGrowthRate * module.Count
                : 0);
        var overcrowdingPenalty = CalculateOvercrowdingPenalty(current, workforce.Capacity);
        var unconstrainedGrowthExact = workforce.Capacity <= 0
            ? 0
            : _parameters.BaseGrowth *
              Math.Max(0, 1 + populationBonus + capacityBonus + welfareBonus - overcrowdingPenalty);
        var unconstrainedGrowth = (int)Math.Round(unconstrainedGrowthExact,
            MidpointRounding.AwayFromZero);
        var remaining = (long)target - current;
        var activeConstraint = StationWorkforceGrowthConstraint.Overcrowding;
        var activeConstraintPenalty = overcrowdingPenalty;
        int growthPerCycle;
        long growthCycles;
        if (remaining < 0)
        {
            growthPerCycle = (int)remaining;
            growthCycles = 1;
            activeConstraint = StationWorkforceGrowthConstraint.Layoff;
            activeConstraintPenalty = CalculateLayoffPenalty(-remaining, _parameters.BaseGrowth);
        }
        else if (remaining > 0 && unconstrainedGrowthExact > remaining)
        {
            growthPerCycle = (int)remaining;
            growthCycles = 1;
            activeConstraint = StationWorkforceGrowthConstraint.LimitedVacancies;
            activeConstraintPenalty = CalculateLimitedVacancyPenalty(remaining, unconstrainedGrowthExact);
        }
        else
        {
            growthPerCycle = unconstrainedGrowth;
            growthCycles = remaining == 0 || unconstrainedGrowth <= 0
                ? 0
                : (long)Math.Ceiling(remaining / (double)unconstrainedGrowth);
        }
        var secondsUntilNextGrowth = growthCycles == 0
            ? 0
            : CalculateSecondsUntilNextGrowth(gameTimeSeconds, lastUpdateTimeSeconds, _parameters.CycleSeconds);
        var secondsToTarget = growthCycles == 0
            ? 0
            : secondsUntilNextGrowth + (growthCycles - 1) * _parameters.CycleSeconds;

        return new StationWorkforceGrowthSummary(target, current, _parameters.BaseGrowth, growthPerCycle,
            _parameters.CycleSeconds,
            populationBonus, capacityBonus, welfareBonus, overcrowdingPenalty,
            activeConstraint, activeConstraintPenalty,
            secondsUntilNextGrowth, secondsToTarget);
    }

    /// <summary>
    /// 存档时间有效时返回当前周期的剩余秒数；缺失或时间逆序时按完整周期处理。
    /// 已超过一个周期表示该次更新已到期，返回 0。
    /// </summary>
    public static long CalculateSecondsUntilNextGrowth(
        double? gameTimeSeconds,
        double? lastUpdateTimeSeconds,
        int cycleSeconds)
    {
        if (cycleSeconds <= 0) return 0;
        if (gameTimeSeconds is null or < 0 || lastUpdateTimeSeconds is null or < 0 ||
            lastUpdateTimeSeconds > gameTimeSeconds)
            return cycleSeconds;
        var elapsed = gameTimeSeconds.Value - lastUpdateTimeSeconds.Value;
        return elapsed >= cycleSeconds
            ? 0
            : (long)Math.Ceiling(cycleSeconds - elapsed);
    }

    /// <summary>计算两个存档绝对时间之间的剩余整秒；字段缺失或为负时不可用。</summary>
    public static long? CalculateRemainingSeconds(double? gameTimeSeconds, double? endTimeSeconds)
    {
        if (gameTimeSeconds is null or < 0 || endTimeSeconds is null or < 0) return null;
        return (long)Math.Ceiling(Math.Max(0, endTimeSeconds.Value - gameTimeSeconds.Value));
    }

    public static double CalculatePopulationBonus(long population, WorkforceGrowthParameters? parameters = null)
    {
        parameters ??= new WorkforceGrowthParameters();
        var steppedPopulation = Math.Max(0, population) / parameters.PopulationBonusStep * parameters.PopulationBonusStep;
        return Math.Min(steppedPopulation / (double)parameters.PopulationBonusLimit, 1) * parameters.MaximumPopulationBonus;
    }

    public static double CalculateCapacityBonus(int capacity, WorkforceGrowthParameters? parameters = null)
    {
        parameters ??= new WorkforceGrowthParameters();
        if (capacity <= parameters.CapacityBonusMinimum) return 0;
        var range = parameters.CapacityBonusLimit - parameters.CapacityBonusMinimum;
        if (range <= 0) return parameters.MaximumCapacityBonus;
        var progress = Math.Min(capacity, parameters.CapacityBonusLimit) - parameters.CapacityBonusMinimum;
        return progress / (double)range *
               parameters.MaximumCapacityBonus;
    }

    /// <summary>
    /// 根据游戏内整数 UI 样本拟合的居住环境拥挤减益。75% 以下为零，
    /// 75%-100% 保持同一线性斜率；这不是 XML 已确认的公式。
    /// </summary>
    public static double CalculateOvercrowdingPenalty(long currentWorkforce, int capacity)
    {
        if (capacity <= 0 || currentWorkforce <= 0) return 0;
        var occupancy = Math.Clamp(currentWorkforce / (double)capacity, 0, 1);
        if (occupancy <= 0.75) return 0;
        return 2 * (occupancy - 0.75);
    }

    /// <summary>剩余职位不足一个正常增长周期时，按被截断比例显示“有限空缺”。</summary>
    public static double CalculateLimitedVacancyPenalty(long vacancies, double unconstrainedGrowth)
    {
        if (unconstrainedGrowth <= 0 || vacancies >= unconstrainedGrowth) return 0;
        return 1 - Math.Clamp(vacancies, 0, unconstrainedGrowth) / (double)unconstrainedGrowth;
    }

    /// <summary>裁员时其他影响失效；该负影响令下一周期变化量恰好等于全部超额人数。</summary>
    public static double CalculateLayoffPenalty(long excessWorkforce, double baseGrowth)
    {
        if (excessWorkforce <= 0 || baseGrowth <= 0) return 0;
        return 1 + excessWorkforce / baseGrowth;
    }
}
