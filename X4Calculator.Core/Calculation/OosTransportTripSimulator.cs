using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

public sealed record OosTransportPreparedBerth(string Id, Vec3 DockOrigin, Vec3 StationDockPosition,
    OosTransportTwoPointTemplate Template, float PointRadius, double LandingSeconds,
    OosTransportPhaseDuration Departure, OosTransportPhaseDuration Unloading);
public sealed record OosTransportPreparedEndpoint(string StationId, string SectorId, string ZoneId,
    X4RigidTransform Transform, X4AxisAlignedBounds Bounds, OosTransportZoneGeometryInput Zone,
    IReadOnlyList<OosTransportPreparedBerth> Berths);
public sealed record OosTransportTripScenario(OosTransportDynamicsScenario Dynamics,
    ulong InitialBerthSeed, ulong GenericTargetSeed,
    double DepartureWaitFraction = .5, double DepartureDispatchSeconds = .5,
    double ZonePostWaitSeconds = .525, double ZoneEntryDispatchSeconds = 0,
    double HandoffLagSeconds = .5, double SelectionToPreparationSeconds = .1,
    double TradeDispatchSeconds = 0, double StepSeconds = .05, double MaximumSeconds = 3600,
    bool NoOtherConstraints = false, bool NoBerthQueue = false,
    bool NoOtherZoneCandidates = false, bool NoNavigationTransitionWait = false);
public sealed record OosTransportDestinationScenario(
    ulong InitialBerthSeed,
    ulong GenericTargetSeed,
    double ZonePostWaitSeconds,
    double ZoneEntryDispatchSeconds,
    double HandoffLagSeconds,
    double SelectionToPreparationSeconds,
    double TradeDispatchSeconds,
    double StepSeconds,
    double MaximumSeconds,
    bool NoOtherConstraints,
    bool NoBerthQueue,
    bool NoOtherZoneCandidates,
    bool NoNavigationTransitionWait,
    bool OrdinaryDestinationWorkerResolutionConfirmed);
public sealed record OosTransportTripPhase(string Name, double StartSeconds, double EndSeconds);
public sealed record OosTransportTripSimulation(string SourceBerthId, string InitialTargetBerthId,
    string SelectedTargetBerthId, double TotalSeconds, IReadOnlyList<OosTransportTripPhase> Phases,
    Vec3 EstimatedComponentPosition, int RefreshCount);

/// <summary>源站装货完成到目的站卸货完成的条件单程；跨区组件位置使用已声明的 zone-entry 近似。</summary>
public static class OosTransportTripSimulator
{
    public static OosTransportTripSimulation Simulate(OosTransportShipProfile profile,
        OosTransportPreparedEndpoint source, OosTransportPreparedEndpoint target,
        string sourceBerthId, OosTransportTripScenario scenario, CancellationToken cancellationToken = default)
    {
        if (!scenario.NoOtherConstraints || !scenario.NoBerthQueue || !scenario.NoOtherZoneCandidates ||
            !scenario.NoNavigationTransitionWait)
            throw new NotSupportedException("Unresolved obstruction, berth queue, zone priority or Navigation transition wait.");
        if (source.SectorId != target.SectorId || source.ZoneId == target.ZoneId)
            throw new NotSupportedException("Two-stage model requires distinct zones in the same sector.");
        if (source.Berths.Count == 0 || target.Berths.Count == 0) throw new NotSupportedException("Eligible berths required.");
        Validate(scenario);
        var src = source.Berths.Single(x => x.Id == sourceBerthId);
        var departure = src.Departure;
        if (departure.Status == OosTransportPhaseStatus.Unknown || departure.MinimumSeconds is null || departure.MaximumSeconds is null)
            throw new NotSupportedException("Source departure contract is unresolved.");
        var now = departure.MinimumSeconds.Value + (departure.MaximumSeconds.Value-departure.MinimumSeconds.Value)*scenario.DepartureWaitFraction
            + scenario.DepartureDispatchSeconds;
        var phases = new List<OosTransportTripPhase> { new("sourceDeparture", 0, now) };
        var start = Pose(src.Template.SectorOrientation, src.Template.FinalSectorPosition);
        var initialDraw = X4NativeRandom.DrawIndex(scenario.InitialBerthSeed, (ulong)target.Berths.Count);
        var initial = target.Berths[(int)initialDraw.Index];
        var shipBounds = profile.Geometry.Bounds ?? throw new NotSupportedException("Ship bounds unavailable.");
        var sizes = OosTransportTerminalPhaseCalculator.CalculateSourceBoundingSizes(shipBounds.Center, shipBounds.HalfExtents);
        var center = target.Transform.TransformPoint(target.Bounds.Center);
        var approach = OosTransportArrivalGeometry.SafePoint(initial.DockOrigin, center, target.Bounds.HalfExtents,
            target.Transform.Rotation.Transform(OosTransportArrivalGeometry.DockQuadrant(initial.StationDockPosition)),
            2*sizes.SafeSize, target.Transform.Rotation, true);
        var targetPose = LookAt(approach.Position, Sub(target.Transform.Position, approach.Position));
        if (Inside(target, Position(start))) throw new NotSupportedException("Overlapping initial zone membership.");
        var generic = OosTransportArrivalGeometry.GenerateGenericTarget(initial.DockOrigin, center, target.Bounds.HalfExtents,
            sizes.Size, sizes.SafeSize, scenario.GenericTargetSeed, target.Transform.Rotation, true, true);
        if (!Inside(target, generic.RandomizedBase)) throw new NotSupportedException("Randomized base leaves destination zone.");
        var longPose = LookAt(generic.Position, Sub(generic.Position, Position(start)));
        var runtime = new OosTransportDynamics(profile, scenario.Dynamics, now);
        var flight = runtime.CreateStationaryFlight(start, longPose, now);
        return ContinueToDestination(profile, source, target, src.Id, initial, generic,
            DestinationScenario(scenario), runtime, flight, phases, now,
            replanAtStart: false, checkSourceZoneOverlap: true, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// 从已经连续建立的普通 Linear/Dynamics 状态接入目的站。调用方负责证明当前状态和
    /// generic target 属于同一 reference space；本方法不会重建引擎、速度、factor 或刷新时钟。
    /// </summary>
    internal static OosTransportTripSimulation ContinueToDestination(
        OosTransportShipProfile profile,
        OosTransportPreparedEndpoint source,
        OosTransportPreparedEndpoint target,
        string sourceBerthId,
        OosTransportPreparedBerth initial,
        OosGenericTarget generic,
        OosTransportDestinationScenario scenario,
        OosTransportDynamics runtime,
        OosLinearFlightController flight,
        List<OosTransportTripPhase> phases,
        double phaseStart,
        bool replanAtStart,
        bool checkSourceZoneOverlap,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(generic);
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(phases);
        Validate(scenario);
        var now = phaseStart;
        if (Math.Abs(flight.Now - now) > 1e-9)
            throw new ArgumentException("Destination continuation time must match the continuous flight clock.", nameof(phaseStart));
        var shipBounds = profile.Geometry.Bounds ?? throw new NotSupportedException("Ship bounds unavailable.");
        var sizes = OosTransportTerminalPhaseCalculator.CalculateSourceBoundingSizes(shipBounds.Center, shipBounds.HalfExtents);
        var center = target.Transform.TransformPoint(target.Bounds.Center);
        var approach = OosTransportArrivalGeometry.SafePoint(initial.DockOrigin, center, target.Bounds.HalfExtents,
            target.Transform.Rotation.Transform(OosTransportArrivalGeometry.DockQuadrant(initial.StationDockPosition)),
            2*sizes.SafeSize, target.Transform.Rotation, true);
        var targetPose = LookAt(approach.Position, Sub(target.Transform.Position, approach.Position));
        if (Inside(target, Position(flight.Pose))) throw new NotSupportedException("Overlapping initial zone membership.");
        if (!Inside(target, generic.RandomizedBase)) throw new NotSupportedException("Randomized base leaves destination zone.");
        var longPose = LookAt(generic.Position, Sub(generic.Position, Position(flight.Pose)));
        if (replanAtStart)
            flight.Replan(longPose, runtime.Parameters);
        void Step(double dt, bool travel)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (flight.Now >= scenario.MaximumSeconds) throw new NotSupportedException("Flight integration timeout.");
            runtime.GenericStep(flight, dt, travel);
        }
        void AdvanceTo(double until, bool travel)
        {
            while (flight.Now < until-1e-9) Step(Math.Min(scenario.StepSeconds, until-flight.Now), travel);
        }
        while (!Inside(target, Position(flight.Pose)))
        {
            Step(scenario.StepSeconds, true);
            if (flight.Completed) throw new NotSupportedException("First controller completed before zone entry.");
        }
        if (checkSourceZoneOverlap && Inside(source, Position(flight.Pose)))
            throw new NotSupportedException("Overlapping endpoint zones.");
        AdvanceTo(flight.Now+scenario.ZoneEntryDispatchSeconds, true);
        var componentPosition = Position(flight.Pose);
        flight.Replan(longPose);
        runtime.Clock.InvalidateAt(flight.Now);
        flight.Replan(longPose, runtime.Resolve(flight));
        AdvanceTo(flight.Now+scenario.ZonePostWaitSeconds, true);
        phases.Add(new("flightToTargetZone", now, flight.Now)); now = flight.Now;
        flight.Replan(targetPose);
        var travel = runtime.Engine.TravelActive || Position(flight.Pose).DistanceTo(Position(targetPose))/Math.Max(1, profile.BasePhysics.ForwardSpeed) > 60;
        var originDistance = Position(flight.Pose).DistanceTo(target.Transform.Position);
        var targetDistance = Position(targetPose).DistanceTo(target.Transform.Position);
        var stationSizes = OosTransportTerminalPhaseCalculator.CalculateSourceBoundingSizes(target.Bounds.Center, target.Bounds.HalfExtents);
        var submit = originDistance + sizes.Size/2 + stationSizes.Size/2 > 10000 && originDistance > targetDistance;
        if (submit)
        {
            do { Step(scenario.StepSeconds, travel); }
            while (!OosTransportArrivalGeometry.IsLastWaypointApproached(flight.U, flight.Length, -1,
                flight.Parameters.Proximity, 1, true));
            AdvanceTo(flight.Now+scenario.HandoffLagSeconds, travel);
        }
        else if (scenario.HandoffLagSeconds != 0)
            throw new NotSupportedException("Stationary skipped-generic scheduler handoff is not implemented.");
        var selected = SelectNearest(target.Berths, componentPosition);
        if (submit) AdvanceTo(flight.Now+scenario.SelectionToPreparationSeconds, travel);
        else if (scenario.SelectionToPreparationSeconds != 0)
            throw new NotSupportedException("Stationary preparation delay is not implemented.");
        var points = SelectPoints(selected, componentPosition);
        phases.Add(new("flightToDockHandoff", now, flight.Now)); now = flight.Now;
        var docking = new OosDockingController(flight.Pose, flight.Velocity, points, runtime.Parameters, flight.Now,
            parameterResolver: (controller, _, _, _) => runtime.Resolve(controller.FlightController),
            modeGate: runtime.Gate, modeRequestSink: (controller, ev) => runtime.Request(controller.FlightController, ev.Request),
            initialFactor: flight.Factor);
        while (!docking.LandingReady)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (docking.FlightController.Now >= scenario.MaximumSeconds) throw new NotSupportedException("Docking timeout.");
            docking.Step(scenario.StepSeconds);
        }
        var ready = docking.LandingReadySourceTime!.Value;
        phases.Add(new("dockFlight", now, ready));
        var landingEnd = ready + selected.LandingSeconds;
        phases.Add(new("landing", ready, landingEnd));
        if (selected.Unloading.ReferenceSeconds is not double unload || selected.Unloading.Status == OosTransportPhaseStatus.Unknown)
            throw new NotSupportedException("Destination unloading contract is unresolved.");
        var end = landingEnd + unload + scenario.TradeDispatchSeconds;
        phases.Add(new("destinationUnload", landingEnd, end));
        return new(sourceBerthId, initial.Id, selected.Id, end, phases, componentPosition, runtime.RefreshCount);
    }

    internal static (OosTransportPreparedBerth Initial, OosGenericTarget Generic) PrepareDestinationTargets(
        OosTransportShipProfile profile,
        OosTransportPreparedEndpoint target,
        ulong initialBerthSeed,
        ulong genericTargetSeed)
    {
        if (target.Berths.Count == 0) throw new NotSupportedException("Eligible berths required.");
        var initialDraw = X4NativeRandom.DrawIndex(initialBerthSeed, (ulong)target.Berths.Count);
        var initial = target.Berths[(int)initialDraw.Index];
        var shipBounds = profile.Geometry.Bounds ?? throw new NotSupportedException("Ship bounds unavailable.");
        var sizes = OosTransportTerminalPhaseCalculator.CalculateSourceBoundingSizes(shipBounds.Center, shipBounds.HalfExtents);
        var center = target.Transform.TransformPoint(target.Bounds.Center);
        var generic = OosTransportArrivalGeometry.GenerateGenericTarget(initial.DockOrigin, center, target.Bounds.HalfExtents,
            sizes.Size, sizes.SafeSize, genericTargetSeed, target.Transform.Rotation, true, true);
        return (initial, generic);
    }

    private static OosTransportDestinationScenario DestinationScenario(OosTransportTripScenario scenario) => new(
        scenario.InitialBerthSeed,
        scenario.GenericTargetSeed,
        scenario.ZonePostWaitSeconds,
        scenario.ZoneEntryDispatchSeconds,
        scenario.HandoffLagSeconds,
        scenario.SelectionToPreparationSeconds,
        scenario.TradeDispatchSeconds,
        scenario.StepSeconds,
        scenario.MaximumSeconds,
        scenario.NoOtherConstraints,
        scenario.NoBerthQueue,
        scenario.NoOtherZoneCandidates,
        scenario.NoNavigationTransitionWait,
        OrdinaryDestinationWorkerResolutionConfirmed: true);

    private static OosTransportPreparedBerth SelectNearest(IReadOnlyList<OosTransportPreparedBerth> candidates, Vec3 position)
    {
        var ordered = candidates.OrderBy(c => Score(position, c.DockOrigin)).ToArray();
        if (ordered.Length > 1 && Score(position, ordered[0].DockOrigin) == Score(position, ordered[1].DockOrigin))
            throw new NotSupportedException("Nearest berth tie requires native enumeration priority.");
        return ordered[0];
    }
    private static IReadOnlyList<OosDockingFlightPoint> SelectPoints(OosTransportPreparedBerth berth, Vec3 position)
    {
        var t = berth.Template;
        var gap = t.FirstSectorPosition.DistanceTo(t.FinalSectorPosition);
        if (gap <= berth.PointRadius) throw new NotSupportedException("Nonstandard final point submission.");
        var radius = gap/2;
        var sum = t.FirstSectorPosition+t.FinalSectorPosition;
        var center = new Vec3(sum.X*.5, sum.Y*.5, sum.Z*.5);
        var local = t.SectorOrientation.Transpose().Transform(Sub(position, center));
        var skip = Math.Abs(local.X) < 10+radius && Math.Abs(local.Y) < 10+radius && Math.Abs(local.Z) < gap/2+10+radius;
        var last = new OosDockingFlightPoint(Pose(t.SectorOrientation, t.FinalSectorPosition), berth.PointRadius, false, false);
        if (skip) return [last];
        var enabled = Score(position, berth.DockOrigin) > 100000000f;
        return [new(Pose(t.SectorOrientation, t.FirstSectorPosition), berth.PointRadius, enabled, enabled), last];
    }
    private static float Score(Vec3 a, Vec3 b)
    {
        var x = (float)a.X-(float)b.X; var y = (float)a.Y-(float)b.Y; var z = (float)a.Z-(float)b.Z;
        return x*x + (y*y + z*z);
    }
    private static bool Inside(OosTransportPreparedEndpoint endpoint, Vec3 position) =>
        OosTransportZoneGeometryCalculator.Evaluate(endpoint.Zone, position).Membership switch
        {
            OosTransportZoneMembership.Inside => true, OosTransportZoneMembership.Outside => false,
            _ => throw new NotSupportedException("Zone geometry is unresolved.")
        };
    private static OosLinearVector4 V(Vec3 v) => new((float)v.X, (float)v.Y, (float)v.Z);
    private static Vec3 Position(OosLinearPose pose) => new(pose.Position.X, pose.Position.Y, pose.Position.Z);
    public static OosLinearPose Pose(X4RotationMatrix rotation, Vec3 position)
    {
        var columns = rotation.Transpose();
        return new(V(position), V(columns.Row0), V(columns.Row1), V(columns.Row2));
    }
    private static Vec3 Sub(Vec3 a, Vec3 b) => new(a.X-b.X, a.Y-b.Y, a.Z-b.Z);
    private static OosLinearPose LookAt(Vec3 position, Vec3 direction) =>
        OosLinearFlightController.CreateLookPose(V(position), V(direction));
    private static void Validate(OosTransportTripScenario s)
    {
        double[] values = [s.DepartureDispatchSeconds, s.ZonePostWaitSeconds, s.ZoneEntryDispatchSeconds,
            s.HandoffLagSeconds, s.SelectionToPreparationSeconds, s.TradeDispatchSeconds];
        if (values.Any(v => !double.IsFinite(v) || v < 0) || !double.IsFinite(s.DepartureWaitFraction) ||
            s.DepartureWaitFraction < 0 || s.DepartureWaitFraction > 1 || !double.IsFinite(s.StepSeconds) ||
            s.StepSeconds <= 0 || s.StepSeconds > .1 || !double.IsFinite(s.MaximumSeconds) || s.MaximumSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(s));
    }


    private static void Validate(OosTransportDestinationScenario s)
    {
        double[] values = [s.ZonePostWaitSeconds, s.ZoneEntryDispatchSeconds,
            s.HandoffLagSeconds, s.SelectionToPreparationSeconds, s.TradeDispatchSeconds];
        if (values.Any(v => !double.IsFinite(v) || v < 0) || !double.IsFinite(s.StepSeconds) ||
            s.StepSeconds <= 0 || s.StepSeconds > .1 || !double.IsFinite(s.MaximumSeconds) || s.MaximumSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(s));
        if (!s.OrdinaryDestinationWorkerResolutionConfirmed)
            throw new NotSupportedException("Destination worker engine/event resolution branch is unresolved.");
    }
}
