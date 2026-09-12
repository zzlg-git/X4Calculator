namespace X4Calculator.Core.Calculation;

public enum OosNavigationMode
{
    Boost,
    Travel
}

public enum OosNavigationModeRequestAction
{
    Start,
    Stop
}

/// <summary>Navigation controller 发出的异步 mode 请求；它本身不改变 cached mode bits。</summary>
public readonly record struct OosNavigationModeRequest(
    OosNavigationMode Mode,
    OosNavigationModeRequestAction Action);

/// <summary>E8F250 一次 boost/travel 门限求值的 typed 结果。</summary>
public sealed record OosNavigationModeDecision(
    bool BoostEvaluated,
    bool TravelEvaluated,
    IReadOnlyList<OosNavigationModeRequest> Requests,
    float OrientationError,
    bool PointBoost,
    bool PointTravel,
    bool BoostActive,
    bool TravelActive,
    bool TravelPending,
    double LastBoostRequestTime,
    double LastTravelRequestTime);

/// <summary>
/// X4 9.00 E8F250 的普通 boost/travel 请求门。request clocks 与 cached mode bits 分离；
/// 调用者必须在实际请求交付后显式回填 cache。
/// </summary>
public sealed class OosNavigationModeRequestGate
{
    public const double NativeRequestClockSentinelLimit = -0.9999d;

    public float StartBoostAngle { get; }
    public float StopBoostAngle { get; }
    public float StartTravelAngle { get; }
    public float StopTravelAngle { get; }
    public double LastBoostRequestTime { get; private set; }
    public double LastTravelRequestTime { get; private set; }
    public bool BoostActive { get; private set; }
    public bool TravelActive { get; private set; }
    public bool TravelPending { get; private set; }

    public OosNavigationModeRequestGate(
        float startBoostAngle,
        float stopBoostAngle,
        float startTravelAngle,
        float stopTravelAngle,
        double lastBoostRequestTime = -1d,
        double lastTravelRequestTime = -1d,
        bool boostActive = false,
        bool travelActive = false,
        bool travelPending = false,
        bool leaderTravelEligible = false)
    {
        StartBoostAngle = RequireFinite(startBoostAngle, nameof(startBoostAngle));
        StopBoostAngle = RequireFinite(stopBoostAngle, nameof(stopBoostAngle));
        StartTravelAngle = RequireFinite(startTravelAngle, nameof(startTravelAngle));
        StopTravelAngle = RequireFinite(stopTravelAngle, nameof(stopTravelAngle));
        LastBoostRequestTime = RequireFinite(lastBoostRequestTime, nameof(lastBoostRequestTime));
        LastTravelRequestTime = RequireFinite(lastTravelRequestTime, nameof(lastTravelRequestTime));
        BoostActive = boostActive;
        TravelActive = travelActive;
        TravelPending = travelPending;

        if (leaderTravelEligible)
        {
            throw new NotSupportedException(
                "Leader travel eligibility is outside the ordinary world-velocity docking branch.");
        }
    }

    /// <summary>只回填已经由 engine/cache 链交付的状态，不把 request 当作即时状态变更。</summary>
    public void SetCachedModes(
        bool? boostActive = null,
        bool? travelActive = null,
        bool? travelPending = null)
    {
        if (boostActive.HasValue)
            BoostActive = boostActive.Value;
        if (travelActive.HasValue)
            TravelActive = travelActive.Value;
        if (travelPending.HasValue)
            TravelPending = travelPending.Value;
    }

    /// <summary>E8F250 使用严格 now &gt; last + interval；相等仍被节流。</summary>
    public static bool IsDue(double now, double lastRequestTime, bool active)
    {
        RequireFinite(now, nameof(now));
        RequireFinite(lastRequestTime, nameof(lastRequestTime));
        var interval = active ? 1d : 5d;
        return lastRequestTime <= NativeRequestClockSentinelLimit ||
               now > lastRequestTime + interval;
    }

    public OosNavigationModeDecision Evaluate(
        double now,
        float orientationError,
        bool pointBoost,
        bool pointTravel)
    {
        RequireFinite(now, nameof(now));
        var error = MathF.Abs(RequireFinite(orientationError, nameof(orientationError)));
        var requests = new List<OosNavigationModeRequest>(2);

        var boostEvaluated = IsDue(now, LastBoostRequestTime, BoostActive);
        if (boostEvaluated)
        {
            var threshold = BoostActive ? StopBoostAngle : StartBoostAngle;
            var desired = pointBoost && error < threshold;
            if (desired && !BoostActive)
            {
                requests.Add(new(OosNavigationMode.Boost, OosNavigationModeRequestAction.Start));
            }
            else if (!desired && BoostActive)
            {
                requests.Add(new(OosNavigationMode.Boost, OosNavigationModeRequestAction.Stop));
            }

            // F38 是 request clock。没有状态变化请求的检查不写入它。
            if (desired != BoostActive)
                LastBoostRequestTime = now;
        }

        var travelState = TravelActive || TravelPending;
        // E91871 的节流周期只看 cached active；pending 仅参与后续请求抑制。
        var travelEvaluated = IsDue(now, LastTravelRequestTime, TravelActive);
        if (travelEvaluated)
        {
            var threshold = TravelActive ? StopTravelAngle : StartTravelAngle;
            var desired = pointTravel && error < threshold;
            if (desired && !travelState)
            {
                requests.Add(new(OosNavigationMode.Travel, OosNavigationModeRequestAction.Start));
            }
            else if (!desired && travelState)
            {
                requests.Add(new(OosNavigationMode.Travel, OosNavigationModeRequestAction.Stop));
            }

            if (desired != travelState)
                LastTravelRequestTime = now;
        }

        return new(
            boostEvaluated,
            travelEvaluated,
            requests,
            error,
            pointBoost,
            pointTravel,
            BoostActive,
            TravelActive,
            TravelPending,
            LastBoostRequestTime,
            LastTravelRequestTime);
    }

    private static float RequireFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName);
        return value;
    }

    private static double RequireFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName);
        return value;
    }
}

/// <summary>已解析到同一 world/reference space 的普通 MoveDocking FlightPoint。</summary>
public sealed record OosDockingFlightPoint(
    OosLinearPose Target,
    float Radius,
    bool Boost,
    bool Travel,
    OosLinearFlightParameters? Parameters = null);

/// <summary>EB27C0 -&gt; E88160 的两个完成 guard。</summary>
public sealed record OosDockingCompletionFlags(
    bool RequireFinalOrientation = false,
    bool SuppressQueueAdvancement = false);

public delegate OosLinearFlightParameters? OosDockingParameterResolver(
    OosDockingController controller,
    int pointIndex,
    OosDockingFlightPoint point,
    double deltaSeconds);

public delegate void OosDockingModeRequestSink(
    OosDockingController controller,
    OosDockingModeRequestEvent requestEvent);

public abstract record OosDockingEvent(double SourceTime, int PointIndex);

public sealed record OosDockingPointPopReplanEvent(
    double SourceTime,
    int CompletedPointIndex,
    int NextPointIndex,
    float U)
    : OosDockingEvent(SourceTime, CompletedPointIndex);

public sealed record OosDockingLandingReadyEvent(
    double SourceTime,
    int PointIndex,
    float U,
    float ChordLength,
    float RemainingChord,
    float Radius)
    : OosDockingEvent(SourceTime, PointIndex);

public sealed record OosDockingModeRequestEvent(
    double SourceTime,
    int PointIndex,
    OosNavigationModeRequest Request)
    : OosDockingEvent(SourceTime, PointIndex);

/// <summary>一次普通 docking queue tick；Flight 与 PointIndex 描述更新前的活动点。</summary>
public sealed record OosDockingStepResult(
    OosLinearFlightStepResult Flight,
    int PointIndex,
    int RemainingPointCount,
    bool SegmentPopped,
    bool LandingReady,
    float? RemainingChord,
    OosDockingEvent? Event,
    OosNavigationModeDecision? ModeDecision,
    IReadOnlyList<OosNavigationModeRequest> ModeRequests);

public sealed record OosDockingRunResult(
    bool LandingReady,
    double? LandingReadySourceTime,
    double EndTime,
    int ActivePointIndex,
    IReadOnlyList<OosDockingEvent> Events,
    IReadOnlyList<OosDockingStepResult> Samples);

/// <summary>
/// 普通 world-velocity MoveDocking FlightPoint 队列。中间点只在 FCM 完成后 pop/replan；
/// 最后一点按剩余弦长与 source radius 的原生边界触发 landing-ready。
/// </summary>
public sealed class OosDockingController
{
    private const float OrientationEpsilon = 1e-4f;
    private readonly OosLinearFlightParameters _baseParameters;
    private readonly OosDockingParameterResolver? _parameterResolver;
    private readonly OosNavigationModeRequestGate? _modeGate;
    private readonly OosDockingModeRequestSink? _modeRequestSink;
    private readonly List<OosDockingEvent> _events = [];
    private OosLinearFlightController _flightController;
    private OosDockingStepResult? _lastStep;

    public OosDockingController(
        OosLinearPose pose,
        OosLinearVector4 velocity,
        IReadOnlyList<OosDockingFlightPoint> points,
        OosLinearFlightParameters parameters,
        double now = 0,
        OosDockingCompletionFlags? completionFlags = null,
        OosDockingParameterResolver? parameterResolver = null,
        OosNavigationModeRequestGate? modeGate = null,
        OosDockingModeRequestSink? modeRequestSink = null,
        float initialFactor = 1,
        bool ordinaryWorldVelocity = true)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(parameters);
        if (!ordinaryWorldVelocity)
        {
            throw new NotSupportedException(
                "Only the ordinary world-velocity MoveDocking branch is supported.");
        }
        if (points.Count == 0)
            throw new ArgumentException("At least one FlightPoint is required.", nameof(points));

        Points = points.ToArray();
        for (var index = 0; index < Points.Count; index++)
        {
            var radius = Points[index].Radius;
            if (!float.IsFinite(radius))
                throw new ArgumentOutOfRangeException(nameof(points), $"FlightPoint {index} radius must be finite.");
            if (radius < 0)
            {
                throw new NotSupportedException(
                    "Negative FlightPoint radius uses a native fallback that is not supported.");
            }
        }

        Flags = completionFlags ?? new OosDockingCompletionFlags();
        if (Flags.SuppressQueueAdvancement)
        {
            throw new NotSupportedException(
                "context+F7A suppresses native E88160 queue advancement and is not supported.");
        }

        _baseParameters = parameters;
        _parameterResolver = parameterResolver;
        _modeGate = modeGate;
        _modeRequestSink = modeRequestSink;
        _flightController = new(
            pose,
            velocity,
            Points[0].Target,
            PointParameters(0),
            now,
            initialFactor);
    }

    public IReadOnlyList<OosDockingFlightPoint> Points { get; }
    public OosDockingCompletionFlags Flags { get; }
    public int PointIndex { get; private set; }
    public OosDockingFlightPoint CurrentPoint => Points[PointIndex];
    public OosLinearFlightController FlightController => _flightController;
    public IReadOnlyList<OosDockingEvent> Events => _events;
    public bool LandingReady { get; private set; }
    public double? LandingReadySourceTime { get; private set; }
    public OosDockingStepResult? LastStep => _lastStep;

    public OosDockingStepResult Step(
        double deltaSeconds,
        OosLinearFlightParameters? parameters = null)
    {
        if (LandingReady)
            return _lastStep!;

        var resolved = parameters;
        if (_parameterResolver is not null)
        {
            var derived = _parameterResolver(this, PointIndex, CurrentPoint, deltaSeconds);
            if (derived is not null)
                resolved = derived;
        }

        var activeIndex = PointIndex;
        var activeCount = Points.Count - activeIndex;
        var flight = _flightController.Step(
            deltaSeconds,
            NormalizeParameters(resolved ?? _flightController.Parameters, activeIndex));
        var segmentPopped = false;
        float? remainingChord = null;
        OosDockingEvent? primaryEvent = null;

        if (activeIndex == Points.Count - 1)
        {
            remainingChord = MathF.Max(0, (float)((float)(1d - flight.U) * _flightController.Length));
            if (remainingChord.Value <= Points[activeIndex].Radius)
            {
                LandingReady = true;
                LandingReadySourceTime = flight.Time;
                primaryEvent = new OosDockingLandingReadyEvent(
                    flight.Time,
                    activeIndex,
                    flight.U,
                    _flightController.Length,
                    remainingChord.Value,
                    Points[activeIndex].Radius);
                _events.Add(primaryEvent);
            }
        }
        else if (IntermediatePointComplete(flight))
        {
            PointIndex++;
            segmentPopped = true;
            primaryEvent = new OosDockingPointPopReplanEvent(
                flight.Time,
                activeIndex,
                PointIndex,
                flight.U);
            _events.Add(primaryEvent);
            _flightController.Replan(Points[PointIndex].Target, PointParameters(PointIndex));
        }

        OosNavigationModeDecision? modeDecision = null;
        IReadOnlyList<OosNavigationModeRequest> modeRequests = Array.Empty<OosNavigationModeRequest>();
        if (_modeGate is not null && !LandingReady)
        {
            var point = CurrentPoint;
            // Replan 保留 source error；显式使用本 tick row 也锁定 E8F250 的更新后顺序。
            modeDecision = _modeGate.Evaluate(flight.Time, flight.OrientationError, point.Boost, point.Travel);
            modeRequests = modeDecision.Requests;
            foreach (var request in modeRequests)
            {
                var requestEvent = new OosDockingModeRequestEvent(
                    flight.Time,
                    PointIndex,
                    request);
                _events.Add(requestEvent);
                _modeRequestSink?.Invoke(this, requestEvent);
            }
        }

        _lastStep = new(
            flight,
            activeIndex,
            activeCount,
            segmentPopped,
            LandingReady,
            remainingChord,
            primaryEvent,
            modeDecision,
            modeRequests);
        return _lastStep;
    }

    public OosDockingRunResult Run(
        double deltaSeconds = 0.05,
        double maximumSeconds = 10_000,
        double samplePeriod = 1)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        if (!double.IsFinite(maximumSeconds) || maximumSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumSeconds));
        if (!double.IsFinite(samplePeriod) || samplePeriod <= 0)
            throw new ArgumentOutOfRangeException(nameof(samplePeriod));

        var samples = new List<OosDockingStepResult>();
        var nextSample = _flightController.Now;
        var stop = _flightController.Now + maximumSeconds;
        if (!double.IsFinite(stop))
            throw new ArgumentOutOfRangeException(nameof(maximumSeconds));
        while (_flightController.Now < stop && !LandingReady)
        {
            var row = Step(Math.Min(deltaSeconds, stop - _flightController.Now));
            if (_flightController.Now >= nextSample || row.SegmentPopped || row.LandingReady)
            {
                samples.Add(row);
                nextSample = _flightController.Now + samplePeriod;
            }
        }

        return new(
            LandingReady,
            LandingReadySourceTime,
            _flightController.Now,
            PointIndex,
            _events.ToArray(),
            samples);
    }

    private bool IntermediatePointComplete(OosLinearFlightStepResult flight)
    {
        if (flight.U < 1)
            return false;
        if (!Flags.RequireFinalOrientation)
            return true;
        return flight.FcmCompletion && flight.OrientationError < OrientationEpsilon;
    }

    private OosLinearFlightParameters PointParameters(int index) =>
        NormalizeParameters(Points[index].Parameters ?? _baseParameters, index);

    private OosLinearFlightParameters NormalizeParameters(
        OosLinearFlightParameters parameters,
        int index) =>
        parameters with
        {
            RemainingPointCount = Points.Count - index,
            RequireOrientation = Flags.RequireFinalOrientation,
            SuppressCompletion = false
        };
}
