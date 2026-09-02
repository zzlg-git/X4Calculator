using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using X4Calculator.Core.Calculation;
using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.Core.Parsing;

/// <summary>以流式方式解析 XML 或 .gz X4 存档中的玩家空间站与扇区控制权。</summary>
public sealed class SavegameParser
{
    private readonly GameDataDB _gameData;
    private readonly Func<string, string?>? _sectorNameResolver;
    private static readonly Regex ModuleMacroRegex = new(
        @"^prod_gen_(?<ware>\w+)_macro$", RegexOptions.Compiled);

    public SavegameParser(GameDataDB gameData, Func<string, string?>? sectorNameResolver = null)
    {
        _gameData = gameData;
        _sectorNameResolver = sectorNameResolver;
    }

    public Task<List<Station>> ParsePlayerStationsAsync(string saveFilePath) =>
        ParsePlayerStationsAsync(saveFilePath, CancellationToken.None);

    public async Task<List<Station>> ParsePlayerStationsAsync(
        string saveFilePath,
        CancellationToken cancellationToken)
    {
        var result = await ParseAsync(saveFilePath, cancellationToken).ConfigureAwait(false);
        return result.Stations.ToList();
    }

    public Task<SavegameImportResult> ParseAsync(string saveFilePath) =>
        ParseAsync(saveFilePath, CancellationToken.None);

    public async Task<SavegameImportResult> ParseAsync(
        string saveFilePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await Task.Run(() => ParseSavegame(saveFilePath, cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private SavegameImportResult ParseSavegame(
        string saveFilePath,
        CancellationToken cancellationToken)
    {
        var stations = new List<Station>();
        var sectorOwnerships = new List<SectorOwnership>();
        var components = new ComponentContext?[256];
        var elementNames = new string?[256];
        var galaxyDepth = -1;
        var blueprintsDepth = -1;
        var playerBlueprintWareIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        double? gameTimeSeconds = null;

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
            if (reader.Depth >= components.Length)
                throw new XmlException($"存档 XML 层级过深：{reader.Depth}");

            if (reader.NodeType == XmlNodeType.Element)
            {
                elementNames[reader.Depth] = reader.Name;

                if (reader.Name == "blueprints")
                {
                    blueprintsDepth = reader.Depth;
                }
                else if (blueprintsDepth >= 0 && reader.Name == "blueprint" &&
                         reader.Depth == blueprintsDepth + 1 &&
                         reader.GetAttribute("ware") is { Length: > 0 } blueprintWareId)
                {
                    playerBlueprintWareIds.Add(blueprintWareId);
                }

                if (reader.Name == "game" && reader.Depth > 0 && elementNames[reader.Depth - 1] == "info" &&
                    reader.GetAttribute("time") is { } gameTimeText &&
                    double.TryParse(gameTimeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedGameTime))
                    gameTimeSeconds = parsedGameTime;

                if (reader.Name == "component")
                {
                    var context = new ComponentContext(
                        reader.GetAttribute("class") ?? string.Empty,
                        reader.GetAttribute("macro") ?? string.Empty);
                    components[reader.Depth] = context;
                    if (string.Equals(context.Class, "galaxy", StringComparison.OrdinalIgnoreCase))
                        galaxyDepth = reader.Depth;

                    if (string.Equals(context.Class, "sector", StringComparison.OrdinalIgnoreCase))
                    {
                        var owner = reader.GetAttribute("owner");
                        if (!string.IsNullOrWhiteSpace(context.Macro) && !string.IsNullOrWhiteSpace(owner))
                        {
                            sectorOwnerships.Add(new SectorOwnership(
                                context.Macro,
                                owner,
                                string.Equals(reader.GetAttribute("contested"), "1", StringComparison.Ordinal)));
                        }
                    }

                    if (string.Equals(context.Class, "station", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(reader.GetAttribute("owner"), "player", StringComparison.OrdinalIgnoreCase))
                    {
                        var sector = FindAncestor(components, reader.Depth, "sector");
                        var zone = FindAncestor(components, reader.Depth, "zone");
                        stations.Add(ParseStation(reader, sector, zone, cancellationToken));
                    }
                }
                else if (reader.Name == "position" && reader.Depth >= 2 &&
                         elementNames[reader.Depth - 1] == "offset")
                {
                    var component = components[reader.Depth - 2];
                    if (component != null) component.Offset = ReadPosition(reader);
                }

                if (reader.IsEmptyElement) elementNames[reader.Depth] = null;
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.Name == "blueprints" && reader.Depth == blueprintsDepth)
                    blueprintsDepth = -1;
                if (reader.Name == "component")
                {
                    if (reader.Depth == galaxyDepth) break;
                    components[reader.Depth] = null;
                }
                elementNames[reader.Depth] = null;
            }
        }

        var supplemental = SavegameOperationsReader.Populate(
            saveFilePath, stations, _gameData, cancellationToken);
        foreach (var station in stations)
            SynchronizeModulesWithConstructionSequence(station);
        foreach (var station in stations.Where(item => string.IsNullOrWhiteSpace(item.Name)))
        {
            StationNameGenerator.AssignGeneratedName(
                station,
                _sectorNameResolver?.Invoke(station.SectorId),
                stations);
        }
        foreach (var station in stations.Where(item => string.IsNullOrWhiteSpace(item.IconKey)))
            station.IconKey = StationIconClassifier.Classify(station);
        return new SavegameImportResult(
            stations,
            sectorOwnerships,
            gameTimeSeconds,
            playerBlueprintWareIds,
            supplemental.NpcStations,
            supplemental.MapObjects);
    }

    private void SynchronizeModulesWithConstructionSequence(Station station)
    {
        foreach (var group in station.ModuleConstructions
                     .Where(item => !string.IsNullOrWhiteSpace(item.ModuleId))
                     .GroupBy(item => item.ModuleId, StringComparer.OrdinalIgnoreCase))
        {
            var desiredCount = group.Count();
            var existingProduction = station.Modules.FirstOrDefault(item =>
                item.ModuleId.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
            if (existingProduction != null)
            {
                existingProduction.Count = Math.Max(existingProduction.Count, desiredCount);
                continue;
            }

            var production = TryCreateModule(group.Key);
            if (production != null)
            {
                production.Count = desiredCount;
                station.Modules.Add(production);
                continue;
            }

            if (!_gameData.StationModuleDefinitions.TryGetValue(group.Key, out var definition)) continue;
            var existingAdditional = station.AdditionalModules.FirstOrDefault(item =>
                item.ModuleId.Equals(group.Key, StringComparison.OrdinalIgnoreCase));
            if (existingAdditional != null)
                existingAdditional.Count = Math.Max(existingAdditional.Count, desiredCount);
            else
                station.AdditionalModules.Add(new StationModule
                    { ModuleId = group.Key, Kind = definition.Kind, Count = desiredCount });
        }
    }

    private Station ParseStation(
        XmlReader reader,
        ComponentContext? sector,
        ComponentContext? zone,
        CancellationToken cancellationToken)
    {
        var macro = reader.GetAttribute("macro") ?? string.Empty;
        var storedName = reader.GetAttribute("name");
        var nameIndex = int.TryParse(reader.GetAttribute("nameindex"), out var parsedNameIndex)
            ? parsedNameIndex
            : 0;
        var station = new Station
        {
            Id = reader.GetAttribute("id") ?? string.Empty,
            Owner = reader.GetAttribute("owner") ?? "player",
            Macro = macro,
            Name = storedName ?? string.Empty,
            Code = reader.GetAttribute("code") ?? string.Empty,
            GeneratedNameIndex = nameIndex > 0 ? nameIndex : null,
            SectorId = sector?.Macro ?? string.Empty,
            ZoneId = zone?.Macro ?? string.Empty,
            IconKey = ResolveIconKey(reader.GetAttribute("icon"), macro)
        };

        var stationOffset = Vec3.Zero;
        var subtreeElements = new string?[256];
        using var subtree = reader.ReadSubtree();
        while (subtree.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (subtree.NodeType == XmlNodeType.EndElement)
            {
                subtreeElements[subtree.Depth] = null;
                continue;
            }
            if (subtree.NodeType != XmlNodeType.Element) continue;
            subtreeElements[subtree.Depth] = subtree.Name;

            if (subtree.Name == "position" && subtree.Depth == 2 && subtreeElements[1] == "offset")
            {
                stationOffset = ReadPosition(subtree);
            }
            // 只读取站点直属 construction/sequence；snapshot 会重复保存同一组 entry。
            else if (subtree.Name == "entry" && subtree.Depth == 3 &&
                     subtreeElements[2] == "sequence" && subtreeElements[1] == "construction")
            {
                var moduleId = subtree.GetAttribute("macro");
                var module = TryCreateModule(moduleId);
                if (module != null) AddOrIncrement(station.Modules, module);
                else if (!string.IsNullOrWhiteSpace(moduleId) &&
                         _gameData.StationModuleDefinitions.TryGetValue(moduleId, out var definition))
                    AddOrIncrement(station.AdditionalModules,
                        new StationModule { ModuleId = moduleId, Kind = definition.Kind });
            }
            else if (string.IsNullOrEmpty(station.IconKey))
            {
                var icon = subtree.GetAttribute("icon");
                if (!string.IsNullOrWhiteSpace(icon)) station.IconKey = icon;
            }

            if (subtree.IsEmptyElement) subtreeElements[subtree.Depth] = null;
        }

        station.SectorPosition = (zone?.Offset ?? Vec3.Zero) + stationOffset;
        return station;
    }

    private static ComponentContext? FindAncestor(
        ComponentContext?[] components, int beforeDepth, string componentClass)
    {
        for (var depth = beforeDepth - 1; depth >= 0; depth--)
        {
            var component = components[depth];
            if (component != null && string.Equals(component.Class, componentClass, StringComparison.OrdinalIgnoreCase))
                return component;
        }
        return null;
    }

    private ProductionModule? TryCreateModule(string? macro)
    {
        if (string.IsNullOrWhiteSpace(macro)) return null;
        if (_gameData.StationModuleDefinitions.TryGetValue(macro, out var definition) &&
            definition.Products.Count > 0)
        {
            var product = definition.Products[0];
            var definedWare = _gameData.FindByWareId(product.WareId);
            var definedRecipe = definedWare?.Production?.FirstOrDefault(item =>
                                    string.Equals(item.Method, product.Method, StringComparison.OrdinalIgnoreCase))
                                ?? definedWare?.Production?.FirstOrDefault(item => item.Method == "default")
                                ?? definedWare?.Production?.FirstOrDefault();
            if (definedWare != null && definedRecipe != null)
                return new ProductionModule { ModuleId = macro, WareId = definedWare.Id, Method = definedRecipe.Method, Count = 1, Ware = definedWare, Recipe = definedRecipe };
        }
        var match = ModuleMacroRegex.Match(macro);
        if (!match.Success) return null;
        var wareId = match.Groups["ware"].Value;

        var ware = _gameData.FindByWareId(wareId);
        if (ware?.Production is not { Count: > 0 }) return null;
        var recipe = ware.Production.FirstOrDefault(item => item.Method == "default") ?? ware.Production[0];
        return new ProductionModule
        {
            ModuleId = macro,
            WareId = wareId,
            Method = recipe.Method,
            Count = 1,
            Ware = ware,
            Recipe = recipe
        };
    }

    private static void AddOrIncrement(ICollection<ProductionModule> modules, ProductionModule incoming)
    {
        var existing = modules.FirstOrDefault(item => string.Equals(item.ModuleId, incoming.ModuleId, StringComparison.OrdinalIgnoreCase));
        if (existing != null) existing.Count += incoming.Count;
        else modules.Add(incoming);
    }

    private static void AddOrIncrement(ICollection<StationModule> modules, StationModule incoming)
    {
        var existing = modules.FirstOrDefault(item => string.Equals(item.ModuleId, incoming.ModuleId, StringComparison.OrdinalIgnoreCase));
        if (existing != null) existing.Count += incoming.Count;
        else modules.Add(incoming);
    }

    private static string ResolveIconKey(string? storedIcon, string macro)
    {
        if (!string.IsNullOrWhiteSpace(storedIcon)) return storedIcon;
        return macro.Contains("headquarters", StringComparison.OrdinalIgnoreCase)
            ? "mapob_playerhq"
            : string.Empty;
    }

    private static Vec3 ReadPosition(XmlReader reader) => new(
        ReadDouble(reader.GetAttribute("x")),
        ReadDouble(reader.GetAttribute("y")),
        ReadDouble(reader.GetAttribute("z")));

    private static double ReadDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private sealed class ComponentContext(string componentClass, string macro)
    {
        public string Class { get; } = componentClass;
        public string Macro { get; } = macro;
        public Vec3 Offset { get; set; }
    }
}
