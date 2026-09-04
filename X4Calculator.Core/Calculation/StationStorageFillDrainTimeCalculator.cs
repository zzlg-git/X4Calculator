namespace X4Calculator.Core.Calculation;

/// <summary>
/// 单种货物用于估算仓储从空填满或从满耗尽所需时间的输入。
/// 正净流量表示填满，负净流量表示耗尽。
/// </summary>
public sealed record StationStorageFillDrainTimeInput(
    string Transport,
    long QuantityLimit,
    double NetPerMinute);

/// <summary>
/// 按运输类型汇总仓储到达边界的最短时间。
/// </summary>
public static class StationStorageFillDrainTimeCalculator
{
    /// <summary>
    /// 返回 <c>container</c>、<c>solid</c> 与 <c>liquid</c> 中具有有效流量的类别；
    /// 同一类别的多种货物取数量上限除以净流量绝对值后的最小分钟数。
    /// </summary>
    public static IReadOnlyDictionary<string, double> Calculate(
        IEnumerable<StationStorageFillDrainTimeInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            if (input.QuantityLimit <= 0 ||
                !double.IsFinite(input.NetPerMinute) ||
                input.NetPerMinute == 0d ||
                !TryNormalizeTransport(input.Transport, out var transport))
            {
                continue;
            }

            var minutes = input.QuantityLimit / Math.Abs(input.NetPerMinute);
            if (!double.IsFinite(minutes)) continue;

            if (!result.TryGetValue(transport, out var shortestMinutes) || minutes < shortestMinutes)
                result[transport] = minutes;
        }

        return result;
    }

    private static bool TryNormalizeTransport(string transport, out string normalizedTransport)
    {
        if (string.Equals(transport, "container", StringComparison.OrdinalIgnoreCase))
        {
            normalizedTransport = "container";
            return true;
        }

        if (string.Equals(transport, "solid", StringComparison.OrdinalIgnoreCase))
        {
            normalizedTransport = "solid";
            return true;
        }

        if (string.Equals(transport, "liquid", StringComparison.OrdinalIgnoreCase))
        {
            normalizedTransport = "liquid";
            return true;
        }

        normalizedTransport = string.Empty;
        return false;
    }
}
