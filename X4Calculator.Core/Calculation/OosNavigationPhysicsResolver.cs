using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

public readonly record struct OosSpeedCurvePoint(float Position, float Value);
public sealed record OosNavigationBasePhysics(float ForwardAcceleration, float ReverseAcceleration,
    float ForwardSpeed, float ReverseSpeed, Vec3 AngularRateRadians);
public sealed record OosNavigationEnginePhysics(float BoostThrust, float TravelThrust,
    float BoostAcceleration, float BoostCoast, float TravelAttack,
    IReadOnlyList<OosSpeedCurvePoint> DecelerationCurve);
public readonly record struct OosNavigationModeSample(bool BoostActive, bool TravelActive,
    float BoostThrustFactor, float TravelFactor, float SpeedNorm, float ForwardSpeed);
public sealed record OosResolvedNavigationPhysics(float Acceleration, float Deceleration,
    float SpeedCap, Vec3 TurnRatesRadians, float MassFactor1, float MassFactor2,
    float SpeedCoordinate, float DecelerationCurveValue, float? ReverseSignedCap = null);

/// <summary>
/// 普通、无改装 JPM Navigation 的物理参数及刹车距离源公式。
/// 只在调用者指定的缓存刷新时点求值，不负责刷新、引擎模式或事件调度。
/// </summary>
public static class OosNavigationPhysicsResolver
{
    private const float Epsilon = 0.0001f;

    public static float SpeedCoordinate(float speed, float normalSpeed, float boostSpeed, float travelSpeed)
    {
        Finite(speed, normalSpeed, boostSpeed, travelSpeed);
        var lo = MathF.Min(boostSpeed, travelSpeed);
        var hi = MathF.Max(boostSpeed, travelSpeed);
        if (MathF.Abs(normalSpeed - lo) < Epsilon)
        {
            if (MathF.Abs(lo - hi) < Epsilon)
            {
                lo = normalSpeed * 1.25f;
                hi = normalSpeed * 1.5f;
            }
            else lo = (hi + normalSpeed) * .5f;
        }
        else if (lo > hi * .85f)
            lo = MathF.Max((hi - normalSpeed) * .5f + normalSpeed, hi * .85f);
        if (speed < normalSpeed && MathF.Abs(normalSpeed) >= Epsilon) return speed / normalSpeed;
        if (speed < lo && MathF.Abs(lo - normalSpeed) >= Epsilon) return (speed - normalSpeed) / (lo - normalSpeed) + 1f;
        if (speed < hi && MathF.Abs(hi - lo) >= Epsilon) return (speed - lo) / (hi - lo) + 2f;
        return 3f;
    }

    public static float StepCurve(IReadOnlyList<OosSpeedCurvePoint> points, float coordinate)
    {
        ArgumentNullException.ThrowIfNull(points);
        Finite(coordinate);
        if (points.Count == 0) return 1f;
        foreach (var point in points)
        {
            Finite(point.Position, point.Value);
            if (coordinate < point.Position) return point.Value;
        }
        return points[^1].Value;
    }

    public static OosResolvedNavigationPhysics ResolveJpm(OosNavigationBasePhysics basis,
        float shipMass, float cargoMass, float maxAdditionalMassRatio,
        OosNavigationEnginePhysics engine, OosNavigationModeSample state,
        bool ordinaryUnmodifiedJpmNavigation)
    {
        ArgumentNullException.ThrowIfNull(basis);
        ArgumentNullException.ThrowIfNull(engine);
        if (!ordinaryUnmodifiedJpmNavigation)
            throw new NotSupportedException("Ordinary unmodified JPM Navigation is required.");
        Finite(shipMass, cargoMass, maxAdditionalMassRatio, basis.ForwardAcceleration, basis.ReverseAcceleration,
            basis.ForwardSpeed, basis.ReverseSpeed, engine.BoostThrust, engine.TravelThrust,
            engine.BoostAcceleration, engine.BoostCoast, engine.TravelAttack,
            state.BoostThrustFactor, state.TravelFactor, state.SpeedNorm, state.ForwardSpeed);
        if (shipMass <= 0 || cargoMass < 0 || maxAdditionalMassRatio < 0)
            throw new ArgumentOutOfRangeException(nameof(shipMass));
        var cargo = MathF.Min(shipMass * maxAdditionalMassRatio, cargoMass);
        var first = shipMass / (shipMass + cargo);
        var second = cargo >= Epsilon ? MathF.Ceiling(1f / (1f + cargo / shipMass) * 10f) * .1f : 1f;
        var baseA = basis.ForwardAcceleration * first;
        var baseB = basis.ReverseAcceleration * first;
        var a = baseA;
        var b = baseB;
        var normal = basis.ForwardSpeed;
        var cap = normal;
        var coordinate = SpeedCoordinate(state.SpeedNorm, normal, normal * engine.BoostThrust, normal * engine.TravelThrust);
        var curve = StepCurve(engine.DecelerationCurve, coordinate);
        if (state.BoostActive)
        {
            cap = MathF.Max(cap, normal * engine.BoostThrust);
            a = MathF.Max(a, baseA * engine.BoostAcceleration);
        }
        if (state.TravelActive)
        {
            cap = MathF.Max(cap, normal * engine.TravelThrust);
            a = normal * engine.TravelThrust / MathF.Max(1f, engine.TravelAttack);
        }
        if (!state.BoostActive && MathF.Abs(state.BoostThrustFactor - 1f) >= Epsilon)
        {
            a *= engine.BoostCoast;
            b *= engine.BoostCoast;
        }
        else if (state.TravelActive)
        {
            if (state.ForwardSpeed >= 0) b = MathF.Max(b, baseA * curve);
            else a = MathF.Max(a, baseA * curve);
        }
        else if (!state.BoostActive)
        {
            a *= curve;
            b *= curve;
        }
        if (!state.TravelActive) a *= second;
        b *= second;
        var rates = basis.AngularRateRadians;
        return new(a, b, cap, new Vec3((float)rates.X, (float)rates.Y, (float)rates.Z),
            first, second, coordinate, curve, -basis.ReverseSpeed);
    }

    /// <summary>6DE670 的半秒刹车积分；负前向速度归一化后复用同一循环。</summary>
    public static float ResolveProximity(OosResolvedNavigationPhysics parameters, float normalSpeed,
        OosNavigationEnginePhysics engine, OosNavigationModeSample state,
        float jerkForwardDecel, float boostReleaseRemaining, float stopSpeed = 0, bool isPlayer = false)
    {
        Finite(normalSpeed, jerkForwardDecel, boostReleaseRemaining, stopSpeed, state.ForwardSpeed,
            state.BoostThrustFactor, state.TravelFactor, parameters.Deceleration, parameters.SpeedCap);
        var velocity = state.ForwardSpeed;
        if (MathF.Abs(velocity) < Epsilon) return 0;
        var deceleration = parameters.Deceleration;
        var cap = parameters.SpeedCap;
        if (velocity < 0)
        {
            if (!parameters.ReverseSignedCap.HasValue)
                throw new NotSupportedException("Negative forward speed needs the signed reverse-cap branch.");
            var reverseSignedCap = parameters.ReverseSignedCap.Value;
            Finite(reverseSignedCap);
            Finite(parameters.Acceleration);
            if (reverseSignedCap > 0)
                throw new ArgumentOutOfRangeException(nameof(parameters), "Reverse signed cap must be nonpositive.");
            velocity = -velocity;
            deceleration = parameters.Acceleration;
            cap = -reverseSignedCap;
        }
        if (deceleration < 0) return 0;
        var boostCap = normalSpeed * engine.BoostThrust;
        var travelCap = normalSpeed * engine.TravelThrust;
        float Coordinate(float speed) => SpeedCoordinate(speed, normalSpeed, boostCap, travelCap);
        float Curve(float speed) => StepCurve(engine.DecelerationCurve, Coordinate(speed));
        var coasting = !state.BoostActive && MathF.Abs(state.BoostThrustFactor - 1f) >= Epsilon;
        var strip = coasting ? engine.BoostCoast : state.BoostActive ? 1f : Curve(velocity);
        var basis = MathF.Abs(strip) >= Epsilon ? deceleration / strip : deceleration;
        var factor = MathF.Max(MathF.Max(state.BoostThrustFactor, state.TravelFactor), 1f);
        var effectiveCap = MathF.Abs(factor - 1f) >= Epsilon ? cap / factor : cap;
        var distance = 0f;
        if (MathF.Abs(factor - 1f) >= Epsilon && !isPlayer)
            distance = (effectiveCap + velocity) * boostReleaseRemaining * .5f;
        var jerk = MathF.Max(.5f, jerkForwardDecel);
        for (var index = 0; index < 200_000; index++)
        {
            if (velocity <= stopSpeed) return distance;
            distance += (velocity - stopSpeed) * .5f;
            var strength = basis;
            if (MathF.Abs(cap) >= Epsilon)
            {
                var coordinate = Coordinate(velocity);
                strength = Curve(velocity) * basis;
                if (coordinate > 1f)
                {
                    var weight = velocity >= MathF.Abs(cap) ? jerk : (jerk - 1f) * velocity / MathF.Abs(cap) + 1f;
                    strength *= weight;
                }
            }
            var delta = MathF.Min(2f * velocity, strength);
            if (MathF.Abs(delta) < Epsilon) return distance;
            velocity += delta * -.5f;
        }
        throw new InvalidOperationException("Source braking integral did not terminate.");
    }

    private static void Finite(params float[] values)
    {
        if (values.Any(value => !float.IsFinite(value)))
            throw new ArgumentOutOfRangeException(nameof(values), "Finite source inputs required.");
    }
}
