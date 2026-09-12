using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using X4Calculator.Core.Calculation;
using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Parsing;

/// <summary>
/// 流式扫描存档，并逐站物化有限子树以解析静态站点、模块与泊位实例几何。
/// 不保留整个存档，也不把静态泊位候选解释为空闲、分配或队列状态。
/// </summary>
public sealed class StationTransportTopologyReader
{
    private static readonly Vec3 NativeDefaultZoneHalfExtents = new(50_000, 50_000, 50_000);
    private readonly GameDataDB _gameData;
    private readonly X4GeometryDefinitionReader _geometry;

    public StationTransportTopologyReader(GameDataDB gameData)
        : this(gameData, [])
    {
    }

    public StationTransportTopologyReader(GameDataDB gameData, IEnumerable<XDocument> supplementalMacroXml)
    {
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        _geometry = new X4GeometryDefinitionReader(gameData, supplementalMacroXml);
    }

    public Task<IReadOnlyList<StationTransportTopology>> ReadAsync(
        string saveFilePath,
        IEnumerable<string>? stationIdentifiers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveFilePath);
        var requested = stationIdentifiers?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.Run<IReadOnlyList<StationTransportTopology>>(
            () => Read(saveFilePath, requested, cancellationToken), cancellationToken);
    }

    internal static void Populate(
        string saveFilePath,
        IReadOnlyCollection<Station> stations,
        GameDataDB gameData,
        CancellationToken cancellationToken)
    {
        if (stations.Count == 0 || !gameData.HasEffectiveXmlSource) return;
        var byId = stations.ToDictionary(station => station.Id, StringComparer.OrdinalIgnoreCase);
        var reader = new StationTransportTopologyReader(gameData);
        foreach (var topology in reader.Read(saveFilePath, byId.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), cancellationToken))
        {
            if (byId.TryGetValue(topology.StationSaveId, out var station))
                station.TransportTopology = topology;
        }
    }

    private IReadOnlyList<StationTransportTopology> Read(
        string saveFilePath,
        IReadOnlySet<string>? requested,
        CancellationToken cancellationToken)
    {
        if (!_gameData.HasEffectiveXmlSource)
            throw new InvalidOperationException("GameDataDB 尚未通过 LoadAsync 建立有效 XML 数据源。");

        var result = new List<StationTransportTopology>();
        var matchedIdentifiers = requested?.ToDictionary(value => value, _ => 0, StringComparer.OrdinalIgnoreCase);
        var elements = new string?[512];
        var components = new ComponentFrame?[512];
        var connectionNames = new string?[512];

        using var stream = SavegameFile.OpenRead(saveFilePath);
        using var xml = XmlReader.Create(stream, new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            IgnoreComments = true,
            DtdProcessing = DtdProcessing.Ignore,
            CloseInput = false
        });

        while (xml.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (xml.Depth >= elements.Length)
                throw new XmlException($"存档 XML 层级过深：{xml.Depth}");

            if (xml.NodeType == XmlNodeType.Element)
            {
                elements[xml.Depth] = xml.Name;
                if (xml.Name == "connection")
                    connectionNames[xml.Depth] = xml.GetAttribute("connection");

                if (xml.Name == "component")
                {
                    var componentClass = xml.GetAttribute("class") ?? string.Empty;
                    var id = xml.GetAttribute("id") ?? string.Empty;
                    var code = xml.GetAttribute("code") ?? string.Empty;
                    var owner = xml.GetAttribute("owner") ?? string.Empty;
                    if (componentClass.Equals("station", StringComparison.OrdinalIgnoreCase) &&
                        ShouldReadStation(id, code, owner, requested))
                    {
                        var parent = FindAncestorComponent(components, xml.Depth);
                        var parentConnection = FindAncestorConnection(connectionNames, xml.Depth);
                        using var subtree = xml.ReadSubtree();
                        subtree.MoveToContent();
                        var stationElement = XElement.Load(subtree, LoadOptions.None);
                        var stationFrame = CreateFrame(stationElement, parent, parentConnection);
                        try
                        {
                            result.Add(BuildTopology(stationElement, stationFrame));
                        }
                        catch (Exception exception) when (IsUnsupportedDefinition(exception))
                        {
                            result.Add(CreateUnsupportedTopology(stationElement, stationFrame, exception.Message));
                        }
                        if (matchedIdentifiers != null)
                        {
                            foreach (var identifier in matchedIdentifiers.Keys.ToArray())
                                if (MatchesIdentifier(id, code, identifier))
                                    matchedIdentifiers[identifier]++;
                        }
                        elements[xml.Depth] = null;
                        continue;
                    }

                    components[xml.Depth] = new ComponentFrame(
                        id,
                        componentClass,
                        xml.GetAttribute("macro") ?? string.Empty,
                        xml.GetAttribute("connection") ?? string.Empty,
                        FindAncestorConnection(connectionNames, xml.Depth) ?? string.Empty,
                        FindAncestorComponent(components, xml.Depth));
                }
                else if (xml.Name == "offset" && xml.Depth > 0 && elements[xml.Depth - 1] == "component")
                {
                    var frame = components[xml.Depth - 1];
                    if (frame != null)
                    {
                        frame.Offset = new XElement("offset");
                        frame.OffsetIsDefault = xml.GetAttribute("default") == "1";
                    }
                }
                else if ((xml.Name == "position" || xml.Name == "rotation" || xml.Name == "quaternion") &&
                         xml.Depth >= 2 && elements[xml.Depth - 1] == "offset" &&
                         elements[xml.Depth - 2] == "component")
                {
                    var frame = components[xml.Depth - 2];
                    frame?.Offset?.Add(CopyCurrentElement(xml));
                }

                if (xml.IsEmptyElement)
                {
                    if (xml.Name == "component") components[xml.Depth] = null;
                    if (xml.Name == "connection") connectionNames[xml.Depth] = null;
                    elements[xml.Depth] = null;
                }
            }
            else if (xml.NodeType == XmlNodeType.EndElement)
            {
                if (xml.Name == "component") components[xml.Depth] = null;
                if (xml.Name == "connection") connectionNames[xml.Depth] = null;
                elements[xml.Depth] = null;
            }
        }

        if (matchedIdentifiers != null)
        {
            foreach (var (identifier, count) in matchedIdentifiers)
            {
                if (count != 1)
                    throw new InvalidDataException(
                        $"station {identifier}: expected exactly one match, found {count}");
            }
        }
        return result;
    }

    private StationTransportTopology BuildTopology(XElement station, ComponentFrame stationFrame)
    {
        var unsupported = new List<string>();
        var sector = FindAncestorFrame(stationFrame, "sector");
        var zone = FindAncestorFrame(stationFrame, "zone");
        if (sector == null)
            unsupported.Add("station has no sector ancestor");

        X4RigidTransform? stationTransform = null;
        X4RigidTransform? zoneTransform = null;
        if (sector != null)
        {
            stationTransform = TryResolveFrameTransform(stationFrame, sector, "station sector transform", unsupported);
            if (zone != null)
                zoneTransform = TryResolveFrameTransform(zone, sector, "zone sector transform", unsupported);
        }
        var (zoneGeometry, zoneGeometryUnsupportedReason) = ReadZoneGeometry(zone, zoneTransform);

        if (!IsOperational(station))
            unsupported.Add("station is not operational");

        var modules = station.Element("connections")?.Elements("connection")
            .Where(connection => string.Equals(
                connection.Attribute("connection")?.Value, "modules", StringComparison.OrdinalIgnoreCase))
            .SelectMany(connection => connection.Elements("component"))
            .ToArray() ?? [];
        var activeModules = modules.Where(IsOperational).ToHashSet();
        var boundsPoints = new List<Vec3>();
        var storageTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var boundsSupported = activeModules.Count > 0;
        if (activeModules.Count == 0)
            unsupported.Add("station has no operational module geometry");

        foreach (var module in activeModules)
        {
            var moduleId = module.Attribute("id")?.Value ?? string.Empty;
            var macroId = module.Attribute("macro")?.Value ?? string.Empty;
            var definition = _geometry.ReadMacroGeometry(macroId);
            foreach (var tag in definition.CargoTags) storageTypes.Add(tag);
            if (definition.Bounds == null)
            {
                boundsSupported = false;
                unsupported.Add($"module {moduleId}: {string.Join("; ", definition.UnsupportedReasons)}");
                continue;
            }

            try
            {
                var pose = ResolveElementTransform(module, station);
                foreach (var corner in X4GeometryDefinitionReader.Corners(
                             definition.Bounds.Center, definition.Bounds.HalfExtents))
                    boundsPoints.Add(pose.TransformPoint(corner));
            }
            catch (Exception exception) when (IsUnsupportedDefinition(exception))
            {
                boundsSupported = false;
                unsupported.Add($"module {moduleId}: {exception.Message}");
            }
        }

        var bounds = boundsSupported && boundsPoints.Count > 0
            ? X4GeometryDefinitionReader.CreateBounds(boundsPoints)
            : null;
        var excluded = new List<OosTransportExcludedBerth>();
        var berths = new List<OosTransportBerth>();
        foreach (var dock in station.Descendants("component")
                     .Where(component => string.Equals(
                         component.Attribute("class")?.Value, "dockingbay", StringComparison.OrdinalIgnoreCase)))
        {
            var chain = dock.AncestorsAndSelf("component").TakeWhile(component => component != station).Append(station).ToArray();
            if (chain.Any(component =>
                    (component.Attribute("class")?.Value ?? string.Empty)
                    .StartsWith("ship_", StringComparison.OrdinalIgnoreCase)))
                continue;

            var dockId = dock.Attribute("id")?.Value ?? string.Empty;
            var dockMacro = dock.Attribute("macro")?.Value ?? string.Empty;
            if (!chain.Any(activeModules.Contains) || !chain.All(IsOperational))
            {
                excluded.Add(new OosTransportExcludedBerth(
                    dockId, dockMacro, "non-operational component or module ancestry"));
                continue;
            }

            try
            {
                var macro = _geometry.GetMacroDefinition(dockMacro);
                var dockTags = macro.Descendants("docksize")
                    .SelectMany(node => SplitTags(node.Attribute("tags")?.Value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (!string.Equals(macro.Attribute("class")?.Value, "dockingbay", StringComparison.OrdinalIgnoreCase) ||
                    dockTags.Length == 0)
                {
                    excluded.Add(new OosTransportExcludedBerth(
                        dockId, dockMacro, "macro is not a dockingbay with dock-size tags"));
                    continue;
                }

                var stationPose = ResolveElementTransform(dock, station);
                if (stationTransform == null)
                {
                    excluded.Add(new OosTransportExcludedBerth(
                        dockId, dockMacro, "station sector transform is unresolved"));
                    continue;
                }
                var sectorPose = X4RigidTransform.Compose(stationTransform.Value, stationPose);
                var geometry = _geometry.ReadMacroGeometry(dockMacro);
                // 泊位模板只消费 clscloselink；dock 自身没有碰撞 OBB 不影响 closelink 的存在与坐标。
                // 定义读取失败时 groups 为空，下面的明确 closelink 分支仍会将其排除。
                var berthUnsupported = geometry.CloseLinkGroups.Count == 0
                    ? geometry.UnsupportedReasons.ToList()
                    : [];
                if (!dockTags.Contains("dock_xl", StringComparer.OrdinalIgnoreCase))
                    berthUnsupported.Add("only dock_xl supports the capital two-point template");
                if (geometry.CloseLinkGroups.Count != 1 || geometry.CloseLinkGroups[0].Side != 1)
                    berthUnsupported.Add(
                        $"supported clscloselink rule requires exactly one +Z group; found sides " +
                        $"[{string.Join(",", geometry.CloseLinkGroups.Select(group => group.Side))}]");
                var component = _geometry.GetComponentDefinitionForMacro(dockMacro);
                if (component.Element("connections")?.Elements("connection").Any(connection =>
                        SplitTags(connection.Attribute("tags")?.Value)
                            .Contains("launchpos", StringComparer.OrdinalIgnoreCase)) == true)
                    berthUnsupported.Add("explicit launchpos pier requires separate departure implementation");

                berths.Add(new OosTransportBerth(
                    dockId,
                    dockMacro,
                    stationPose,
                    sectorPose,
                    dockTags,
                    geometry.CloseLinkGroups,
                    berthUnsupported.Distinct(StringComparer.Ordinal).ToArray()));
            }
            catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException or FormatException)
            {
                excluded.Add(new OosTransportExcludedBerth(dockId, dockMacro, exception.Message));
            }
        }

        var inventoryUnsupported = new List<string>();
        var installedCargoDrones = ReadInstalledCargoDroneCount(station, inventoryUnsupported);
        unsupported.AddRange(inventoryUnsupported);
        return new StationTransportTopology(
            station.Attribute("id")?.Value ?? string.Empty,
            station.Attribute("code")?.Value ?? string.Empty,
            station.Attribute("name")?.Value ?? string.Empty,
            station.Attribute("owner")?.Value ?? string.Empty,
            station.Attribute("macro")?.Value ?? string.Empty,
            sector?.Id ?? string.Empty,
            sector?.Macro ?? string.Empty,
            zone?.Id,
            zone?.Macro,
            zoneTransform,
            zoneGeometry,
            zoneGeometryUnsupportedReason,
            stationTransform,
            bounds,
            berths,
            excluded,
            activeModules.Count,
            modules.Length - activeModules.Count,
            storageTypes.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            installedCargoDrones,
            unsupported.Distinct(StringComparer.Ordinal).ToArray())
        {
            CargoDroneInventoryUnsupportedReasons = inventoryUnsupported.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    private int? ReadInstalledCargoDroneCount(XElement station, List<string> unsupported)
    {
        var available = station.Element("ammunition")?.Element("available");
        if (available == null)
        {
            unsupported.Add("station installed cargo-drone inventory is not serialized");
            return null;
        }

        var total = 0;
        foreach (var item in available.Elements("item"))
        {
            var macroId = item.Attribute("macro")?.Value;
            if (string.IsNullOrWhiteSpace(macroId))
            {
                unsupported.Add("station ammunition item is missing macro");
                return null;
            }
            try
            {
                var macro = _geometry.GetMacroDefinition(macroId);
                var properties = macro.Element("properties");
                var isCargoDrone = string.Equals(macro.Attribute("class")?.Value, "ship_xs", StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(properties?.Element("ship")?.Attribute("type")?.Value,
                                       "xsdrone", StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(properties?.Element("purpose")?.Attribute("primary")?.Value,
                                       "trade", StringComparison.OrdinalIgnoreCase);
                if (!isCargoDrone) continue;
                if (!int.TryParse(item.Attribute("amount")?.Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var amount) || amount < 0)
                {
                    unsupported.Add($"cargo-drone item {macroId} has invalid amount");
                    return null;
                }
                checked { total += amount; }
            }
            catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException)
            {
                unsupported.Add($"cannot classify station ammunition item {macroId}: {exception.Message}");
                return null;
            }
        }
        return total;
    }

    private X4RigidTransform? TryResolveFrameTransform(
        ComponentFrame component,
        ComponentFrame ancestor,
        string label,
        List<string> unsupported)
    {
        try
        {
            return ResolveFrameTransform(component, ancestor, []);
        }
        catch (Exception exception) when (IsUnsupportedDefinition(exception))
        {
            unsupported.Add($"{label}: {exception.Message}");
            return null;
        }
    }

    private X4RigidTransform ResolveFrameTransform(
        ComponentFrame component,
        ComponentFrame ancestor,
        HashSet<ComponentFrame> visited)
    {
        if (ReferenceEquals(component, ancestor)) return X4RigidTransform.Identity;
        if (!visited.Add(component)) throw new InvalidDataException("cycle in save component ancestry");
        if (component.Parent == null)
            throw new InvalidDataException($"component {component.Id}: ancestor is not reachable");
        var local = ResolveLocalTransform(
            component.Id,
            component.Class,
            component.Macro,
            component.CounterpartConnection,
            component.ParentConnection,
            component.Parent.Class,
            component.Parent.Macro,
            component.Offset,
            component.OffsetIsDefault);
        return X4RigidTransform.Compose(
            ResolveFrameTransform(component.Parent, ancestor, visited), local);
    }

    private X4RigidTransform ResolveElementTransform(XElement component, XElement ancestor)
    {
        if (component == ancestor) return X4RigidTransform.Identity;
        var link = component.Parent;
        var connections = link?.Parent;
        var parent = connections?.Parent;
        if (link?.Name != "connection" || connections?.Name != "connections" || parent?.Name != "component")
            throw new InvalidDataException(
                $"component {component.Attribute("id")?.Value}: missing parent connection chain");
        var local = ResolveLocalTransform(
            component.Attribute("id")?.Value ?? string.Empty,
            component.Attribute("class")?.Value ?? string.Empty,
            component.Attribute("macro")?.Value ?? string.Empty,
            component.Attribute("connection")?.Value ?? string.Empty,
            link.Attribute("connection")?.Value ?? string.Empty,
            parent.Attribute("class")?.Value ?? string.Empty,
            parent.Attribute("macro")?.Value ?? string.Empty,
            component.Element("offset"),
            component.Element("offset")?.Attribute("default")?.Value == "1");
        return X4RigidTransform.Compose(ResolveElementTransform(parent, ancestor), local);
    }

    private X4RigidTransform ResolveLocalTransform(
        string id,
        string componentClass,
        string macro,
        string counterpartConnection,
        string parentConnection,
        string parentClass,
        string parentMacro,
        XElement? explicitOffset,
        bool offsetIsDefault)
    {
        if (explicitOffset != null && !offsetIsDefault)
            return X4GeometryDefinitionReader.ReadTransform(explicitOffset);
        if (string.IsNullOrWhiteSpace(parentConnection) || string.IsNullOrWhiteSpace(counterpartConnection))
            throw new InvalidDataException($"component {id}: XML-derived placement lacks connection names");
        return X4RigidTransform.Compose(
            ResolveSaveConnectionTransform(parentClass, parentMacro, parentConnection),
            ResolveSaveConnectionTransform(componentClass, macro, counterpartConnection).Inverse());
    }

    private X4RigidTransform ResolveSaveConnectionTransform(
        string componentClass,
        string macro,
        string connectionName)
    {
        try
        {
            return _geometry.ResolveConnectionTransform(macro, connectionName);
        }
        catch (KeyNotFoundException) when (
            componentClass.Equals("zone", StringComparison.OrdinalIgnoreCase))
        {
            // 存档可引用未出现在当前导出清单中的 MOD zone。Python 研究链只允许
            // standardzone 上同名且恒等的连接作为窄回退，避免臆测宏级连接偏移。
            var fallback = _geometry.ResolveComponentConnectionTransform("standardzone", connectionName);
            if (fallback != X4RigidTransform.Identity)
                throw new InvalidDataException(
                    $"zone macro {macro} is unavailable and standardzone connection {connectionName} is not identity");
            return fallback;
        }
    }

    private (OosTransportZoneGeometryInput? Geometry, string? UnsupportedReason) ReadZoneGeometry(
        ComponentFrame? zone,
        X4RigidTransform? zoneTransform)
    {
        if (zone == null) return (null, "station has no zone ancestor");
        if (zoneTransform == null) return (null, "zone sector transform is unresolved");
        try
        {
            var macro = _geometry.GetMacroDefinition(zone.Macro);
            if (!string.Equals(macro.Attribute("class")?.Value, "zone", StringComparison.OrdinalIgnoreCase))
                return (null, $"zone {zone.Id}: macro {zone.Macro} is not class zone");

            var explicitBoundaries = macro.Element("properties")?.Element("boundaries")?
                .Elements("boundary").ToArray() ?? [];
            if (explicitBoundaries.Length > 0)
                return (null,
                    $"zone {zone.Id}: explicit boundary geometry is unsupported " +
                    $"[{string.Join(",", explicitBoundaries.Select(boundary => boundary.Attribute("class")?.Value ?? "unknown"))}]");

            var rotation = zoneTransform.Value.Rotation;
            return (new OosTransportZoneGeometryInput(
                OosTransportZoneShapeKind.DefaultAxisAlignedBox,
                new OosTransportZoneTransform(
                    zoneTransform.Value.Position,
                    new Vec3(rotation.Row0.X, rotation.Row1.X, rotation.Row2.X),
                    new Vec3(rotation.Row0.Y, rotation.Row1.Y, rotation.Row2.Y),
                    new Vec3(rotation.Row0.Z, rotation.Row1.Z, rotation.Row2.Z)),
                NativeDefaultZoneHalfExtents), null);
        }
        catch (Exception exception) when (IsUnsupportedDefinition(exception))
        {
            return (null, $"zone {zone.Id} geometry: {exception.Message}");
        }
    }

    private static StationTransportTopology CreateUnsupportedTopology(
        XElement station,
        ComponentFrame stationFrame,
        string reason)
    {
        var sector = FindAncestorFrame(stationFrame, "sector");
        var zone = FindAncestorFrame(stationFrame, "zone");
        return new StationTransportTopology(
            station.Attribute("id")?.Value ?? string.Empty,
            station.Attribute("code")?.Value ?? string.Empty,
            station.Attribute("name")?.Value ?? string.Empty,
            station.Attribute("owner")?.Value ?? string.Empty,
            station.Attribute("macro")?.Value ?? string.Empty,
            sector?.Id ?? string.Empty,
            sector?.Macro ?? string.Empty,
            zone?.Id,
            zone?.Macro,
            null,
            null,
            reason,
            null,
            null,
            [],
            [],
            0,
            0,
            [],
            null,
            [$"topology parse failed: {reason}"]);
    }

    private static bool IsUnsupportedDefinition(Exception exception) =>
        exception is InvalidDataException or KeyNotFoundException or FormatException or OverflowException;

    private static ComponentFrame CreateFrame(
        XElement component,
        ComponentFrame? parent,
        string? parentConnection) => new(
            component.Attribute("id")?.Value ?? string.Empty,
            component.Attribute("class")?.Value ?? string.Empty,
            component.Attribute("macro")?.Value ?? string.Empty,
            component.Attribute("connection")?.Value ?? string.Empty,
            parentConnection ?? string.Empty,
            parent)
        {
            Offset = component.Element("offset") is { } offset ? new XElement(offset) : null,
            OffsetIsDefault = component.Element("offset")?.Attribute("default")?.Value == "1"
        };

    private static bool ShouldReadStation(
        string id,
        string code,
        string owner,
        IReadOnlySet<string>? requested) => requested == null
            ? owner.Equals("player", StringComparison.OrdinalIgnoreCase)
            : requested.Any(identifier => MatchesIdentifier(id, code, identifier));

    private static bool MatchesIdentifier(string id, string code, string requested)
    {
        if (id.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
            code.Equals(requested, StringComparison.OrdinalIgnoreCase)) return true;
        return TryParseSaveId(id, out var actual) && TryParseSaveId(requested, out var expected) && actual == expected;
    }

    private static bool TryParseSaveId(string value, out long result)
    {
        var text = value.Trim();
        if (text.StartsWith('[') && text.EndsWith(']')) text = text[1..^1].Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result);
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static bool IsOperational(XElement component) =>
        string.Equals(component.Attribute("state")?.Value ?? "operational", "operational", StringComparison.Ordinal);

    private static ComponentFrame? FindAncestorComponent(ComponentFrame?[] components, int beforeDepth)
    {
        for (var depth = beforeDepth - 1; depth >= 0; depth--)
            if (components[depth] != null) return components[depth];
        return null;
    }

    private static ComponentFrame? FindAncestorFrame(ComponentFrame frame, string componentClass)
    {
        for (var current = frame.Parent; current != null; current = current.Parent)
            if (current.Class.Equals(componentClass, StringComparison.OrdinalIgnoreCase)) return current;
        return null;
    }

    private static string? FindAncestorConnection(string?[] connectionNames, int beforeDepth)
    {
        for (var depth = beforeDepth - 1; depth >= 0; depth--)
            if (connectionNames[depth] != null) return connectionNames[depth];
        return null;
    }

    private static XElement CopyCurrentElement(XmlReader reader)
    {
        var element = new XElement(reader.Name);
        if (!reader.HasAttributes) return element;
        while (reader.MoveToNextAttribute()) element.SetAttributeValue(reader.Name, reader.Value);
        reader.MoveToElement();
        return element;
    }

    private static IEnumerable<string> SplitTags(string? value) =>
        (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed class ComponentFrame(
        string id,
        string componentClass,
        string macro,
        string counterpartConnection,
        string parentConnection,
        ComponentFrame? parent)
    {
        public string Id { get; } = id;
        public string Class { get; } = componentClass;
        public string Macro { get; } = macro;
        public string CounterpartConnection { get; } = counterpartConnection;
        public string ParentConnection { get; } = parentConnection;
        public ComponentFrame? Parent { get; } = parent;
        public XElement? Offset { get; set; }
        public bool OffsetIsDefault { get; set; }
    }
}
