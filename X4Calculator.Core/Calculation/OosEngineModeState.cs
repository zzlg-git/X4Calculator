namespace X4Calculator.Core.Calculation;

// 源码对应关系：
// 来源：tools/x4-native-analysis/OosDockingEngineState.py
// 相关类型：EngineModeProperties、EngineModeState、TravelStartQueue，
// 以及由 OosTransportDynamics.py 消费的 travel_start_ratio 和 travel_charge_delay。

/// <summary>X4 引擎宏中由 XML/source 解析出的 boost 与 travel 参数。</summary>
public sealed record OosEngineModeProperties
{
    public float BoostThrust { get; }
    public float BoostChargeSeconds { get; }
    public float BoostDurationSeconds { get; }
    public float BoostRechargeDurationSeconds { get; }
    public float BoostReleaseSeconds { get; }
    public float TravelThrust { get; }
    public float TravelChargeSeconds { get; }
    public float TravelAttackSeconds { get; }
    public float TravelReleaseSeconds { get; }
    public float TravelStartThrust { get; }

    public OosEngineModeProperties(
        float boostThrust,
        float boostChargeSeconds,
        float boostDurationSeconds,
        float boostRechargeDurationSeconds,
        float boostReleaseSeconds,
        float travelThrust,
        float travelChargeSeconds,
        float travelAttackSeconds,
        float travelReleaseSeconds,
        float travelStartThrust = 1f)
    {
        BoostThrust = OosEngineModeMath.RequireFinite(boostThrust, nameof(boostThrust));
        BoostChargeSeconds = OosEngineModeMath.RequireFinite(boostChargeSeconds, nameof(boostChargeSeconds));
        BoostDurationSeconds = OosEngineModeMath.RequireFinite(boostDurationSeconds, nameof(boostDurationSeconds));
        BoostRechargeDurationSeconds = OosEngineModeMath.RequireFinite(
            boostRechargeDurationSeconds,
            nameof(boostRechargeDurationSeconds));
        BoostReleaseSeconds = OosEngineModeMath.RequireFinite(boostReleaseSeconds, nameof(boostReleaseSeconds));
        TravelThrust = OosEngineModeMath.RequireFinite(travelThrust, nameof(travelThrust));
        TravelChargeSeconds = OosEngineModeMath.RequireFinite(travelChargeSeconds, nameof(travelChargeSeconds));
        TravelAttackSeconds = OosEngineModeMath.RequireFinite(travelAttackSeconds, nameof(travelAttackSeconds));
        TravelReleaseSeconds = OosEngineModeMath.RequireFinite(travelReleaseSeconds, nameof(travelReleaseSeconds));
        TravelStartThrust = OosEngineModeMath.RequireFinite(travelStartThrust, nameof(travelStartThrust));
    }
}

/// <summary>单个引擎组件的可序列化 boost 子状态；null 字段表示 save 中缺少该属性。</summary>
public sealed record OosEngineBoostSaveState(
    double? StartSeconds = null,
    double? RechargeSeconds = null,
    float? ThrustFactor = null);

/// <summary>单个引擎组件的可序列化 travel 子状态；null 字段表示 save 中缺少该属性。</summary>
public sealed record OosEngineTravelSaveState(
    float? Factor = null,
    bool? Active = null);

/// <summary>
/// 从提取器 saveComponentState 转成的强类型引擎记录。
/// ComponentClass 必须由调用者从保存节点提供，避免把舰船层级 boost 当成引擎状态。
/// </summary>
public sealed record OosEngineComponentSaveState(
    string ComponentClass,
    OosEngineBoostSaveState? Boost = null,
    OosEngineTravelSaveState? Travel = null);

/// <summary>已经确认属于一个引擎组件的 boost/travel 保存态。</summary>
public sealed record OosEngineModeSaveState(
    OosEngineBoostSaveState? Boost = null,
    OosEngineTravelSaveState? Travel = null);

/// <summary>由导航包装器在本次引擎更新明确提供的动态控制条件。</summary>
public sealed record OosEngineModeUpdateInputs(
    float? TravelControl,
    float? BoostControl,
    bool? BoostReleaseEnabled);

/// <summary>两个引擎本地条件之外的 boost 来源谓词；null 表示来源尚未闭合。</summary>
public sealed record OosBoostEligibilitySource(
    bool? EngineChainAvailable,
    bool? Class49ComponentPresent,
    bool? Class49TimeBlocked);

public enum OosBoostEligibilityStatus
{
    Eligible,
    Ineligible,
    Unknown
}

public sealed record OosBoostEligibilityDecision(
    OosBoostEligibilityStatus Status,
    IReadOnlyList<string> Reasons);

/// <summary>提供给已配置物理解析器的引擎模式快照。</summary>
public readonly record struct OosEngineModeSnapshot(
    bool BoostActive,
    bool TravelActive,
    float BoostThrustFactor,
    float TravelFactor);

public enum OosTravelStartRequestAction
{
    IgnoredActiveOrPending,
    StartNow,
    Charge
}

public readonly record struct OosTravelStartRequestResult(
    OosTravelStartRequestAction Action,
    float? DelaySeconds,
    double? Deadline);

/// <summary>已闭合的普通 travel 启动充能算术。</summary>
public static class OosEngineModeMath
{
    public const float FloatEpsilon = 1e-4f;

    /// <summary>6D9DE0..6D9DF7 对应运算：本地前向速度 / 普通速度上限。</summary>
    public static float CalculateTravelStartRatio(float forwardSpeed, float normalSpeedCap)
    {
        RequireFinite(forwardSpeed, nameof(forwardSpeed));
        RequireFinite(normalSpeedCap, nameof(normalSpeedCap));
        return MathF.Abs(normalSpeedCap) >= FloatEpsilon
            ? forwardSpeed / normalSpeedCap
            : 0f;
    }

    /// <summary>
    /// 6D8BF0 在 travel 请求实际交付时计算充能延迟。
    /// 本方法不判定启动资格，也不处理 save 中已经存在期限的事件。
    /// </summary>
    public static float CalculateTravelChargeDelay(
        float forwardSpeed,
        float normalSpeedCap,
        float travelThrust,
        float effectiveChargeSeconds)
    {
        RequireFinite(forwardSpeed, nameof(forwardSpeed));
        RequireFinite(normalSpeedCap, nameof(normalSpeedCap));
        RequireFinite(travelThrust, nameof(travelThrust));
        RequireFinite(effectiveChargeSeconds, nameof(effectiveChargeSeconds));
        if (normalSpeedCap <= 0 ||
            travelThrust < 1 ||
            effectiveChargeSeconds < 0 ||
            effectiveChargeSeconds >= 2_147_483_648f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(effectiveChargeSeconds),
                "Charge formula requires a positive normal cap, travel thrust >= 1, and bounded nonnegative charge.");
        }

        if (forwardSpeed <= normalSpeedCap)
            return effectiveChargeSeconds;

        var maximum = normalSpeedCap * travelThrust;
        if (!float.IsFinite(maximum))
            throw new ArgumentOutOfRangeException(nameof(travelThrust), "Travel cap overflows float32.");
        if (forwardSpeed >= maximum)
            return 0f;

        var denominator = maximum - normalSpeedCap;
        float mapped;
        if (MathF.Abs(denominator) < FloatEpsilon)
        {
            mapped = effectiveChargeSeconds * 0.5f;
        }
        else
        {
            mapped = ((forwardSpeed - normalSpeedCap) * -effectiveChargeSeconds) /
                denominator + effectiveChargeSeconds;
        }

        var rounded = (float)Math.Ceiling(mapped);
        return MathF.Max(0f, MathF.Min(effectiveChargeSeconds, rounded));
    }

    internal static float RequireFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName);
        return value;
    }

    internal static double RequireFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName);
        return value;
    }

    internal static float ToFiniteSingle(double value, string parameterName)
    {
        var result = (float)value;
        if (!float.IsFinite(result))
            throw new ArgumentOutOfRangeException(parameterName, "Value cannot be represented as finite float32.");
        return result;
    }
}

/// <summary>
/// 一个普通舰船尚在源时间树中的待处理 travel-start 事件。
/// 此状态独立于导航控制器缓存的 TravelPending 位。
/// </summary>
public sealed class OosTravelStartQueue
{
    public double? Deadline { get; private set; }

    public OosTravelStartQueue(double? deadline = null)
    {
        if (deadline.HasValue)
            OosEngineModeMath.RequireFinite(deadline.Value, nameof(deadline));
        Deadline = deadline;
    }

    public OosTravelStartRequestResult Request(
        double requestDeliveryTime,
        bool travelActive,
        float forwardSpeed,
        float normalSpeedCap,
        float travelThrust,
        float effectiveChargeSeconds)
    {
        OosEngineModeMath.RequireFinite(requestDeliveryTime, nameof(requestDeliveryTime));
        if (travelActive || Deadline.HasValue)
        {
            return new(
                OosTravelStartRequestAction.IgnoredActiveOrPending,
                DelaySeconds: null,
                Deadline);
        }

        var delay = OosEngineModeMath.CalculateTravelChargeDelay(
            forwardSpeed,
            normalSpeedCap,
            travelThrust,
            effectiveChargeSeconds);
        if (MathF.Abs(delay) < OosEngineModeMath.FloatEpsilon)
            return new(OosTravelStartRequestAction.StartNow, delay, Deadline: null);

        var deadline = requestDeliveryTime + delay;
        OosEngineModeMath.RequireFinite(deadline, nameof(requestDeliveryTime));
        Deadline = deadline;
        return new(OosTravelStartRequestAction.Charge, delay, deadline);
    }

    /// <summary>由调用者选择消费者 tick；返回原计划时刻并移除已到期事件。</summary>
    public double? ConsumeDue(double consumerTime)
    {
        OosEngineModeMath.RequireFinite(consumerTime, nameof(consumerTime));
        if (!Deadline.HasValue || Deadline.Value > consumerTime)
            return null;

        var plannedTime = Deadline.Value;
        Deadline = null;
        return plannedTime;
    }

    public void Cancel() => Deadline = null;
}

/// <summary>
/// X4 9.00 普通引擎的序列化模式状态与仅运行时积分时钟。
/// 外部控制器/来源条件必须由调用者提供；本类型不会猜测未知条件。
/// </summary>
public sealed class OosEngineModeState
{
    public const double SerializedTimeSentinel = -1d;
    public const double NativeSentinelLimit = -0.9999d;

    public OosEngineModeProperties Properties { get; }
    public double BoostStartSeconds { get; private set; }
    public double BoostRechargeSeconds { get; private set; }
    public double? BoostLastUpdateSeconds { get; private set; }
    public float BoostThrustFactor { get; private set; }
    public float TravelFactor { get; private set; }
    public bool TravelActive { get; private set; }
    public double? TravelLastUpdateSeconds { get; private set; }

    private OosEngineModeState(
        OosEngineModeProperties properties,
        OosEngineModeSaveState? saveState)
    {
        ArgumentNullException.ThrowIfNull(properties);
        Properties = properties;
        var boost = saveState?.Boost;
        var travel = saveState?.Travel;
        BoostStartSeconds = OosEngineModeMath.RequireFinite(
            boost?.StartSeconds ?? SerializedTimeSentinel,
            nameof(boost.StartSeconds));
        BoostRechargeSeconds = OosEngineModeMath.RequireFinite(
            boost?.RechargeSeconds ?? SerializedTimeSentinel,
            nameof(boost.RechargeSeconds));
        BoostThrustFactor = OosEngineModeMath.RequireFinite(
            boost?.ThrustFactor ?? 1f,
            nameof(boost.ThrustFactor));
        TravelFactor = OosEngineModeMath.RequireFinite(
            travel?.Factor ?? 1f,
            nameof(travel.Factor));
        TravelActive = travel?.Active ?? false;

        // +0x2F0/+0x338 仅存在于运行时；存档还原绝不凭空补造它们。
        BoostLastUpdateSeconds = null;
        TravelLastUpdateSeconds = null;
    }

    public static OosEngineModeState FromSave(
        OosEngineModeProperties properties,
        OosEngineModeSaveState? saveState = null) =>
        new(properties, saveState);

    public static OosEngineModeState FromEngineComponentSave(
        OosEngineModeProperties properties,
        OosEngineComponentSaveState componentState)
    {
        ArgumentNullException.ThrowIfNull(componentState);
        if (!string.Equals(componentState.ComponentClass, "engine", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Expected an extracted engine saveComponentState.",
                nameof(componentState));
        }

        return FromSave(
            properties,
            new OosEngineModeSaveState(componentState.Boost, componentState.Travel));
    }

    public bool IsBoostCharging(double now)
    {
        OosEngineModeMath.RequireFinite(now, nameof(now));
        return BoostStartSeconds > NativeSentinelLimit &&
               now < BoostStartSeconds + Properties.BoostChargeSeconds;
    }

    public bool IsBoostActive(double now)
    {
        OosEngineModeMath.RequireFinite(now, nameof(now));
        if (BoostStartSeconds <= NativeSentinelLimit)
            return false;

        var activeStart = BoostStartSeconds + Properties.BoostChargeSeconds;
        var activeEnd = activeStart + Properties.BoostDurationSeconds;
        return activeStart <= now && now < activeEnd;
    }

    public OosBoostEligibilityDecision EvaluateBoostEligibility(
        double now,
        OosBoostEligibilitySource source)
    {
        OosEngineModeMath.RequireFinite(now, nameof(now));
        ArgumentNullException.ThrowIfNull(source);
        var rejected = new List<string>();
        var unknown = new List<string>();

        if (MathF.Abs(Properties.BoostThrust - 1f) < OosEngineModeMath.FloatEpsilon)
            rejected.Add("boost-thrust-is-one");
        if (source.EngineChainAvailable is false)
            rejected.Add("engine-chain-unavailable-0x51E8B0");
        else if (!source.EngineChainAvailable.HasValue)
            unknown.Add("engine-chain-availability-unresolved-0x51E8B0");
        if (IsBoostCharging(now))
            rejected.Add("boost-charging-0x54CD70");
        if (IsBoostActive(now))
            rejected.Add("boost-active-0x54CE90");
        if (TravelActive)
            rejected.Add("travel-active");

        if (source.Class49ComponentPresent is false)
        {
            rejected.Add("class49-component-missing");
        }
        else if (!source.Class49ComponentPresent.HasValue)
        {
            unknown.Add("class49-component-presence-unresolved");
        }
        else if (source.Class49TimeBlocked is true)
        {
            rejected.Add("class49-vfunc2010-blocked");
        }
        else if (!source.Class49TimeBlocked.HasValue)
        {
            unknown.Add("class49-vfunc2010-unresolved");
        }

        if (rejected.Count > 0)
            return new(OosBoostEligibilityStatus.Ineligible, rejected.Concat(unknown).ToArray());
        if (unknown.Count > 0)
            return new(OosBoostEligibilityStatus.Unknown, unknown);
        return new(OosBoostEligibilityStatus.Eligible, Array.Empty<string>());
    }

    /// <summary>通过 0x54CBD0 关卡后执行 0x6D85E4 boost-start 写入。</summary>
    public OosBoostEligibilityDecision RequestBoost(
        double now,
        OosBoostEligibilitySource source)
    {
        var decision = EvaluateBoostEligibility(now, source);
        if (decision.Status == OosBoostEligibilityStatus.Eligible)
            BoostStartSeconds = now;
        return decision;
    }

    /// <summary>0x54CC80/0x54C680：完成 recharge clock 并清除 boost start。</summary>
    public void StopBoost(double now)
    {
        OosEngineModeMath.RequireFinite(now, nameof(now));
        var start = BoostStartSeconds;
        if (start > NativeSentinelLimit)
        {
            var activeStart = start + Properties.BoostChargeSeconds;
            if (now >= activeStart)
            {
                var rechargeClock = now;
                var rechargeDuration = Properties.BoostRechargeDurationSeconds;
                var release = Properties.BoostReleaseSeconds;
                if (MathF.Abs(rechargeDuration) >= OosEngineModeMath.FloatEpsilon &&
                    MathF.Abs(release) >= OosEngineModeMath.FloatEpsilon)
                {
                    var fraction = (float)((now - activeStart) / rechargeDuration);
                    fraction = MathF.Max(0f, MathF.Min(1f, fraction));
                    if (MathF.Abs(fraction - 1f) >= OosEngineModeMath.FloatEpsilon)
                    {
                        var remaining = (1f - fraction) * release;
                        rechargeClock = now - remaining;
                    }
                }

                BoostRechargeSeconds = rechargeClock;
            }
        }

        BoostStartSeconds = SerializedTimeSentinel;
    }

    public bool StartTravel(float startSpeedRatio)
    {
        OosEngineModeMath.RequireFinite(startSpeedRatio, nameof(startSpeedRatio));
        if (TravelActive)
            return false;

        BoostStartSeconds = SerializedTimeSentinel;
        BoostRechargeSeconds = SerializedTimeSentinel;
        BoostThrustFactor = 1f;
        TravelActive = true;
        TravelFactor = MathF.Max(
            Properties.TravelStartThrust,
            MathF.Min(startSpeedRatio, Properties.TravelThrust));
        return true;
    }

    /// <summary>
    /// 同步清除 travel active。0x54DDFF 的引擎宏分派不被当前研究运行时消费，
    /// 因此保持在编排边界之外；它不是物理刷新队列。
    /// </summary>
    public bool StopTravel()
    {
        if (!TravelActive)
            return false;
        TravelActive = false;
        return true;
    }

    /// <summary>按 0x6DC8CF/0x6DC8DE 调用顺序推进 travel 与 boost 因子。</summary>
    public float Update(double now, OosEngineModeUpdateInputs inputs)
    {
        OosEngineModeMath.RequireFinite(now, nameof(now));
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.TravelControl.HasValue)
            OosEngineModeMath.RequireFinite(inputs.TravelControl.Value, nameof(inputs.TravelControl));
        if (inputs.BoostControl.HasValue)
            OosEngineModeMath.RequireFinite(inputs.BoostControl.Value, nameof(inputs.BoostControl));

        ValidateUpdateSources(now, inputs);
        UpdateTravelFactor(now, inputs.TravelControl);
        UpdateBoostFactor(now, inputs.BoostControl, inputs.BoostReleaseEnabled);
        return TravelFactor;
    }

    public OosEngineModeSnapshot GetSnapshot(double now) =>
        new(IsBoostActive(now), TravelActive, BoostThrustFactor, TravelFactor);

    /// <summary>已配置 proximity resolver 实际消费的 boost 释放剩余量。</summary>
    public double CalculateBoostReleaseRemaining(double now)
    {
        OosEngineModeMath.RequireFinite(now, nameof(now));
        if (IsBoostActive(now))
            return Properties.BoostReleaseSeconds;
        if (BoostRechargeSeconds > NativeSentinelLimit)
            return Math.Max(0d, Properties.BoostReleaseSeconds - (now - BoostRechargeSeconds));
        return 0d;
    }

    private void ValidateUpdateSources(double now, OosEngineModeUpdateInputs inputs)
    {
        var travelIntegrates = TravelLastUpdateSeconds is > NativeSentinelLimit &&
                               now > TravelLastUpdateSeconds.Value;
        if (travelIntegrates && TravelActive && !inputs.TravelControl.HasValue)
        {
            throw new InvalidOperationException(
                "Active travel update requires current controller TravelControl.");
        }

        var boostIntegrates = BoostLastUpdateSeconds is > NativeSentinelLimit &&
                              now > BoostLastUpdateSeconds.Value;
        if (!boostIntegrates)
            return;
        if (IsBoostActive(now) && !inputs.BoostControl.HasValue)
        {
            throw new InvalidOperationException(
                "Active boost update requires current caller BoostControl.");
        }
        if (!IsBoostActive(now) &&
            MathF.Abs(BoostThrustFactor - 1f) >= OosEngineModeMath.FloatEpsilon &&
            MathF.Abs(Properties.BoostThrust - 1f) >= OosEngineModeMath.FloatEpsilon &&
            MathF.Abs(Properties.BoostReleaseSeconds) >= OosEngineModeMath.FloatEpsilon &&
            !inputs.BoostReleaseEnabled.HasValue)
        {
            throw new InvalidOperationException(
                "Inactive boost-factor release requires current caller BoostReleaseEnabled.");
        }
    }

    private void UpdateTravelFactor(double now, float? travelControl)
    {
        var last = TravelLastUpdateSeconds;
        if (!last.HasValue || last.Value <= NativeSentinelLimit)
        {
            TravelLastUpdateSeconds = now;
            return;
        }
        if (now <= last.Value)
        {
            TravelLastUpdateSeconds = now;
            return;
        }

        var dt = OosEngineModeMath.ToFiniteSingle(now - last.Value, nameof(now));
        var thrust = Properties.TravelThrust;
        float target;
        float seconds;
        if (TravelActive)
        {
            target = MathF.Max((travelControl!.Value - 1f) * thrust, 1f);
            seconds = Properties.TravelAttackSeconds;
        }
        else
        {
            target = 1f;
            seconds = Properties.TravelReleaseSeconds;
        }

        var rate = MathF.Abs(seconds) < OosEngineModeMath.FloatEpsilon
            ? 0f
            : (thrust - 1f) / seconds;
        TravelFactor = MoveFactor(TravelFactor, target, dt, rate);
        TravelLastUpdateSeconds = now;
    }

    private void UpdateBoostFactor(
        double now,
        float? boostControl,
        bool? boostReleaseEnabled)
    {
        var last = BoostLastUpdateSeconds;
        if (!last.HasValue || last.Value <= NativeSentinelLimit)
        {
            BoostLastUpdateSeconds = now;
            return;
        }
        if (now <= last.Value)
        {
            BoostLastUpdateSeconds = now;
            return;
        }

        var dt = OosEngineModeMath.ToFiniteSingle(now - last.Value, nameof(now));
        var thrust = Properties.BoostThrust;
        float target;
        float seconds;
        if (IsBoostActive(now))
        {
            target = MathF.Max(thrust * boostControl!.Value, 1f);
            seconds = Properties.BoostRechargeDurationSeconds;
        }
        else
        {
            target = 1f;
            seconds = boostReleaseEnabled is true ? Properties.BoostReleaseSeconds : 0f;
        }

        var rate = MathF.Abs(seconds) < OosEngineModeMath.FloatEpsilon
            ? 0f
            : (thrust - 1f) / seconds;
        BoostThrustFactor = MoveFactor(BoostThrustFactor, target, dt, rate);
        BoostLastUpdateSeconds = now;
    }

    private static float MoveFactor(float current, float target, float dt, float rate)
    {
        if (MathF.Abs(rate) < OosEngineModeMath.FloatEpsilon)
            return target;
        var step = dt * rate;
        if (current < target)
            return MathF.Min(current + step, target);
        if (current > target)
            return MathF.Max(current - step, target);
        return target;
    }
}
