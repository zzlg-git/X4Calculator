using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

public sealed record ProductionEffectContext(double WorkforceCoverage = 0);

public sealed record ProductionEffectResult(
    double CycleTimeMultiplier,
    double ProductAmountMultiplier)
{
    public double ThroughputMultiplier => ProductAmountMultiplier / CycleTimeMultiplier;
}

/// <summary>集中求值 X4 配方效果，避免各业务计算器为具体 MOD 增加分支。</summary>
public static class ProductionEffectCalculator
{
    public static ProductionEffectResult Evaluate(
        ProductionRecipe recipe,
        ProductionEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(context);

        var workforceCoverage = ClampCoverage(context.WorkforceCoverage);
        var workEffects = recipe.Effects
            .Where(effect => effect.Type.Equals("work", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // 原版与 No DA Wares 都只有一个 work effect。多个 work effect 的组合顺序、
        // 负值及越界值没有运行时证据，因此一律保持中性。手工构造的旧模型没有
        // Effects，继续读取兼容字段，避免改变既有调用契约。
        if (workEffects.Length > 1) return new ProductionEffectResult(1, 1);
        var productBonus = workEffects.Length == 1
            ? workEffects[0].Product ?? 0
            : recipe.WorkforceProductBonus;
        var cycleBonus = workEffects.Length == 1
            ? workEffects[0].Cycle ?? 0
            : recipe.WorkforceCycleBonus;
        if (!double.IsFinite(productBonus) || productBonus < 0 ||
            !double.IsFinite(cycleBonus) || cycleBonus < 0 || cycleBonus >= 1)
            return new ProductionEffectResult(1, 1);

        var cycleMultiplier = 1 - workforceCoverage * cycleBonus;
        var productMultiplier = CalculateWorkforceProductMultiplier(
            recipe, workforceCoverage, productBonus);
        return new ProductionEffectResult(cycleMultiplier, productMultiplier);
    }

    private static double CalculateWorkforceProductMultiplier(
        ProductionRecipe recipe,
        double coverage,
        double productBonus)
    {
        if (!double.IsFinite(recipe.Amount) || recipe.Amount <= 0 || productBonus == 0) return 1;
        var bonusPerBatch = Math.Floor(recipe.Amount * coverage * productBonus);
        return 1 + bonusPerBatch / recipe.Amount;
    }

    private static double ClampCoverage(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

}
