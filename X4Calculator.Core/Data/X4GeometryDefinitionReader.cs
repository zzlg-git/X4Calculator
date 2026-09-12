using System.Globalization;
using System.Xml.Linq;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Data;

/// <summary>
/// 从 <see cref="GameDataDB"/> 的最终有效 XML 读取可确认的 macro/component/part 几何。
/// 只解析静态 OBB 与 clscloselink waypoint，不推断缺失尺寸。
/// </summary>
public sealed class X4GeometryDefinitionReader
{
    private static readonly HashSet<string> CollisionSuppressedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "nocollision", "nocollision_jolt", "platformcollision"
    };

    private readonly GameDataDB _gameData;
    private readonly Dictionary<string, XElement> _macros = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XElement> _components = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, X4MacroGeometryResult> _geometry = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PartResolution?> _partRefs = new(StringComparer.OrdinalIgnoreCase);

    public X4GeometryDefinitionReader(GameDataDB gameData)
        : this(gameData, [])
    {
    }

    /// <summary>
    /// 使用显式补充的 macro 文档解析几何。补充定义只覆盖同名 macro，适合读取存档所依赖、
    /// 但未包含在当前 GameData 导出清单中的扩展定义；component 仍由有效数据索引解析。
    /// </summary>
    public X4GeometryDefinitionReader(GameDataDB gameData, IEnumerable<XDocument> supplementalMacroXml)
    {
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        ArgumentNullException.ThrowIfNull(supplementalMacroXml);
        foreach (var document in supplementalMacroXml)
        foreach (var macro in document.Descendants("macro"))
        {
            var name = macro.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!_macros.TryAdd(NormalizeMacroId(name), new XElement(macro)))
                throw new InvalidDataException($"supplemental macro has duplicate definition: {name}");
        }
    }

    public X4MacroGeometryResult ReadMacroGeometry(string macroId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macroId);
        macroId = NormalizeMacroId(macroId);
        if (_geometry.TryGetValue(macroId, out var cached)) return cached;

        try
        {
            var points = new List<Vec3>();
            var macro = GetMacro(macroId);
            var cargoTags = SplitTags(macro.Element("properties")?.Element("cargo")?.Attribute("tags")?.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            CollectMacro(macroId, points, []);
            var bounds = points.Count == 0 ? null : CreateBounds(points);
            var reasons = bounds == null
                ? new[] { $"{macroId}: confirmed-filter geometry is empty" }
                : Array.Empty<string>();
            var groups = ReadCloseLinkGroups(macroId, bounds);
            var result = new X4MacroGeometryResult(
                macroId,
                bounds,
                groups,
                cargoTags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ToArray(),
                reasons);
            _geometry[macroId] = result;
            return result;
        }
        catch (Exception exception) when (exception is InvalidDataException or KeyNotFoundException or FormatException)
        {
            var result = new X4MacroGeometryResult(
                macroId,
                null,
                [],
                [],
                new[] { exception.Message });
            _geometry[macroId] = result;
            return result;
        }
    }

    internal X4RigidTransform ResolveConnectionTransform(string macroId, string connectionName)
    {
        macroId = NormalizeMacroId(macroId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        var macro = GetMacro(macroId);
        var componentId = macro.Element("component")?.Attribute("ref")?.Value;
        if (string.IsNullOrWhiteSpace(componentId))
            throw new InvalidDataException($"{macroId}: missing component ref");
        var component = GetComponent(componentId);

        var allMacroConnections = macro.Element("connections")?.Elements("connection").ToArray() ?? [];
        var namedMacroConnections = allMacroConnections
            .Where(connection => string.Equals(
                connection.Attribute("name")?.Value, connectionName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (namedMacroConnections.Length > 1)
            throw new InvalidDataException(
                $"{macroId}: connection {connectionName} has {namedMacroConnections.Length} named macro definitions");

        var namedMacroConnection = namedMacroConnections.SingleOrDefault();
        var unnamedOverrides = namedMacroConnection == null
            ? allMacroConnections.Where(connection =>
                    string.IsNullOrWhiteSpace(connection.Attribute("name")?.Value) &&
                    string.Equals(connection.Attribute("ref")?.Value, connectionName,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : [];
        if (unnamedOverrides.Length > 1)
            throw new InvalidDataException(
                $"{macroId}: connection {connectionName} has {unnamedOverrides.Length} unnamed macro overrides");

        var macroConnection = namedMacroConnection ?? unnamedOverrides.SingleOrDefault();
        var overrideOffset = macroConnection?.Element("offset");
        if (overrideOffset != null) return ReadTransform(overrideOffset);

        var componentConnectionName = namedMacroConnection?.Attribute("ref")?.Value ?? connectionName;

        var componentConnections = component.Element("connections")?.Elements("connection")
            .Where(connection => string.Equals(
                connection.Attribute("name")?.Value, componentConnectionName, StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];
        if (componentConnections.Length != 1)
            throw new InvalidDataException(
                $"{macroId}: connection {componentConnectionName} has {componentConnections.Length} component definitions");
        return ReadTransform(componentConnections[0].Element("offset"));
    }

    internal X4RigidTransform ResolveComponentConnectionTransform(string componentId, string connectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        var component = GetComponent(componentId);
        var connections = component.Element("connections")?.Elements("connection")
            .Where(connection => string.Equals(
                connection.Attribute("name")?.Value, connectionName, StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];
        if (connections.Length != 1)
            throw new InvalidDataException(
                $"{componentId}: connection {connectionName} has {connections.Length} component definitions");
        return ReadTransform(connections[0].Element("offset"));
    }

    internal XElement GetMacroDefinition(string macroId) => new(GetMacro(NormalizeMacroId(macroId)));

    internal XElement GetComponentDefinitionForMacro(string macroId)
    {
        var macro = GetMacro(NormalizeMacroId(macroId));
        var componentId = macro.Element("component")?.Attribute("ref")?.Value;
        if (string.IsNullOrWhiteSpace(componentId))
            throw new InvalidDataException($"{macroId}: missing component ref");
        return new XElement(GetComponent(componentId));
    }

    private void CollectMacro(
        string macroId,
        List<Vec3> points,
        HashSet<string> chain)
    {
        if (!chain.Add(macroId))
            throw new InvalidDataException($"macro reference cycle: {string.Join(" -> ", chain)} -> {macroId}");
        try
        {
            var macro = GetMacro(macroId);
            var componentId = macro.Element("component")?.Attribute("ref")?.Value;
            if (string.IsNullOrWhiteSpace(componentId))
                throw new InvalidDataException($"{macroId}: missing component ref");
            var component = GetComponent(componentId);
            var connections = component.Element("connections")?.Elements("connection").ToArray() ?? [];
            var connectionsByName = connections
                .Where(connection => !string.IsNullOrWhiteSpace(connection.Attribute("name")?.Value))
                .GroupBy(connection => connection.Attribute("name")!.Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

            foreach (var connection in connections)
            {
                var transform = ReadTransform(connection.Element("offset"));
                var localTags = SplitTags(connection.Attribute("tags")?.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var part in connection.Element("parts")?.Elements("part") ?? [])
                {
                    var resolved = ResolvePart(part, localTags, []);
                    if (resolved == null || CollisionSuppressedTags.Overlaps(resolved.Tags)) continue;
                    foreach (var corner in Corners(resolved.Bounds.Center, resolved.Bounds.HalfExtents))
                        points.Add(transform.TransformPoint(corner));
                }
            }

            foreach (var macroConnection in macro.Element("connections")?.Elements("connection")
                         .Where(connection => connection.Element("macro") != null) ?? [])
            {
                var parentConnectionName = macroConnection.Attribute("ref")?.Value;
                if (string.IsNullOrWhiteSpace(parentConnectionName) ||
                    !connectionsByName.TryGetValue(parentConnectionName, out var parentConnections) ||
                    parentConnections.Length != 1)
                {
                    throw new InvalidDataException(
                        $"{macroId}: missing unique parent connection {parentConnectionName}");
                }

                var childReference = macroConnection.Element("macro")!;
                var childId = NormalizeMacroId(childReference.Attribute("ref")?.Value ?? string.Empty);
                var counterpartName = childReference.Attribute("connection")?.Value;
                if (string.IsNullOrWhiteSpace(childId) || string.IsNullOrWhiteSpace(counterpartName))
                    throw new InvalidDataException($"{macroId}: child macro connection is incomplete");
                var child = GetMacro(childId);
                var childComponentId = child.Element("component")?.Attribute("ref")?.Value;
                if (string.IsNullOrWhiteSpace(childComponentId))
                    throw new InvalidDataException($"{childId}: missing component ref");
                var childComponent = GetComponent(childComponentId);
                var counterparts = childComponent.Element("connections")?.Elements("connection")
                    .Where(connection => string.Equals(
                        connection.Attribute("name")?.Value, counterpartName, StringComparison.OrdinalIgnoreCase))
                    .ToArray() ?? [];
                if (counterparts.Length != 1)
                    throw new InvalidDataException(
                        $"{macroId}: child {childId} counterpart {counterpartName} definitions={counterparts.Length}");

                var parentOffset = macroConnection.Element("offset") ?? parentConnections[0].Element("offset");
                var childToParent = X4RigidTransform.Compose(
                    ReadTransform(parentOffset),
                    ReadTransform(counterparts[0].Element("offset")).Inverse());
                var childPoints = new List<Vec3>();
                CollectMacro(childId, childPoints, chain);
                if (childPoints.Count == 0) continue;
                var childBounds = CreateBounds(childPoints);
                foreach (var corner in Corners(childBounds.Center, childBounds.HalfExtents))
                    points.Add(childToParent.TransformPoint(corner));
            }
        }
        finally
        {
            chain.Remove(macroId);
        }
    }

    private IReadOnlyList<X4CloseLinkGroup> ReadCloseLinkGroups(
        string macroId,
        X4AxisAlignedBounds? bounds)
    {
        var macro = GetMacro(macroId);
        var componentId = macro.Element("component")?.Attribute("ref")?.Value;
        if (string.IsNullOrWhiteSpace(componentId)) return [];
        var component = GetComponent(componentId);
        var center = bounds?.Center ?? Vec3.Zero;
        var half = bounds == null
            ? new Vec3(1, 1, 1)
            : new Vec3(
                Math.Max(bounds.HalfExtents.X, 1),
                Math.Max(bounds.HalfExtents.Y, 1),
                Math.Max(bounds.HalfExtents.Z, 1));
        var groups = new Dictionary<int, List<Vec3>>();
        foreach (var waypoint in component.Element("layers")?.Elements("layer")
                     .SelectMany(layer => layer.Element("waypoints")?.Elements("waypoint") ?? []) ?? [])
        {
            if (!SplitTags(waypoint.Attribute("tags")?.Value).Contains("closelink", StringComparer.OrdinalIgnoreCase))
                continue;
            var point = ReadPosition(waypoint);
            var normalizedX = (point.X - center.X) / half.X;
            var normalizedZ = (point.Z - center.Z) / half.Z;
            var side = Math.Abs(normalizedX) > Math.Abs(normalizedZ)
                ? normalizedX > 0 ? 8 : 4
                : normalizedZ > 0 ? 1 : 2;
            if (!groups.TryGetValue(side, out var values)) groups[side] = values = [];
            values.Add(point);
        }

        return groups.OrderBy(group => group.Key).Select(group => new X4CloseLinkGroup(
            group.Key,
            group.Value.ToArray(),
            new Vec3(
                group.Value.Average(point => point.X),
                group.Value.Average(point => point.Y),
                group.Value.Average(point => point.Z)))).ToArray();
    }

    private PartResolution? ResolvePart(
        XElement part,
        IReadOnlySet<string> localTags,
        HashSet<string> chain)
    {
        PartResolution? referenced = null;
        var reference = part.Attribute("ref")?.Value;
        if (!string.IsNullOrWhiteSpace(reference)) referenced = ResolvePartReference(reference, chain);
        var localBounds = ReadPartBounds(part);
        var bounds = localBounds ?? referenced?.Bounds;
        if (bounds == null) return null;
        var referencedTags = referenced == null
            ? Enumerable.Empty<string>()
            : referenced.Tags.AsEnumerable();
        var tags = localTags.Concat(referencedTags)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new PartResolution(bounds, tags);
    }

    private PartResolution? ResolvePartReference(string reference, HashSet<string> chain)
    {
        if (_partRefs.TryGetValue(reference, out var cached)) return cached;
        if (!chain.Add(reference))
            throw new InvalidDataException($"part reference cycle: {string.Join(" -> ", chain)} -> {reference}");
        try
        {
            var separator = reference.LastIndexOf('.');
            if (separator <= 0 || separator == reference.Length - 1)
                throw new InvalidDataException($"invalid part reference: {reference}");
            var componentId = reference[..separator];
            var partName = reference[(separator + 1)..];
            var component = GetComponent(componentId);
            var matches = component.Element("connections")?.Elements("connection")
                .SelectMany(connection => connection.Element("parts")?.Elements("part") ?? [])
                .Where(part => string.Equals(
                    part.Attribute("name")?.Value, partName, StringComparison.OrdinalIgnoreCase))
                .ToArray() ?? [];
            if (matches.Length != 1)
                throw new InvalidDataException($"{reference}: referenced part definitions={matches.Length}");
            var sourcePart = matches[0];
            var sourceConnection = sourcePart.Parent?.Parent;
            var tags = SplitTags(sourceConnection?.Attribute("tags")?.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nested = ResolvePart(sourcePart, tags, chain);
            _partRefs[reference] = nested;
            return nested;
        }
        finally
        {
            chain.Remove(reference);
        }
    }

    private static X4AxisAlignedBounds? ReadPartBounds(XElement part)
    {
        var size = part.Element("size");
        var maximum = size?.Element("max");
        var center = size?.Element("center");
        if (maximum == null || center == null) return null;
        var centerValue = ReadPosition(center);
        var halfExtents = ReadPosition(maximum);
        return new X4AxisAlignedBounds(
            new Vec3(centerValue.X - halfExtents.X, centerValue.Y - halfExtents.Y, centerValue.Z - halfExtents.Z),
            new Vec3(centerValue.X + halfExtents.X, centerValue.Y + halfExtents.Y, centerValue.Z + halfExtents.Z),
            centerValue,
            halfExtents);
    }

    internal static X4RigidTransform ReadTransform(XElement? offset)
    {
        if (offset == null) return X4RigidTransform.Identity;
        var position = ReadPosition(offset.Element("position"));
        var quaternion = offset.Element("quaternion");
        var rotation = offset.Element("rotation");
        var matrix = quaternion != null
            ? QuaternionMatrix(
                ReadNumber(quaternion, "qx"), ReadNumber(quaternion, "qy"),
                ReadNumber(quaternion, "qz"), ReadNumber(quaternion, "qw", 1))
            : rotation != null
                ? EulerMatrix(
                    ReadNumber(rotation, "yaw"),
                    ReadNumber(rotation, "pitch"),
                    ReadNumber(rotation, "roll"))
                : X4RotationMatrix.Identity;
        return new X4RigidTransform(matrix, position);
    }

    internal static X4AxisAlignedBounds CreateBounds(IReadOnlyCollection<Vec3> points)
    {
        if (points.Count == 0) throw new ArgumentException("At least one point is required.", nameof(points));
        var minimum = new Vec3(points.Min(point => point.X), points.Min(point => point.Y), points.Min(point => point.Z));
        var maximum = new Vec3(points.Max(point => point.X), points.Max(point => point.Y), points.Max(point => point.Z));
        var center = new Vec3(
            (minimum.X + maximum.X) / 2,
            (minimum.Y + maximum.Y) / 2,
            (minimum.Z + maximum.Z) / 2);
        return new X4AxisAlignedBounds(
            minimum,
            maximum,
            center,
            new Vec3(
                (maximum.X - minimum.X) / 2,
                (maximum.Y - minimum.Y) / 2,
                (maximum.Z - minimum.Z) / 2));
    }

    internal static IReadOnlyList<Vec3> Corners(Vec3 center, Vec3 halfExtents)
    {
        var result = new List<Vec3>(8);
        foreach (var x in new[] { -1, 1 })
        foreach (var y in new[] { -1, 1 })
        foreach (var z in new[] { -1, 1 })
            result.Add(new Vec3(
                center.X + x * halfExtents.X,
                center.Y + y * halfExtents.Y,
                center.Z + z * halfExtents.Z));
        return result;
    }

    private XElement GetMacro(string macroId)
    {
        if (_macros.TryGetValue(macroId, out var cached)) return cached;
        var document = _gameData.GetEffectiveMacroXml(macroId);
        var matches = document.Descendants("macro")
            .Where(element => string.Equals(
                element.Attribute("name")?.Value, macroId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException($"{macroId}: macro definitions={matches.Length}");
        return _macros[macroId] = new XElement(matches[0]);
    }

    private XElement GetComponent(string componentId)
    {
        if (_components.TryGetValue(componentId, out var cached)) return cached;
        var document = _gameData.GetEffectiveComponentXml(componentId);
        var matches = document.Descendants("component")
            .Where(element => string.Equals(
                element.Attribute("name")?.Value, componentId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException($"{componentId}: component definitions={matches.Length}");
        return _components[componentId] = new XElement(matches[0]);
    }

    private static X4RotationMatrix QuaternionMatrix(double x, double y, double z, double w)
    {
        var magnitude = Math.Sqrt(x * x + y * y + z * z + w * w);
        if (magnitude == 0) return X4RotationMatrix.Identity;
        x /= magnitude;
        y /= magnitude;
        z /= magnitude;
        w /= magnitude;
        return new X4RotationMatrix(
            new Vec3(1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)),
            new Vec3(2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)),
            new Vec3(2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)));
    }

    private static X4RotationMatrix EulerMatrix(double yawDegrees, double pitchDegrees, double rollDegrees)
    {
        var yaw = DegreesToRadians(yawDegrees);
        var pitch = DegreesToRadians(pitchDegrees);
        var roll = DegreesToRadians(rollDegrees);
        var rotateY = new X4RotationMatrix(
            new Vec3(Math.Cos(yaw), 0, Math.Sin(yaw)),
            new Vec3(0, 1, 0),
            new Vec3(-Math.Sin(yaw), 0, Math.Cos(yaw)));
        var rotateX = new X4RotationMatrix(
            new Vec3(1, 0, 0),
            new Vec3(0, Math.Cos(pitch), -Math.Sin(pitch)),
            new Vec3(0, Math.Sin(pitch), Math.Cos(pitch)));
        var rotateZ = new X4RotationMatrix(
            new Vec3(Math.Cos(roll), -Math.Sin(roll), 0),
            new Vec3(Math.Sin(roll), Math.Cos(roll), 0),
            new Vec3(0, 0, 1));
        return rotateY * (rotateX * rotateZ);
    }

    private static double DegreesToRadians(double value) => value * Math.PI / 180;

    private static Vec3 ReadPosition(XElement? element) => element == null
        ? Vec3.Zero
        : new Vec3(ReadNumber(element, "x"), ReadNumber(element, "y"), ReadNumber(element, "z"));

    private static double ReadNumber(XElement element, string attribute, double defaultValue = 0)
    {
        var text = element.Attribute(attribute)?.Value;
        if (string.IsNullOrWhiteSpace(text)) return defaultValue;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ||
            !double.IsFinite(result))
            throw new FormatException($"{element.Name}@{attribute} is not a finite number: {text}");
        return result;
    }

    private static IEnumerable<string> SplitTags(string? value) =>
        (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeMacroId(string value) =>
        value.StartsWith("macro.", StringComparison.OrdinalIgnoreCase) ? value["macro.".Length..] : value;

    private sealed record PartResolution(X4AxisAlignedBounds Bounds, IReadOnlySet<string> Tags);
}
