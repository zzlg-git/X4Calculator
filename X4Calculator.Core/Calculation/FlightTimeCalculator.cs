namespace X4Calculator.Core.Calculation;

/// <summary>
/// 舰船单段巡航飞行耗时计算（纯静态函数，无状态、无副作用）。
///
/// 与路线/距离管线完全解耦：输入仅为一套飞行属性（<see cref="FlightStats"/>）与一段距离，
/// 输出该段的飞行耗时（秒）。因此可被任意调用方复用——包括后续"把耗时作为图搜索权重
/// 解决相同跳数时路径选择"的场景（见 SectorGraph / RouteDistanceCalculator）。
///
/// 飞行模型（2026-08-12 确认）：
/// - 加速阶段：charge 秒充电（速度不增）后，attack 秒内从 0 线性加速到巡航速度 vt；
/// - 减速阶段：release 秒从 vt 线性减速到 0；
/// - 距离足够时呈"梯形"速度曲线（加速 → 全速巡航 → 减速），距离不足时呈"三角形"曲线
///   （加速到峰值后立即减速，未达全速）；
/// - 唯一不启动巡航的情形：充电期间（charge 秒）常规航行已能到达终点
///   （D ≤ 常规速度 × charge），此时全程常规飞行。
///
/// 过门段（<paramref name="endAtGate"/> = true，即该段以穿越星门/加速器/超级高速结束）的简化：
/// 简化点 1 —— 以"门上 0km"作为巡航终点，忽略实际停靠点（get_safe_pos，门前约 1–3 倍船身
///   长度处）的位置偏差。该偏差 = 停靠距离/巡航速度 ≈ 0.1–2s（L/XL），相对分钟级跨区总时间
///   <2%，可忽略（2026-08-12 用户确认）。
/// 简化点 2 —— 过门段末段不用 release 减速段，改用固定门时间 <see cref="GateCrossingSeconds"/>
///   （6.5s）替代。低注意力下该固定时间已覆盖"接近门减速 + 静止等待 + warp"总开销。
///   超级高速航线（superhighway）同样统一记为 6.5s：其实际穿越接近瞬时（通道内速度极高，
///   观察约 0.1–0.3c，数万 km 通道仅需 2–5s），且入口不减速（move.gate.xml superhighway
///   分支仅 move_to 巡航进入通道，无 stop_moving/wait/warp）；为简化计算与跳门/加速器
///   不做区分。
///
/// 穿越耗时（星门/SH）不计——本计算器只负责"飞行段"耗时，穿越属于瞬时段，且对所有舰船
/// 相同，不参与舰船间对比。
/// </summary>
public static class FlightTimeCalculator
{
    /// <summary>
    /// 过门段固定收尾时间（秒）。
    /// 来源：move.gate.xml 的 &lt;wait min="6s" max="7s"/&gt;（无中断、低注意力下恒定），
    /// 取解包平均等待时间 = (6+7)/2 = 6.5s。
    /// 简化点：此固定时间替代过门段的 release 减速段，覆盖"接近门减速 + 静止等待 + warp"。
    ///
    /// 适用范围（统一 6.5s，不做区分）：
    /// - 星门（jump gate）/ 轨道加速器（accelerator）：warp 瞬移前的固定等待（6–7s）。
    /// - 超级高速航线（superhighway）：实际穿越接近瞬时（通道内速度约 0.1–0.3c，数万 km
    ///   通道仅需 2–5s），且入口不减速（move.gate.xml superhighway 分支仅 move_to 巡航进入
    ///   通道，无 stop_moving/wait/warp）；为简化计算同样记为 6.5s。
    /// </summary>
    public const double GateCrossingSeconds = 6.5;

    /// <summary>离港耗时模型的公共截距（秒，实测 12 艘 L 固体矿船拟合）。</summary>
    private const double UndockInterceptSeconds = 5.527;

    /// <summary>离港耗时模型对“倒退一倍船长耗时”的回归斜率。</summary>
    private const double UndockSlope = 0.674;

    /// <summary>
    /// 计算一段距离为 <paramref name="distanceMeters"/>（米）的巡航飞行耗时（秒）。
    /// 距离 ≤ 0 或速度无意义（巡航速度 ≤ 0 且常规速度 ≤ 0）时返回 0。
    /// </summary>
    /// <param name="endAtGate">该段飞行终点为星门/加速器（需穿越）：末段用固定门时间
    /// 替代 release 减速段。简化点：以门上 0km 为巡航终点，忽略门前停靠点偏差（约 0.1–2s，可忽略）。</param>
    public static double CalculateSegmentTime(FlightStats stats, double distanceMeters, bool endAtGate = false)
    {
        if (stats is null || distanceMeters <= 0) return 0;

        double vf = stats.ForwardSpeed;        // 常规速度
        double vt = stats.TravelSpeed;         // 巡航速度
        double charge = stats.TravelCharge;    // 充电
        double attack = stats.TravelAttack;    // 加速时间
        double release = stats.TravelRelease;  // 减速时间

        // 防御：无巡航能力时回退常规飞行（或 0）
        if (vt <= 0)
            return vf > 0 ? distanceMeters / vf : 0;

        // 1) 常规飞行：充电期间常规航行已能到达终点（D ≤ vf·charge）
        if (vf > 0 && distanceMeters <= vf * charge)
            return distanceMeters / vf;

        // 2) 过门段（endAtGate）：加速 + 匀速 + 固定门时间（替代 release 减速段）
        //    简化点：以门上 0km 为巡航终点；末段固定 GateCrossingSeconds 覆盖减速+等待+warp。
        if (endAtGate)
        {
            double accDist = 0.5 * vt * attack;  // 加速段距离（从 0 线性加速到 vt）
            if (distanceMeters >= accDist)
                return charge + attack + GateCrossingSeconds + (distanceMeters - accDist) / vt;
            // 距离不足，加速未达全速即到门：三角形近似加速段 + 固定门时间
            double triangleTime = Math.Sqrt(2 * distanceMeters * attack / vt);
            return charge + triangleTime + GateCrossingSeconds;
        }

        // 3) 梯形速度曲线（不过门，距离足够，含匀速巡航段）
        double accDecDist = 0.5 * vt * (attack + release);
        if (distanceMeters >= accDecDist)
            return charge + attack + release + (distanceMeters - accDecDist) / vt;

        // 4) 三角形速度曲线（不过门，距离不足，加速到峰值后立即减速，未达全速）
        //    加减速总时间 = √(2·D·(attack+release)/vt)；在 D = accDecDist 处与梯形无缝衔接。
        double triangleTime2 = Math.Sqrt(2 * distanceMeters * (attack + release) / vt);
        return charge + triangleTime2;
    }

    /// <summary>
    /// 计算一段距离为 <paramref name="distanceKm"/>（公里）的巡航飞行耗时（秒）。
    /// </summary>
    public static double CalculateSegmentTimeKm(FlightStats stats, double distanceKm, bool endAtGate = false)
        => CalculateSegmentTime(stats, distanceKm * 1000.0, endAtGate);

    /// <summary>
    /// 计算静止起转、静止结束的偏航旋转耗时（秒）。
    /// 候选模型：最高角速度与角加速度均受 AI 驾驶员技能影响；根据转角自动选择梯形或三角形角速度曲线。
    /// 尚未纳入游戏的 jerk 与 steeringcurve，缺少角加速度时回退至旧的匀速近似。
    /// </summary>
    public static double CalculateRotationTime(FlightStats stats, double angleDegrees = 180)
    {
        if (stats is null || angleDegrees <= 0 || stats.YawRate <= 0) return 0;

        double omega = stats.YawRate;
        double alpha = stats.YawAngularAcceleration;
        if (!double.IsFinite(omega) || !double.IsFinite(alpha) || alpha <= 0)
            return angleDegrees / omega;

        double fullSpeedAngle = omega * omega / alpha;
        return angleDegrees >= fullSpeedAngle
            ? angleDegrees / omega + omega / alpha
            : 2 * Math.Sqrt(angleDegrees / alpha);
    }

    /// <summary>
    /// 进港耗时 = 从满常规速度减到 0 速的制动时间（秒）。
    /// ⚠️ 当前公式仅适用于 L/XL 舰船（S/M 暂不使用）。
    /// 模型：引擎倒车恒定制动（游戏自动停靠用反向推力刹停，非松油门阻力滑行）：
    ///   a_rev = 引擎数 × 反向推力 × 反向加速度系数 / 质量
    ///          （accfactors 无 reverse 属性 → 缺省 1.0；单位自洽：N/kg = m/s²）
    ///   t     = v_fwd / a_rev                                  （(m/s)/(m/s²) = s）
    /// 无制动能力（a_rev ≤ 0，如数据缺失）时返回 0。
    /// 与离港耗时（<see cref="CalculateUndockTime"/>）相互独立、分别展示，不合并。
    /// </summary>
    public static double CalculateDockTime(FlightStats stats)
    {
        if (stats is null || stats.ReverseAcceleration <= 0) return 0;
        return stats.ForwardSpeed / stats.ReverseAcceleration;
    }

    /// <summary>
    /// 离港耗时 = 从静止倒车脱离码头所需总时间（秒）。
    /// ⚠️ 当前公式仅适用于 L/XL 舰船（S/M 暂不使用）。
    /// 模型（2026-08 实测 12 艘 L 固体矿船拟合，留一 MAE ≈ 3.2 s）：
    ///   T = 5.527 + 0.674 × reverse_accel_1l
    /// 其中 reverse_accel_1l 为“从静止倒退一倍船长所需理论时间”（含加速段）：
    ///   d_acc = v_rev² / (2·a_rev)
    ///   reverse_accel_1l = 船长 ≤ d_acc ? √(2·船长 / a_rev)
    ///                                   : v_rev/a_rev + (船长 − d_acc)/v_rev
    /// 公共截距 5.527 s 代表所有船大致共有、运输效率比较中可抵消的阶段；
    /// 无反向能力（v_rev ≤ 0 或 a_rev ≤ 0）时返回 0。
    /// 与进港耗时（<see cref="CalculateDockTime"/>）相互独立、分别展示，不合并。
    /// </summary>
    public static double CalculateUndockTime(FlightStats stats, double shipLengthMeters)
    {
        if (stats is null || shipLengthMeters <= 0) return 0;
        double v = stats.ReverseSpeed;
        double a = stats.ReverseAcceleration;
        if (!double.IsFinite(v) || !double.IsFinite(a) || v <= 0 || a <= 0) return 0;

        // reverse_accel_1l：从静止倒退一倍船长所需理论时间（含加速段）
        double accelDistance = v * v / (2 * a);
        double reverseAccel1l = shipLengthMeters <= accelDistance
            ? Math.Sqrt(2 * shipLengthMeters / a)
            : v / a + (shipLengthMeters - accelDistance) / v;

        return UndockInterceptSeconds + UndockSlope * reverseAccel1l;
    }
}
