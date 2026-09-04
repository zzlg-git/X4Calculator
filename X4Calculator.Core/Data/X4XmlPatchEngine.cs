using System.Collections;
using System.Xml.Linq;
using System.Xml.XPath;

namespace X4Calculator.Core.Data;

public enum X4XmlPatchDiagnosticSeverity
{
    Warning,
    Error
}

public sealed record X4XmlPatchDiagnostic(
    X4XmlPatchDiagnosticSeverity Severity,
    string PackageId,
    string SourcePath,
    string Operation,
    string? Selector,
    string Message);

public sealed record X4XmlPatchResult(
    XDocument Document,
    int AppliedOperations,
    int SkippedOperations,
    IReadOnlyList<X4XmlPatchDiagnostic> Diagnostics);

/// <summary>按文档顺序执行 X4 XML diff；调用方负责确定补丁对应的虚拟文件。</summary>
public sealed class X4XmlPatchEngine
{
    public X4XmlPatchResult Apply(
        XDocument source,
        XDocument patch,
        string packageId,
        string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var patchRoot = patch.Root;
        if (!string.Equals(patchRoot?.Name.LocalName, "diff", StringComparison.Ordinal))
            throw new InvalidDataException($"XML 补丁根节点不是 diff：{sourcePath}");

        var document = new XDocument(source);
        var diagnostics = new List<X4XmlPatchDiagnostic>();
        var applied = 0;
        var skipped = 0;

        foreach (var operation in patchRoot!.Elements())
        {
            var operationName = operation.Name.LocalName;
            var selector = operation.Attribute("sel")?.Value;
            if (operationName is not ("add" or "replace" or "remove"))
            {
                diagnostics.Add(new X4XmlPatchDiagnostic(
                    X4XmlPatchDiagnosticSeverity.Error,
                    packageId,
                    sourcePath,
                    operationName,
                    selector,
                    $"不支持的 X4 diff 操作：{operationName}"));
                skipped++;
                continue;
            }
            if (string.IsNullOrWhiteSpace(selector))
            {
                diagnostics.Add(new X4XmlPatchDiagnostic(
                    X4XmlPatchDiagnosticSeverity.Error,
                    packageId,
                    sourcePath,
                    operationName,
                    selector,
                    "X4 diff 操作缺少 sel。"));
                skipped++;
                continue;
            }

            var condition = operation.Attribute("if")?.Value;
            if (!string.IsNullOrWhiteSpace(condition))
            {
                try
                {
                    if (!EvaluateBoolean(document, condition))
                    {
                        skipped++;
                        continue;
                    }
                }
                catch (Exception ex) when (ex is XPathException or InvalidOperationException)
                {
                    diagnostics.Add(new X4XmlPatchDiagnostic(
                        X4XmlPatchDiagnosticSeverity.Error,
                        packageId,
                        sourcePath,
                        operationName,
                        selector,
                        $"if XPath 无效：{condition}；{ex.Message}"));
                    skipped++;
                    continue;
                }
            }

            IReadOnlyList<XObject> targets;
            try
            {
                targets = SelectTargets(document, selector);
            }
            catch (Exception ex) when (ex is XPathException or InvalidOperationException)
            {
                diagnostics.Add(new X4XmlPatchDiagnostic(
                    X4XmlPatchDiagnosticSeverity.Error,
                    packageId,
                    sourcePath,
                    operationName,
                    selector,
                    $"sel XPath 无效：{ex.Message}"));
                skipped++;
                continue;
            }

            if (targets.Count == 0)
            {
                if (!IsTrue(operation.Attribute("silent")?.Value))
                {
                    diagnostics.Add(new X4XmlPatchDiagnostic(
                        X4XmlPatchDiagnosticSeverity.Warning,
                        packageId,
                        sourcePath,
                        operationName,
                        selector,
                        "选择器没有匹配当前有效 XML。"));
                }
                skipped++;
                continue;
            }

            if (operationName is "replace" or "remove" && HasAncestorAndDescendantTargets(targets))
            {
                diagnostics.Add(CreateOperationError(
                    packageId,
                    sourcePath,
                    operation,
                    "同一操作同时选择了祖先和后代节点，无法保证原子执行。"));
                skipped++;
                continue;
            }

            bool operationApplied;
            try
            {
                operationApplied = operationName switch
                {
                    "add" => ApplyAdd(targets, operation, packageId, sourcePath, diagnostics),
                    "replace" => ApplyReplace(targets, operation, packageId, sourcePath, diagnostics),
                    "remove" => ApplyRemove(targets),
                    _ => false
                };
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Xml.XmlException)
            {
                diagnostics.Add(CreateOperationError(
                    packageId, sourcePath, operation, $"执行 XML 补丁操作失败：{ex.Message}"));
                operationApplied = false;
            }
            if (operationApplied) applied++;
            else skipped++;
        }

        return new X4XmlPatchResult(document, applied, skipped, diagnostics);
    }

    private static IReadOnlyList<XObject> SelectTargets(XDocument document, string selector)
    {
        var result = document.XPathEvaluate(selector);
        if (result is not IEnumerable sequence || result is string)
            throw new InvalidOperationException("选择器结果不是 XML 节点集合。");
        return sequence.Cast<object>().OfType<XObject>().ToList();
    }

    private static bool EvaluateBoolean(XDocument document, string expression)
    {
        var result = document.XPathEvaluate(expression);
        return result switch
        {
            bool value => value,
            string value => value.Length > 0,
            double value => value != 0 && !double.IsNaN(value),
            IEnumerable sequence => sequence.Cast<object>().Any(),
            _ => result != null
        };
    }

    private static bool ApplyAdd(
        IReadOnlyList<XObject> targets,
        XElement operation,
        string packageId,
        string sourcePath,
        ICollection<X4XmlPatchDiagnostic> diagnostics)
    {
        var type = operation.Attribute("type")?.Value;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!type.StartsWith('@') || type.Length == 1)
            {
                diagnostics.Add(CreateOperationError(
                    packageId, sourcePath, operation, $"不支持的 add type：{type}"));
                return false;
            }
            if (targets.Any(target => target is not XElement))
            {
                diagnostics.Add(CreateOperationError(
                    packageId, sourcePath, operation, "add type=@... 只能作用于元素。"));
                return false;
            }

            var attributeName = type[1..];
            try
            {
                _ = System.Xml.XmlConvert.VerifyNCName(attributeName);
            }
            catch (Exception ex) when (ex is ArgumentException or System.Xml.XmlException)
            {
                diagnostics.Add(CreateOperationError(
                    packageId, sourcePath, operation, $"add 属性名称无效：{attributeName}；{ex.Message}"));
                return false;
            }
            foreach (var element in targets.Cast<XElement>())
            {
                if (element.Attribute(attributeName) != null)
                {
                    diagnostics.Add(CreateOperationError(
                        packageId, sourcePath, operation,
                        $"目标元素已经包含属性 {attributeName}。"));
                    return false;
                }
            }
            foreach (var element in targets.Cast<XElement>())
                element.Add(new XAttribute(attributeName, operation.Value));
            return true;
        }

        var position = operation.Attribute("pos")?.Value;
        var payload = GetPayload(operation);
        if (payload.Count == 0)
        {
            // 实际扩展会保留空的 <add sel="..."/> 作为兼容占位。X4 将其视为
            // 无副作用操作；它不应让整个有效文档变成不可加载。
            return true;
        }

        if (string.IsNullOrWhiteSpace(position))
        {
            if (targets.Any(target => target is not XElement))
            {
                diagnostics.Add(CreateOperationError(
                    packageId, sourcePath, operation, "没有 pos 的 add 只能向元素内部追加内容。"));
                return false;
            }
            foreach (var element in targets.Cast<XElement>())
                element.Add(ClonePayload(payload));
            return true;
        }

        switch (position.ToLowerInvariant())
        {
            case "prepend":
                if (targets.Any(target => target is not XElement))
                {
                    diagnostics.Add(CreateOperationError(
                        packageId, sourcePath, operation, "pos=prepend 只能作用于元素。"));
                    return false;
                }
                foreach (var element in targets.Cast<XElement>())
                    element.AddFirst(ClonePayload(payload));
                return true;
            case "before":
                if (targets.Any(target => target is not XNode node || node.Parent == null))
                {
                    diagnostics.Add(CreateOperationError(
                        packageId, sourcePath, operation, "pos=before 的目标必须是有父节点的 XML 节点。"));
                    return false;
                }
                foreach (var node in targets.Cast<XNode>())
                    node.AddBeforeSelf(ClonePayload(payload));
                return true;
            case "after":
                if (targets.Any(target => target is not XNode node || node.Parent == null))
                {
                    diagnostics.Add(CreateOperationError(
                        packageId, sourcePath, operation, "pos=after 的目标必须是有父节点的 XML 节点。"));
                    return false;
                }
                foreach (var node in targets.Cast<XNode>())
                    node.AddAfterSelf(ClonePayload(payload));
                return true;
            default:
                diagnostics.Add(CreateOperationError(
                    packageId, sourcePath, operation, $"不支持的 add pos：{position}"));
                return false;
        }
    }

    private static bool ApplyReplace(
        IReadOnlyList<XObject> targets,
        XElement operation,
        string packageId,
        string sourcePath,
        ICollection<X4XmlPatchDiagnostic> diagnostics)
    {
        var payload = GetPayload(operation);
        if (targets.All(target => target is XAttribute))
        {
            foreach (var attribute in targets.Cast<XAttribute>())
                attribute.Value = operation.Value;
            return true;
        }
        if (targets.Any(target => target is XAttribute))
        {
            diagnostics.Add(CreateOperationError(
                packageId, sourcePath, operation, "replace 不能同时匹配属性和节点。"));
            return false;
        }
        if (payload.Count == 0)
        {
            diagnostics.Add(CreateOperationError(
                packageId, sourcePath, operation, "节点 replace 没有替换内容。"));
            return false;
        }
        if (targets.Any(target => target is not XNode node || node.Parent == null))
        {
            diagnostics.Add(CreateOperationError(
                packageId, sourcePath, operation, "replace 的节点目标必须有父节点。"));
            return false;
        }
        foreach (var node in targets.Cast<XNode>())
            node.ReplaceWith(ClonePayload(payload));
        return true;
    }

    private static bool ApplyRemove(IReadOnlyList<XObject> targets)
    {
        foreach (var target in targets)
        {
            switch (target)
            {
                case XAttribute attribute:
                    attribute.Remove();
                    break;
                case XNode node:
                    node.Remove();
                    break;
            }
        }
        return true;
    }

    private static bool HasAncestorAndDescendantTargets(IReadOnlyList<XObject> targets)
    {
        var selectedNodes = new HashSet<XNode>(targets.OfType<XNode>(), ReferenceEqualityComparer.Instance);
        return selectedNodes.Any(node => node.Ancestors().Any(selectedNodes.Contains));
    }

    private static IReadOnlyList<XNode> GetPayload(XElement operation) => operation.Nodes()
        .Where(node => node is not XText text || !string.IsNullOrWhiteSpace(text.Value))
        .ToList();

    private static IEnumerable<XNode> ClonePayload(IEnumerable<XNode> payload) => payload.Select(CloneNode);

    private static XNode CloneNode(XNode node) => node switch
    {
        XElement element => new XElement(element),
        XCData cdata => new XCData(cdata.Value),
        XText text => new XText(text.Value),
        XComment comment => new XComment(comment.Value),
        XProcessingInstruction instruction => new XProcessingInstruction(instruction.Target, instruction.Data),
        XDocumentType documentType => new XDocumentType(
            documentType.Name,
            documentType.PublicId,
            documentType.SystemId,
            documentType.InternalSubset),
        _ => throw new InvalidDataException($"不支持克隆 XML 节点：{node.NodeType}")
    };

    private static X4XmlPatchDiagnostic CreateOperationError(
        string packageId,
        string sourcePath,
        XElement operation,
        string message) => new(
            X4XmlPatchDiagnosticSeverity.Error,
            packageId,
            sourcePath,
            operation.Name.LocalName,
            operation.Attribute("sel")?.Value,
            message);

    private static bool IsTrue(string? value) =>
        value is not null &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");
}
