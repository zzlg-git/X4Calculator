using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

/// <summary>
/// 产线链节点，表示一个生产模块及其上下游关系。
/// </summary>
public class ChainNode
{
    /// <summary>
    /// 生产模块。
    /// </summary>
    public ProductionModule Module { get; set; }

    /// <summary>
    /// 上游节点（消耗本模块产物的模块）。
    /// </summary>
    public List<ChainNode> Upstream { get; set; } = new();

    /// <summary>
    /// 下游节点（为本模块提供原料的模块）。
    /// </summary>
    public List<ChainNode> Downstream { get; set; } = new();

    /// <summary>
    /// 每分钟净产出（正数：有盈余；负数：有缺口）。
    /// </summary>
    public Dictionary<string, double> NetOutput { get; set; } = new();

    public ChainNode(ProductionModule module)
    {
        Module = module;
    }
}

/// <summary>
/// 完整产线链，包含多个生产模块及其上下游连接关系。
/// </summary>
public class ProductionChain
{
    /// <summary>
    /// 产线中的所有节点。
    /// </summary>
    public List<ChainNode> Nodes { get; } = new();

    /// <summary>
    /// 添加一个生产模块到产线中。
    /// </summary>
    public ChainNode AddModule(ProductionModule module)
    {
        var node = new ChainNode(module);
        Nodes.Add(node);
        return node;
    }

    /// <summary>
    /// 自动匹配上下游关系（基于中间产物的消耗/产出）。
    /// </summary>
    public void AutoMatch()
    {
        // 构建产出映射：wareId -> 产出节点列表
        var producers = new Dictionary<string, List<ChainNode>>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in Nodes)
        {
            if (node.Module.Ware != null)
            {
                var wareId = node.Module.Ware.Name;
                if (!producers.ContainsKey(wareId))
                    producers[wareId] = new();
                producers[wareId].Add(node);

                // 将产出写入 NetOutput
                if (!node.NetOutput.ContainsKey(wareId))
                    node.NetOutput[wareId] = 0;
                node.NetOutput[wareId] += node.Module.Count * (node.Module.Recipe?.OutputPerMinute ?? 0);
            }
        }

        // 消费匹配
        foreach (var node in Nodes)
        {
            if (node.Module.Recipe?.Consumption == null) continue;

            foreach (var (consumedId, amount) in node.Module.Recipe.Consumption)
            {
                var totalConsumed = amount * node.Module.Count / (node.Module.Recipe?.Time ?? 1) * 60;

                // 记录净产出（负值表示消耗）
                if (!node.NetOutput.ContainsKey(consumedId))
                    node.NetOutput[consumedId] = 0;
                node.NetOutput[consumedId] -= totalConsumed;

                // 寻找生产者
                if (producers.TryGetValue(consumedId, out var producerNodes))
                {
                    foreach (var producer in producerNodes)
                    {
                        if (!node.Downstream.Contains(producer))
                            node.Downstream.Add(producer);
                        if (!producer.Upstream.Contains(node))
                            producer.Upstream.Add(node);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 计算整体供需平衡。
    /// </summary>
    public Dictionary<string, double> CalculateBalance()
    {
        var balance = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in Nodes)
        {
            foreach (var (wareId, amount) in node.NetOutput)
            {
                if (!balance.ContainsKey(wareId))
                    balance[wareId] = 0;
                balance[wareId] += amount;
            }
        }

        return balance;
    }
}
