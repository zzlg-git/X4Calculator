using System.Xml;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Parsing;

/// <summary>
/// 存档门实例扫描器。
/// 使用 XmlReader 流式读取大存档（不整文件载入内存），
/// 沿 universe → galaxy → cluster → sector → zone → gate 组件链
/// 抓取所有 <c>class="gate"</c> 实例的连接名、代码、宏、组件 id、destination 指向与所在 Zone。
///
/// 存档中 gate 结构（连接名小写，与静态门大小写不敏感匹配）：
/// <code>
///   &lt;connection connection="connection_clustergate409to410"&gt;
///     &lt;component class="gate" macro="props_gates_orb_accelerator_01_macro"
///                connection="space" code="RSY-973" id="[0x1a0657b7]"&gt;
///       &lt;connection connection="destination" id="[0x600df5bd]"/&gt;
///     &lt;/component&gt;
///   &lt;/connection&gt;
/// </code>
/// </summary>
public class SavegameGateScanner
{
    // 静态数据中已知的门/加速器宏（用于过滤无意义组件，也用于类型判断）
    private static readonly HashSet<string> KnownGateMacros = new(StringComparer.OrdinalIgnoreCase)
    {
        "props_gates_anc_gate_macro",
        "props_gates_orb_accelerator_01_macro"
    };

    /// <summary>
    /// 流式扫描存档中的所有门实例。
    /// </summary>
    /// <param name="saveFilePath">存档文件路径（UTF-8 XML，可能很大）。</param>
    /// <returns>扫描到的门实例列表。</returns>
    public async Task<List<SaveGateInstance>> ScanAsync(string saveFilePath)
    {
        var result = new List<SaveGateInstance>();
        var stack = new Stack<(string Class, string? Macro)>();
        string? currentConnection = null;

        using var reader = XmlReader.Create(saveFilePath, new XmlReaderSettings
        {
            Async = true,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            DtdProcessing = DtdProcessing.Ignore
        });

        while (await reader.ReadAsync())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (reader.Name == "connection")
                    {
                        currentConnection = reader.GetAttribute("connection")
                            ?? reader.GetAttribute("name");
                    }
                    else if (reader.Name == "component")
                    {
                        var cls = reader.GetAttribute("class");
                        stack.Push((cls ?? string.Empty, reader.GetAttribute("macro")));

                        if (string.Equals(cls, "gate", StringComparison.OrdinalIgnoreCase))
                        {
                            var instance = await ReadGateInstanceAsync(reader, currentConnection, stack);
                            if (instance != null) result.Add(instance);
                        }
                    }
                    break;

                case XmlNodeType.EndElement:
                    if (reader.Name == "component" && stack.Count > 0)
                    {
                        stack.Pop();
                        currentConnection = null;
                    }
                    else if (reader.Name == "connection")
                    {
                        currentConnection = null;
                    }
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// 读取门 component 的内部内容（destination 指向）并组装实例信息。
    /// </summary>
    private static async Task<SaveGateInstance?> ReadGateInstanceAsync(
        XmlReader reader,
        string? connectionName,
        Stack<(string Class, string? Macro)> stack)
    {
        var macro = reader.GetAttribute("macro");
        var code = reader.GetAttribute("code");
        var componentId = reader.GetAttribute("id");

        string? destination = null;
        using (var sub = reader.ReadSubtree())
        {
            while (await sub.ReadAsync())
            {
                if (sub.NodeType == XmlNodeType.Element &&
                    sub.Name == "connection" &&
                    string.Equals(
                        sub.GetAttribute("connection") ?? sub.GetAttribute("ref"),
                        "destination",
                        StringComparison.OrdinalIgnoreCase))
                {
                    destination = sub.GetAttribute("id") ?? sub.GetAttribute("target");
                }
            }
        }

        // 所在 Zone：从组件栈向上找最近的 zone 级 component
        var zoneId = stack
            .Reverse()
            .FirstOrDefault(x => string.Equals(x.Class, "zone", StringComparison.OrdinalIgnoreCase))
            .Macro ?? string.Empty;

        return new SaveGateInstance
        {
            ConnectionName = connectionName ?? string.Empty,
            Code = code ?? string.Empty,
            Macro = macro ?? string.Empty,
            ComponentId = componentId ?? string.Empty,
            ZoneId = zoneId,
            DestinationComponentId = destination
        };
    }
}
