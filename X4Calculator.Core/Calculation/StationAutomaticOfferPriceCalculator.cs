namespace X4Calculator.Core.Calculation;

public readonly record struct StationAutomaticOfferCurveInputs(long EffectiveAmount, long EffectiveTarget);
public readonly record struct StationAutomaticOfferPricePair(decimal BuyPriceCredits, decimal SellPriceCredits);

/// <summary>
/// 复现 X4 9.00 普通生产与纯贸易自动报价的曲线输入、基础价格曲线及双向价差约束。
/// </summary>
public static class StationAutomaticOfferPriceCalculator
{
    private const long MaximumInventoryValueHundredths = 500_000_000L;

    /// <summary>生产购买：Q=max(Mbuy-A0,0)，T=min(Tstorage,A40-A0)。</summary>
    public static StationAutomaticOfferCurveInputs CreateProductionBuyCurveInputs(
        long inventoryAndReservations,
        long storageTarget,
        double totalConsumptionPerHour,
        int consumingModuleCount)
    {
        var normalizedStorageTarget = Math.Max(0L, storageTarget);
        var a0 = consumingModuleCount > 0
            ? TruncateNonNegativeToLong(totalConsumptionPerHour / consumingModuleCount)
            : 0L;
        var a40 = TruncateNonNegativeToLong(totalConsumptionPerHour * 40d);
        var effectiveAmount = inventoryAndReservations > a0 ? inventoryAndReservations - a0 : 0L;
        var consumptionTarget = a40 > a0 ? a40 - a0 : 0L;

        return new StationAutomaticOfferCurveInputs(
            effectiveAmount,
            Math.Min(normalizedStorageTarget, consumptionTarget));
    }

    /// <summary>生产出售：Q=Msell，T=min(floor(500000000/A),max(Tstorage-B,0))。</summary>
    public static StationAutomaticOfferCurveInputs CreateProductionSellCurveInputs(
        long inventoryAfterReservations,
        long storageTarget,
        long productionBatchAmount,
        int averagePriceCredits)
    {
        var normalizedInventory = Math.Max(0L, inventoryAfterReservations);
        var normalizedStorageTarget = Math.Max(0L, storageTarget);
        var targetAfterBatch = normalizedStorageTarget > productionBatchAmount
            ? normalizedStorageTarget - Math.Max(0L, productionBatchAmount)
            : 0L;
        var averagePriceHundredths = checked((long)averagePriceCredits * 100L);
        var valueLimitedTarget = averagePriceHundredths > 0
            ? MaximumInventoryValueHundredths / averagePriceHundredths
            : long.MaxValue;

        return new StationAutomaticOfferCurveInputs(
            normalizedInventory,
            Math.Min(valueLimitedTarget, targetAfterBatch));
    }

    /// <summary>纯贸易：买卖两侧共享 Q=Mtrade、T=Tstorage。</summary>
    public static StationAutomaticOfferCurveInputs CreateTradeCurveInputs(
        long inventoryAndReservations,
        long storageTarget) =>
        new(Math.Max(0L, inventoryAndReservations), Math.Max(0L, storageTarget));

    /// <summary>
    /// 按有效数量和目标计算基础价格，返回单位为 Cr 的价格。
    /// 最低、均价和最高价来自同一货物的本地数据。
    /// </summary>
    public static decimal CalculateBasePriceCredits(
        long effectiveAmount,
        long targetAmount,
        int minimumPriceCredits,
        int averagePriceCredits,
        int maximumPriceCredits)
    {
        var minimumHundredths = checked((long)minimumPriceCredits * 100L);
        var averageHundredths = checked((long)averagePriceCredits * 100L);
        var maximumHundredths = checked((long)maximumPriceCredits * 100L);

        if (effectiveAmount > targetAmount)
            return minimumHundredths / 100m;
        if (effectiveAmount <= 0 || targetAmount <= 0)
            return maximumHundredths / 100m;

        var halfTarget = targetAmount / 2d;
        if (Math.Abs(effectiveAmount - halfTarget) < 0.0000001d)
            return averageHundredths / 100m;

        var z = Math.Max(effectiveAmount - halfTarget, -halfTarget) / halfTarget;
        var spread = z < 0d
            ? maximumHundredths - averageHundredths
            : averageHundredths - minimumHundredths;
        var curve = (3d * z * z * z - 10d * z) / 7d;
        var priceHundredths = (long)(averageHundredths + spread * curve);

        return priceHundredths / 100m;
    }

    public static decimal CalculateBasePriceCredits(
        StationAutomaticOfferCurveInputs inputs,
        int minimumPriceCredits,
        int averagePriceCredits,
        int maximumPriceCredits) =>
        CalculateBasePriceCredits(inputs.EffectiveAmount, inputs.EffectiveTarget,
            minimumPriceCredits, averagePriceCredits, maximumPriceCredits);

    /// <summary>
    /// 两侧均自动时优先下调买价；最低价边界无法满足时再抬高卖价。
    /// </summary>
    public static StationAutomaticOfferPricePair ApplyMinimumSpread(
        decimal candidateBuyPriceCredits,
        decimal candidateSellPriceCredits,
        int minimumPriceCredits,
        int maximumPriceCredits)
    {
        var buy = Math.Clamp(candidateBuyPriceCredits, minimumPriceCredits, maximumPriceCredits);
        var sell = Math.Clamp(candidateSellPriceCredits, minimumPriceCredits, maximumPriceCredits);
        if (buy <= sell - 1m) return new StationAutomaticOfferPricePair(buy, sell);

        buy = Math.Max(minimumPriceCredits, sell - 1m);
        if (buy > sell - 1m)
            sell = Math.Min(maximumPriceCredits, buy + 1m);

        return new StationAutomaticOfferPricePair(buy, sell);
    }

    private static long TruncateNonNegativeToLong(double value)
    {
        if (double.IsNaN(value) || value <= 0d) return 0L;
        if (value >= long.MaxValue) return long.MaxValue;
        return (long)value;
    }
}
