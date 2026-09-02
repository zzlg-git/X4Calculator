using System.Globalization;
using System.Xml;
using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Parsing;

/// <summary>
/// 第二遍流式扫描存档，补充跨组件关联的空间站运营数据。独立于基础站点扫描，
/// 使舰船/建造仓储即使位于其他扇区或出现在站点之前也能通过 ID 关联。
/// </summary>
internal static class SavegameOperationsReader
{
    private static readonly string[] BlacklistTypes = ["sectortravel", "sectoractivity", "objectactivity"];

    public static SavegameSupplementalResult Populate(string saveFilePath, IReadOnlyCollection<Station> stations, GameDataDB gameData,
        CancellationToken cancellationToken)
    {
        var stationById = stations.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var stationStates = stations.ToDictionary(item => item.Id, _ => new StationState(), StringComparer.OrdinalIgnoreCase);
        var shipsByCommanderConnection = new Dictionary<string, ShipState>(StringComparer.OrdinalIgnoreCase);
        var npcs = new Dictionary<string, NpcState>(StringComparer.OrdinalIgnoreCase);
        var blacklistBuilders = new Dictionary<(string Type, int Id), BlacklistRuleBuilder>();
        var tradeRules = new Dictionary<int, StationTradeRule>();
        var defaultBlacklists = new Dictionary<string, (int? Civilian, int? Military)>(StringComparer.OrdinalIgnoreCase);
        var buildOrders = new Dictionary<string, BuildOrderState>(StringComparer.OrdinalIgnoreCase);
        var buildStorages = new Dictionary<string, BuildStorageState>(StringComparer.OrdinalIgnoreCase);
        var processorIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var queuedOrdersByActiveOrder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var npcStationStates = new List<NpcStationState>();
        var mapObjectStates = new List<MapObjectState>();

        var elementNames = new string?[512];
        var components = new ComponentState?[512];
        var activeStationDepth = -1;
        Station? activeStation = null;
        StationState? activeStationState = null;
        var activeShipDepth = -1;
        ShipState? activeShip = null;
        var activeNpcDepth = -1;
        NpcState? activeNpc = null;
        var activeNpcStationDepth = -1;
        NpcStationState? activeNpcStation = null;
        var playerFactionDepth = -1;
        var globalBlacklistDepth = -1;
        BlacklistRuleBuilder? activeBlacklist = null;
        var buildOrderDepth = -1;
        BuildOrderState? activeBuildOrder = null;
        var processorBuildDepth = -1;
        var activeProcessorOrder = string.Empty;
        var activeBuildStorageDepth = -1;
        BuildStorageState? activeBuildStorage = null;
        int? playerDefaultBuyTradeRuleId = null;
        var galaxyDepth = -1;

        using var stream = SavegameFile.OpenRead(saveFilePath);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            IgnoreComments = true,
            DtdProcessing = DtdProcessing.Ignore,
            CloseInput = false
        });

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.Depth >= elementNames.Length)
                throw new XmlException($"存档 XML 层级过深：{reader.Depth}");

            if (reader.NodeType == XmlNodeType.Element)
            {
                elementNames[reader.Depth] = reader.Name;

                if (reader.Name == "faction" && string.Equals(reader.GetAttribute("id"), "player", StringComparison.OrdinalIgnoreCase))
                    playerFactionDepth = reader.Depth;

                if (reader.Name == "blacklist" && reader.GetAttribute("id") is { } ruleIdText &&
                    string.Equals(reader.GetAttribute("owner"), "player", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(ruleIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ruleId))
                {
                    activeBlacklist = new BlacklistRuleBuilder
                    {
                        Id = ruleId,
                        Type = reader.GetAttribute("type") ?? string.Empty,
                        Name = reader.GetAttribute("name") ?? string.Empty
                    };
                    globalBlacklistDepth = reader.Depth;
                }

                if (reader.Name == "traderule" && reader.GetAttribute("id") is { } tradeRuleIdText &&
                    string.Equals(reader.GetAttribute("owner"), "player", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(tradeRuleIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tradeRuleId))
                {
                    tradeRules[tradeRuleId] = new StationTradeRule
                    {
                        Id = tradeRuleId,
                        Name = reader.GetAttribute("name") ?? $"贸易规则 {tradeRuleId}",
                        Factions = SplitIds(reader.GetAttribute("factions")),
                        IsWhitelist = ReadInt(reader.GetAttribute("allow")) == 1
                    };
                }
                else if (activeBlacklist != null && reader.Name == "owner" && reader.Depth == globalBlacklistDepth + 1)
                {
                    activeBlacklist.OwnerRelation = reader.GetAttribute("relation") ?? string.Empty;
                    activeBlacklist.Factions = SplitIds(reader.GetAttribute("factions"));
                    activeBlacklist.IsWhitelist = ReadInt(reader.GetAttribute("whitelist")) == 1;
                }

                if (playerFactionDepth >= 0 && reader.Name == "blacklist" && reader.Depth > playerFactionDepth &&
                    reader.GetAttribute("id") == null && reader.GetAttribute("ref") == null &&
                    reader.GetAttribute("type") is { } defaultType)
                {
                    defaultBlacklists[defaultType] =
                        (ReadNullableInt(reader.GetAttribute("civilian")), ReadNullableInt(reader.GetAttribute("military")));
                }

                if (playerFactionDepth >= 0 && reader.Name == "traderule" &&
                    reader.Depth == playerFactionDepth + 2 && elementNames[reader.Depth - 1] == "traderules" &&
                    string.Equals(reader.GetAttribute("type"), "buy", StringComparison.OrdinalIgnoreCase))
                {
                    playerDefaultBuyTradeRuleId = ReadNullableInt(reader.GetAttribute("traderule"));
                }

                if (reader.Name == "component")
                {
                    var component = new ComponentState
                    {
                        Id = reader.GetAttribute("id") ?? string.Empty,
                        Class = reader.GetAttribute("class") ?? string.Empty,
                        Macro = reader.GetAttribute("macro") ?? string.Empty,
                        Name = reader.GetAttribute("name") ?? string.Empty,
                        Code = reader.GetAttribute("code") ?? string.Empty,
                        Owner = reader.GetAttribute("owner") ?? string.Empty,
                        IconKey = reader.GetAttribute("icon") ?? string.Empty,
                        ConstructionEntryId = reader.GetAttribute("construction") ?? string.Empty,
                        RuntimeState = reader.GetAttribute("state") ?? string.Empty
                    };
                    components[reader.Depth] = component;
                    if (string.Equals(component.Class, "galaxy", StringComparison.OrdinalIgnoreCase))
                        galaxyDepth = reader.Depth;

                    var parentComponent = FindNearestAncestor(components, reader.Depth);
                    if (string.Equals(parentComponent?.Class, "zone", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(reader.GetAttribute("connection"), "space", StringComparison.OrdinalIgnoreCase) &&
                        TryGetMapObjectKind(component, out var mapObjectKind))
                    {
                        mapObjectStates.Add(new MapObjectState
                        {
                            Object = component,
                            Sector = FindAncestor(components, reader.Depth, "sector"),
                            Zone = parentComponent,
                            Kind = mapObjectKind
                        });
                    }

                    if (activeStation == null && stationById.TryGetValue(component.Id, out var foundStation))
                    {
                        activeStation = foundStation;
                        activeStationState = stationStates[foundStation.Id];
                        activeStationDepth = reader.Depth;
                    }

                    if (activeShip == null && component.Class.StartsWith("ship_", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(reader.GetAttribute("owner"), "player", StringComparison.OrdinalIgnoreCase))
                    {
                        activeShip = new ShipState { Id = component.Id, Macro = component.Macro, Name = component.Name };
                        activeShipDepth = reader.Depth;
                    }

                    if (activeNpc == null && string.Equals(component.Class, "npc", StringComparison.OrdinalIgnoreCase))
                    {
                        activeNpc = new NpcState { Id = component.Id, Name = component.Name };
                        activeNpcDepth = reader.Depth;
                    }

                    if (activeNpcStation == null &&
                        string.Equals(component.Class, "station", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(component.Owner, "player", StringComparison.OrdinalIgnoreCase))
                    {
                        activeNpcStation = new NpcStationState
                        {
                            Station = component,
                            Sector = FindAncestor(components, reader.Depth, "sector"),
                            Zone = FindAncestor(components, reader.Depth, "zone")
                        };
                        activeNpcStationDepth = reader.Depth;
                    }

                    if (activeNpcStation != null && reader.Depth > activeNpcStationDepth &&
                        string.Equals(component.Class, "signalleak", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(reader.GetAttribute("type"), "voice", StringComparison.OrdinalIgnoreCase))
                        activeNpcStation.CurrentVoiceSignalLeakCount++;

                    if (activeBuildStorage == null &&
                        string.Equals(component.Class, "buildstorage", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(reader.GetAttribute("owner"), "player", StringComparison.OrdinalIgnoreCase))
                    {
                        activeBuildStorage = new BuildStorageState
                        {
                            Id = component.Id,
                            Code = reader.GetAttribute("code") ?? string.Empty
                        };
                        activeBuildStorageDepth = reader.Depth;
                    }

                    if (activeStation != null && reader.Depth == activeStationDepth + 3 &&
                        !string.IsNullOrWhiteSpace(component.ConstructionEntryId))
                    {
                        activeStationState!.RuntimeModules[component.ConstructionEntryId] = component;
                    }
                }

                if (activeNpc != null && reader.Name == "skills" && reader.Depth > activeNpcDepth)
                {
                    activeNpc.Management = ReadInt(reader.GetAttribute("management"));
                    activeNpc.Morale = ReadInt(reader.GetAttribute("morale"));
                }

                if (activeNpc != null && reader.Name == "traits" && reader.Depth > activeNpcDepth)
                    activeNpc.TradesVisible |= HasFlagToken(reader.GetAttribute("flags"), "tradesvisible");

                if (reader.Name == "position" && reader.Depth >= 2 && elementNames[reader.Depth - 1] == "offset" &&
                    components[reader.Depth - 2] is { } offsetComponent)
                    offsetComponent.Offset = ReadPosition(reader);

                if (activeNpcStation != null)
                    ReadNpcStationElement(reader, elementNames, activeNpcStationDepth, activeNpcStation);

                if (activeStation != null)
                    ReadStationElement(reader, elementNames, components, activeStationDepth, activeStation, activeStationState!);

                if (activeShip != null)
                    ReadShipElement(reader, elementNames, activeShipDepth, activeShip);

                if (activeBuildStorage != null)
                    ReadBuildStorageElement(reader, elementNames, components, activeBuildStorageDepth,
                        activeBuildStorage);

                if (reader.Name == "build" && reader.GetAttribute("type") == "expand" &&
                    reader.GetAttribute("id") is { } buildId && reader.GetAttribute("component") is { } targetStationId)
                {
                    activeBuildOrder = new BuildOrderState
                    {
                        Id = buildId,
                        StationId = targetStationId,
                        BuilderId = reader.GetAttribute("builder") ?? string.Empty
                    };
                    buildOrders[buildId] = activeBuildOrder;
                    buildOrderDepth = reader.Depth;
                }
                else if (reader.Name == "build" && reader.GetAttribute("order") is { } processorOrder &&
                         ReadNullableInt(reader.GetAttribute("sequenceindex")) is { } sequenceIndex)
                {
                    processorIndexes[processorOrder] = sequenceIndex;
                    activeProcessorOrder = processorOrder;
                    processorBuildDepth = reader.Depth;
                }
                else if (reader.Name == "queue" && !string.IsNullOrWhiteSpace(activeProcessorOrder) &&
                         reader.Depth == processorBuildDepth + 1 && reader.GetAttribute("order") is { } queuedOrder)
                {
                    queuedOrdersByActiveOrder[activeProcessorOrder] = queuedOrder;
                }
                else if (activeBuildOrder != null && reader.Name == "entry" &&
                         reader.Depth == buildOrderDepth + 2 && elementNames[reader.Depth - 1] == "sequence")
                {
                    activeBuildOrder.Entries.Add(CreateConstruction(reader, false, activeBuildOrder.Id));
                }

                if (reader.IsEmptyElement)
                {
                    FinishEmptyElement(reader.Depth, reader.Name);
                    elementNames[reader.Depth] = null;
                    if (reader.Name == "component") components[reader.Depth] = null;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                FinishElement(reader.Depth, reader.Name);
                elementNames[reader.Depth] = null;
                if (reader.Name == "component") components[reader.Depth] = null;
                if (reader.Name == "component" && reader.Depth == galaxyDepth) break;
            }
        }

        ResolveManagers(stations, stationStates, npcs);
        ResolveSubordinates(stations, stationStates, shipsByCommanderConnection);
        ResolveBuildOrders(stationById, buildOrders, processorIndexes, queuedOrdersByActiveOrder);
        ResolveBlacklists(stations, stationStates, blacklistBuilders, defaultBlacklists);
        ResolveBuildStorages(stationById, buildOrders, buildStorages, tradeRules,
            playerDefaultBuyTradeRuleId, gameData);
        foreach (var station in stations)
            station.TradeRules = tradeRules.Values.OrderBy(item => item.Name, StringComparer.Ordinal).ToList();
        return new SavegameSupplementalResult(
            ResolveNpcStations(npcStationStates, npcs),
            ResolveMapObjects(mapObjectStates, gameData));

        void FinishEmptyElement(int depth, string name)
        {
            FinishConnection(depth, name);
            if (name == "component") FinishComponent(depth);
            if (name == "blacklist") FinishBlacklist(depth);
            if (name == "build") FinishBuild(depth);
            if (name == "faction" && depth == playerFactionDepth) playerFactionDepth = -1;
        }

        void FinishElement(int depth, string name)
        {
            FinishConnection(depth, name);
            if (name == "component") FinishComponent(depth);
            if (name == "blacklist") FinishBlacklist(depth);
            if (name == "build") FinishBuild(depth);
            if (name == "faction" && depth == playerFactionDepth) playerFactionDepth = -1;
        }

        void FinishConnection(int depth, string name)
        {
            if (name != "connection") return;
            if (activeStationState?.ActiveSubordinateConnectionDepth == depth)
            {
                activeStationState.ActiveSubordinateConnectionDepth = -1;
                activeStationState.ActiveSubordinateConnectionId = string.Empty;
            }
            if (activeShip?.CommanderConnectionDepth == depth)
                activeShip.CommanderConnectionDepth = -1;
        }

        void FinishComponent(int depth)
        {
            if (depth == activeNpcDepth && activeNpc != null)
            {
                if (!string.IsNullOrWhiteSpace(activeNpc.Id)) npcs[activeNpc.Id] = activeNpc;
                activeNpc = null;
                activeNpcDepth = -1;
            }
            if (depth == activeShipDepth && activeShip != null)
            {
                if (!string.IsNullOrWhiteSpace(activeShip.CommanderConnectionId))
                    shipsByCommanderConnection[activeShip.CommanderConnectionId] = activeShip;
                activeShip = null;
                activeShipDepth = -1;
            }
            if (depth == activeStationDepth && activeStation != null)
            {
                FinalizeStationConstructions(activeStation, activeStationState!);
                activeStation = null;
                activeStationState = null;
                activeStationDepth = -1;
            }
            if (depth == activeNpcStationDepth && activeNpcStation != null)
            {
                npcStationStates.Add(activeNpcStation);
                activeNpcStation = null;
                activeNpcStationDepth = -1;
            }
            if (depth == activeBuildStorageDepth && activeBuildStorage != null)
            {
                if (!string.IsNullOrWhiteSpace(activeBuildStorage.Id))
                    buildStorages[activeBuildStorage.Id] = activeBuildStorage;
                activeBuildStorage = null;
                activeBuildStorageDepth = -1;
            }
        }

        void FinishBlacklist(int depth)
        {
            if (depth != globalBlacklistDepth || activeBlacklist == null) return;
            blacklistBuilders[(activeBlacklist.Type, activeBlacklist.Id)] = activeBlacklist;
            activeBlacklist = null;
            globalBlacklistDepth = -1;
        }

        void FinishBuild(int depth)
        {
            if (depth == buildOrderDepth)
            {
                activeBuildOrder = null;
                buildOrderDepth = -1;
            }
            if (depth == processorBuildDepth)
            {
                activeProcessorOrder = string.Empty;
                processorBuildDepth = -1;
            }
        }
    }

    private static void ReadStationElement(XmlReader reader, string?[] names, ComponentState?[] components,
        int stationDepth, Station station, StationState state)
    {
        var relativeDepth = reader.Depth - stationDepth;
        var parent = reader.Depth > 0 ? names[reader.Depth - 1] : null;

        if (reader.Name == "entry" && relativeDepth == 3 && parent == "sequence" &&
            names[reader.Depth - 2] == "construction")
        {
            var entry = CreateConstruction(reader, true, string.Empty);
            station.ModuleConstructions.Add(entry);
            state.ConstructionsById[entry.EntryId] = entry;
        }
        else if (reader.Name == "group" && relativeDepth == 2 && parent == "subordinates")
        {
            var index = ReadInt(reader.GetAttribute("index"));
            state.Assignments[index] = reader.GetAttribute("assignmment") ?? string.Empty;
        }
        else if (reader.Name == "post" && relativeDepth == 2 && parent == "control" &&
                 string.Equals(reader.GetAttribute("id"), "manager", StringComparison.OrdinalIgnoreCase))
        {
            state.ManagerId = reader.GetAttribute("component") ?? string.Empty;
        }
        else if (reader.Name == "workforces" && relativeDepth == 1)
        {
            station.WorkforceLastUpdateTimeSeconds = ReadNullableDouble(reader.GetAttribute("lasttime"));
        }
        else if (reader.Name == "build" && relativeDepth == 1)
        {
            station.BuildMethod = reader.GetAttribute("method") ?? string.Empty;
        }
        else if (reader.Name == "trade" && relativeDepth == 1)
        {
            foreach (var ware in SplitIds(reader.GetAttribute("wares")))
                GetWareSetting(station, ware).IsTradeWare = true;
        }
        else if (reader.Name == "workforce" && relativeDepth == 2 && parent == "workforces")
        {
            var race = reader.GetAttribute("race") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(race)) station.WorkforceByRace[race] = ReadLong(reader.GetAttribute("amount"));
        }
        else if (reader.Name == "bonus" && relativeDepth == 2 && parent == "workforces")
        {
            station.WorkforceEfficiencyEndTimeSeconds = ReadNullableDouble(reader.GetAttribute("endtime"));
            station.WorkforceEfficiencyBonus = ReadNullableDouble(reader.GetAttribute("value"));
        }
        else if (reader.Name == "blacklist" && relativeDepth == 2 && parent == "blacklists" &&
                 reader.GetAttribute("type") is { } blacklistType)
        {
            state.LocalBlacklistRefs[blacklistType] = ReadInt(reader.GetAttribute("ref"));
        }
        else if (reader.Name == "setting" && relativeDepth == 3 && parent == "settings")
        {
            var enabled = SplitIds(reader.GetAttribute("wares"));
            foreach (var ware in enabled)
            {
                var setting = GetWareSetting(station, ware);
                if (reader.GetAttribute("name") == "buy") setting.BuyEnabled = true;
                if (reader.GetAttribute("name") == "sell") setting.SellEnabled = true;
            }
        }
        else if (reader.Name == "trade" && relativeDepth == 4 && parent == "production" &&
                 names[reader.Depth - 2] == "offers" && reader.GetAttribute("ware") is { } offerWare)
        {
            var offer = CreateTradeOffer(reader);
            var setting = GetWareSetting(station, offerWare);
            if (string.Equals(reader.GetAttribute("buyer"), station.Id, StringComparison.OrdinalIgnoreCase))
            {
                if (offer.IsStationSupply) setting.StationSupplyBuyOffer = offer;
                else setting.BuyOffer = offer;
            }
            if (string.Equals(reader.GetAttribute("seller"), station.Id, StringComparison.OrdinalIgnoreCase)) setting.SellOffer = offer;
        }
        else if (reader.Name == "ware" && relativeDepth == 3 && parent == "wares" &&
                 names[reader.Depth - 2] == "supplies" && reader.GetAttribute("ware") is { } supplyWare)
        {
            station.SupplyWares.Add(new StationSupplyWare
            {
                WareId = supplyWare,
                Amount = ReadLong(reader.GetAttribute("amount"))
            });
        }
        else if (reader.Name == "ware" && relativeDepth == 3 && parent == "orders" &&
                 names[reader.Depth - 2] == "supplies" && reader.GetAttribute("ware") is { } orderWare)
        {
            station.SupplyOrders.Add(new StationSupplyOrder
            {
                WareId = orderWare,
                Amount = ReadLong(reader.GetAttribute("amount"))
            });
        }
        else if (reader.Name == "ware" && relativeDepth == 4 && parent == "override" &&
                 names[reader.Depth - 2] == "prices" && reader.GetAttribute("ware") is { } priceWare)
        {
            var setting = GetWareSetting(station, priceWare);
            setting.BuyPriceOverride = ReadPositiveDecimal(reader.GetAttribute("buy"));
            setting.SellPriceOverride = ReadPositiveDecimal(reader.GetAttribute("sell"));
        }
        else if (reader.Name == "ware" && relativeDepth == 3 && parent == "max" &&
                 names[reader.Depth - 2] == "overrides" &&
                 reader.GetAttribute("ware") is { } storageWare &&
                 ReadNullableLong(reader.GetAttribute("amount")) is { } storageAmount && storageAmount >= 0)
        {
            var setting = GetWareSetting(station, storageWare);
            setting.StorageAllocationOverride = storageAmount;
            setting.StorageAllocationStatus = StationStorageAllocationStatus.Manual;
        }
        else if (reader.Name == "ware" && parent == "cargo" && reader.GetAttribute("ware") is { } cargoWare &&
                 stationDepth + 3 < components.Length &&
                 components[stationDepth + 3] is { Class: "storage" })
        {
            GetWareSetting(station, cargoWare).CurrentAmount += ReadLong(reader.GetAttribute("amount"));
        }
        else if (reader.Name == "traderules" && relativeDepth == 1)
        {
            station.DefaultBuyTradeRuleId = ReadNullableInt(reader.GetAttribute("buy"));
            station.DefaultSellTradeRuleId = ReadNullableInt(reader.GetAttribute("sell"));
        }
        else if (reader.Name == "ware" && relativeDepth == 3 && parent == "wares" &&
                 names[reader.Depth - 2] == "traderules" && reader.GetAttribute("ware") is { } ruleWare)
        {
            var setting = GetWareSetting(station, ruleWare);
            if (ReadNullableInt(reader.GetAttribute("buy")) is { } buyRule)
            {
                setting.UseStationBuyTradeRule = false;
                setting.BuyTradeRuleId = buyRule;
            }
            if (ReadNullableInt(reader.GetAttribute("sell")) is { } sellRule)
            {
                setting.UseStationSellTradeRule = false;
                setting.SellTradeRuleId = sellRule;
            }
        }

        if (reader.Name == "connection" && relativeDepth == 2 && parent == "connections" &&
            reader.GetAttribute("connection") == "subordinates")
        {
            state.ActiveSubordinateConnectionDepth = reader.Depth;
            state.ActiveSubordinateConnectionId = reader.GetAttribute("id") ?? string.Empty;
        }
        else if (reader.Name == "connected" && state.ActiveSubordinateConnectionDepth >= 0 &&
                 reader.Depth == state.ActiveSubordinateConnectionDepth + 1)
        {
            state.SubordinateConnections.Add((state.ActiveSubordinateConnectionId,
                reader.GetAttribute("connection") ?? string.Empty));
        }
    }

    private static void ReadNpcStationElement(XmlReader reader, string?[] names, int stationDepth,
        NpcStationState state)
    {
        var relativeDepth = reader.Depth - stationDepth;
        var parent = reader.Depth > 0 ? names[reader.Depth - 1] : null;

        if (reader.Name == "post" && relativeDepth == 2 && parent == "control" &&
            string.Equals(reader.GetAttribute("id"), "shadyguy", StringComparison.OrdinalIgnoreCase))
            state.BlackMarketTraderComponentId = reader.GetAttribute("component") ?? string.Empty;

    }

    private static void ReadBuildStorageElement(XmlReader reader, string?[] names, ComponentState?[] components,
        int buildStorageDepth, BuildStorageState state)
    {
        var relativeDepth = reader.Depth - buildStorageDepth;
        var parent = reader.Depth > 0 ? names[reader.Depth - 1] : null;

        if (reader.Name == "traderules" && relativeDepth == 1)
        {
            if (ReadNullableInt(reader.GetAttribute("buy")) is { } buyRuleId)
            {
                state.HasOwnBuyTradeRule = true;
                state.BuyTradeRuleId = buyRuleId;
            }
        }
        else if (reader.Name == "ware" && relativeDepth == 3 && parent == "wares" &&
                 names[reader.Depth - 2] == "traderules" && reader.GetAttribute("ware") is { } ruleWare &&
                 ReadNullableInt(reader.GetAttribute("buy")) is { } wareBuyRuleId)
        {
            var ware = GetBuildStorageWare(state, ruleWare);
            ware.HasOwnBuyTradeRule = true;
            ware.BuyTradeRuleId = wareBuyRuleId;
        }
        else if (reader.Name == "trade" && relativeDepth == 4 && parent == "production" &&
                 names[reader.Depth - 2] == "offers" && reader.GetAttribute("ware") is { } offerWare &&
                 string.Equals(reader.GetAttribute("buyer"), state.Id, StringComparison.OrdinalIgnoreCase))
        {
            GetBuildStorageWare(state, offerWare).BuyOffer = CreateTradeOffer(reader);
        }
        else if (reader.Name == "reservation" && relativeDepth == 3 && parent == "reservations" &&
                 names[reader.Depth - 2] == "trade" && reader.GetAttribute("ware") is { } reservationWare &&
                 string.Equals(reader.GetAttribute("buyer"), state.Id, StringComparison.OrdinalIgnoreCase))
        {
            GetBuildStorageWare(state, reservationWare).IncomingOrders.Add(new StationBuildStorageIncomingOrder
            {
                ReservationId = reader.GetAttribute("id") ?? string.Empty,
                ReserverId = reader.GetAttribute("reserver") ?? string.Empty,
                PartnerId = reader.GetAttribute("partner") ?? string.Empty,
                SellerId = reader.GetAttribute("seller") ?? string.Empty,
                Amount = ReadLong(reader.GetAttribute("amount")),
                DesiredAmount = ReadNullableLong(reader.GetAttribute("desired")),
                TransferredAmount = ReadLong(reader.GetAttribute("transferred")),
                PriceHundredths = ReadLong(reader.GetAttribute("price"))
            });
        }
        else if (reader.Name == "build" && IsInsideBuildProcessor(components, buildStorageDepth, reader.Depth))
        {
            state.BuildState = reader.GetAttribute("state") ?? string.Empty;
            state.BuildStep = ReadInt(reader.GetAttribute("step"));
            state.BuildSteps = ReadInt(reader.GetAttribute("steps"));
        }
        else if (reader.Name == "ware" && parent == "cargo" && reader.GetAttribute("ware") is { } cargoWare &&
                 buildStorageDepth + 3 < components.Length &&
                 components[buildStorageDepth + 3] is { Class: "storage" })
        {
            GetBuildStorageWare(state, cargoWare).CurrentAmount += ReadLong(reader.GetAttribute("amount"));
        }
        else if (reader.Name == "ware" && parent is "resources" or "nextresources" &&
                 reader.GetAttribute("ware") is { } requiredWare &&
                 IsInsideBuildProcessor(components, buildStorageDepth, reader.Depth))
        {
            // Only direct resources/ware nodes are quantities. resources/insufficient/ware@amount
            // is a shortage start time in real saves and is deliberately excluded by the parent check.
            var ware = GetBuildStorageWare(state, requiredWare);
            if (parent == "resources")
                ware.CurrentBuildResourceAmount += ReadLong(reader.GetAttribute("amount"));
            else
                ware.NextResourceAmount += ReadLong(reader.GetAttribute("amount"));
        }
    }

    private static StationTradeOffer CreateTradeOffer(XmlReader reader) => new()
    {
        PriceHundredths = ReadLong(reader.GetAttribute("price")),
        Amount = ReadLong(reader.GetAttribute("amount")),
        DesiredAmount = ReadNullableLong(reader.GetAttribute("desired")),
        Flags = reader.GetAttribute("flags") ?? string.Empty
    };

    private static bool IsInsideBuildProcessor(ComponentState?[] components, int buildStorageDepth, int depth)
    {
        for (var componentDepth = depth - 1; componentDepth > buildStorageDepth; componentDepth--)
        {
            if (components[componentDepth] is not { } component) continue;
            return string.Equals(component.Class, "buildprocessor", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static void ReadShipElement(XmlReader reader, string?[] names, int shipDepth, ShipState ship)
    {
        var relativeDepth = reader.Depth - shipDepth;
        var parent = reader.Depth > 0 ? names[reader.Depth - 1] : null;
        if (reader.Name == "subordinate" && relativeDepth == 1)
            ship.GroupIndex = ReadInt(reader.GetAttribute("group"));
        else if (reader.Name == "connection" && relativeDepth == 2 && parent == "connections" &&
                 reader.GetAttribute("connection") == "commander")
        {
            ship.CommanderConnectionDepth = reader.Depth;
            ship.CommanderConnectionId = reader.GetAttribute("id") ?? string.Empty;
        }
        else if (reader.Name == "connected" && ship.CommanderConnectionDepth >= 0 &&
                 reader.Depth == ship.CommanderConnectionDepth + 1)
        {
            ship.CommanderConnectedId = reader.GetAttribute("connection") ?? string.Empty;
        }
    }

    private static void FinalizeStationConstructions(Station station, StationState state)
    {
        foreach (var entry in station.ModuleConstructions)
        {
            if (!state.RuntimeModules.TryGetValue(entry.EntryId, out var runtime)) continue;
            entry.RuntimeComponentId = runtime.Id;
            entry.State = ParseRuntimeState(runtime.RuntimeState);
        }
        station.ModuleConstructions.Sort((left, right) => left.Index.CompareTo(right.Index));
    }

    private static void ResolveManagers(IEnumerable<Station> stations, IReadOnlyDictionary<string, StationState> states,
        IReadOnlyDictionary<string, NpcState> npcs)
    {
        foreach (var station in stations)
        {
            var managerId = states[station.Id].ManagerId;
            if (string.IsNullOrWhiteSpace(managerId) || !npcs.TryGetValue(managerId, out var npc)) continue;
            station.Manager = new StationManager
            {
                ComponentId = npc.Id,
                Name = npc.Name,
                ManagementSkill = npc.Management,
                MoraleSkill = npc.Morale
            };
        }
    }

    private static void ResolveSubordinates(IEnumerable<Station> stations,
        IReadOnlyDictionary<string, StationState> states, IReadOnlyDictionary<string, ShipState> ships)
    {
        foreach (var station in stations)
        {
            var state = states[station.Id];
            foreach (var (stationConnectionId, shipConnectionId) in state.SubordinateConnections)
            {
                if (!ships.TryGetValue(shipConnectionId, out var ship)) continue;
                if (!string.IsNullOrWhiteSpace(ship.CommanderConnectedId) &&
                    !string.Equals(ship.CommanderConnectedId, stationConnectionId, StringComparison.OrdinalIgnoreCase)) continue;
                state.Assignments.TryGetValue(ship.GroupIndex, out var assignment);
                station.Subordinates.Add(new StationSubordinate
                {
                    ShipId = ship.Id,
                    Name = ship.Name,
                    Macro = ship.Macro,
                    GroupIndex = ship.GroupIndex,
                    Assignment = assignment ?? string.Empty
                });
            }
        }
    }

    private static void ResolveBuildOrders(IReadOnlyDictionary<string, Station> stations,
        IReadOnlyDictionary<string, BuildOrderState> orders,
        IReadOnlyDictionary<string, int> processorIndexes,
        IReadOnlyDictionary<string, string> queuedOrdersByActiveOrder)
    {
        foreach (var stationOrders in orders.Values.GroupBy(item => item.StationId, StringComparer.OrdinalIgnoreCase))
        {
            if (!stations.TryGetValue(stationOrders.Key, out var station)) continue;
            var orderList = stationOrders.ToArray();
            var activeOrder = orderList.FirstOrDefault(item => processorIndexes.ContainsKey(item.Id));
            BuildOrderState? targetOrder = null;
            if (activeOrder != null && queuedOrdersByActiveOrder.TryGetValue(activeOrder.Id, out var queuedId))
                orders.TryGetValue(queuedId, out targetOrder);
            targetOrder ??= activeOrder ?? orderList.OrderByDescending(item => item.Entries.Count).FirstOrDefault();
            if (targetOrder == null) continue;

            var existing = station.ModuleConstructions.ToDictionary(item => item.EntryId, StringComparer.OrdinalIgnoreCase);
            string? currentEntryId = null;
            if (activeOrder != null && processorIndexes.TryGetValue(activeOrder.Id, out var currentZeroBased) &&
                currentZeroBased >= 0 && currentZeroBased < activeOrder.Entries.Count)
            {
                currentEntryId = activeOrder.Entries[currentZeroBased].EntryId;
            }

            foreach (var queued in targetOrder.Entries)
            {
                if (existing.TryGetValue(queued.EntryId, out var current))
                {
                    current.BuildOrderId = targetOrder.Id;
                    if (string.Equals(queued.EntryId, currentEntryId, StringComparison.OrdinalIgnoreCase))
                        current.State = StationModuleConstructionState.UnderConstruction;
                }
                else
                {
                    if (string.Equals(queued.EntryId, currentEntryId, StringComparison.OrdinalIgnoreCase))
                        queued.State = StationModuleConstructionState.UnderConstruction;
                    station.ModuleConstructions.Add(queued);
                }
            }
            station.ModuleConstructions.Sort((left, right) => left.Index.CompareTo(right.Index));
        }
    }

    private static void ResolveBuildStorages(IReadOnlyDictionary<string, Station> stations,
        IReadOnlyDictionary<string, BuildOrderState> orders,
        IReadOnlyDictionary<string, BuildStorageState> buildStorages,
        IReadOnlyDictionary<int, StationTradeRule> tradeRules,
        int? playerDefaultBuyTradeRuleId,
        GameDataDB gameData)
    {
        foreach (var stationOrders in orders.Values.GroupBy(item => item.StationId, StringComparer.OrdinalIgnoreCase))
        {
            if (!stations.TryGetValue(stationOrders.Key, out var station)) continue;
            var storageState = stationOrders
                .Select(item => buildStorages.GetValueOrDefault(item.BuilderId))
                .FirstOrDefault(item => item != null);
            if (storageState == null) continue;

            var storageEffectiveRuleId = storageState.HasOwnBuyTradeRule
                ? storageState.BuyTradeRuleId
                : playerDefaultBuyTradeRuleId;
            var wares = storageState.Wares.Values
                .Select(ware =>
                {
                    var effectiveRuleId = ware.HasOwnBuyTradeRule
                        ? ware.BuyTradeRuleId
                        : storageEffectiveRuleId;
                    return new StationBuildStorageWare
                    {
                        WareId = ware.WareId,
                        WareName = gameData.FindByWareId(ware.WareId)?.Name ?? ware.WareId,
                        RequiredAmount = CalculateRemainingRequiredAmount(storageState, ware),
                        CurrentAmount = ware.CurrentAmount,
                        IncomingOrders = ware.IncomingOrders.ToArray(),
                        BuyOffer = ware.BuyOffer,
                        UsesBuildStorageBuyTradeRule = !ware.HasOwnBuyTradeRule,
                        BuyTradeRuleId = ware.HasOwnBuyTradeRule ? ware.BuyTradeRuleId : null,
                        EffectiveBuyTradeRuleId = effectiveRuleId,
                        EffectiveBuyTradeRule = ResolveTradeRule(effectiveRuleId, tradeRules)
                    };
                })
                .OrderBy(item => item.WareName, StringComparer.Ordinal)
                .ThenBy(item => item.WareId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            station.BuildStorage = new StationBuildStorage
            {
                ComponentId = storageState.Id,
                Code = storageState.Code,
                Wares = wares,
                UsesGlobalBuyTradeRule = !storageState.HasOwnBuyTradeRule,
                BuyTradeRuleId = storageState.HasOwnBuyTradeRule ? storageState.BuyTradeRuleId : null,
                EffectiveBuyTradeRuleId = storageEffectiveRuleId,
                EffectiveBuyTradeRule = ResolveTradeRule(storageEffectiveRuleId, tradeRules)
            };
        }
    }

    private static long CalculateRemainingRequiredAmount(BuildStorageState storage, BuildStorageWareState ware)
    {
        var current = ware.CurrentBuildResourceAmount;
        if (string.Equals(storage.BuildState, "building", StringComparison.OrdinalIgnoreCase) &&
            storage.BuildSteps > 0 && storage.BuildStep > 0)
        {
            // Validated against building autosave samples: step/steps is the consumed share of the
            // current module. The native PrepareBuildSequenceResources2 implementation is not available.
            var completedSteps = Math.Min(storage.BuildStep, storage.BuildSteps);
            current -= (long)Math.Floor((decimal)current * completedSteps / storage.BuildSteps);
        }
        return current + ware.NextResourceAmount;
    }

    private static StationTradeRule? ResolveTradeRule(int? id,
        IReadOnlyDictionary<int, StationTradeRule> tradeRules) =>
        id is > 0 && tradeRules.TryGetValue(id.Value, out var rule) ? rule : null;

    private static void ResolveBlacklists(IEnumerable<Station> stations,
        IReadOnlyDictionary<string, StationState> states,
        IReadOnlyDictionary<(string Type, int Id), BlacklistRuleBuilder> rules,
        IReadOnlyDictionary<string, (int? Civilian, int? Military)> defaults)
    {
        foreach (var station in stations)
        {
            var state = states[station.Id];
            foreach (var type in BlacklistTypes.Concat(state.LocalBlacklistRefs.Keys).Concat(defaults.Keys)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                state.LocalBlacklistRefs.TryGetValue(type, out var localRef);
                var hasLocal = state.LocalBlacklistRefs.ContainsKey(type);
                defaults.TryGetValue(type, out var global);
                station.Blacklists.Add(new StationBlacklistSetting
                {
                    Type = type,
                    Selection = !hasLocal ? StationBlacklistSelection.Inherited
                        : localRef == -1 ? StationBlacklistSelection.Disabled
                        : StationBlacklistSelection.Explicit,
                    ReferenceId = hasLocal ? localRef : null,
                    ExplicitRule = hasLocal && localRef > 0 ? ResolveRule(type, localRef, rules) : null,
                    CivilianDefaultRuleId = global.Civilian,
                    CivilianDefaultRule = global.Civilian is { } civilian ? ResolveRule(type, civilian, rules) : null,
                    MilitaryDefaultRuleId = global.Military,
                    MilitaryDefaultRule = global.Military is { } military ? ResolveRule(type, military, rules) : null
                });
            }
        }
    }

    private static StationBlacklistRule? ResolveRule(string type, int id,
        IReadOnlyDictionary<(string Type, int Id), BlacklistRuleBuilder> rules) =>
        rules.TryGetValue((type, id), out var rule) ? rule.Build() : null;

    private static IReadOnlyList<NpcStation> ResolveNpcStations(IEnumerable<NpcStationState> states,
        IReadOnlyDictionary<string, NpcState> npcs) => states.Select(state =>
    {
        NpcState? trader = null;
        var traderResolved = !string.IsNullOrWhiteSpace(state.BlackMarketTraderComponentId) &&
                             npcs.TryGetValue(state.BlackMarketTraderComponentId, out trader);
        return new NpcStation
        {
            Id = state.Station.Id,
            Name = ResolveNpcStationName(state.Station),
            Code = state.Station.Code,
            Owner = state.Station.Owner,
            Macro = state.Station.Macro,
            IconKey = ResolveNpcStationIcon(state.Station),
            SectorId = state.Sector?.Macro ?? string.Empty,
            ZoneId = state.Zone?.Macro ?? string.Empty,
            SectorPosition = (state.Zone?.Offset ?? Vec3.Zero) + state.Station.Offset,
            BlackMarketTraderComponentId = state.BlackMarketTraderComponentId,
            BlackMarketTraderName = traderResolved ? trader!.Name : string.Empty,
            IsBlackMarketTraderResolved = traderResolved,
            IsBlackMarketTraderUnlocked = traderResolved && trader!.TradesVisible,
            CurrentVoiceSignalLeakCount = state.CurrentVoiceSignalLeakCount,
            IsWreck = state.Station.RuntimeState.Contains("wreck", StringComparison.OrdinalIgnoreCase)
        };
    }).ToArray();

    private static IReadOnlyList<SaveMapObject> ResolveMapObjects(
        IEnumerable<MapObjectState> states,
        GameDataDB gameData) => states.Select(state =>
    {
        var component = state.Object;
        var name = state.Kind == SaveMapObjectKind.OwnerlessShip
            ? component.Name
            : string.Empty;
        if (string.IsNullOrWhiteSpace(name) && state.Kind == SaveMapObjectKind.OwnerlessShip &&
            gameData.Ships.TryGetValue(component.Macro, out var ship))
            name = ship.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = state.Kind switch
            {
                SaveMapObjectKind.OwnerlessShip => component.Macro,
                SaveMapObjectKind.DataVault => "数据保险库",
                SaveMapObjectKind.ErlkingDataVault => "妖王数据保险库",
                _ => component.Macro
            };
        }

        return new SaveMapObject
        {
            Id = component.Id,
            Name = name,
            Code = component.Code,
            Macro = component.Macro,
            ObjectClass = component.Class,
            Kind = state.Kind,
            SectorId = state.Sector?.Macro ?? string.Empty,
            ZoneId = state.Zone?.Macro ?? string.Empty,
            SectorPosition = (state.Zone?.Offset ?? Vec3.Zero) + component.Offset
        };
    }).ToArray();

    private static bool TryGetMapObjectKind(ComponentState component, out SaveMapObjectKind kind)
    {
        if (component.Class.StartsWith("ship_", StringComparison.OrdinalIgnoreCase) &&
            component.Owner.Equals("ownerless", StringComparison.OrdinalIgnoreCase) &&
            !component.RuntimeState.Contains("wreck", StringComparison.OrdinalIgnoreCase))
        {
            kind = SaveMapObjectKind.OwnerlessShip;
            return true;
        }
        if (component.Class.Equals("datavault", StringComparison.OrdinalIgnoreCase))
        {
            kind = SaveMapObjectKind.DataVault;
            return true;
        }
        if (component.Macro.StartsWith("landmarks_erlking_vault_", StringComparison.OrdinalIgnoreCase))
        {
            kind = SaveMapObjectKind.ErlkingDataVault;
            return true;
        }
        kind = default;
        return false;
    }

    private static string ResolveNpcStationIcon(ComponentState station)
    {
        if (!string.IsNullOrWhiteSpace(station.IconKey)) return station.IconKey;
        if (station.Owner.Equals("khaak", StringComparison.OrdinalIgnoreCase))
            return station.Macro.Contains("weaponplatform", StringComparison.OrdinalIgnoreCase)
                ? "mapob_weaponplatform"
                : "mapob_hive";
        return "mapob_factory";
    }

    private static string ResolveNpcStationName(ComponentState station)
    {
        if (!string.IsNullOrWhiteSpace(station.Name)) return station.Name;
        if (!station.Owner.Equals("khaak", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        return station.Macro.Contains("weaponplatform", StringComparison.OrdinalIgnoreCase)
            ? "Kha'ak 武器平台"
            : "Kha'ak 空间站";
    }

    private static ComponentState? FindAncestor(ComponentState?[] components, int beforeDepth, string componentClass)
    {
        for (var depth = beforeDepth - 1; depth >= 0; depth--)
        {
            var component = components[depth];
            if (component != null && string.Equals(component.Class, componentClass, StringComparison.OrdinalIgnoreCase))
                return component;
        }
        return null;
    }

    private static ComponentState? FindNearestAncestor(ComponentState?[] components, int beforeDepth)
    {
        for (var depth = beforeDepth - 1; depth >= 0; depth--)
        {
            if (components[depth] is { } component) return component;
        }
        return null;
    }

    private static StationModuleConstruction CreateConstruction(XmlReader reader, bool inStationSequence, string orderId) => new()
    {
        EntryId = reader.GetAttribute("id") ?? string.Empty,
        Index = ReadInt(reader.GetAttribute("index")),
        ModuleId = reader.GetAttribute("macro") ?? string.Empty,
        Connection = reader.GetAttribute("connection") ?? string.Empty,
        IsInStationSequence = inStationSequence,
        BuildOrderId = orderId,
        State = StationModuleConstructionState.Planned
    };

    private static StationModuleConstructionState ParseRuntimeState(string state)
    {
        if (string.Equals(state, "construction", StringComparison.OrdinalIgnoreCase))
            return StationModuleConstructionState.UnderConstruction;
        if (state.Contains("wreck", StringComparison.OrdinalIgnoreCase))
            return StationModuleConstructionState.Wrecked;
        return StationModuleConstructionState.Operational;
    }

    private static StationWareSetting GetWareSetting(Station station, string wareId)
    {
        var existing = station.WareSettings.FirstOrDefault(item =>
            string.Equals(item.WareId, wareId, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        var created = new StationWareSetting { WareId = wareId };
        station.WareSettings.Add(created);
        return created;
    }

    private static BuildStorageWareState GetBuildStorageWare(BuildStorageState state, string wareId)
    {
        if (state.Wares.TryGetValue(wareId, out var existing)) return existing;
        var created = new BuildStorageWareState { WareId = wareId };
        state.Wares[wareId] = created;
        return created;
    }

    private static string[] SplitIds(string? value) => string.IsNullOrWhiteSpace(value)
        ? Array.Empty<string>()
        : value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool HasFlagToken(string? flags, string expected) => !string.IsNullOrWhiteSpace(flags) &&
        flags.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(flag => string.Equals(flag, expected, StringComparison.OrdinalIgnoreCase));

    private static Vec3 ReadPosition(XmlReader reader) => new(
        ReadNullableDouble(reader.GetAttribute("x")) ?? 0,
        ReadNullableDouble(reader.GetAttribute("y")) ?? 0,
        ReadNullableDouble(reader.GetAttribute("z")) ?? 0);

    private static int ReadInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static int? ReadNullableInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static long ReadLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static long? ReadNullableLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static decimal? ReadPositiveDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) && result > 0 ? result : null;

    private static double? ReadNullableDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;

    private sealed class StationState
    {
        public string ManagerId { get; set; } = string.Empty;
        public Dictionary<int, string> Assignments { get; } = new();
        public List<(string StationConnectionId, string ShipConnectionId)> SubordinateConnections { get; } = new();
        public int ActiveSubordinateConnectionDepth { get; set; } = -1;
        public string ActiveSubordinateConnectionId { get; set; } = string.Empty;
        public Dictionary<string, int> LocalBlacklistRefs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, StationModuleConstruction> ConstructionsById { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ComponentState> RuntimeModules { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ComponentState
    {
        public string Id { get; init; } = string.Empty;
        public string Class { get; init; } = string.Empty;
        public string Macro { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Code { get; init; } = string.Empty;
        public string Owner { get; init; } = string.Empty;
        public string IconKey { get; set; } = string.Empty;
        public Vec3 Offset { get; set; }
        public string ConstructionEntryId { get; init; } = string.Empty;
        public string RuntimeState { get; init; } = string.Empty;
    }

    private sealed class NpcStationState
    {
        public required ComponentState Station { get; init; }
        public ComponentState? Sector { get; init; }
        public ComponentState? Zone { get; init; }
        public string BlackMarketTraderComponentId { get; set; } = string.Empty;
        public int CurrentVoiceSignalLeakCount { get; set; }
    }

    private sealed class MapObjectState
    {
        public required ComponentState Object { get; init; }
        public ComponentState? Sector { get; init; }
        public ComponentState? Zone { get; init; }
        public SaveMapObjectKind Kind { get; init; }
    }

    private sealed class ShipState
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Macro { get; init; } = string.Empty;
        public int GroupIndex { get; set; }
        public int CommanderConnectionDepth { get; set; } = -1;
        public string CommanderConnectionId { get; set; } = string.Empty;
        public string CommanderConnectedId { get; set; } = string.Empty;
    }

    private sealed class NpcState
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public int Management { get; set; }
        public int Morale { get; set; }
        public bool TradesVisible { get; set; }
    }

    private sealed class BuildOrderState
    {
        public string Id { get; init; } = string.Empty;
        public string StationId { get; init; } = string.Empty;
        public string BuilderId { get; init; } = string.Empty;
        public List<StationModuleConstruction> Entries { get; } = new();
    }

    private sealed class BuildStorageState
    {
        public string Id { get; init; } = string.Empty;
        public string Code { get; init; } = string.Empty;
        public bool HasOwnBuyTradeRule { get; set; }
        public int? BuyTradeRuleId { get; set; }
        public string BuildState { get; set; } = string.Empty;
        public int BuildStep { get; set; }
        public int BuildSteps { get; set; }
        public Dictionary<string, BuildStorageWareState> Wares { get; }
            = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class BuildStorageWareState
    {
        public string WareId { get; init; } = string.Empty;
        public long CurrentBuildResourceAmount { get; set; }
        public long NextResourceAmount { get; set; }
        public long CurrentAmount { get; set; }
        public List<StationBuildStorageIncomingOrder> IncomingOrders { get; } = new();
        public StationTradeOffer? BuyOffer { get; set; }
        public bool HasOwnBuyTradeRule { get; set; }
        public int? BuyTradeRuleId { get; set; }
    }

    private sealed class BlacklistRuleBuilder
    {
        public int Id { get; init; }
        public string Type { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string OwnerRelation { get; set; } = string.Empty;
        public IReadOnlyList<string> Factions { get; set; } = Array.Empty<string>();
        public bool IsWhitelist { get; set; }

        public StationBlacklistRule Build() => new()
        {
            Id = Id,
            Type = Type,
            Name = Name,
            OwnerRelation = OwnerRelation,
            Factions = Factions,
            IsWhitelist = IsWhitelist
        };
    }
}

internal sealed record SavegameSupplementalResult(
    IReadOnlyList<NpcStation> NpcStations,
    IReadOnlyList<SaveMapObject> MapObjects);
