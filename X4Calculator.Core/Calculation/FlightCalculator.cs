using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

/// <summary>
/// 舰船飞行属性计算器。
/// 公式均已在游戏内实测验证（2026-08-04），关键规则：
/// - 引擎数量倍率来自舰船组件文件的 con_engine_N 连接点数，每条船不同；
/// - 推进器为单一逻辑单元，三轴转向不按数量倍乘；
/// - 常规加速度按未取整的实际值计算（游戏内仅显示时取整）。
/// </summary>
public static class FlightCalculator
{
    /// <summary>
    /// 计算舰船在指定推进器和引擎下的飞行属性。
    /// </summary>
    /// <param name="ship">舰船（含物理参数与引擎数量）。</param>
    /// <param name="engine">引擎。</param>
    /// <param name="thruster">推进器。</param>
    /// <param name="pilotingStars">AI 驾驶员技能星级（0~5，默认 5）。</param>
    public static FlightStats Calculate(ShipInfo ship, EngineInfo engine, ThrusterInfo thruster, int pilotingStars = 5)
    {
        // 前向总推力 = 引擎数 × 单引擎前向推力
        double forwardThrust = ship.EngineCount * engine.ThrustForward;
        var (steeringSpeedFactor, steeringAccelerationFactor) = GetSteeringSkillFactors(pilotingStars);

        return new FlightStats
        {
            // 常规速度 = 总前向推力 / 前向阻力
            ForwardSpeed = forwardThrust / ship.DragForward,

            // 常规加速度 = 总前向推力 × 前向加速度系数 / 质量（不取整，游戏实际按此值运行）
            Acceleration = forwardThrust * ship.AccFactorForward / ship.Mass,

            // 反向制动减速度 = 引擎数 × 反向推力 × 反向加速度系数 / 质量
            // （accfactors 无 reverse 属性 → 缺省 1.0；进港刹停/离港倒车用，与常规加速度同构）
            ReverseAcceleration = ship.EngineCount * engine.ThrustReverse * ship.AccFactorReverse / ship.Mass,

            // 反向最大速度 = 引擎数 × 反向推力 / 后向阻力（离港倒车用）
            ReverseSpeed = ship.EngineCount * engine.ThrustReverse / ship.DragReverse,

            // 三轴最高转向速度 = 推进器对应推力 / 对应阻力 × AI 驾驶员技能系数（不倍乘）
            PitchRate = thruster.Pitch / ship.DragPitch * steeringSpeedFactor,
            YawRate = thruster.Yaw / ship.DragYaw * steeringSpeedFactor,
            RollRate = thruster.Roll / ship.DragRoll * steeringSpeedFactor,

            // 三轴角加速度候选模型 = 推进器对应推力 / 对应轴惯量 × AI 驾驶员技能系数。
            // 不包含尚未经实测验证的 jerk 与 steeringcurve。
            PitchAngularAcceleration = ship.InertiaPitch > 0 ? thruster.Pitch / ship.InertiaPitch * steeringAccelerationFactor : 0,
            YawAngularAcceleration = ship.InertiaYaw > 0 ? thruster.Yaw / ship.InertiaYaw * steeringAccelerationFactor : 0,
            RollAngularAcceleration = ship.InertiaRoll > 0 ? thruster.Roll / ship.InertiaRoll * steeringAccelerationFactor : 0,

            // 巡航速度 = 常规速度 × 引擎巡航推力倍率
            TravelSpeed = forwardThrust * engine.TravelThrust / ship.DragForward,

            // 巡航引擎启动时间（引擎写死）
            TravelCharge = engine.TravelCharge,

            // 巡航加速时间（引擎写死）
            TravelAttack = engine.TravelAttack,

            // 巡航减速时间（引擎写死）
            TravelRelease = engine.TravelRelease,
        };
    }

    private static (double SteeringSpeed, double SteeringAcceleration) GetSteeringSkillFactors(int pilotingStars)
        => Math.Clamp(pilotingStars, 0, 5) switch
        {
            0 => (0.50, 0.50),
            1 => (0.50, 0.70),
            2 => (0.75, 0.75),
            3 => (0.85, 0.85),
            4 => (0.90, 0.90),
            _ => (1.00, 1.00),
        };
}
