namespace X4Calculator.Core.Calculation;

public enum OosTransportRefreshRegime { Periodic, Continuous }

/// <summary>单次条件模拟使用的调度输入；不同刷新情形不在此混合加权。</summary>
public sealed record OosTransportDynamicsScenario(
    OosTransportRefreshRegime Refresh,
    ulong? RefreshRandomState,
    float RefreshUnitScenario,
    OosEngineModeUpdateInputs Controls,
    OosBoostEligibilitySource BoostEligibility);

/// <summary>
/// 同一单程共享的引擎、请求队列、Navigation cache 和事件时钟。
/// 起点是显式声明的停泊静止、引擎未激活状态，不恢复存档中不可见的缓存。
/// </summary>
public sealed class OosTransportDynamics
{
    private readonly OosTransportShipProfile _profile;
    private readonly OosTransportDynamicsScenario _scenario;
    private ulong? _randomState;
    public OosEngineModeState Engine { get; }
    public OosTravelStartQueue TravelQueue { get; } = new();
    public OosNavigationModeRequestGate Gate { get; }
    public OosPhysicsRefreshClock Clock { get; }
    public OosLinearFlightParameters Parameters { get; private set; }
    public int RefreshCount { get; private set; }

    public OosTransportDynamics(OosTransportShipProfile profile, OosTransportDynamicsScenario scenario, double launchTime)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(scenario);
        if (!profile.Jpm.Allow) throw new NotSupportedException("JPM-enabled ordinary configuration required.");
        if (!Enum.IsDefined(scenario.Refresh)) throw new ArgumentOutOfRangeException(nameof(scenario));
        _ = OosPhysicsRefreshClock.DelayFromUnitScenario(scenario.RefreshUnitScenario);
        _profile = profile; _scenario = scenario; _randomState = scenario.RefreshRandomState;
        Engine = OosEngineModeState.FromSave(profile.Engine.ModeProperties);
        Clock = new(launchTime - 1);
        var angles = profile.AiFlight;
        Gate = new(Radians(angles.StartBoostDegrees), Radians(angles.StopBoostDegrees),
            Radians(angles.StartTravelDegrees), Radians(angles.StopTravelDegrees));
        var b = profile.BasePhysics;
        var rates = new OosLinearAngularRates((float)b.AngularRateRadians.X, (float)b.AngularRateRadians.Y,
            (float)b.AngularRateRadians.Z);
        if (rates.Yaw <= 0 || rates.Pitch <= 0 || rates.Roll <= 0)
            throw new NotSupportedException("Positive configured angular rates required.");
        var curvature = MathF.Min(angles.SplineMaximumTurnRadius,
            angles.SplineTurnRadius * b.ForwardSpeed / rates.Yaw);
        Parameters = new(b.ForwardAcceleration, b.ReverseAcceleration, b.ForwardSpeed,
            rates, rates, 0, curvature);
    }

    public OosLinearFlightController CreateStationaryFlight(OosLinearPose pose, OosLinearPose target, double now)
    {
        var flight = new OosLinearFlightController(pose, OosLinearVector4.Zero, target, Parameters, now);
        // 在首次运动前完成解析，不推进曲线或请求时钟。
        flight.Replan(target, Resolve(flight));
        return flight;
    }

    public OosLinearFlightParameters Resolve(OosLinearFlightController flight)
    {
        var now = flight.Now;
        if (TravelQueue.ConsumeDue(now).HasValue) StartTravel(flight);
        Engine.Update(now, _scenario.Controls);
        if (_scenario.Refresh != OosTransportRefreshRegime.Continuous && !Clock.IsDue(now)) return Parameters;
        RefreshParameters(flight);
        if (_scenario.Refresh == OosTransportRefreshRegime.Periodic)
        {
            if (_randomState is ulong seed) _randomState = Clock.ScheduleAfterDelivery(now, seed).NextRandomState;
            else Clock.ScheduleAfterDelivery(now, _scenario.RefreshUnitScenario);
        }
        return Parameters;
    }

    /// <summary>
    /// 普通 parent-change 的直接参数刷新。调用方提供刷新前缓存对应的控制量，并先应用实际监听器效果。
    /// 不交付 travel-start、不取消模式、不消费或重排周期事件；不代表完整过门事件处理。
    /// </summary>
    public OosLinearFlightParameters ForceParameterRefresh(
        OosLinearFlightController flight, OosEngineModeUpdateInputs currentControls)
    {
        ArgumentNullException.ThrowIfNull(flight);
        return ForceParameterRefresh(flight, flight.Now, currentControls);
    }

    /// <summary>使用实际事件时刻刷新；不以空 Linear tick 推进事件时间。</summary>
    public OosLinearFlightParameters ForceParameterRefresh(
        OosLinearFlightController flight, double now, OosEngineModeUpdateInputs currentControls)
    {
        ArgumentNullException.ThrowIfNull(flight);
        if (!double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(now));
        Engine.Update(now, currentControls);
        return RefreshParameters(flight, now);
    }

    private OosLinearFlightParameters RefreshParameters(OosLinearFlightController flight) =>
        RefreshParameters(flight, flight.Now);

    private OosLinearFlightParameters RefreshParameters(OosLinearFlightController flight, double now)
    {
        var mode = Engine.GetSnapshot(now);
        var state = new OosNavigationModeSample(mode.BoostActive, mode.TravelActive,
            mode.BoostThrustFactor, mode.TravelFactor, Norm(flight.Velocity), Forward(flight));
        var resolved = OosNavigationPhysicsResolver.ResolveJpm(_profile.BasePhysics, _profile.BaseMass,
            _profile.CargoMass, _profile.MaxAdditionalMassRatio, _profile.EnginePhysics, state, true);
        var proximity = OosNavigationPhysicsResolver.ResolveProximity(resolved, _profile.BasePhysics.ForwardSpeed,
            _profile.EnginePhysics, state, _profile.JerkForwardDecel, (float)Engine.CalculateBoostReleaseRemaining(now));
        var rates = resolved.TurnRatesRadians;
        Parameters = Parameters with { Acceleration = resolved.Acceleration, Deceleration = resolved.Deceleration,
            SpeedCap = resolved.SpeedCap, TurnRates = new((float)rates.X, (float)rates.Y, (float)rates.Z),
            Proximity = proximity };
        Gate.SetCachedModes(mode.BoostActive, mode.TravelActive, TravelQueue.Deadline.HasValue);
        RefreshCount++;
        return Parameters;
    }

    public void Request(OosLinearFlightController flight, OosNavigationModeRequest request) =>
        Request(flight, flight.Now, request);

    /// <summary>只交付调用方明确给出的请求，不从事件到期或路径模式自动推断交付。</summary>
    public void Request(OosLinearFlightController flight, double now, OosNavigationModeRequest request)
    {
        ArgumentNullException.ThrowIfNull(flight);
        if (!double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(now));
        if (request.Mode == OosNavigationMode.Travel)
        {
            if (request.Action == OosNavigationModeRequestAction.Start)
            {
                var engine = _profile.Engine.ModeProperties;
                var result = TravelQueue.Request(now, Engine.TravelActive, Forward(flight),
                    _profile.BasePhysics.ForwardSpeed, engine.TravelThrust, engine.TravelChargeSeconds);
                if (result.Action == OosTravelStartRequestAction.StartNow) StartTravel(flight);
            }
            else { TravelQueue.Cancel(); Engine.StopTravel(); }
        }
        else if (request.Action == OosNavigationModeRequestAction.Start)
        {
            var decision = Engine.RequestBoost(now, _scenario.BoostEligibility);
            if (decision.Status == OosBoostEligibilityStatus.Unknown)
                throw new NotSupportedException("Boost request eligibility is unresolved.");
        }
        else Engine.StopBoost(now);
    }

    /// <summary>调用方已经确定 travel-start 的实际交付相位；未到期或无事件时不更改状态。</summary>
    public bool DeliverTravelStart(OosLinearFlightController flight, double now)
    {
        ArgumentNullException.ThrowIfNull(flight);
        if (!double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(now));
        if (!TravelQueue.ConsumeDue(now).HasValue) return false;
        StartTravel(flight);
        return true;
    }

    public OosLinearFlightStepResult GenericStep(
        OosLinearFlightController flight,
        double dt,
        bool travel,
        bool boost = false)
    {
        var result = flight.Step(dt, Resolve(flight));
        foreach (var request in Gate.Evaluate(flight.Now, flight.OrientationError, boost, travel).Requests)
            Request(flight, request);
        return result;
    }

    private void StartTravel(OosLinearFlightController flight) => Engine.StartTravel(
        OosEngineModeMath.CalculateTravelStartRatio(Forward(flight), _profile.BasePhysics.ForwardSpeed));
    private static float Radians(float degrees) => (float)(degrees * Math.PI / 180d);
    private static float Forward(OosLinearFlightController flight)
    {
        var v = flight.Velocity; var z = flight.Pose.ZAxis;
        return v.Z*z.Z + (v.X*z.X + v.Y*z.Y);
    }
    private static float Norm(OosLinearVector4 v) => (float)Math.Sqrt(v.X*v.X + (v.Y*v.Y + v.Z*v.Z));
}
