using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

/// <summary>
/// 从空间站当前已建模块解析可用于货运的泊位尺寸和仓储类型。
/// </summary>
public sealed class StationTransportCapabilityCalculator
{
    private readonly GameDataDB _gameData;

    public StationTransportCapabilityCalculator(GameDataDB gameData)
    {
        _gameData = gameData;
    }

    /// <summary>
    /// 计算空间站当前可用的运输设施。若保存了施工实例，只计入 operational 模块；
    /// 规划站或已由界面生成的已建快照没有施工实例时，直接使用模块数量。
    /// </summary>
    public StationTransportCapability Calculate(Station station)
    {
        ArgumentNullException.ThrowIfNull(station);

        var operationalCounts = station.ModuleConstructions.Count == 0
            ? null
            : station.ModuleConstructions
                .Where(module => module.State == StationModuleConstructionState.Operational)
                .GroupBy(module => module.ModuleId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var hasPort = false;
        var supportsLargeShips = false;
        var supportsSmallShips = false;
        var storageTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var module in EnumerateModules(station))
        {
            if (module.Count <= 0) continue;
            var count = operationalCounts is null
                ? module.Count
                : Math.Min(module.Count, operationalCounts.GetValueOrDefault(module.ModuleId));
            if (count <= 0 ||
                !_gameData.StationModuleDefinitions.TryGetValue(module.ModuleId, out var definition)) continue;

            var isDockArea = definition.Kind.Equals("dockarea", StringComparison.OrdinalIgnoreCase);
            var isPier = definition.Kind.Equals("pier", StringComparison.OrdinalIgnoreCase);
            var isBuildModule = definition.Kind.Equals("buildmodule", StringComparison.OrdinalIgnoreCase);
            hasPort |= isDockArea || isPier || isBuildModule;
            supportsLargeShips |= isPier || definition.BuildClasses.Any(buildClass =>
                buildClass.Equals("ship_l", StringComparison.OrdinalIgnoreCase) ||
                buildClass.Equals("ship_xl", StringComparison.OrdinalIgnoreCase));
            supportsSmallShips |= isDockArea || definition.BuildClasses.Any(buildClass =>
                buildClass.Equals("ship_s", StringComparison.OrdinalIgnoreCase) ||
                buildClass.Equals("ship_m", StringComparison.OrdinalIgnoreCase));

            if (definition.StorageCapacity <= 0) continue;
            foreach (var storageType in definition.StorageTypes.Where(type => !string.IsNullOrWhiteSpace(type)))
                storageTypes.Add(storageType);
        }

        return new StationTransportCapability(
            hasPort,
            supportsLargeShips,
            supportsSmallShips,
            storageTypes);
    }

    private static IEnumerable<(string ModuleId, int Count)> EnumerateModules(Station station) =>
        station.Modules.Select(module => (module.ModuleId, module.Count))
            .Concat(station.AdditionalModules.Select(module => (module.ModuleId, module.Count)));
}

/// <summary>空间站当前已建模块提供的货运能力。</summary>
public sealed record StationTransportCapability(
    bool HasPort,
    bool SupportsLargeShips,
    bool SupportsSmallShips,
    IReadOnlySet<string> StorageTypes)
{
    public bool HasStorage => StorageTypes.Count > 0;

    public bool SupportsStorage(string storageType) =>
        !string.IsNullOrWhiteSpace(storageType) && StorageTypes.Contains(storageType);
}
