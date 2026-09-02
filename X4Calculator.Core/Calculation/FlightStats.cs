namespace X4Calculator.Core.Calculation;

/// <summary>
/// 舰船装配指定推进器和引擎后的飞行属性计算结果。
/// </summary>
public class FlightStats
{
    /// <summary>
    /// 常规最大速度（m/s）。
    /// </summary>
    public double ForwardSpeed { get; set; }

    /// <summary>
    /// 反向最大速度（m/s，= 引擎数 × 单引擎反向推力 / 后向阻力）。
    /// </summary>
    public double ReverseSpeed { get; set; }

    /// <summary>
    /// 常规加速度（m/s²，未取整的实际值）。
    /// </summary>
    public double Acceleration { get; set; }

    /// <summary>
    /// 反向制动减速度（m/s²，未取整的实际值；引擎倒车刹停/进港减速用）。
    /// </summary>
    public double ReverseAcceleration { get; set; }

    /// <summary>
    /// 俯仰转向速度（°/s）。
    /// </summary>
    public double PitchRate { get; set; }

    /// <summary>
    /// 俯仰角加速度（°/s²；候选模型 = 推进器俯仰推力 ÷ 船体俯仰惯量 × 技能系数）。
    /// </summary>
    public double PitchAngularAcceleration { get; set; }

    /// <summary>
    /// 偏航转向速度（°/s）。
    /// </summary>
    public double YawRate { get; set; }

    /// <summary>
    /// 偏航角加速度（°/s²；候选模型 = 推进器偏航推力 ÷ 船体偏航惯量 × 技能系数）。
    /// 不包含尚未验证的 jerk 与 steeringcurve。
    /// </summary>
    public double YawAngularAcceleration { get; set; }

    /// <summary>
    /// 翻滚转向速度（°/s）。
    /// </summary>
    public double RollRate { get; set; }

    /// <summary>
    /// 翻滚角加速度（°/s²；候选模型 = 推进器翻滚推力 ÷ 船体翻滚惯量 × 技能系数）。
    /// </summary>
    public double RollAngularAcceleration { get; set; }

    /// <summary>
    /// 巡航最大速度（m/s）。
    /// </summary>
    public double TravelSpeed { get; set; }

    /// <summary>
    /// 巡航引擎启动时间（秒，引擎 travel.charge）。
    /// </summary>
    public double TravelCharge { get; set; }

    /// <summary>
    /// 巡航加速时间（秒，引擎 travel.attack）。
    /// </summary>
    public double TravelAttack { get; set; }

    /// <summary>
    /// 巡航减速时间（秒，引擎 travel.release）。
    /// </summary>
    public double TravelRelease { get; set; }
}
