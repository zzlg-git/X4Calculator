using System.Xml;
using System.Xml.Linq;
using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Parsing;

/// <summary>
/// 存档门实例扫描器。
/// 使用 XmlReader 流式读取大存档（不整文件载入内存），
/// 沿 universe → galaxy → cluster → sector → zone → gate 组件链
/// 抓取所有 <c>class="gate"</c> 实例的连接名、代码、宏、组件 id、destination 指向与所在 Zone，
/// 并仅在每个必要层级有显式 offset 时保留 Gate→Zone、Zone→Sector 刚性变换。
///
/// 存档中 gate 结构（连接名小写，与静态门大小写不敏感匹配）：
/// <code>
///   &lt;connection connection="connection_clustergate409to410"&gt;
///     &lt;component class="gate" macro="props_gates_orb_accelerator_01_macro"
///                connection="space" code="RSY-973" id="[0x1a0657b7]"&gt;
///       &lt;connection connection="destination" id="[0x600df5bd]"&gt;
///         &lt;connected connection="[0x600df5be]"/&gt;
///       &lt;/connection&gt;
///     &lt;/component&gt;
///   &lt;/connection&gt;
/// </code>
/// </summary>
public class SavegameGateScanner
{
    /// <summary>
    /// 流式扫描存档中的所有门实例。
    /// </summary>
    /// <param name="saveFilePath">存档文件路径（UTF-8 XML，可能很大）。</param>
    /// <returns>扫描到的门实例列表。</returns>
    public Task<List<SaveGateInstance>> ScanAsync(string saveFilePath) =>
        ScanAsync(saveFilePath, CancellationToken.None);

    public async Task<List<SaveGateInstance>> ScanAsync(string saveFilePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new List<SaveGateInstance>();
        var components = new Stack<ComponentFrame>();
        var connections = new Stack<ConnectionFrame>();

        using var stream = SavegameFile.OpenRead(saveFilePath);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            DtdProcessing = DtdProcessing.Ignore
        });

        while (await reader.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (reader.Name == "connection")
                    {
                        var connection = reader.GetAttribute("connection")
                            ?? reader.GetAttribute("name") ?? reader.GetAttribute("ref");
                        var connectionFrame = new ConnectionFrame(connection ?? string.Empty, reader.GetAttribute("id") ?? string.Empty);
                        connections.Push(connectionFrame);
                        if (components.TryPeek(out var parent) && parent.Class.Equals("gate", StringComparison.OrdinalIgnoreCase))
                        {
                            parent.Connections.Add(connectionFrame);
                            if (string.Equals(connection, "destination", StringComparison.OrdinalIgnoreCase))
                                parent.DestinationComponentId = reader.GetAttribute("target");
                        }
                    }
                    else if (reader.Name == "connected" && connections.TryPeek(out var connectedFrame) &&
                             reader.GetAttribute("connection") is { Length: > 0 } targetConnection)
                    {
                        connectedFrame.Connected.Add(targetConnection);
                    }
                    else if (reader.Name == "component")
                    {
                        components.Push(new ComponentFrame(
                            reader.GetAttribute("class") ?? string.Empty,
                            reader.GetAttribute("macro") ?? string.Empty,
                            reader.GetAttribute("connection") ?? string.Empty,
                            reader.GetAttribute("code") ?? string.Empty,
                            reader.GetAttribute("id") ?? string.Empty,
                            connections.TryPeek(out var connection) ? connection.Name : string.Empty,
                            components.TryPeek(out var parent) ? parent : null,
                            reader.Depth));
                        if (reader.IsEmptyElement)
                        {
                            var frame = components.Pop();
                            if (frame.Class.Equals("gate", StringComparison.OrdinalIgnoreCase))
                                result.Add(CreateGateInstance(frame));
                        }
                    }
                    else if (reader.Name == "offset" && components.TryPeek(out var frame) &&
                             reader.Depth == frame.Depth + 1)
                    {
                        // `default="1"` 表示存档未序列化实际的局部位置与姿态；
                        // 补全这些信息需要宏数据，本扫描器不会自行猜测。
                        // ReadSubtree 会将外层读取器停留在 <offset> 上；若用
                        // XNode.ReadFrom 消费该节点，外层循环就会跳过下一个节点。
                        using var subtree = reader.ReadSubtree();
                        subtree.MoveToContent();
                        var offset = XElement.Load(subtree, LoadOptions.None);
                        frame.SetExplicitOffset(offset);
                    }
                    if (reader.Name == "connection" && reader.IsEmptyElement && connections.Count > 0)
                        connections.Pop();
                    break;

                case XmlNodeType.EndElement:
                    if (reader.Name == "component" && components.Count > 0)
                    {
                        var frame = components.Pop();
                        if (frame.Class.Equals("gate", StringComparison.OrdinalIgnoreCase))
                            result.Add(CreateGateInstance(frame));
                    }
                    else if (reader.Name == "connection")
                    {
                        if (connections.Count > 0) connections.Pop();
                    }
                    break;
            }
        }

        // 存档中的 <connection id> 标识当前门自身的连接，其 <connected>
        // 子节点指向另一个连接；该连接的所属组件才是目标门。
        var owners = result.SelectMany(g => g.Connections.Where(c => c.Id.Length > 0).Select(c => (c.Id, Gate: g)))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Gate).Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var gate in result)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var references = gate.Connections.SelectMany(c => c.ConnectedConnectionIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (references.Length == 0) continue;
            var targets = references.Select(id => owners.TryGetValue(id, out var matches) && matches.Length == 1
                    ? matches[0] : null).ToArray();
            var unique = targets.Where(g => g is not null).Distinct().ToArray();
            gate.DestinationComponentId = targets.All(g => g is not null) && unique.Length == 1 &&
                !ReferenceEquals(unique[0], gate) ? unique[0]!.ComponentId : null;
        }
        return result;
    }

    private static SaveGateInstance CreateGateInstance(ComponentFrame gate)
    {
        var zone = FindAncestor(gate, "zone");
        var sector = FindAncestor(gate, "sector");
        X4RigidTransform? gateZoneTransform = zone == null ? null : ResolveTransform(gate, zone);
        X4RigidTransform? zoneSectorTransform = zone == null || sector == null ? null : ResolveTransform(zone, sector);
        var gateZoneSource = ResolveTransformSource(gate, zone, gateZoneTransform);
        var zoneSectorSource = ResolveTransformSource(zone, sector, zoneSectorTransform);
        X4RigidTransform? gateSectorTransform = gateZoneTransform is { } gateZone && zoneSectorTransform is { } zoneSector
            ? X4RigidTransform.Compose(zoneSector, gateZone)
            : null;

        return new SaveGateInstance
        {
            ConnectionName = gate.ParentConnection,
            Code = gate.Code,
            Macro = gate.Macro,
            ComponentId = gate.Id,
            ZoneId = zone?.Macro ?? string.Empty,
            SectorMacro = sector?.Macro ?? string.Empty,
            GateConnectionName = gate.CounterpartConnection,
            ZoneParentConnectionName = zone?.ParentConnection ?? string.Empty,
            ZoneConnectionName = zone?.CounterpartConnection ?? string.Empty,
            GateZoneTransform = gateZoneTransform,
            GateZoneTransformSource = gateZoneSource,
            ZoneSectorTransform = zoneSectorTransform,
            ZoneSectorTransformSource = zoneSectorSource,
            GateSectorTransform = gateSectorTransform,
            DestinationComponentId = gate.DestinationComponentId,
            Connections = gate.Connections.Select(c => new SaveGateConnection(c.Name, c.Id, c.Connected.ToArray())).ToArray()
        };
    }

    private static ComponentFrame? FindAncestor(ComponentFrame frame, string componentClass)
    {
        for (var current = frame.Parent; current != null; current = current.Parent)
            if (current.Class.Equals(componentClass, StringComparison.OrdinalIgnoreCase)) return current;
        return null;
    }

    private static X4RigidTransform? ResolveTransform(ComponentFrame component, ComponentFrame ancestor)
    {
        var transform = X4RigidTransform.Identity;
        for (var current = component; !ReferenceEquals(current, ancestor); current = current.Parent!)
        {
            if (current.Parent == null || current.ExplicitOffset == null) return null;
            transform = X4RigidTransform.Compose(current.ExplicitOffset.Value, transform);
        }
        return transform;
    }

    private static SaveGateTransformSource ResolveTransformSource(
        ComponentFrame? component,
        ComponentFrame? ancestor,
        X4RigidTransform? transform)
    {
        if (transform != null) return SaveGateTransformSource.ExplicitSave;
        return component != null && ancestor != null && ReferenceEquals(component.Parent, ancestor) &&
               component.OffsetSource == SaveGateTransformSource.MacroDefault
            ? SaveGateTransformSource.MacroDefault
            : SaveGateTransformSource.Unknown;
    }

    private sealed class ComponentFrame(
        string componentClass,
        string macro,
        string counterpartConnection,
        string code,
        string id,
        string parentConnection,
        ComponentFrame? parent,
        int depth)
    {
        public string Class { get; } = componentClass;
        public string Macro { get; } = macro;
        public string CounterpartConnection { get; } = counterpartConnection;
        public string Code { get; } = code;
        public string Id { get; } = id;
        public string ParentConnection { get; } = parentConnection;
        public ComponentFrame? Parent { get; } = parent;
        public int Depth { get; } = depth;
        public X4RigidTransform? ExplicitOffset { get; private set; }
        public SaveGateTransformSource OffsetSource { get; private set; }
        public string? DestinationComponentId { get; set; }
        public List<ConnectionFrame> Connections { get; } = [];

        public void SetExplicitOffset(XElement offset)
        {
            if (ExplicitOffset != null || OffsetSource == SaveGateTransformSource.MacroDefault) return;
            if (offset.Attribute("default")?.Value == "1")
            {
                OffsetSource = SaveGateTransformSource.MacroDefault;
                return;
            }
            ExplicitOffset = X4GeometryDefinitionReader.ReadTransform(offset);
            OffsetSource = SaveGateTransformSource.ExplicitSave;
        }
    }

    private sealed class ConnectionFrame(string name, string id)
    {
        public string Name { get; } = name;
        public string Id { get; } = id;
        public List<string> Connected { get; } = [];
    }
}
