using System.Globalization;
using System.Xml.Linq;
using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

public enum OosTransportShipProfileStatus
{
    Qualified,
    Unknown
}

/// <summary>
/// 由产品配置声明的普通 OOS L 级货船。该输入不包含存档或 runtime 状态。
/// </summary>
public sealed record OosTransportShipProfileRequest(
    StationTransportShipConfiguration Configuration,
    string WareId,
    int? CargoUnits,
    int MoraleStars,
    int CargoDrones,
    string DockSoftwareId,
    bool HasAttachedCollectableAmmo);

public sealed record OosTransportDirectionalPhysics(
    float Forward,
    float Reverse,
    float Horizontal,
    float Vertical);

public sealed record OosTransportRotationalPhysics(float Pitch, float Yaw, float Roll);

public sealed record OosTransportShipPhysics(
    OosTransportDirectionalPhysics AccelerationFactors,
    OosTransportRotationalPhysics Inertia,
    OosTransportDirectionalPhysics LinearDrag,
    OosTransportRotationalPhysics RotationalDrag,
    OosTransportRotationalPhysics ThrusterThrust);

public sealed record OosTransportJpmProfile(bool Allow, float ParameterFactor);

public sealed record OosTransportAiFlightProfile(
    float StartBoostDegrees,
    float StopBoostDegrees,
    float StartTravelDegrees,
    float StopTravelDegrees,
    float SplineTurnRadius,
    float SplineRotationAccelerationFactor,
    float SplineMaximumTurnRadius);

public sealed record OosTransportEnginePhysicalProfile(
    float ForwardThrust,
    float ReverseThrust,
    float BoostAcceleration,
    float BoostCoast,
    OosEngineModeProperties ModeProperties,
    IReadOnlyList<OosSpeedCurvePoint> DecelerationCurve);

public sealed record OosTransportStorageProfile(
    string MacroId,
    string Transport,
    string WareId,
    int CargoUnits,
    float VolumePerUnit,
    float OccupiedVolume,
    float Capacity,
    int MaximumUnits,
    int CargoDrones);

/// <summary>
/// 只由最终有效 XML 与显式产品配置构成的普通、无改装 L 级货船 profile。
/// Navigation pose、引擎 mode、时钟、存档 hull/modification 等动态值由后续调用者提供。
/// </summary>
public sealed record OosTransportShipProfile(
    string ShipMacroId,
    string EngineMacroId,
    int EngineCount,
    string ThrusterMacroId,
    string DockSoftwareId,
    int PilotingStars,
    int MoraleStars,
    float BaseMass,
    float CargoDensity,
    float CargoMass,
    float CappedCargoMass,
    float MaxAdditionalMassRatio,
    float JerkForwardDecel,
    OosTransportStorageProfile Storage,
    OosTransportShipPhysics ShipPhysics,
    OosTransportJpmProfile Jpm,
    OosTransportAiFlightProfile AiFlight,
    OosTransportEnginePhysicalProfile Engine,
    X4MacroGeometryResult Geometry,
    OosNavigationBasePhysics BasePhysics,
    OosNavigationEnginePhysics EnginePhysics)
{
    public const string Scope =
        "configuration-only ordinary unmodified L ship; homogeneous intact engines; one cargo storage; five-star piloting and morale; no attached collectable ammo";
}

public sealed record OosTransportShipProfileResolution(
    OosTransportShipProfileStatus Status,
    OosTransportShipProfile? Profile,
    IReadOnlyList<string> UnsupportedReasons)
{
    public static OosTransportShipProfileResolution Qualified(OosTransportShipProfile profile) =>
        new(OosTransportShipProfileStatus.Qualified, profile, Array.Empty<string>());

    public static OosTransportShipProfileResolution Unknown(params string[] reasons) =>
        new(OosTransportShipProfileStatus.Unknown, null, reasons);
}

/// <summary>
/// 将现有站间运输船配置解析为 native OOS 连续飞行控制器所需的静态 profile。
/// 解析只读取 <see cref="GameDataDB"/> 已物化的有效 XML，不读取 save/runtime，也不估算现实密度。
/// </summary>
public sealed class OosTransportShipProfileResolver
{
    private const string SupportedDockSoftware = "software_dockmk2";
    private const double DegreesToRadians = Math.PI / 180d;

    private readonly GameDataDB _gameData;
    private readonly X4GeometryDefinitionReader _geometryReader;

    public OosTransportShipProfileResolver(GameDataDB gameData)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        _gameData = gameData;
        _geometryReader = new X4GeometryDefinitionReader(gameData);
    }

    public OosTransportShipProfileResolution Resolve(OosTransportShipProfileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Configuration);
        ArgumentNullException.ThrowIfNull(request.Configuration.Ship);
        ArgumentNullException.ThrowIfNull(request.Configuration.Engine);
        ArgumentNullException.ThrowIfNull(request.Configuration.Thruster);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WareId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DockSoftwareId);
        if (request.CargoUnits is < 0)
            throw new ArgumentOutOfRangeException(nameof(request.CargoUnits));
        if (request.CargoDrones < 0)
            throw new ArgumentOutOfRangeException(nameof(request.CargoDrones));

        var configuration = request.Configuration;
        var shipInfo = configuration.Ship;
        var engineInfo = configuration.Engine;
        var thrusterInfo = configuration.Thruster;
        var scopeReasons = new List<string>();
        if (!string.Equals(shipInfo.SizeCategory, "L", StringComparison.OrdinalIgnoreCase))
            scopeReasons.Add("only-ordinary-ship-l-supported");
        if (configuration.PilotingStars != 5 || request.MoraleStars != 5)
            scopeReasons.Add("five-star-piloting-and-morale-required");
        if (!string.Equals(request.DockSoftwareId, SupportedDockSoftware, StringComparison.Ordinal))
            scopeReasons.Add("software-dockmk2-required");
        if (request.HasAttachedCollectableAmmo)
            scopeReasons.Add("attached-collectable-ammo-unsupported");
        if (scopeReasons.Count > 0)
            return Unknown(scopeReasons);

        try
        {
            var ship = FindMacro(_gameData.GetEffectiveMacroXml(shipInfo.Id), shipInfo.Id);
            var engine = FindMacro(_gameData.GetEffectiveMacroXml(engineInfo.Id), engineInfo.Id);
            var thruster = FindMacro(_gameData.GetEffectiveMacroXml(thrusterInfo.Id), thrusterInfo.Id);
            if (!string.Equals((string?)ship.Attribute("class"), "ship_l", StringComparison.Ordinal))
                return Unknown("only-ordinary-ship-l-supported");
            if (!string.Equals((string?)engine.Attribute("class"), "engine", StringComparison.Ordinal) ||
                !string.Equals((string?)thruster.Attribute("class"), "engine", StringComparison.Ordinal))
                return Unknown("engine-or-thruster-macro-class-unsupported");

            var shipProperties = RequiredElement(ship, "properties", shipInfo.Id);
            var shipPhysics = RequiredElement(shipProperties, "physics", $"{shipInfo.Id}/properties");
            var engineProperties = RequiredElement(engine, "properties", engineInfo.Id);
            var thrusterProperties = RequiredElement(thruster, "properties", thrusterInfo.Id);

            var software = shipProperties.Element("software")?.Elements("software")
                .Where(node => string.Equals((string?)node.Attribute("ware"), request.DockSoftwareId, StringComparison.Ordinal))
                .ToArray() ?? [];
            if (software.Length != 1 ||
                !software.Any(node => IsOne(node.Attribute("compatible")) || IsOne(node.Attribute("default"))))
                return Unknown("configured-dock-software-not-compatible");

            var droneCapacity = OptionalInt(shipProperties.Element("storage"), "unit", 0);
            if (request.CargoDrones > droneCapacity)
                return Unknown("configured-cargo-drones-exceed-unit-capacity");

            var shipComponentId = RequiredAttribute(RequiredElement(ship, "component", shipInfo.Id), "ref");
            var engineComponentId = RequiredAttribute(RequiredElement(engine, "component", engineInfo.Id), "ref");
            var thrusterComponentId = RequiredAttribute(RequiredElement(thruster, "component", thrusterInfo.Id), "ref");
            var shipComponent = FindComponent(_gameData.GetEffectiveComponentXml(shipComponentId), shipComponentId);
            var engineComponent = FindComponent(_gameData.GetEffectiveComponentXml(engineComponentId), engineComponentId);
            var thrusterComponent = FindComponent(_gameData.GetEffectiveComponentXml(thrusterComponentId), thrusterComponentId);

            var engineConnector = FindUniqueConnector(engineComponent, "engine");
            var engineOwnershipTags = Tags(engineConnector)
                .Where(tag => !string.Equals(tag, "component", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var engineSlots = shipComponent.Element("connections")?.Elements("connection")
                .Where(connection => Tags(connection).Contains("engine", StringComparer.OrdinalIgnoreCase))
                .ToArray() ?? [];
            if (engineSlots.Length == 0 ||
                engineSlots.Any(slot => !engineOwnershipTags.IsSubsetOf(Tags(slot))))
                return Unknown("configured-homogeneous-engine-not-compatible-with-every-slot");
            if (shipInfo.EngineCount != engineSlots.Length)
                return Unknown("ship-engine-count-does-not-match-effective-component");

            var thrusterConnector = FindUniqueConnector(thrusterComponent, "thruster");
            var thrusterOwnershipTags = Tags(thrusterConnector)
                .Where(tag => !string.Equals(tag, "component", StringComparison.OrdinalIgnoreCase) &&
                              !string.Equals(tag, "thruster", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var shipThrusterTags = Tags(shipProperties.Element("thruster"));
            if (!thrusterOwnershipTags.IsSubsetOf(shipThrusterTags))
                return Unknown("configured-thruster-not-compatible-with-ship");

            var storageMacros = new List<XElement>();
            foreach (var macroRef in ship.Element("connections")?.Elements("connection")
                         .Select(connection => (string?)connection.Element("macro")?.Attribute("ref"))
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Cast<string>() ?? [])
            {
                var child = FindMacro(_gameData.GetEffectiveMacroXml(macroRef), macroRef);
                if (string.Equals((string?)child.Attribute("class"), "storage", StringComparison.Ordinal))
                    storageMacros.Add(child);
            }
            if (storageMacros.Count != 1)
                return Unknown($"exactly-one-cargo-storage-required:{storageMacros.Count}");

            if (!_gameData.Wares.TryGetValue(request.WareId, out var ware))
                return Unknown("configured-ware-not-found-in-effective-data");
            if (ware.Volume <= 0)
                return Unknown("configured-ware-volume-must-be-positive");
            var storageMacro = storageMacros[0];
            var cargo = storageMacro.Element("properties")?.Element("cargo")
                ?? throw new InvalidDataException($"{RequiredAttribute(storageMacro, "name")}: properties/cargo missing");
            var cargoTags = Tags(cargo);
            if (ware.Transport is not ("container" or "solid" or "liquid") ||
                !cargoTags.Contains(ware.Transport, StringComparer.OrdinalIgnoreCase))
                return Unknown("configured-ware-transport-not-compatible-with-storage");

            var capacity = RequiredPositive(cargo, "max");
            var unitVolume = (float)ware.Volume;
            var maximumUnits = checked((int)Math.Floor(capacity / unitVolume));
            var cargoUnits = request.CargoUnits ?? maximumUnits;
            if (cargoUnits > maximumUnits)
                return Unknown("configured-cargo-units-exceed-storage-capacity");
            var occupiedVolume = cargoUnits * unitVolume;

            var parameters = _gameData.GetEffectiveXml("libraries/parameters.xml");
            var defaults = _gameData.GetEffectiveXml("libraries/defaults.xml");
            var parameterRoot = parameters.Root ?? throw new InvalidDataException("parameters.xml root missing");
            var density = RequiredElement(RequiredElement(parameterRoot, "physics", "parameters"), "density", "parameters/physics");
            var additionalMass = RequiredElement(parameterRoot.Element("physics")!, "additionalmass", "parameters/physics");
            var cargoDensity = RequiredNonnegative(density, "cargo");
            var maximumAdditionalMassRatio = RequiredNonnegative(additionalMass, "max");
            var cargoMass = occupiedVolume * cargoDensity;
            var baseMass = RequiredPositive(shipPhysics, "mass");
            var cappedCargoMass = MathF.Min(cargoMass, baseMass * maximumAdditionalMassRatio);

            var accelerationFactors = Directional(shipPhysics.Element("accfactors"), 1f);
            var inertia = Rotational(shipPhysics.Element("inertia"), 1f);
            var drag = Directional(shipPhysics.Element("drag"), 1f);
            var rotationalDrag = Rotational(shipPhysics.Element("drag"), 1f);
            EnsurePositive(drag.Forward, "drag.forward");
            EnsurePositive(drag.Reverse, "drag.reverse");
            EnsurePositive(drag.Horizontal, "drag.horizontal");
            EnsurePositive(drag.Vertical, "drag.vertical");
            EnsurePositive(rotationalDrag.Pitch, "drag.pitch");
            EnsurePositive(rotationalDrag.Yaw, "drag.yaw");
            EnsurePositive(rotationalDrag.Roll, "drag.roll");

            var engineThrust = RequiredElement(engineProperties, "thrust", $"{engineInfo.Id}/properties");
            var forwardThrust = RequiredFinite(engineThrust, "forward");
            var reverseThrust = RequiredFinite(engineThrust, "reverse");
            var aggregateForward = engineSlots.Length * forwardThrust;
            var aggregateReverse = engineSlots.Length * reverseThrust;
            var thrusterThrust = Rotational(RequiredElement(thrusterProperties, "thrust", $"{thrusterInfo.Id}/properties"), 0f);

            var boost = RequiredElement(engineProperties, "boost", $"{engineInfo.Id}/properties");
            var travel = RequiredElement(engineProperties, "travel", $"{engineInfo.Id}/properties");
            var boostThrust = RequiredFinite(boost, "thrust");
            var boostAcceleration = RequiredFinite(boost, "acceleration");
            var boostCoast = OptionalFinite(boost, "coast", 1f);
            var travelThrust = RequiredFinite(travel, "thrust");
            var travelAttack = RequiredFinite(travel, "attack");
            var curve = engineProperties.Element("decelerationcurve")?.Elements("point")
                .Select(point => new OosSpeedCurvePoint(
                    RequiredFinite(point, "position"),
                    RequiredFinite(point, "value")))
                .ToArray() ?? [];
            var modeProperties = new OosEngineModeProperties(
                boostThrust,
                RequiredFinite(boost, "attack"),
                RequiredFinite(boost, "duration"),
                RequiredFinite(boost, "recharge"),
                RequiredFinite(boost, "release"),
                travelThrust,
                RequiredFinite(travel, "charge"),
                travelAttack,
                RequiredFinite(travel, "release"),
                OptionalFinite(travel, "startthrust", 1f));

            var defaultShipProperties = defaults.Root?.Elements("dataset")
                .SingleOrDefault(node => string.Equals((string?)node.Attribute("class"), "ship", StringComparison.Ordinal))
                ?.Element("properties")
                ?? throw new InvalidDataException("defaults.xml ship dataset properties missing");
            var shipJpm = shipProperties.Element("jpm");
            var defaultJpm = RequiredElement(defaultShipProperties, "jpm", "defaults ship properties");
            var jpmAllow = ParseBoolean((string?)(shipJpm ?? defaultJpm).Attribute("allow"), true);
            if (!jpmAllow)
                return Unknown("ship-jpm-disabled");
            var playerFlight = RequiredElement(parameterRoot, "playerflight", "parameters");
            var throttle = RequiredElement(RequiredElement(playerFlight, "input", "parameters/playerflight"), "throttle", "parameters/playerflight/input");
            var jpmFactor = RequiredFinite(throttle, "jpmfactor");
            var aiFlight = RequiredElement(parameterRoot, "aiflight", "parameters");
            var angles = RequiredElement(aiFlight, "angles", "parameters/aiflight");
            var spline = RequiredElement(aiFlight, "spline", "parameters/aiflight");
            // 保持 Predict-OosDockingDuration.source_jerk_forward_decel 的语义：
            // 该值必须由最终有效的舰船宏自身声明。
            var jerk = shipProperties.Element("jerk")?.Element("forward") ??
                       throw new InvalidDataException("ship properties/jerk/forward missing");
            var jerkForwardDecel = RequiredFinite(jerk, "decel");

            var geometry = _geometryReader.ReadMacroGeometry(shipInfo.Id);
            if (geometry.Bounds is null || geometry.CloseLinkGroups.Count == 0 || geometry.UnsupportedReasons.Count > 0)
            {
                var reasons = geometry.UnsupportedReasons
                    .Select(reason => $"ship-geometry:{reason}")
                    .ToList();
                if (geometry.Bounds is null)
                    reasons.Add("ship-geometry:bounds-missing");
                if (geometry.CloseLinkGroups.Count == 0)
                    reasons.Add("ship-geometry:closelinks-missing");
                return Unknown(reasons);
            }

            var basePhysics = new OosNavigationBasePhysics(
                aggregateForward * accelerationFactors.Forward / baseMass,
                aggregateReverse * accelerationFactors.Reverse / baseMass,
                aggregateForward / drag.Forward,
                aggregateReverse / drag.Reverse,
                new Vec3(
                    thrusterThrust.Pitch / rotationalDrag.Pitch * DegreesToRadians,
                    thrusterThrust.Yaw / rotationalDrag.Yaw * DegreesToRadians,
                    thrusterThrust.Roll / rotationalDrag.Roll * DegreesToRadians));
            var enginePhysics = new OosNavigationEnginePhysics(
                boostThrust,
                travelThrust,
                boostAcceleration,
                boostCoast,
                travelAttack,
                curve);
            var physicalEngine = new OosTransportEnginePhysicalProfile(
                forwardThrust,
                reverseThrust,
                boostAcceleration,
                boostCoast,
                modeProperties,
                curve);
            var storage = new OosTransportStorageProfile(
                RequiredAttribute(storageMacro, "name"),
                ware.Transport,
                ware.Id,
                cargoUnits,
                unitVolume,
                occupiedVolume,
                capacity,
                maximumUnits,
                request.CargoDrones);
            var resolvedShipPhysics = new OosTransportShipPhysics(
                accelerationFactors,
                inertia,
                drag,
                rotationalDrag,
                thrusterThrust);
            var resolvedAiFlight = new OosTransportAiFlightProfile(
                RequiredFinite(angles, "startboost"),
                RequiredFinite(angles, "stopboost"),
                RequiredFinite(angles, "starttravel"),
                RequiredFinite(angles, "stoptravel"),
                RequiredFinite(spline, "turnradius"),
                RequiredFinite(spline, "rotaccfactor"),
                RequiredFinite(spline, "maxturnradius"));

            return OosTransportShipProfileResolution.Qualified(new OosTransportShipProfile(
                shipInfo.Id,
                engineInfo.Id,
                engineSlots.Length,
                thrusterInfo.Id,
                request.DockSoftwareId,
                configuration.PilotingStars,
                request.MoraleStars,
                baseMass,
                cargoDensity,
                cargoMass,
                cappedCargoMass,
                maximumAdditionalMassRatio,
                jerkForwardDecel,
                storage,
                resolvedShipPhysics,
                new OosTransportJpmProfile(jpmAllow, jpmFactor),
                resolvedAiFlight,
                physicalEngine,
                geometry,
                basePhysics,
                enginePhysics));
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or InvalidDataException or FormatException or OverflowException)
        {
            return Unknown($"effective-xml-profile-unresolved:{exception.Message}");
        }
    }

    private static OosTransportShipProfileResolution Unknown(IEnumerable<string> reasons) =>
        OosTransportShipProfileResolution.Unknown(reasons.Distinct(StringComparer.Ordinal).ToArray());

    private static OosTransportShipProfileResolution Unknown(string reason) =>
        OosTransportShipProfileResolution.Unknown(reason);

    private static XElement FindMacro(XDocument document, string macroId) =>
        FindNamedElement(document, "macro", macroId);

    private static XElement FindComponent(XDocument document, string componentId) =>
        FindNamedElement(document, "component", componentId);

    private static XElement FindNamedElement(XDocument document, string elementName, string id)
    {
        ArgumentNullException.ThrowIfNull(document);
        var matches = document.Descendants(elementName)
            .Where(node => string.Equals((string?)node.Attribute("name"), id, StringComparison.Ordinal))
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException($"{id}: expected one {elementName}, found {matches.Length}");
    }

    private static XElement FindUniqueConnector(XElement component, string kind)
    {
        var matches = component.Element("connections")?.Elements("connection")
            .Where(connection =>
            {
                var tags = Tags(connection);
                return tags.Contains(kind, StringComparer.OrdinalIgnoreCase) &&
                       tags.Contains("component", StringComparer.OrdinalIgnoreCase);
            })
            .ToArray() ?? [];
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException(
                $"{(string?)component.Attribute("name")}: expected one {kind} component connector, found {matches.Length}");
    }

    private static XElement RequiredElement(XElement parent, string name, string context) =>
        parent.Element(name) ?? throw new InvalidDataException($"{context}/{name} missing");

    private static string RequiredAttribute(XElement element, string name) =>
        (string?)element.Attribute(name) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"{element.Name.LocalName}@{name} missing");

    private static OosTransportDirectionalPhysics Directional(XElement? node, float defaultValue) =>
        new(
            OptionalFinite(node, "forward", defaultValue),
            OptionalFinite(node, "reverse", defaultValue),
            OptionalFinite(node, "horizontal", defaultValue),
            OptionalFinite(node, "vertical", defaultValue));

    private static OosTransportRotationalPhysics Rotational(XElement? node, float defaultValue) =>
        new(
            OptionalFinite(node, "pitch", defaultValue),
            OptionalFinite(node, "yaw", defaultValue),
            OptionalFinite(node, "roll", defaultValue));

    private static float RequiredPositive(XElement node, string attribute)
    {
        var result = RequiredFinite(node, attribute);
        EnsurePositive(result, $"{node.Name.LocalName}.{attribute}");
        return result;
    }

    private static float RequiredNonnegative(XElement node, string attribute)
    {
        var result = RequiredFinite(node, attribute);
        if (result < 0)
            throw new InvalidDataException($"{node.Name.LocalName}.{attribute} must be nonnegative");
        return result;
    }

    private static void EnsurePositive(float value, string label)
    {
        if (value <= 0)
            throw new InvalidDataException($"{label} must be positive");
    }

    private static float RequiredFinite(XElement node, string attribute) =>
        (string?)node.Attribute(attribute) is { } raw
            ? ParseFinite(raw, $"{node.Name.LocalName}.{attribute}")
            : throw new InvalidDataException($"{node.Name.LocalName}@{attribute} missing");

    private static float OptionalFinite(XElement? node, string attribute, float defaultValue) =>
        (string?)node?.Attribute(attribute) is { } raw
            ? ParseFinite(raw, $"{node!.Name.LocalName}.{attribute}")
            : defaultValue;

    private static float ParseFinite(string raw, string label)
    {
        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !float.IsFinite(value))
            throw new FormatException($"{label} must be a finite float");
        return value;
    }

    private static int OptionalInt(XElement? node, string attribute, int defaultValue)
    {
        if ((string?)node?.Attribute(attribute) is not { } raw)
            return defaultValue;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0)
            throw new FormatException($"{node!.Name.LocalName}.{attribute} must be a nonnegative integer");
        return value;
    }

    private static IReadOnlyList<string> Tags(XElement? node) =>
        ((string?)node?.Attribute("tags") ?? string.Empty)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsOne(XAttribute? attribute) =>
        string.Equals(attribute?.Value, "1", StringComparison.Ordinal);

    private static bool ParseBoolean(string? raw, bool defaultValue)
    {
        if (raw is null)
            return defaultValue;
        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" => true,
            "0" or "false" or "no" => false,
            _ => throw new FormatException($"invalid boolean: {raw}")
        };
    }
}
