using System.Globalization;
using System.Xml.Linq;
using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

/// <summary>
/// 端点卸货的已确认 carrier 合同。它只止于 <c>execute_trade</c> 完成，
/// 不能推导源端的无人机回收、Ready、detach 或离港等待。
/// </summary>
public abstract record OosTransportEndpointUnloadingContract;

/// <summary>
/// 单一显式 holder 提供全部同质 cargo carrier 的多波卸货合同。
/// <paramref name="ReservedCarrierCount"/> 必须由调用方明确传入；<c>null</c> 表示已知未闭合，结果保持 Unknown，
/// 绝不按零 reservation 补齐。
/// </summary>
public sealed record OosTransportHomogeneousSingleHolderUnloadingContract(
    long CarrierCapacityVolume,
    long HolderTotal,
    long? ReservedCarrierCount,
    OosTransportCargoCarrierHolderRole CarrierHolderRole,
    bool OrdinaryOosGeometry,
    bool EndpointsAreStationary,
    bool CarriersShareSelectedHolder,
    bool CarriersAreHomogeneous,
    bool CarrierStartsAreSynchronized,
    bool NoCarrierCompetition) : OosTransportEndpointUnloadingContract;

/// <summary>
/// 货源和货物目的两个显式 holder 的同质初始单波卸货合同。
/// 它不接受也不外推混供多波；任一 reservation 未闭合时结果保持 Unknown。
/// </summary>
public sealed record OosTransportHomogeneousMixedHolderSingleWaveUnloadingContract(
    long CarrierCapacityVolume,
    long SourceHolderTotal,
    long? SourceReservedCarrierCount,
    long DestinationHolderTotal,
    long? DestinationReservedCarrierCount,
    bool OrdinaryOosGeometry,
    bool EndpointsAreStationary,
    bool CarriersAreHomogeneous,
    bool CarrierStartsAreSynchronized,
    bool NoCarrierCompetition) : OosTransportEndpointUnloadingContract;

/// <summary>需由调用方明确提供的泊位资格和 station-holder 单波条件；不从总库存推断可用量。</summary>
public sealed record OosTransportEndpointConditions(IReadOnlyList<string> EligibleBerthIds,
    long CarrierCapacityVolume, long? ReservedCarrierUpperBound, bool StationProvidesAllCarriers,
    string? DepartureOrder, OosTransportZoneGeometryInput? VerifiedZoneGeometry = null,
    OosTransportEndpointUnloadingContract? UnloadingContract = null);

public sealed class OosTransportEndpointPreparer(GameDataDB gameData)
{
    public OosTransportPreparedEndpoint Prepare(StationTransportTopology topology,
        OosTransportShipProfile profile, OosTransportEndpointConditions conditions)
    {
        var topologyReasons = conditions.UnloadingContract is not null && !conditions.StationProvidesAllCarriers
            ? topology.UnsupportedReasons.Except(topology.CargoDroneInventoryUnsupportedReasons, StringComparer.Ordinal).ToArray()
            : topology.UnsupportedReasons.ToArray();
        if (topology.StationSectorTransform is not X4RigidTransform transform || topology.StationLocalBounds is not { } bounds ||
            string.IsNullOrEmpty(topology.ZoneSaveId) || topologyReasons.Length > 0)
            throw new NotSupportedException("Station topology is incomplete: " + string.Join("; ", topologyReasons));
        var zone = conditions.VerifiedZoneGeometry ?? topology.ZoneGeometry ??
            throw new NotSupportedException("Verified zone geometry is required.");
        if (!topology.StorageTypes.Contains(profile.Storage.Transport, StringComparer.Ordinal))
            throw new NotSupportedException("Station storage is incompatible with cargo.");
        if (conditions.EligibleBerthIds.Count == 0 || conditions.EligibleBerthIds.Distinct().Count() != conditions.EligibleBerthIds.Count)
            throw new NotSupportedException("Unique same-priority eligible berth enumeration required.");
        var installedCarrierCount = topology.InstalledCargoDroneCount;
        var hasStationProvidedLoading = installedCarrierCount.HasValue && conditions.StationProvidesAllCarriers;
        if (!hasStationProvidedLoading && conditions.UnloadingContract is null)
            throw new NotSupportedException("Station-provided cargo carriers are unresolved.");
        var volume = profile.Storage.VolumePerUnit;
        if (volume != MathF.Truncate(volume)) throw new NotSupportedException("Native integer ware volume required.");
        var stationLoadingCapacity = hasStationProvidedLoading
            ? OosTransportTerminalPhaseCalculator.CalculateSingleWaveCapacity(new(
                [profile.Storage.CargoUnits], (long)volume, conditions.CarrierCapacityVolume,
                installedCarrierCount!.Value, conditions.ReservedCarrierUpperBound))
            : null;
        var geometry = profile.Geometry.Bounds ?? throw new NotSupportedException("Ship bounds missing.");
        var sizes = OosTransportTerminalPhaseCalculator.CalculateSourceBoundingSizes(geometry.Center, geometry.HalfExtents);
        var parameters = gameData.GetEffectiveXml("libraries/parameters.xml");
        var dock = parameters.Root!.Element("docking")!.Elements("dock")
            .Single(x => ((string?)x.Attribute("tags") ?? "").Split(' ').Contains("dock_xl"));
        var offset = dock.Element("maxoffsets")!.Elements("maxoffset")
            .Single(x => (string?)x.Attribute("software") == profile.DockSoftwareId);
        var radius = Math.Abs(Number(offset.Element("position")!, "z"));
        var landing = dock.Element("landing")!.Elements("entry").Single(x => (string?)x.Attribute("traffic") == "normal");
        var landingSeconds = Number(landing, "duration");
        var takeoff = dock.Element("takeoff")?.Elements("entry").Where(x => (string?)x.Attribute("traffic") == "normal").ToArray() ?? [];
        if (takeoff.Length > 1) throw new NotSupportedException("Ambiguous takeoff profile.");
        var sourceProfile = new OosTransportDockingSourceProfile("effective-normal-capital", landingSeconds,
            takeoff.Length == 0 ? 0 : Number(takeoff[0], "duration"));
        var result = new List<OosTransportPreparedBerth>();
        // 保留导入时的原生枚举顺序，与研究中的候选筛选规则保持一致。
        foreach (var berth in topology.Berths.Where(b => conditions.EligibleBerthIds.Contains(b.SaveId)))
        {
            var template = OosTransportBerthGeometryCalculator.CreateTwoPointTemplate(berth, profile.Geometry, sizes.Radius);
            var separation = OosTransportTerminalPhaseCalculator.CalculateRawSurfaceSeparation(template.FinalStationPosition,
                bounds.Center, bounds.HalfExtents, geometry.HalfExtents);
            var departure = stationLoadingCapacity is null
                ? OosTransportPhaseDuration.Unknown(
                    "Source carrier recovery is unresolved: station-provided single-wave loading was not declared.")
                : OosTransportTerminalPhaseCalculator.CalculateDepartureWait(
                    OosTransportDepartureStartBoundary.ExecuteTradeComplete,
                    sourceProfile,
                    OosTransportDetachNetworkCondition.MatchingNetwork,
                    OosTransportTerminalPhaseCalculator.CalculateStationProvidedSingleWaveLoading(
                        separation,
                        stationLoadingCapacity,
                        ordinaryOosGeometry: true,
                        stationProvidesAllCarriers: true).RemainingRecoveryDuration).Duration;
            var columns = template.SectorOrientation.Transpose();
            var safe = OosTransportArrivalGeometry.SafePoint(template.FinalSectorPosition, transform.TransformPoint(bounds.Center),
                bounds.HalfExtents, new(-columns.Row2.X, -columns.Row2.Y, -columns.Row2.Z), sizes.SafeSize/2,
                transform.Rotation, true);
            if (conditions.DepartureOrder is null) departure = OosTransportPhaseDuration.Unknown("Departure order branch is unresolved.");
            else if (stationLoadingCapacity is not null)
            {
                var clearance = OosTransportTerminalPhaseCalculator.CalculateClearanceMovement(template.FinalSectorPosition,
                    columns.Row0, columns.Row1, columns.Row2, berth.SectorTransform.Position, safe.Position, sizes.Size,
                    conditions.DepartureOrder);
                if (clearance.Action is not (OosTransportClearanceAction.None or OosTransportClearanceAction.MoveToZeroTimeHandoff))
                    departure = OosTransportPhaseDuration.Unknown("Departure requires unsupported clearance motion: " + clearance.Action);
            }
            var unload = conditions.UnloadingContract is null
                ? stationLoadingCapacity is null
                    ? OosTransportPhaseDuration.Unknown(
                        "Station-provided single-wave unloading was not declared and no carrier unloading contract was supplied.")
                    : OosTransportTerminalPhaseCalculator.CalculateSingleWaveTransfer(
                        separation,
                        stationLoadingCapacity,
                        ordinaryOosGeometry: true).Duration
                : CalculateUnloading(
                    conditions.UnloadingContract,
                    profile.Storage.CargoUnits,
                    (long)volume,
                    separation);
            result.Add(new(berth.SaveId, berth.SectorTransform.Position, berth.StationTransform.Position,
                template, (float)radius, landingSeconds, departure, unload));
        }
        if (result.Count != conditions.EligibleBerthIds.Count) throw new NotSupportedException("An eligible berth is unavailable or unsupported.");
        return new(topology.StationSaveId, topology.SectorSaveId, topology.ZoneSaveId, transform, bounds, zone, result);
    }

    private static double Number(XElement node, string attribute)
    {
        if (!double.TryParse((string?)node.Attribute(attribute), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ||
            !double.IsFinite(result) || result < 0)
            throw new NotSupportedException($"Unresolved effective docking attribute {node.Name}/{attribute}.");
        return result;
    }

    private static OosTransportPhaseDuration CalculateUnloading(
        OosTransportEndpointUnloadingContract contract,
        long cargoUnits,
        long wareVolume,
        double rawSurfaceSeparation) =>
        contract switch
        {
            OosTransportHomogeneousSingleHolderUnloadingContract singleHolder =>
                OosTransportTerminalPhaseCalculator.CalculateHomogeneousCarrierWaveTradeComplete(new(
                    cargoUnits,
                    wareVolume,
                    singleHolder.CarrierCapacityVolume,
                    singleHolder.HolderTotal,
                    singleHolder.ReservedCarrierCount,
                    singleHolder.CarrierHolderRole,
                    rawSurfaceSeparation,
                    singleHolder.OrdinaryOosGeometry,
                    singleHolder.EndpointsAreStationary,
                    singleHolder.CarriersShareSelectedHolder,
                    singleHolder.CarriersAreHomogeneous,
                    singleHolder.CarrierStartsAreSynchronized,
                    singleHolder.NoCarrierCompetition)).TradeCompleteDuration,
            OosTransportHomogeneousMixedHolderSingleWaveUnloadingContract mixedHolder =>
                OosTransportTerminalPhaseCalculator.CalculateHomogeneousMixedHolderSingleWaveTradeComplete(new(
                    cargoUnits,
                    wareVolume,
                    mixedHolder.CarrierCapacityVolume,
                    mixedHolder.SourceHolderTotal,
                    mixedHolder.SourceReservedCarrierCount,
                    mixedHolder.DestinationHolderTotal,
                    mixedHolder.DestinationReservedCarrierCount,
                    rawSurfaceSeparation,
                    mixedHolder.OrdinaryOosGeometry,
                    mixedHolder.EndpointsAreStationary,
                    mixedHolder.CarriersAreHomogeneous,
                    mixedHolder.CarrierStartsAreSynchronized,
                    mixedHolder.NoCarrierCompetition)).TradeCompleteDuration,
            _ => throw new NotSupportedException("Unsupported endpoint carrier unloading contract.")
        };
}
