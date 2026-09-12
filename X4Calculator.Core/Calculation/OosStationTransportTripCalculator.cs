namespace X4Calculator.Core.Calculation;

public sealed record OosTransportConditionalMean(string SourceBerthId, OosTransportRefreshRegime Refresh,
    int SampleCount, double MeanSeconds, double SampleMinimumSeconds, double SampleMaximumSeconds,
    double StandardErrorSeconds);
public sealed record OosTransportMeanPrediction(OosTransportPhaseStatus Status,
    IReadOnlyList<OosTransportConditionalMean> Conditions, IReadOnlyList<string> UnknownReasons)
{
    public double? MinimumConditionalMeanSeconds => Status != OosTransportPhaseStatus.Conditional || Conditions.Count == 0 ? null : Conditions.Min(x => x.MeanSeconds);
    public double? MaximumConditionalMeanSeconds => Status != OosTransportPhaseStatus.Conditional || Conditions.Count == 0 ? null : Conditions.Max(x => x.MeanSeconds);
    public const string Boundary = "source loading complete -> destination unloading complete";
    public const string Scope = "ordinary OOS; same-sector distinct default zones; no queue/other constraints; station-holder single wave; estimated zone-entry component position; conditional source berth, not steady-state source weights";
}
public sealed record OosCrossGateTripSample(string SourceBerthId, OosTransportRefreshRegime Refresh,
    OosCrossGateTripResult Result);
public sealed record OosCrossGateConditionalMean(string SourceBerthId, OosTransportRefreshRegime Refresh,
    OosCrossGateScenarioBasis Basis, int SampleCount, double MeanSeconds, double SampleMinimumSeconds,
    double SampleMaximumSeconds, double StandardErrorSeconds);
public sealed record OosCrossGateMeanResult(OosTransportPhaseStatus Status,
    OosCrossGateScenarioBasis? Basis, IReadOnlyList<OosCrossGateConditionalMean> Conditions,
    IReadOnlyList<string> UnknownReasons)
{
    public bool IsPredeclaredPrediction => Status == OosTransportPhaseStatus.Conditional &&
        Basis == OosCrossGateScenarioBasis.PredeclaredScenario;
    public const string Boundary = "source loading complete -> destination unloading complete";
    public const string Scope = "ordinary OOS cross-gate conditional event execution; explicit continuous state and event order; no implicit condition weights";
}

/// <summary>在声明条件下抽样，不给不同源泊位或刷新情形分配未经证明的稳态概率。</summary>
public static class OosStationTransportTripCalculator
{
    public static OosTransportMeanPrediction Calculate(OosTransportShipProfile profile,
        OosTransportPreparedEndpoint source, OosTransportPreparedEndpoint target,
        IReadOnlyList<string> sourceBerthIds, IReadOnlyList<OosTransportRefreshRegime> regimes,
        OosTransportTripScenario declaredScenario, int samplesPerCondition = 32, ulong seed = 20260908,
        CancellationToken cancellationToken = default, Action<int, int>? progress = null)
    {
        if (samplesPerCondition < 2 || samplesPerCondition > 4096) throw new ArgumentOutOfRangeException(nameof(samplesPerCondition));
        if (sourceBerthIds.Count == 0 || regimes.Count == 0)
            return new(OosTransportPhaseStatus.Unknown, [], ["Source berth and refresh conditions are required."]);
        var rows = new List<OosTransportConditionalMean>(); var reasons = new List<string>();
        var completed = 0; var total = checked(sourceBerthIds.Count*regimes.Count);
        foreach (var berth in sourceBerthIds)
        foreach (var regime in regimes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = seed;
            ulong Next() => state = X4NativeRandom.NextState(state);
            var samples = new List<double>();
            try
            {
                for (var sample = 0; sample < samplesPerCondition; sample++)
                {
                    var initial = Next(); var generic = Next(); var refresh = Next();
                    var waitDraw = X4NativeRandom.DrawFloat(Next(), .45f);
                    var scenario = declaredScenario with { InitialBerthSeed = initial, GenericTargetSeed = generic,
                        ZonePostWaitSeconds = .3 + waitDraw.Value,
                        Dynamics = declaredScenario.Dynamics with { Refresh = regime, RefreshRandomState = refresh } };
                    samples.Add(OosTransportTripSimulator.Simulate(profile, source, target, berth, scenario, cancellationToken).TotalSeconds);
                }
                var mean = samples.Average();
                var variance = samples.Sum(v => (v-mean)*(v-mean))/(samples.Count-1);
                rows.Add(new(berth, regime, samples.Count, mean, samples.Min(), samples.Max(), Math.Sqrt(variance/samples.Count)));
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
            {
                // 不得丢弃未完成样本，以免在未说明的情况下使平均耗时偏低。
                reasons.Add($"{berth}/{regime}: {ex.Message}");
            }
            progress?.Invoke(++completed, total);
        }
        return new(reasons.Count == 0 ? OosTransportPhaseStatus.Conditional : OosTransportPhaseStatus.Unknown, rows, reasons);
    }

    /// <summary>
    /// 聚合已经独立执行的跨门条件样本。任一 Unknown 不会被丢弃；重放与事前情景也不会混成同一个均值。
    /// </summary>
    public static OosCrossGateMeanResult CalculateCrossGate(IReadOnlyList<OosCrossGateTripSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            return new(OosTransportPhaseStatus.Unknown, null, [], ["Cross-gate condition samples are required."]);
        if (samples.Any(x => x is null || x.Result is null))
            throw new ArgumentException("Cross-gate samples and results cannot be null.", nameof(samples));

        var bases = samples.Select(x => x.Result.Basis).Distinct().ToArray();
        if (bases.Length != 1)
            return new(OosTransportPhaseStatus.Unknown, null, [],
                ["Conditional event replay and predeclared scenarios cannot be mixed."]);

        var reasons = new List<string>();
        foreach (var sample in samples.Where(x =>
                     x.Result.Status != OosTransportPhaseStatus.Conditional || x.Result.Simulation is null))
        {
            if (sample.Result.UnknownReasons.Count == 0)
                reasons.Add($"{sample.SourceBerthId}/{sample.Refresh}: cross-gate sample is incomplete.");
            else
                reasons.AddRange(sample.Result.UnknownReasons.Select(reason =>
                    $"{sample.SourceBerthId}/{sample.Refresh}: {reason}"));
        }
        var rows = new List<OosCrossGateConditionalMean>();
        foreach (var group in samples
                     .Where(x => x.Result.Status == OosTransportPhaseStatus.Conditional && x.Result.Simulation is not null)
                     .GroupBy(x => (x.SourceBerthId, x.Refresh, x.Result.Basis)))
        {
            var values = group.Select(x => x.Result.Simulation!.Trip.TotalSeconds).ToArray();
            if (values.Length < 2)
            {
                reasons.Add($"{group.Key.SourceBerthId}/{group.Key.Refresh}: at least two complete samples are required.");
                continue;
            }
            var mean = values.Average();
            var variance = values.Sum(value => (value-mean)*(value-mean))/(values.Length-1);
            rows.Add(new(group.Key.SourceBerthId, group.Key.Refresh, group.Key.Basis, values.Length,
                mean, values.Min(), values.Max(), Math.Sqrt(variance/values.Length)));
        }
        return new(reasons.Count == 0 ? OosTransportPhaseStatus.Conditional : OosTransportPhaseStatus.Unknown,
            bases[0], rows, reasons);
    }
}
