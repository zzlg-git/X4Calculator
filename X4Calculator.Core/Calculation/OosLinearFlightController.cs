namespace X4Calculator.Core.Calculation;

/// <summary>Linear FCM 使用的 float32 四维向量；位置和方向的 W 分量通常为 0。</summary>
public readonly record struct OosLinearVector4(float X, float Y, float Z, float W = 0)
{
    public static OosLinearVector4 Zero => new(0, 0, 0, 0);
}

/// <summary>位置以及局部 X/Y/Z 轴在世界坐标中的列向量。</summary>
public readonly record struct OosLinearPose(
    OosLinearVector4 Position,
    OosLinearVector4 XAxis,
    OosLinearVector4 YAxis,
    OosLinearVector4 ZAxis)
{
    public static OosLinearPose IdentityAt(OosLinearVector4 position) => new(
        position,
        new OosLinearVector4(1, 0, 0),
        new OosLinearVector4(0, 1, 0),
        new OosLinearVector4(0, 0, 1));
}

/// <summary>Yaw、Pitch、Roll 三轴角速度，单位为弧度/秒。</summary>
public readonly record struct OosLinearAngularRates(float Yaw, float Pitch, float Roll);

/// <summary>
/// 普通 world-velocity Linear FCM 的源参数。RemainingPointCount 是包含当前点在内的剩余点数。
/// ActiveAndPendingPointCount 是可选的原生导航队列快照；未提供时不改变既有普通点行为。
/// Formation/leader 与仅轴向控制器不属于此参数类型支持的分支。
/// </summary>
public sealed record OosLinearFlightParameters(
    float Acceleration,
    float Deceleration,
    float SpeedCap,
    OosLinearAngularRates TurnRates,
    OosLinearAngularRates EtaTurnRates,
    float Proximity,
    float Curvature,
    int RemainingPointCount = 1,
    bool RequireOrientation = false,
    bool SuppressCompletion = false,
    int? ActiveAndPendingPointCount = null);

/// <summary>一次 Linear FCM 更新后的可观测结果。</summary>
public sealed record OosLinearFlightStepResult(
    double Time,
    float U,
    OosLinearPose Pose,
    OosLinearVector4 Velocity,
    float Speed,
    float DesiredSpeed,
    float OrientationError,
    float SpeedFactor,
    double LinearTimeEstimate,
    float AngularTimeEstimate,
    bool PositionReached,
    bool FcmCompletion);

/// <summary>
/// 从原生例程恢复的普通 Linear FCM。所有路径和运动运算在与研究参考实现相同的表达式边界
/// 舍入到 IEEE-754 binary32；控制器时钟保留 double，因为 Python/原生调度时间不在这些边界舍入。
/// </summary>
public sealed class OosLinearFlightController
{
    private const double Epsilon = 1e-27;
    private const float AngleEpsilon = 1e-4f;
    private const float HalfPiFactor = 2f / MathF.PI;
    private static readonly OosLinearVector4 Ry180X = new(-1, 0, F(8.742277657e-8), 0);
    private static readonly OosLinearVector4 Ry180Y = new(0, 1, 0, 0);
    private static readonly OosLinearVector4 Ry180Z = new(-F(8.742277657e-8), 0, -1, 0);

    private Curve? _curve;
    private OosLinearPose _start;
    private OosLinearPose _target;
    private OosLinearVector4 _delta;
    private OosLinearPose _initialLook;
    private bool _finalOrientation;

    public OosLinearFlightController(
        OosLinearPose pose,
        OosLinearVector4 velocity,
        OosLinearPose target,
        OosLinearFlightParameters parameters,
        double now = 0,
        float initialFactor = 1)
    {
        ValidatePose(pose, nameof(pose));
        ValidateVector(velocity, nameof(velocity));
        ValidatePose(target, nameof(target));
        ValidateParameters(parameters);
        if (!double.IsFinite(now))
            throw new ArgumentOutOfRangeException(nameof(now));
        if (!float.IsFinite(initialFactor))
            throw new ArgumentOutOfRangeException(nameof(initialFactor));

        Pose = Round(pose);
        Velocity = Round(velocity);
        Parameters = parameters;
        Now = now;
        Factor = initialFactor;
        OrientationError = 0;
        Replan(target);
    }

    public OosLinearPose Pose { get; private set; }
    public OosLinearVector4 Velocity { get; private set; }
    public float U { get; private set; }
    public float Length { get; private set; }
    public float Factor { get; private set; }
    public double Now { get; private set; }
    public bool Completed { get; private set; }
    public OosLinearFlightParameters Parameters { get; private set; }
    public float OrientationError { get; private set; }
    public double? PositionReachedAt { get; private set; }
    public OosLinearPose Target => _target;

    /// <summary>
    /// 仅将显式 pose 的普通 warp 设置器应用于此处表示的运动状态。
    /// 速度、曲线、目标、因子与时钟保持不变；这既不是重新规划，也不保证其他原生
    /// 运动 bank/context 命令已更新。调用方负责 bank 选择、reference-space 有效性及后续 worker 交付。
    /// </summary>
    public void ApplyExplicitWarpPose(OosLinearPose pose)
    {
        ValidatePose(pose, nameof(pose));
        Pose = Round(pose);
    }

    /// <summary>
    /// 普通 worker 已完成 reference 校验后的读→写运动字段装载。
    /// 只替换本 tick 的 pose/速度，不恢复或重建 FCM 曲线、目标、进度、因子与时钟。
    /// </summary>
    public void ApplyWorkerMotionState(OosLinearPose pose, OosLinearVector4 velocity)
    {
        ValidatePose(pose, nameof(pose));
        ValidateVector(velocity, nameof(velocity));
        Pose = Round(pose);
        Velocity = Round(velocity);
    }

    /// <summary>
    /// 使用 Linear FCM 自身的 float32 朝向旋转规则，以指定位置和前向方向构造 pose。
    /// </summary>
    public static OosLinearPose CreateLookPose(
        OosLinearVector4 position,
        OosLinearVector4 direction)
    {
        ValidateVector(position, nameof(position));
        ValidateVector(direction, nameof(direction));
        var rotation = LookRotation(Round(direction));
        return WithRotation(Round(position), rotation);
    }

    /// <summary>
    /// 可先应用下一 FlightPoint 的整组参数，再以当前 pose 重建曲线。该操作保留
    /// 速度、因子、now 和上一 tick 姿态误差，重置路径进度与完成状态。
    /// </summary>
    public void Replan(OosLinearPose target, OosLinearFlightParameters? parameters = null)
    {
        ValidatePose(target, nameof(target));
        if (parameters is not null)
        {
            ValidateParameters(parameters);
            Parameters = parameters;
        }
        _target = Round(target);
        _start = Pose;
        _curve = BuildCurve(_start, _target, Parameters.Curvature);
        _delta = Subtract(_target.Position, _start.Position);
        Length = Norm(_delta);
        U = _curve is null ? 1 : 0;
        _finalOrientation = false;
        Completed = false;
        PositionReachedAt = null;
        _initialLook = LookRotation(_delta);
    }

    /// <summary>
    /// 前进一次控制器 tick。传入 parameters 时整组替换本 tick 及后续 tick 使用的源参数；
    /// 路径曲率只在构造或显式 Replan 时消费。
    /// </summary>
    public OosLinearFlightStepResult Step(double deltaSeconds, OosLinearFlightParameters? parameters = null) =>
        StepAt(Now + deltaSeconds, deltaSeconds, parameters);

    /// <summary>在显式 worker TLS 时刻积分一个实际 dt；事件间隔不被自动当成积分步长。</summary>
    public OosLinearFlightStepResult StepAt(
        double now, double deltaSeconds, OosLinearFlightParameters? parameters = null)
    {
        if (!double.IsFinite(now) || now < Now)
            throw new ArgumentOutOfRangeException(nameof(now));
        if (!double.IsFinite(deltaSeconds) || deltaSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        if (parameters is not null)
        {
            ValidateParameters(parameters);
            Parameters = parameters;
        }

        Now = now;
        var dt = F(deltaSeconds);
        var oldRotation = Pose;
        var speed = MathF.Max(0, Dot(Velocity, oldRotation.ZAxis));
        // 原生同位置分支比较存储的 start/target 分量，而非与当前 pose 的距离。
        // 此处仅在队列显式为空时闭合其终止消费者。
        var samePositionWithoutPoints = Parameters.ActiveAndPendingPointCount == 0 &&
            MathF.Abs(_delta.X) < AngleEpsilon &&
            MathF.Abs(_delta.Y) < AngleEpsilon &&
            MathF.Abs(_delta.Z) < AngleEpsilon;
        // DesiredSpeed 用于诊断：快捷分支不评估速度请求，因此报告传入的前向速度。
        // 此值不参与原生运动算术。
        var desired = speed;
        double linearTime = 0;
        if (samePositionWithoutPoints)
        {
            U = 1;
        }
        else
        {
            var remaining = F((double)F(1d - U) * Length);
            var cap = MathF.Abs(Parameters.SpeedCap) < 1e-4f
                ? 0
                : MathF.Max(F((double)Parameters.SpeedCap * Factor), 10);
            var acceleration = Parameters.Acceleration;
            var deceleration = Parameters.Deceleration;
            var near = remaining <= Parameters.Proximity;
            desired = cap;
            if (near && Parameters.RemainingPointCount <= 1)
            {
                desired = 10;
                if (remaining <= 0 && speed != 0)
                    deceleration = float.PositiveInfinity;
                else if (remaining != 0)
                    deceleration = MathF.Max(deceleration, F((double)F((double)speed * speed) / F(2d * remaining)));
            }

            if (MathF.Abs(speed - desired) < AngleEpsilon)
                speed = desired;
            else if (speed > desired)
                speed = MathF.Max(F((double)speed - F((double)deceleration * dt)), desired);
            else
                speed = MathF.Min(F((double)speed + F((double)acceleration * dt)), desired);

            linearTime = MathF.Abs(deceleration) >= 1e-4f ? F((double)speed / deceleration) : 0;
            if (!near && MathF.Abs(speed) >= 1e-4f)
                linearTime += F((double)F((double)remaining - Parameters.Proximity) / speed);

            Velocity = Scale(oldRotation.ZAxis, speed);
            if (Length > 10_000_000)
                U = F(1d - F(100_000d / Length));
            else if (Length < 1e-4f)
                U = 1;
            else
                U = Math.Clamp(F((double)U + F((double)F((double)speed * dt) / Length)), 0, 1);
        }

        var errors = RelativeAngles(oldRotation, _target);
        var angularTime = MathF.Max(
            F((double)errors.Yaw / MathF.Max(Parameters.EtaTurnRates.Yaw, 0.01f)),
            MathF.Max(
                F((double)errors.Pitch / MathF.Max(Parameters.EtaTurnRates.Pitch, 0.01f)),
                F((double)errors.Roll / MathF.Max(Parameters.EtaTurnRates.Roll, 0.01f))));
        if (U >= 1 || (angularTime > linearTime && MathF.Abs(speed) >= 1e-4f))
            _finalOrientation = true;

        OosLinearVector4 position;
        OosLinearPose desiredRotation;
        if (_curve is not null)
        {
            var evaluated = EvaluateCurve(_curve, U);
            position = evaluated.Position;
            desiredRotation = LookRotation(evaluated.Tangent);
        }
        else
        {
            position = Add(_start.Position, Scale(_delta, U));
            desiredRotation = _finalOrientation ? _target : _initialLook;
        }

        var currentAngles = Angles(oldRotation);
        var targetAngles = Angles(desiredRotation);
        var nextAngles = new OosLinearAngularRates(
            StepAngle(currentAngles.Yaw, targetAngles.Yaw, Parameters.TurnRates.Yaw, dt, wrap: true),
            StepAngle(currentAngles.Pitch, targetAngles.Pitch, Parameters.TurnRates.Pitch, dt, wrap: false),
            StepAngle(currentAngles.Roll, targetAngles.Roll, Parameters.TurnRates.Roll, dt, wrap: true));
        var newRotation = Rotation(nextAngles);
        var nextErrors = RelativeAngles(newRotation, desiredRotation);
        OrientationError = MathF.Max(nextErrors.Yaw, MathF.Max(nextErrors.Pitch, nextErrors.Roll));
        Factor = OrientationError > F(Math.PI / 2) ? 0 : F(1d - F((double)OrientationError * HalfPiFactor));
        if (Factor < 0.5f)
        {
            var timeRatio = F((double)angularTime / Math.Max(linearTime, 0.001));
            var weightedRatio = F((double)F((double)timeRatio * 0.5f) * F((double)Factor - 0.5f));
            Factor = F(0.5d + weightedRatio);
        }

        if (U >= 1)
        {
            position = _target.Position;
            PositionReachedAt ??= Now;
            if (Parameters.ActiveAndPendingPointCount == 0)
            {
                // 原生 Linear FCM 仅在抵达的 target 没有活动或待处理 navigation points 时
                // 清除写 bank 的线速度。不要将该状态视为点完成：消费者
                // 仍负责 navigation 和控制器交接。
                Velocity = OosLinearVector4.Zero;
                speed = 0;
                Completed = false;
            }
            else
            {
                var gate = !Parameters.RequireOrientation ||
                           (OrientationError < AngleEpsilon && _finalOrientation);
                Completed = gate && !Parameters.SuppressCompletion;
            }
        }

        Pose = WithRotation(position, newRotation);
        return new(
            Now,
            U,
            Pose,
            Velocity,
            speed,
            desired,
            OrientationError,
            Factor,
            linearTime,
            angularTime,
            U >= 1,
            Completed);
    }

    private static Curve? BuildCurve(OosLinearPose start, OosLinearPose end, float radius)
    {
        var delta = Subtract(end.Position, start.Position);
        var length = Norm(delta);
        if (F((double)length * length) < 2)
            return null;

        OosLinearVector4[] points;
        OosLinearVector4[] directions;
        if (MathF.Abs(radius) < 1e-4f ||
            F((double)length * length) <= F((double)F((double)radius * radius) * 9))
        {
            points = [start.Position, end.Position];
            directions = [start.ZAxis, end.ZAxis];
        }
        else
        {
            var endBackRotation = MatrixMultiply(end, Ry180X, Ry180Y, Ry180Z);
            var firstHalf = HalfAngleDirection(TransposeVector(start, delta));
            var secondHalf = HalfAngleDirection(TransposeVector(endBackRotation, Subtract(start.Position, end.Position)));
            var first = Add(start.Position, Scale(MatrixVector(start, firstHalf), radius));
            var second = Add(end.Position, Scale(MatrixVector(endBackRotation, secondHalf), radius));
            var middleDirection = Subtract(second, first);
            middleDirection = Scale(middleDirection, 1d / (Norm(middleDirection) + Epsilon));
            points = [start.Position, first, second, end.Position];
            directions = [start.ZAxis, middleDirection, middleDirection, end.ZAxis];
        }

        var segments = new CurveSegment[points.Length - 1];
        var total = 0f;
        for (var index = 0; index < segments.Length; index++)
        {
            var q0 = points[index];
            var q3 = points[index + 1];
            var handle = F((double)Norm(Subtract(q3, q0)) * F(1d / 3));
            var q1 = Add(q0, Scale(directions[index], handle));
            var q2 = Subtract(q3, Scale(directions[index + 1], handle));
            var origin = Scale(Add(Add(Add(q0, q1), q2), q3), 0.25);
            var r0 = Subtract(q0, origin);
            var r1 = Subtract(q1, origin);
            var r2 = Subtract(q2, origin);
            var r3 = Subtract(q3, origin);
            var coefficientA = Subtract(Add(Scale(Subtract(r1, r2), 3), r3), r0);
            var coefficientB = Scale(Add(Subtract(r2, Scale(r1, 2)), r0), 3);
            var coefficientC = Scale(Subtract(r1, r0), 3);
            var firstPair = F((double)Norm(Subtract(r2, r1)) + Norm(Subtract(r1, r0)));
            var secondPair = F((double)Norm(Subtract(r3, r0)) + Norm(Subtract(r3, r2)));
            var segmentLength = F((double)F((double)firstPair + secondPair) * 0.5);
            if (segmentLength == 0)
                segmentLength = 1;
            segments[index] = new(origin, coefficientA, coefficientB, coefficientC, r0, segmentLength);
            total = F((double)total + segmentLength);
        }

        var knots = new float[segments.Length + 1];
        for (var index = 0; index < segments.Length - 1; index++)
            knots[index + 1] = F((double)knots[index] + F((double)segments[index].Length / total));
        knots[^1] = 1;
        return new(segments, knots, total);
    }

    private static (OosLinearVector4 Position, OosLinearVector4 Tangent) EvaluateCurve(Curve curve, float u)
    {
        var index = u <= 0 ? 0 : curve.Segments.Length - 1;
        if (u is > 0 and < 1)
        {
            for (var candidate = 0; candidate < curve.Segments.Length; candidate++)
            {
                if (u <= curve.Knots[candidate + 1])
                {
                    index = candidate;
                    break;
                }
            }
        }

        var segment = curve.Segments[index];
        var t = F((double)F((double)F((double)u - curve.Knots[index]) * curve.TotalLength) / segment.Length);
        var t2 = F((double)t * t);
        var t3 = F((double)t2 * t);
        return (
            EvaluatePosition(segment, t, t2, t3),
            EvaluateTangent(segment, t, t2));
    }

    private static OosLinearVector4 EvaluatePosition(CurveSegment segment, float t, float t2, float t3) => new(
        EvaluatePositionAxis(segment.A.X, segment.B.X, segment.C.X, segment.D.X, segment.Origin.X, t, t2, t3),
        EvaluatePositionAxis(segment.A.Y, segment.B.Y, segment.C.Y, segment.D.Y, segment.Origin.Y, t, t2, t3),
        EvaluatePositionAxis(segment.A.Z, segment.B.Z, segment.C.Z, segment.D.Z, segment.Origin.Z, t, t2, t3),
        EvaluatePositionAxis(segment.A.W, segment.B.W, segment.C.W, segment.D.W, segment.Origin.W, t, t2, t3));

    private static float EvaluatePositionAxis(float a, float b, float c, float d, float origin, float t, float t2, float t3)
    {
        var linear = F((double)t * c + d);
        var quadratic = F((double)t2 * b);
        var cubic = F((double)t3 * a);
        return F((double)F((double)F((double)linear + quadratic) + cubic) + origin);
    }

    private static OosLinearVector4 EvaluateTangent(CurveSegment segment, float t, float t2) => new(
        EvaluateTangentAxis(segment.A.X, segment.B.X, segment.C.X, t, t2),
        EvaluateTangentAxis(segment.A.Y, segment.B.Y, segment.C.Y, t, t2),
        EvaluateTangentAxis(segment.A.Z, segment.B.Z, segment.C.Z, t, t2),
        EvaluateTangentAxis(segment.A.W, segment.B.W, segment.C.W, t, t2));

    private static float EvaluateTangentAxis(float a, float b, float c, float t, float t2)
    {
        var twoT = F(2d * t);
        var quadratic = F((double)twoT * b);
        var threeT2 = F(3d * t2);
        var cubic = F((double)threeT2 * a);
        return F((double)F((double)quadratic + c) + cubic);
    }

    private static OosLinearPose LookRotation(OosLinearVector4 direction)
    {
        var normalized = Normalize(direction);
        if (MathF.Abs(normalized.X) < 1e-5f && MathF.Abs(normalized.Z) < 1e-5f)
            return Rotation(new OosLinearAngularRates(0, MathF.CopySign(F(Math.PI / 2), normalized.Y), 0));

        var right = Normalize(new OosLinearVector4(normalized.Z, 0, -normalized.X, 0));
        var up = Cross(normalized, right);
        return new(OosLinearVector4.Zero, right, up, normalized);
    }

    private static OosLinearPose Rotation(OosLinearAngularRates angles)
    {
        var sinYaw = F(Math.Sin(angles.Yaw));
        var cosYaw = F(Math.Cos(angles.Yaw));
        var sinPitch = F(Math.Sin(angles.Pitch));
        var cosPitch = F(Math.Cos(angles.Pitch));
        var sinRoll = F(Math.Sin(angles.Roll));
        var cosRoll = F(Math.Cos(angles.Roll));
        return new(
            OosLinearVector4.Zero,
            new(
                F((double)cosYaw * cosRoll + (double)sinYaw * sinPitch * sinRoll),
                F(-(double)cosPitch * sinRoll),
                F(-(double)sinYaw * cosRoll + (double)cosYaw * sinPitch * sinRoll),
                0),
            new(
                F((double)cosYaw * sinRoll - (double)sinYaw * sinPitch * cosRoll),
                F((double)cosPitch * cosRoll),
                F(-(double)sinYaw * sinRoll - (double)cosYaw * sinPitch * cosRoll),
                0),
            new(
                F((double)sinYaw * cosPitch),
                sinPitch,
                F((double)cosYaw * cosPitch),
                0));
    }

    private static OosLinearAngularRates Angles(OosLinearPose rotation) => new(
        F(-Math.Atan2(-rotation.ZAxis.X, rotation.ZAxis.Z)),
        F(Math.Asin(Math.Clamp(rotation.ZAxis.Y, -1, 1))),
        F(Math.Atan2(-rotation.XAxis.Y, rotation.YAxis.Y)));

    private static OosLinearAngularRates RelativeAngles(OosLinearPose current, OosLinearPose desired)
    {
        var relative = new OosLinearPose(
            OosLinearVector4.Zero,
            new(Dot(current.XAxis, desired.XAxis), Dot(current.YAxis, desired.XAxis), Dot(current.ZAxis, desired.XAxis)),
            new(Dot(current.XAxis, desired.YAxis), Dot(current.YAxis, desired.YAxis), Dot(current.ZAxis, desired.YAxis)),
            new(Dot(current.XAxis, desired.ZAxis), Dot(current.YAxis, desired.ZAxis), Dot(current.ZAxis, desired.ZAxis)));
        var angles = Angles(relative);
        return new(MathF.Abs(angles.Yaw), MathF.Abs(angles.Pitch), MathF.Abs(angles.Roll));
    }

    private static float StepAngle(float current, float target, float rate, float dt, bool wrap)
    {
        if (wrap)
        {
            var period = F(2 * Math.PI);
            var halfPeriod = F((double)period * 0.5);
            var difference = F((double)target - current);
            var dividend = (double)difference + halfPeriod;
            var modulo = dividend % period;
            if (modulo < 0)
                modulo += period;
            target = F((double)current + F(modulo) - halfPeriod);
        }
        if (MathF.Abs(current - target) < AngleEpsilon)
            return target;
        var step = F((double)rate * dt);
        return current < target
            ? MathF.Min(F((double)current + step), target)
            : MathF.Max(F((double)current - step), target);
    }

    private static OosLinearVector4 HalfAngleDirection(OosLinearVector4 localDirection)
    {
        var direction = NormalizeDirection(localDirection);
        var pitch = F(Math.Asin(Math.Clamp(direction.Y, -1, 1)));
        var yaw = F(Math.Atan2(direction.X, direction.Z));
        var pitchHalf = F((double)pitch * 0.5);
        var yawHalf = F((double)yaw * 0.5);
        var sinPitch = F(Math.Sin(pitchHalf));
        var cosPitch = F(Math.Cos(pitchHalf));
        var sinYaw = F(Math.Sin(yawHalf));
        var cosYaw = F(Math.Cos(yawHalf));
        return new(
            F((double)sinYaw * cosPitch),
            sinPitch,
            F((double)cosYaw * cosPitch),
            0);
    }

    private static OosLinearVector4 NormalizeDirection(OosLinearVector4 vector)
    {
        if (MathF.Abs(vector.X) < 1e-5f && MathF.Abs(vector.Z) < 1e-5f)
        {
            var signedZero = MathF.CopySign(0, vector.Y);
            return new(signedZero, MathF.CopySign(1, vector.Y), signedZero, signedZero);
        }
        return Scale(vector, 1d / (Norm(vector) + Epsilon));
    }

    private static OosLinearVector4 Normalize(OosLinearVector4 vector) =>
        Scale(vector, 1d / (Norm(vector) + Epsilon));

    private static OosLinearVector4 MatrixVector(OosLinearPose rotation, OosLinearVector4 vector) => new(
        MatrixAxis(rotation.XAxis.X, rotation.YAxis.X, rotation.ZAxis.X, vector),
        MatrixAxis(rotation.XAxis.Y, rotation.YAxis.Y, rotation.ZAxis.Y, vector),
        MatrixAxis(rotation.XAxis.Z, rotation.YAxis.Z, rotation.ZAxis.Z, vector),
        MatrixAxis(rotation.XAxis.W, rotation.YAxis.W, rotation.ZAxis.W, vector));

    private static float MatrixAxis(float x, float y, float z, OosLinearVector4 vector) =>
        F((double)F((double)x * vector.X) + F((double)F((double)y * vector.Y) + F((double)z * vector.Z)));

    private static OosLinearVector4 TransposeVector(OosLinearPose rotation, OosLinearVector4 vector) => new(
        Dot(rotation.XAxis, vector),
        Dot(rotation.YAxis, vector),
        Dot(rotation.ZAxis, vector),
        0);

    private static OosLinearPose MatrixMultiply(
        OosLinearPose left,
        OosLinearVector4 rightX,
        OosLinearVector4 rightY,
        OosLinearVector4 rightZ) =>
        new(OosLinearVector4.Zero, MatrixVector(left, rightX), MatrixVector(left, rightY), MatrixVector(left, rightZ));

    private static OosLinearVector4 Add(OosLinearVector4 left, OosLinearVector4 right) => new(
        F((double)left.X + right.X), F((double)left.Y + right.Y),
        F((double)left.Z + right.Z), F((double)left.W + right.W));

    private static OosLinearVector4 Subtract(OosLinearVector4 left, OosLinearVector4 right) => new(
        F((double)left.X - right.X), F((double)left.Y - right.Y),
        F((double)left.Z - right.Z), F((double)left.W - right.W));

    private static OosLinearVector4 Scale(OosLinearVector4 vector, double scalar) => new(
        F(vector.X * scalar), F(vector.Y * scalar), F(vector.Z * scalar), F(vector.W * scalar));

    private static float Dot(OosLinearVector4 left, OosLinearVector4 right)
    {
        var x = F((double)left.X * right.X);
        var yz = F((double)F((double)left.Y * right.Y) + F((double)left.Z * right.Z));
        return F((double)x + yz);
    }

    private static float Norm(OosLinearVector4 vector) => F(Math.Sqrt(Math.Max(0, Dot(vector, vector))));

    private static OosLinearVector4 Cross(OosLinearVector4 left, OosLinearVector4 right) => new(
        F((double)left.Y * right.Z - (double)left.Z * right.Y),
        F((double)left.Z * right.X - (double)left.X * right.Z),
        F((double)left.X * right.Y - (double)left.Y * right.X),
        0);

    private static OosLinearPose WithRotation(OosLinearVector4 position, OosLinearPose rotation) =>
        new(position, rotation.XAxis, rotation.YAxis, rotation.ZAxis);

    private static OosLinearVector4 Round(OosLinearVector4 vector) =>
        new(F(vector.X), F(vector.Y), F(vector.Z), F(vector.W));

    private static OosLinearPose Round(OosLinearPose pose) =>
        new(Round(pose.Position), Round(pose.XAxis), Round(pose.YAxis), Round(pose.ZAxis));

    private static float F(double value) => (float)value;

    private static void ValidateParameters(OosLinearFlightParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (!float.IsFinite(parameters.Acceleration) || parameters.Acceleration < 0 ||
            !float.IsFinite(parameters.Deceleration) || parameters.Deceleration < 0 ||
            !float.IsFinite(parameters.SpeedCap) || parameters.SpeedCap < 0 ||
            !float.IsFinite(parameters.Proximity) || parameters.Proximity < 0 ||
            !float.IsFinite(parameters.Curvature) ||
            parameters.RemainingPointCount < 1 ||
            parameters.ActiveAndPendingPointCount is < 0 ||
            !AreFiniteAndNonNegative(parameters.TurnRates) ||
            !AreFiniteAndNonNegative(parameters.EtaTurnRates))
        {
            throw new ArgumentOutOfRangeException(nameof(parameters),
                "Motion values and rates must be finite and non-negative, curvature must be finite, at least one remaining point must exist, and the optional active/pending point count must be non-negative.");
        }
    }

    private static bool AreFiniteAndNonNegative(OosLinearAngularRates rates) =>
        float.IsFinite(rates.Yaw) && rates.Yaw >= 0 &&
        float.IsFinite(rates.Pitch) && rates.Pitch >= 0 &&
        float.IsFinite(rates.Roll) && rates.Roll >= 0;

    private static void ValidatePose(OosLinearPose pose, string parameterName)
    {
        ValidateVector(pose.Position, parameterName);
        ValidateVector(pose.XAxis, parameterName);
        ValidateVector(pose.YAxis, parameterName);
        ValidateVector(pose.ZAxis, parameterName);
    }

    private static void ValidateVector(OosLinearVector4 vector, string parameterName)
    {
        if (!float.IsFinite(vector.X) || !float.IsFinite(vector.Y) ||
            !float.IsFinite(vector.Z) || !float.IsFinite(vector.W))
            throw new ArgumentOutOfRangeException(parameterName, "Vector components must be finite float32 values.");
    }

    private sealed record Curve(CurveSegment[] Segments, float[] Knots, float TotalLength);
    private sealed record CurveSegment(
        OosLinearVector4 Origin,
        OosLinearVector4 A,
        OosLinearVector4 B,
        OosLinearVector4 C,
        OosLinearVector4 D,
        float Length);
}
