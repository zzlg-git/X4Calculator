namespace X4Calculator.Core.Calculation;

// 源码对应关系：
// 对应 tools/x4-native-analysis/OosDockingPhysicsCache.py 中的 PhysicsRefreshClock，
// 以及 OosTransportDynamics.py 使用的 delay_from_unit_scenario 和 deadline_envelope。

public readonly record struct OosPhysicsRefreshSchedule(
    double DelaySeconds,
    double Deadline,
    ulong? NextRandomState);

public readonly record struct OosPhysicsRefreshDeadlineEnvelope(
    double EarliestDeadline,
    double LatestDeadline);

/// <summary>
/// OOS UpdateEngineParametersEvent 的 source clock。首个 deadline 来自 save；后续事件从实际交付时刻
/// 按 8 + native_random(2) 排程。缓存参数值属于另一份状态，不由该 deadline 恢复。
/// </summary>
public sealed class OosPhysicsRefreshClock
{
    public double Deadline { get; private set; }
    public int RefreshCount { get; private set; }

    public OosPhysicsRefreshClock(double savedDeadline)
    {
        Deadline = RequireFinite(savedDeadline, nameof(savedDeadline));
    }

    public bool IsDue(double now) => RequireFinite(now, nameof(now)) >= Deadline;

    /// <summary>
    /// 已由 orchestration 判定 cache 失效时，把现有事件提前到 now；保留已交付计数。
    /// 本方法不自行推断 visibility、zone entry 或其他失效条件。
    /// </summary>
    public void InvalidateAt(double now)
    {
        Deadline = RequireFinite(now, nameof(now));
    }

    /// <summary>显式 [0,1] sensitivity draw；1 只用于闭区间上界情景。</summary>
    public OosPhysicsRefreshSchedule ScheduleAfterDelivery(double deliveryTime, float unitScenario)
    {
        EnsureDue(deliveryTime);
        var delay = DelayFromUnitScenario(unitScenario);
        return Commit(deliveryTime, delay, nextRandomState: null);
    }

    /// <summary>复用已验证的 X4NativeRandom，返回推进后的 RNG state。</summary>
    public OosPhysicsRefreshSchedule ScheduleAfterDelivery(double deliveryTime, ulong randomState)
    {
        EnsureDue(deliveryTime);
        var draw = X4NativeRandom.DrawFloat(randomState, 2f);
        return Commit(deliveryTime, 8d + draw.Value, draw.NextState);
    }

    public static double DelayFromUnitScenario(float unitScenario)
    {
        if (!float.IsFinite(unitScenario) || unitScenario < 0f || unitScenario > 1f)
            throw new ArgumentOutOfRangeException(nameof(unitScenario));
        var randomPart = unitScenario * 2f;
        return 8d + randomPart;
    }

    /// <summary>仅描述事件时钟，不构成 nonlinear flight ETA 的范围。</summary>
    public static OosPhysicsRefreshDeadlineEnvelope CalculateDeadlineEnvelope(
        double firstDeadline,
        int additionalRefreshes,
        double maximumDeliveryLagSeconds = 0d)
    {
        RequireFinite(firstDeadline, nameof(firstDeadline));
        RequireFinite(maximumDeliveryLagSeconds, nameof(maximumDeliveryLagSeconds));
        if (additionalRefreshes < 0)
            throw new ArgumentOutOfRangeException(nameof(additionalRefreshes));
        if (maximumDeliveryLagSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDeliveryLagSeconds));

        var earliest = firstDeadline + additionalRefreshes * 8d;
        var latest = firstDeadline + additionalRefreshes * (10d + maximumDeliveryLagSeconds);
        return new(
            RequireFinite(earliest, nameof(additionalRefreshes)),
            RequireFinite(latest, nameof(additionalRefreshes)));
    }

    private void EnsureDue(double deliveryTime)
    {
        if (!IsDue(deliveryTime))
            throw new InvalidOperationException("Cannot deliver a future physics refresh event.");
    }

    private OosPhysicsRefreshSchedule Commit(
        double deliveryTime,
        double delaySeconds,
        ulong? nextRandomState)
    {
        var deadline = RequireFinite(deliveryTime + delaySeconds, nameof(deliveryTime));
        Deadline = deadline;
        RefreshCount = checked(RefreshCount + 1);
        return new(delaySeconds, deadline, nextRandomState);
    }

    private static double RequireFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName);
        return value;
    }
}
