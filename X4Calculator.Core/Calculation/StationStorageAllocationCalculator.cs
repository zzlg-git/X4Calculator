using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Calculation;

/// <summary>
/// 按 X4 自动仓储分配的可观测规则估算每种货物的数量上限：同运输类型共享容量，
/// 生产相关货物按每分钟吞吐体积加权；纯贸易货物平均分配。
/// </summary>
public sealed class StationStorageAllocationCalculator
{
    private const double ContainerTradeShare = 0.15;
    private const double BulkTradeShare = 0.60;
    // X4 9.00 的 economy/storage@factor=0.1 会使多产品队列的仓储分配低于简单的等运行时间吞吐量估算。
    // 在引擎侧公式可观测前，此系数根据双产品废料回收模块样本校准。
    private const double MultiProductOutputWeightFactor = 0.77;
    private const double WorkforceStorageMinutes = 240;
    private readonly GameDataDB _gameData;
    private readonly StationProductionPlanner _planner;

    public StationStorageAllocationCalculator(GameDataDB gameData)
    {
        _gameData = gameData;
        _planner = new StationProductionPlanner(gameData);
    }

    public IReadOnlyDictionary<string, StationWareStorageAllocation> Calculate(
        Station station,
        IEnumerable<string> wareIds,
        bool operationalOnly,
        IEnumerable<string>? playerBlueprintWareIds = null,
        string buildMethod = "default")
    {
        var effective = CreateEffectiveStation(station, operationalOnly);
        var shipBuildRequirements = CalculateShipBuildRequirementsCore(
            effective, playerBlueprintWareIds ?? [], NormalizeShipBuildMethod(buildMethod));
        var baseModuleContributions = _planner.CalculateModuleContributions(
            effective, 0, useSavedWorkforceEfficiency: false);
        var producedWareIds = baseModuleContributions.Where(item => item.PerMinute > 0)
            .Select(item => item.WareId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var workforceDemandVolumes = CalculateWorkforceDemandVolumes(effective);
        var hasProducedWorkforceWare = workforceDemandVolumes.Keys.Any(producedWareIds.Contains);
        var moduleContributions = hasProducedWorkforceWare
            ? _planner.CalculateModuleContributions(effective, effective.CurrentWorkforce)
            : baseModuleContributions;
        var contributions = moduleContributions
            .GroupBy(item => item.WareId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => Math.Max(
                    group.Where(item => item.PerMinute > 0).Sum(item => item.PerMinute),
                    -group.Where(item => item.PerMinute < 0).Sum(item => item.PerMinute)),
                StringComparer.OrdinalIgnoreCase);
        ApplyMultiProductOutputWeight(effective, contributions);
        var workforceReservations = workforceDemandVolumes
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        var manualQuantityLimits = station.WareSettings
            .Where(setting => setting.StorageAllocationStatus == StationStorageAllocationStatus.Manual &&
                              setting.StorageAllocationOverride is >= 0)
            .ToDictionary(setting => setting.WareId, setting => setting.StorageAllocationOverride!.Value,
                StringComparer.OrdinalIgnoreCase);
        var wares = wareIds.Concat(workforceDemandVolumes.Keys).Concat(shipBuildRequirements.Keys)
            .Concat(manualQuantityLimits.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(_gameData.FindByWareId).OfType<Ware>().ToArray();
        var explicitTradeWareIds = station.WareSettings.Where(setting =>
                setting.IsTradeWare || setting.BuyEnabled == true || setting.SellEnabled ||
                setting.BuyOffer != null || setting.SellOffer != null)
            .Select(setting => setting.WareId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, StationWareStorageAllocation>(StringComparer.OrdinalIgnoreCase);

        foreach (var transportGroup in wares.GroupBy(ware => ware.Transport, StringComparer.OrdinalIgnoreCase))
        {
            var capacity = CalculateStorageCapacity(station, transportGroup.Key, operationalOnly);
            var manualItems = transportGroup
                .Where(ware => manualQuantityLimits.ContainsKey(ware.Id))
                .Select(ware => new
                {
                    Ware = ware,
                    Quantity = manualQuantityLimits[ware.Id],
                    Volume = manualQuantityLimits[ware.Id] * Math.Max(ware.Volume, 0)
                })
                .ToArray();
            foreach (var item in manualItems)
            {
                result[item.Ware.Id] = new StationWareStorageAllocation(
                    item.Ware.Id, item.Ware.Transport, item.Quantity, item.Volume, capacity);
            }
            // 原生 cargo.target 自动池与手动 stock-limit override 相互独立；手动覆盖不挤占自动池。
            var automaticPoolCapacity = capacity;
            var totalReservedVolume = workforceReservations
                .Where(item => string.Equals(_gameData.FindByWareId(item.Key)?.Transport,
                    transportGroup.Key, StringComparison.OrdinalIgnoreCase) &&
                    !manualQuantityLimits.ContainsKey(item.Key))
                .Sum(item => item.Value);
            var reservationScale = totalReservedVolume > automaticPoolCapacity && totalReservedVolume > 0
                ? automaticPoolCapacity / totalReservedVolume
                : 1d;
            var scaledReservedVolume = totalReservedVolume * reservationScale;
            var weighted = transportGroup.Select(ware => new WeightedWare(
                ware,
                contributions.GetValueOrDefault(ware.Id) * Math.Max(ware.Volume, 0),
                workforceReservations.GetValueOrDefault(ware.Id) * reservationScale))
                .Where(item => !manualQuantityLimits.ContainsKey(item.Ware.Id))
                .ToArray();
            var shipBuildItems = transportGroup
                .Where(ware => shipBuildRequirements.ContainsKey(ware.Id) &&
                               !manualQuantityLimits.ContainsKey(ware.Id))
                .Select(ware => new ShipBuildWare(ware, shipBuildRequirements[ware.Id]))
                .Where(item => item.RawQuantity > 0)
                .ToArray();
            foreach (var item in weighted.Where(item => item.ReservedVolume > 0))
            {
                var quantity = item.Ware.Volume > 0
                    ? (long)Math.Floor(item.ReservedVolume / item.Ware.Volume)
                    : 0;
                result[item.Ware.Id] = new StationWareStorageAllocation(
                    item.Ware.Id, item.Ware.Transport, quantity,
                    quantity * item.Ware.Volume, capacity);
            }

            var production = weighted.Where(item =>
                item.Weight > 0 &&
                (item.ReservedVolume <= 0 || producedWareIds.Contains(item.Ware.Id))).ToArray();
            var trade = weighted.Where(item => item.Weight <= 0 && item.ReservedVolume <= 0 &&
                                              !shipBuildRequirements.ContainsKey(item.Ware.Id) &&
                                              (shipBuildRequirements.Count == 0 ||
                                               explicitTradeWareIds.Contains(item.Ware.Id))).ToArray();
            var hasNonTradeGroup = production.Length > 0 || shipBuildItems.Length > 0;
            var remainingCapacity = Math.Max(0, automaticPoolCapacity - scaledReservedVolume);
            double tradeCapacity;
            double productionCapacity;
            double shipBuildCapacity;
            if (shipBuildItems.Length > 0 && production.Length > 0 && trade.Length > 0 &&
                transportGroup.Key.Equals("container", StringComparison.OrdinalIgnoreCase))
            {
                // X4 9.00 原生三组分支：生产/贸易/造船 = 55%/5%/40%。
                tradeCapacity = Math.Floor(remainingCapacity * Math.BitDecrement(0.05d));
                productionCapacity = Math.Round(remainingCapacity * 0.55d, MidpointRounding.AwayFromZero);
                shipBuildCapacity = Math.Max(0, remainingCapacity - tradeCapacity - productionCapacity);
            }
            else if (shipBuildItems.Length > 0 && production.Length == 0 && trade.Length > 0 &&
                transportGroup.Key.Equals("container", StringComparison.OrdinalIgnoreCase))
            {
                // 匹配 X4 9.00 原生换算边界：在 31,926,740m³ 样本中，
                // 为贸易保留 7,981,684m³，为造船保留 23,945,056m³。
                tradeCapacity = Math.Floor(remainingCapacity * Math.BitDecrement(0.25d));
                productionCapacity = 0;
                shipBuildCapacity = remainingCapacity - tradeCapacity;
            }
            else
            {
                var tradeShare = hasNonTradeGroup && trade.Length > 0
                    ? GetTradeShare(transportGroup.Key)
                    : trade.Length > 0 ? 1d : 0d;
                tradeCapacity = remainingCapacity * tradeShare;
                var nonTradeCapacity = remainingCapacity - tradeCapacity;
                shipBuildCapacity = shipBuildItems.Length > 0
                    ? production.Length > 0 ? nonTradeCapacity / 2 : nonTradeCapacity
                    : 0;
                productionCapacity = production.Length > 0
                    ? shipBuildItems.Length > 0 ? nonTradeCapacity / 2 : nonTradeCapacity
                    : 0;
            }
            Allocate(production, productionCapacity, capacity, result);
            AllocateShipBuild(shipBuildItems, shipBuildCapacity, capacity, result);
            Allocate(trade, tradeCapacity, capacity, result);
        }

        foreach (var (wareId, demandVolume) in workforceDemandVolumes)
        {
            if (!result.TryGetValue(wareId, out var allocation)) continue;
            var ware = _gameData.FindByWareId(wareId);
            var automaticBuyAmount = ware is { Volume: > 0 }
                ? (long)Math.Floor(demandVolume / ware.Volume)
                : 0;
            result[wareId] = allocation with
            {
                IsWorkforceConsumable = true,
                WorkforceAutomaticBuyAmount = automaticBuyAmount
            };
        }

        return result;
    }

    private void ApplyMultiProductOutputWeight(
        Station station,
        IDictionary<string, double> contributions)
    {
        foreach (var module in station.Modules)
        {
            if (!_gameData.StationModuleDefinitions.TryGetValue(module.ModuleId, out var definition) ||
                definition.Products.Count <= 1) continue;
            foreach (var product in definition.Products)
            {
                if (contributions.TryGetValue(product.WareId, out var weight))
                    contributions[product.WareId] = weight * MultiProductOutputWeightFactor;
            }
        }
    }

    private IReadOnlyDictionary<string, double> CalculateWorkforceDemandVolumes(Station source)
    {
        var requirements = _planner.CalculateWorkforceRequirements(source);
        var workforce = _planner.CalculateWorkforce(source);
        var effectiveWorkforce = Math.Min(requirements.CurrentRequired, workforce.Capacity);
        if (effectiveWorkforce <= 0)
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var capacityByRace = workforce.CapacityByRace
            .Where(item => !string.IsNullOrWhiteSpace(item.Race) && item.Capacity > 0)
            .ToDictionary(item => item.Race, item => (double)item.Capacity, StringComparer.OrdinalIgnoreCase);
        var totalRaceCapacity = capacityByRace.Values.Sum();
        if (totalRaceCapacity <= 0)
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (race, raceCapacity) in capacityByRace)
        {
            var method = NormalizeWorkforceMethod(race);
            var workunit = _gameData.FindByWareId("workunit_busy");
            var recipe = workunit?.Production?.FirstOrDefault(item =>
                item.Method.Equals(method, StringComparison.OrdinalIgnoreCase));
            if (recipe is not { Time: > 0, Amount: > 0 } || recipe.Consumption == null) continue;
            var racePopulation = effectiveWorkforce * raceCapacity / totalRaceCapacity;
            foreach (var (wareId, amount) in recipe.Consumption)
            {
                if (wareId.StartsWith("secondary:", StringComparison.OrdinalIgnoreCase)) continue;
                var perMinute = amount / recipe.Amount / recipe.Time * 60 * racePopulation;
                var ware = _gameData.FindByWareId(wareId);
                if (ware is not { Volume: > 0 }) continue;
                result.TryGetValue(wareId, out var current);
                result[wareId] = current + perMinute * WorkforceStorageMinutes * ware.Volume;
            }
        }
        return result;
    }

    private static string NormalizeWorkforceMethod(string race) => race.ToLowerInvariant() switch
    {
        "argon" => "default",
        "paranid" => "paranid",
        "teladi" => "teladi",
        "split" => "split",
        "terran" => "terran",
        "boron" => "boron",
        _ => "default"
    };

    private static double GetTradeShare(string transport) =>
        transport.Equals("container", StringComparison.OrdinalIgnoreCase)
            ? ContainerTradeShare
            : BulkTradeShare;

    private static void Allocate(
        IReadOnlyCollection<WeightedWare> items,
        double allocatedCapacity,
        long transportCapacity,
        IDictionary<string, StationWareStorageAllocation> result)
    {
        if (items.Count == 0) return;
        var totalWeight = items.Sum(item => item.Weight);
        foreach (var item in items)
        {
            var ware = item.Ware;
            var share = totalWeight > 0 ? item.Weight / totalWeight : 1d / items.Count;
            var totalVolume = item.ReservedVolume + allocatedCapacity * share;
            var quantity = ware.Volume > 0
                ? (long)Math.Floor(totalVolume / ware.Volume)
                : 0;
            result[ware.Id] = new StationWareStorageAllocation(
                ware.Id, ware.Transport, quantity, quantity * ware.Volume, transportCapacity);
        }
    }

    private sealed record WeightedWare(Ware Ware, double Weight, double ReservedVolume);
    private sealed record ShipBuildWare(Ware Ware, double RawQuantity);

    private static void AllocateShipBuild(
        IReadOnlyCollection<ShipBuildWare> items,
        double allocatedCapacity,
        long transportCapacity,
        IDictionary<string, StationWareStorageAllocation> result)
    {
        if (items.Count == 0 || allocatedCapacity <= 0) return;
        var rawVolume = items.Sum(item => item.RawQuantity * Math.Max(item.Ware.Volume, 0));
        var scale = rawVolume > 0 ? Math.Min(1d, allocatedCapacity / rawVolume) : 0;
        foreach (var item in items)
        {
            var fullStorageLimit = item.Ware.Volume > 0
                ? (long)Math.Floor((double)transportCapacity / item.Ware.Volume)
                : 0;
            var quantity = Math.Min((long)Math.Floor(item.RawQuantity * scale), fullStorageLimit);
            result.TryGetValue(item.Ware.Id, out var current);
            if (current != null)
                quantity += current.QuantityLimit;
            result[item.Ware.Id] = new StationWareStorageAllocation(
                item.Ware.Id, item.Ware.Transport, quantity,
                quantity * item.Ware.Volume, transportCapacity,
                current?.IsWorkforceConsumable ?? false,
                current?.WorkforceAutomaticBuyAmount);
        }
    }

    public IReadOnlyDictionary<string, double> CalculateShipBuildRequirements(
        Station station,
        IEnumerable<string> playerBlueprintWareIds,
        string buildMethod,
        bool operationalOnly = false)
        => CalculateShipBuildRequirementsCore(
            operationalOnly ? CreateEffectiveStation(station, true) : station,
            playerBlueprintWareIds,
            NormalizeShipBuildMethod(buildMethod));

    private IReadOnlyDictionary<string, double> CalculateShipBuildRequirementsCore(
        Station station,
        IEnumerable<string> playerBlueprintWareIds,
        string buildMethod)
    {
        var buildModules = station.AdditionalModules
            .Select(module => (Module: module, Definition: _gameData.StationModuleDefinitions.GetValueOrDefault(module.ModuleId)))
            .Where(item => item.Module.Count > 0 && item.Definition is
                { Kind: "buildmodule", BuildProcessorCount: > 0, BuildClasses.Count: > 0 })
            .ToArray();
        if (buildModules.Length == 0) return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var processorCount = buildModules.Sum(item => item.Module.Count * item.Definition!.BuildProcessorCount);
        var supportedClasses = buildModules.SelectMany(item => item.Definition!.BuildClasses)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (processorCount <= 0 || supportedClasses.Count == 0)
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var blueprints = playerBlueprintWareIds.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => _gameData.BlueprintBuildables.GetValueOrDefault(id))
            .OfType<PlayerBuildableDefinition>()
            .ToArray();
        var compatibleShips = blueprints.Where(item => item.Kind == "Ship" &&
            supportedClasses.Contains(item.BuildClass)).ToArray();
        if (compatibleShips.Length == 0)
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var shipMaterialMaxima = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var ship in compatibleShips)
        {
            var materials = _gameData.ResolveBlueprintBuildMaterials(ship.WareId, buildMethod)?.Materials ?? [];
            foreach (var material in materials)
                shipMaterialMaxima[material.WareId] = Math.Max(
                    shipMaterialMaxima.GetValueOrDefault(material.WareId), material.Amount);
        }
        foreach (var (wareId, amount) in shipMaterialMaxima)
            result[wareId] = amount * processorCount * 2;

        if (_gameData.FindByWareId("khaakalloy") != null)
            result["khaakalloy"] = result.GetValueOrDefault("khaakalloy") + 100d * processorCount * 2;

        var usesShipyardFactors = supportedClasses.Contains("ship_l") || supportedClasses.Contains("ship_xl");
        var factors = usesShipyardFactors
            ? _gameData.ShipyardUpgradeResourceFactors
            : _gameData.WharfUpgradeResourceFactors;
        foreach (var categoryGroup in blueprints.Where(item => item.Kind != "Ship" &&
                     !string.IsNullOrWhiteSpace(item.UpgradeCategory) &&
                     factors.ContainsKey(item.UpgradeCategory) &&
                     IsCompatibleUpgrade(item, supportedClasses))
                 .GroupBy(item => item.UpgradeCategory, StringComparer.OrdinalIgnoreCase))
        {
            var maxima = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var buildable in categoryGroup)
            {
                var materials = _gameData.ResolveBlueprintBuildMaterials(buildable.WareId, buildMethod)?.Materials ?? [];
                foreach (var material in materials)
                    maxima[material.WareId] = Math.Max(maxima.GetValueOrDefault(material.WareId), material.Amount);
            }
            var factor = factors[categoryGroup.Key];
            foreach (var (wareId, amount) in maxima)
                result[wareId] = result.GetValueOrDefault(wareId) + amount * factor * processorCount;
        }
        return result;
    }

    private static bool IsCompatibleUpgrade(
        PlayerBuildableDefinition buildable,
        IReadOnlySet<string> supportedClasses)
    {
        if (buildable.UpgradeCategory.Equals("missile", StringComparison.OrdinalIgnoreCase)) return true;
        return string.IsNullOrWhiteSpace(buildable.BuildClass) || supportedClasses.Contains(buildable.BuildClass);
    }

    private static string NormalizeShipBuildMethod(string method) => method.ToLowerInvariant() switch
    {
        // TEL 是 X4Calculator 的生产规划偏好，不是原生舰船建造方式。
        // 因此，船厂仓储将其完全按照普通的 default 方式处理。
        "teladi" => "default",
        "recycling" => "closedloop",
        _ => method
    };

    private Station CreateEffectiveStation(Station station, bool operationalOnly)
    {
        if (!operationalOnly || station.ModuleConstructions.Count == 0) return station;
        var counts = station.ModuleConstructions
            .Where(module => module.State == StationModuleConstructionState.Operational)
            .GroupBy(module => module.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        return new Station
        {
            SolarEfficiencyPercent = station.SolarEfficiencyPercent,
            WorkforceByRace = new Dictionary<string, long>(station.WorkforceByRace, StringComparer.OrdinalIgnoreCase),
            WorkforceEfficiencyBonus = station.WorkforceEfficiencyBonus,
            WareSettings = station.WareSettings,
            Modules = station.Modules.Select(module => new ProductionModule
            {
                ModuleId = module.ModuleId,
                WareId = module.WareId,
                Method = module.Method,
                Count = counts.GetValueOrDefault(module.ModuleId),
                IsAutoAdded = module.IsAutoAdded,
                SelectedProductWareId = module.SelectedProductWareId,
                OperatingMode = module.OperatingMode,
                Recipe = module.Recipe,
                Ware = module.Ware
            }).Where(module => module.Count > 0).ToList(),
            AdditionalModules = station.AdditionalModules.Select(module => new StationModule
            {
                ModuleId = module.ModuleId,
                Kind = module.Kind,
                Count = Math.Min(module.Count, counts.GetValueOrDefault(module.ModuleId))
            }).Where(module => module.Count > 0).ToList()
        };
    }

    private long CalculateStorageCapacity(Station station, string transport, bool operationalOnly)
    {
        Dictionary<string, int>? operationalCounts = null;
        if (operationalOnly && station.ModuleConstructions.Count > 0)
        {
            operationalCounts = station.ModuleConstructions
                .Where(module => module.State == StationModuleConstructionState.Operational)
                .GroupBy(module => module.ModuleId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        }

        return station.AdditionalModules.Sum(module =>
        {
            var definition = _gameData.StationModuleDefinitions.GetValueOrDefault(module.ModuleId);
            if (definition is not { StorageCapacity: > 0 } ||
                !definition.StorageTypes.Contains(transport, StringComparer.OrdinalIgnoreCase)) return 0;
            var count = operationalCounts?.GetValueOrDefault(module.ModuleId) ?? module.Count;
            return definition.StorageCapacity * count;
        });
    }
}

public sealed record StationWareStorageAllocation(
    string WareId,
    string Transport,
    long QuantityLimit,
    long AllocatedVolume,
    long TransportCapacity,
    bool IsWorkforceConsumable = false,
    long? WorkforceAutomaticBuyAmount = null);
