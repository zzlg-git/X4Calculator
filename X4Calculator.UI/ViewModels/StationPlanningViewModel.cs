using System.Collections.ObjectModel;
using X4Calculator.Core.Calculation;
using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.UI.ViewModels;

public sealed class StationPlanningViewModel : ViewModelBase, IStationTransportMapSource,
    IStationTransportOptimizationSource, IStationMapPlacementSource, IDisposable
{
    private const string AllFilterValue = "*";
    private static readonly IReadOnlyList<StationModuleFilterOption> KindFilters =
    [
        new("production", "生产"), new("construction", "建造"), new("docking", "停靠"),
        new("habitation", "居住"), new("storage", "仓储"), new("connection", "连接"),
        new("defence", "防御"), new("other", "其他")
    ];
    private readonly GameDataDB _gameData;
    private readonly StationProductionPlanner _planner;
    private readonly StationWorkforceGrowthCalculator _workforceGrowthCalculator;
    private readonly StationStorageAllocationCalculator _storageAllocationCalculator;
    private readonly StationWorkforceConsumptionCalculator _workforceConsumptionCalculator;
    private readonly StationTransportNetworkCalculator _transportNetworkCalculator = new();
    private readonly StationTransportCapabilityCalculator _transportCapabilityCalculator;
    private readonly TransportShipConfigurationStore _transportShipConfigurations;
    private readonly Action<string>? _showError;
    private StarMapDB? _starMap;
    private SectorGraph? _sectorGraph;
    private StationTransportEfficiencyCalculator? _transportEfficiencyCalculator;
    private double? _savegameTimeSeconds;
    private IReadOnlySet<string> _playerBlueprintWareIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private StationPlanningStationItemViewModel? _selectedStationItem;
    private StationPlanningStationItemViewModel? _placementTarget;
    private bool _isOverview = true, _isImportedStationsExpanded = true, _isPlannedStationsExpanded = true;
    private bool _isAutoAddedModulesExpanded = true, _isSelectedModulesExpanded = true;
    private bool _isStorageEconomyView, _isInstantBuildAllPending;
    private bool _isIncomingGroupedByWare = true, _isOutgoingGroupedByWare = true;
    private bool _isIncomingTransportExpanded = true, _isOutgoingTransportExpanded = true;
    private bool _isTransportLoading, _hasTransportStatus = true, _hasTransportContent;
    private bool _useSmallTransportShips;
    private TransportShipSize _selectedStationInfoShipSize = TransportShipSize.Large;
    private bool _hasStationTransportInfoError;
    private string _searchText = string.Empty, _stationName = string.Empty, _stationSectorText = string.Empty;
    private string _selectedTradeWareId = string.Empty;
    private string _transportStatusText = "导入空间站后将在后台计算运输链路。";
    private string _stationTransportInfoErrorText = string.Empty;
    private string _selectedKindFilter = AllFilterValue, _selectedRaceFilter = AllFilterValue;
    private StationProductionRateUnit _rateUnit;
    private int _nextPlannedStationNumber = 1;
    private int _transportGeneration;
    private readonly object _transportCancellationGate = new();
    private readonly SemaphoreSlim _transportWorkerGate = new(1, 1);
    private CancellationTokenSource? _transportCancellation;
    private StationTransportNetwork? _transportNetwork;
    private Dictionary<string, StationTransportLinkSelection> _transportSelections =
        new(StringComparer.OrdinalIgnoreCase);

    public StationPlanningViewModel(
        GameDataDB gameData,
        Action<string>? showError = null,
        TransportShipConfigurationStore? transportShipConfigurations = null)
    {
        _gameData = gameData;
        _planner = new StationProductionPlanner(gameData);
        _workforceGrowthCalculator = new StationWorkforceGrowthCalculator(gameData);
        _storageAllocationCalculator = new StationStorageAllocationCalculator(gameData);
        _workforceConsumptionCalculator = new StationWorkforceConsumptionCalculator(gameData);
        _transportCapabilityCalculator = new StationTransportCapabilityCalculator(gameData);
        _transportShipConfigurations = transportShipConfigurations ?? new TransportShipConfigurationStore();
        _transportShipConfigurations.ConfigurationChanged += TransportShipConfigurationsOnConfigurationChanged;
        _showError = showError;
        SelectOverviewCommand = new RelayCommand(_ => SelectOverview());
        SelectStationCommand = new RelayCommand(SelectStation);
        CreateStationCommand = new RelayCommand(_ => CreateStation());
        DeleteStationCommand = new RelayCommand(DeleteStation);
        AutoNameStationCommand = new RelayCommand(_ => AutoNameStation());
        ChooseStationPlacementCommand = new RelayCommand(
            _ => BeginStationPlacement(),
            _ => CanChooseStationPlacement && !IsPlacementActive);
        AddModuleCommand = new RelayCommand(value => AddModule(value as StationModuleDefinition));
        RemoveModuleCommand = new RelayCommand(value => RemoveModule(value as StationPlanningModuleItem));
        AutoAddIntermediateProductsCommand = new RelayCommand(_ => AutoAddIntermediateProducts());
        InstantBuildCommand = new RelayCommand(_ => InstantBuild());
        InstantBuildAllCommand = new RelayCommand(_ => InstantBuildAll());
        ToggleImportedStationsCommand = new RelayCommand(_ => IsImportedStationsExpanded = !IsImportedStationsExpanded);
        TogglePlannedStationsCommand = new RelayCommand(_ => IsPlannedStationsExpanded = !IsPlannedStationsExpanded);
        ToggleAutoAddedModulesCommand = new RelayCommand(_ => IsAutoAddedModulesExpanded = !IsAutoAddedModulesExpanded);
        ToggleSelectedModulesCommand = new RelayCommand(_ => IsSelectedModulesExpanded = !IsSelectedModulesExpanded);
        ToggleCapacityItemCommand = new RelayCommand(value => { if (value is StationCapacityProductItemViewModel item) item.IsExpanded = !item.IsExpanded; });
        ToggleRateUnitCommand = new RelayCommand(_ => RateUnit = RateUnit == StationProductionRateUnit.PerMinute
            ? StationProductionRateUnit.PerHour : StationProductionRateUnit.PerMinute);
        ToggleStorageEconomyViewCommand = new RelayCommand(_ => IsStorageEconomyView = !IsStorageEconomyView);
        ToggleIncomingTransportCommand = new RelayCommand(_ => IsIncomingTransportExpanded = !IsIncomingTransportExpanded);
        ToggleOutgoingTransportCommand = new RelayCommand(_ => IsOutgoingTransportExpanded = !IsOutgoingTransportExpanded);
        ToggleTransportShipSizeCommand = new RelayCommand(_ =>
            UseSmallTransportShips = !UseSmallTransportShips);
        AddTradeWareCommand = new RelayCommand(_ => AddTradeWare(), _ => CanAddTradeWare);
    }

    public ObservableCollection<StationPlanningStationItemViewModel> ImportedStations { get; } = new();
    public ObservableCollection<StationPlanningStationItemViewModel> PlannedStations { get; } = new();
    public ObservableCollection<StationCapacityProductItemViewModel> OverviewItems { get; } = new();
    public ObservableCollection<StationPlanningModuleItem> Modules { get; } = new();
    public ObservableCollection<StationPlanningModuleItem> AutoAddedModules { get; } = new();
    public ObservableCollection<StationPlanningModuleItem> SelectedModules { get; } = new();
    public ObservableCollection<StationCapacityProductItemViewModel> CapacityItems { get; } = new();
    public ObservableCollection<StationWorkforceCapacityItemViewModel> WorkforceCapacityItems { get; } = new();
    public ObservableCollection<StationModuleDefinition> SearchResults { get; } = new();
    public ObservableCollection<StationWareItemViewModel> StorageWareItems { get; } = new();
    public ObservableCollection<StationTradeWareOption> AvailableTradeWareOptions { get; } = new();
    public ObservableCollection<StationTransportGroupItemViewModel> IncomingTransportWareGroups { get; } = new();
    public ObservableCollection<StationTransportGroupItemViewModel> IncomingTransportStationGroups { get; } = new();
    public ObservableCollection<StationTransportGroupItemViewModel> OutgoingTransportWareGroups { get; } = new();
    public ObservableCollection<StationTransportGroupItemViewModel> OutgoingTransportStationGroups { get; } = new();
    public ObservableCollection<StationTransportStorageStatisticItemViewModel> StationTransportStatistics { get; } = new();
    public ObservableCollection<StationModuleFilterOption> KindFilterOptions { get; } = new();
    public ObservableCollection<StationModuleFilterOption> RaceFilterOptions { get; } = new();
    public IReadOnlyList<StationPlanningSelectionOption<int>> ManagerStarOptions { get; } =
        [new(5, "5"), new(4, "4"), new(3, "3"), new(2, "2"), new(1, "1"), new(0, "0")];
    public IReadOnlyList<StationPlanningSelectionOption<string>> StationDutyOptions { get; } =
        [new("工厂", "工厂"), new("贸易", "贸易"), new("终端", "终端")];
    public IReadOnlyList<StationPlanningSelectionOption<string>> PreferredBuildMethodOptions { get; } =
        [new("default", "常规"), new("terran", "TER"), new("recycling", "闭环")];
    public IReadOnlyList<StationPlanningSelectionOption<string>> PreferredRaceOptions { get; } =
        [new("argon", "Argon"), new("boron", "Boron"), new("paranid", "Paranid"),
         new("split", "Split"), new("teladi", "Teladi"), new("terran", "Terran")];
    public IReadOnlyList<StationPlanningSelectionOption<TransportShipSize>> TransportShipSizeOptions { get; } =
        [new(TransportShipSize.Large, "大型船只"), new(TransportShipSize.Small, "小型船只")];
    public RelayCommand SelectOverviewCommand { get; }
    public RelayCommand SelectStationCommand { get; }
    public RelayCommand CreateStationCommand { get; }
    public RelayCommand DeleteStationCommand { get; }
    public RelayCommand AutoNameStationCommand { get; }
    public RelayCommand ChooseStationPlacementCommand { get; }
    public RelayCommand AddModuleCommand { get; }
    public RelayCommand RemoveModuleCommand { get; }
    public RelayCommand AutoAddIntermediateProductsCommand { get; }
    public RelayCommand InstantBuildCommand { get; }
    public RelayCommand InstantBuildAllCommand { get; }
    public RelayCommand ToggleImportedStationsCommand { get; }
    public RelayCommand TogglePlannedStationsCommand { get; }
    public RelayCommand ToggleAutoAddedModulesCommand { get; }
    public RelayCommand ToggleSelectedModulesCommand { get; }
    public RelayCommand ToggleCapacityItemCommand { get; }
    public RelayCommand ToggleRateUnitCommand { get; }
    public RelayCommand ToggleStorageEconomyViewCommand { get; }
    public RelayCommand ToggleIncomingTransportCommand { get; }
    public RelayCommand ToggleOutgoingTransportCommand { get; }
    public RelayCommand ToggleTransportShipSizeCommand { get; }
    public RelayCommand AddTradeWareCommand { get; }

    public bool IsStorageEconomyView
    {
        get => _isStorageEconomyView;
        private set
        {
            if (!SetProperty(ref _isStorageEconomyView, value)) return;
            OnPropertyChanged(nameof(IsModuleProductionView));
            OnPropertyChanged(nameof(StorageEconomyToggleText));
            if (value) RefreshTransportView();
        }
    }
    public bool IsModuleProductionView => !IsStorageEconomyView;
    public string StorageEconomyToggleText => IsStorageEconomyView ? "切换到模块/产能视图" : "切换到仓储/经济视图";

    public string SelectedTradeWareId
    {
        get => _selectedTradeWareId;
        set
        {
            if (!SetProperty(ref _selectedTradeWareId, value ?? string.Empty)) return;
            OnPropertyChanged(nameof(CanAddTradeWare));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool CanAddTradeWare => SelectedStation != null && AvailableTradeWareOptions.Any(option =>
        option.WareId.Equals(SelectedTradeWareId, StringComparison.OrdinalIgnoreCase));

    public bool IsIncomingGroupedByWare
    {
        get => _isIncomingGroupedByWare;
        set
        {
            if (!SetProperty(ref _isIncomingGroupedByWare, value)) return;
            OnPropertyChanged(nameof(IsIncomingGroupedByStation));
        }
    }
    public bool IsIncomingGroupedByStation => !IsIncomingGroupedByWare;
    public bool IsIncomingTransportExpanded
    {
        get => _isIncomingTransportExpanded;
        private set
        {
            if (SetProperty(ref _isIncomingTransportExpanded, value))
                OnPropertyChanged(nameof(IncomingTransportToggleGlyph));
        }
    }
    public string IncomingTransportToggleGlyph => IsIncomingTransportExpanded ? "▲" : "▼";
    public bool IsOutgoingGroupedByWare
    {
        get => _isOutgoingGroupedByWare;
        set
        {
            if (!SetProperty(ref _isOutgoingGroupedByWare, value)) return;
            OnPropertyChanged(nameof(IsOutgoingGroupedByStation));
        }
    }
    public bool IsOutgoingGroupedByStation => !IsOutgoingGroupedByWare;
    public bool IsOutgoingTransportExpanded
    {
        get => _isOutgoingTransportExpanded;
        private set
        {
            if (SetProperty(ref _isOutgoingTransportExpanded, value))
                OnPropertyChanged(nameof(OutgoingTransportToggleGlyph));
        }
    }
    public string OutgoingTransportToggleGlyph => IsOutgoingTransportExpanded ? "▲" : "▼";
    public bool UseSmallTransportShips
    {
        get => _useSmallTransportShips;
        private set
        {
            if (!SetProperty(ref _useSmallTransportShips, value)) return;
            OnPropertyChanged(nameof(TransportShipSizeToggleText));
            RefreshTransportView();
        }
    }
    public string TransportShipSizeToggleText => UseSmallTransportShips
        ? "切换为使用大型船只运输"
        : "切换为使用小型船只运输";
    public TransportShipSize SelectedStationInfoShipSize
    {
        get => _selectedStationInfoShipSize;
        set
        {
            if (!SetProperty(ref _selectedStationInfoShipSize, value)) return;
            RefreshStationTransportStatistics();
        }
    }
    public bool IsStationTransportShipSizeFilterVisible { get; private set; }
    public bool HasStationTransportInfoError
    {
        get => _hasStationTransportInfoError;
        private set => SetProperty(ref _hasStationTransportInfoError, value);
    }
    public string StationTransportInfoErrorText
    {
        get => _stationTransportInfoErrorText;
        private set => SetProperty(ref _stationTransportInfoErrorText, value);
    }
    public double TransportEfficiencyM3PerSecond =>
        IncomingTransportEfficiencyM3PerSecond + OutgoingTransportEfficiencyM3PerSecond;
    public string TransportEfficiencyText => $"{TransportEfficiencyM3PerSecond:0.#} m³/s";
    public double IncomingTransportEfficiencyM3PerSecond => IncomingTransportWareGroups.Sum(group => group.EfficiencyM3PerSecond);
    public string IncomingTransportEfficiencyText => $"{IncomingTransportEfficiencyM3PerSecond:0.#} m³/s";
    public double OutgoingTransportEfficiencyM3PerSecond => OutgoingTransportWareGroups.Sum(group => group.EfficiencyM3PerSecond);
    public string OutgoingTransportEfficiencyText => $"{OutgoingTransportEfficiencyM3PerSecond:0.#} m³/s";
    public bool IsTransportLoading { get => _isTransportLoading; private set => SetProperty(ref _isTransportLoading, value); }
    public bool HasTransportStatus { get => _hasTransportStatus; private set => SetProperty(ref _hasTransportStatus, value); }
    public bool HasTransportContent { get => _hasTransportContent; private set => SetProperty(ref _hasTransportContent, value); }
    public string TransportStatusText { get => _transportStatusText; private set => SetProperty(ref _transportStatusText, value); }
    public Task TransportCalculationTask { get; private set; } = Task.CompletedTask;
    public IReadOnlyList<StationTransportMapRoute> SelectedTransportRoutes => _transportNetwork?.Routes
        .Where(route => _transportSelections.TryGetValue(GetTransportRouteKey(route), out var selection) &&
                        selection.IsSelected)
        .Select(route => new StationTransportMapRoute(route, ResolveTransportWareName(route.WareId)))
        .ToArray() ?? [];
    public bool IsTransportNetworkReady => !IsTransportLoading && _transportNetwork != null;
    public IReadOnlyList<StationTransportOptimizationLink> SelectedTransportOptimizationLinks
    {
        get
        {
            if (!IsTransportNetworkReady) return [];
            var stations = AllStations()
                .Where(item => !string.IsNullOrWhiteSpace(item.Station.Id))
                .GroupBy(item => item.Station.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Station,
                    StringComparer.OrdinalIgnoreCase);
            return _transportNetwork!.Routes
                .Where(route => route.Path != null &&
                    _transportSelections.TryGetValue(GetTransportRouteKey(route), out var selection) &&
                    selection.IsSelected)
                .Select(route =>
                {
                    if (ResolveTransportStorageType(route.WareId) is not { } storageType ||
                        !stations.TryGetValue(route.SourceStationId, out var source) ||
                        !stations.TryGetValue(route.TargetStationId, out var target))
                        return null;
                    return new StationTransportOptimizationLink(
                        storageType, route.Path!, source.SectorPosition, target.SectorPosition);
                })
                .Where(link => link != null)
                .Select(link => link!)
                .ToArray();
        }
    }
    public event EventHandler? TransportMapStateChanged;
    public event EventHandler? TransportOptimizationStateChanged;
    public event EventHandler? PlacementStateChanged;
    public event EventHandler<StationMapPlacementNavigationEventArgs>? PlacementNavigationRequested;

    public bool IsOverview { get => _isOverview; private set { if (SetProperty(ref _isOverview, value)) OnPropertyChanged(nameof(IsStationSelected)); } }
    public bool IsStationSelected => !IsOverview && SelectedStation != null;
    public Station? SelectedStation => _selectedStationItem?.Station;
    public StationPlanningStationItemViewModel? SelectedStationItem => _selectedStationItem;
    public bool CanChooseStationPlacement => _selectedStationItem?.IsPlanned == true;
    public bool IsPlacementActive => _placementTarget != null;
    public Station? PendingPlacementStation => _placementTarget?.Station;
    public IReadOnlyList<Station> PlacedPlannedStations => PlannedStations
        .Where(item => item.HasMapPlacement && !ReferenceEquals(item, _placementTarget))
        .Select(item => item.Station)
        .ToArray();
    public string StationName { get => _stationName; set { if (SetProperty(ref _stationName, value) && _selectedStationItem != null) _selectedStationItem.Name = value; } }
    public string StationCode => _selectedStationItem is { IsPlanned: false }
        ? SelectedStation?.Code ?? string.Empty
        : string.Empty;
    public bool HasStationCode => !string.IsNullOrWhiteSpace(StationCode);
    public string StationSectorText
    {
        get => _stationSectorText;
        set
        {
            if (!SetProperty(ref _stationSectorText, value)) return;
            if (_selectedStationItem == null || _starMap == null) return;
            var sector = FindSectorByName(value);
            if (sector == null) return;
            var sectorChanged = !string.Equals(
                _selectedStationItem.Station.SectorId,
                sector.Id,
                StringComparison.OrdinalIgnoreCase);
            if (sectorChanged)
            {
                _selectedStationItem.Station.GeneratedNameIndex = null;
                if (_selectedStationItem is { IsPlanned: true, HasMapPlacement: true })
                {
                    _selectedStationItem.HasMapPlacement = false;
                    OnPropertyChanged(nameof(PlacedPlannedStations));
                    PlacementStateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            _selectedStationItem.Station.SectorId = sector.Id;
            _selectedStationItem.Station.SolarEfficiencyPercent = (int)Math.Round(sector.SunlightFactor * 100, MidpointRounding.AwayFromZero);
            OnPropertyChanged(nameof(SolarEfficiencyPercent));
            RefreshStation();
            StartTransportCalculation();
        }
    }
    public IReadOnlyList<string> SectorNames { get; private set; } = Array.Empty<string>();
    public bool FillWorkforceCapacity { get => _selectedStationItem?.FillWorkforceCapacity ?? false; set { if (_selectedStationItem == null) return; _selectedStationItem.FillWorkforceCapacity = value; OnPropertyChanged(); } }
    public bool SkipWorkforceGrowth { get => _selectedStationItem?.SkipWorkforceGrowth ?? true; set { if (_selectedStationItem == null) return; _selectedStationItem.SkipWorkforceGrowth = value; OnPropertyChanged(); } }
    public bool UseTeladianiumMaterials { get => _selectedStationItem?.UseTeladianiumMaterials ?? false; set { if (_selectedStationItem == null) return; _selectedStationItem.UseTeladianiumMaterials = value; OnPropertyChanged(); RefreshCapacityItems(); } }
    public string PreferredRace { get => _selectedStationItem?.PreferredRace ?? "argon"; set { if (_selectedStationItem == null) return; _selectedStationItem.PreferredRace = value; OnPropertyChanged(); } }
    public int ManagerStars { get => _selectedStationItem?.ManagerStars ?? 5; set { if (_selectedStationItem == null) return; _selectedStationItem.ManagerStars = value; OnPropertyChanged(); } }
    public string StationDuty { get => _selectedStationItem?.StationDuty ?? "工厂"; set { if (_selectedStationItem == null) return; _selectedStationItem.StationDuty = value; OnPropertyChanged(); } }
    public string PreferredBuildMethod
    {
        get => _selectedStationItem?.PreferredBuildMethod ?? "default";
        set
        {
            if (_selectedStationItem == null || string.Equals(
                    _selectedStationItem.PreferredBuildMethod, value, StringComparison.OrdinalIgnoreCase)) return;
            _selectedStationItem.PreferredBuildMethod = value;
            OnPropertyChanged();
            RefreshStation();
        }
    }
    public int SolarEfficiencyPercent
    {
        get => SelectedStation?.SolarEfficiencyPercent ?? 100;
        set
        {
            if (SelectedStation == null) return;
            var normalized = Math.Clamp(value, SolarEfficiencyMinimumPercent, SolarEfficiencyMaximumPercent);
            if (SelectedStation.SolarEfficiencyPercent == normalized) { OnPropertyChanged(); return; }
            SelectedStation.SolarEfficiencyPercent = normalized;
            OnPropertyChanged();
            RefreshStation();
            StartTransportCalculation();
        }
    }
    public int SolarEfficiencyMinimumPercent => _starMap is { Sectors.Count: > 0 }
        ? _starMap.Sectors.Values.Min(item => item.SunlightPercent) : 2;
    public int SolarEfficiencyMaximumPercent => _starMap is { Sectors.Count: > 0 }
        ? _starMap.Sectors.Values.Max(item => item.SunlightPercent) : 1390;
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) RebuildSearch(); } }
    public string SelectedKindFilter { get => _selectedKindFilter; set { if (SetProperty(ref _selectedKindFilter, value)) RebuildSearch(); } }
    public string SelectedRaceFilter { get => _selectedRaceFilter; set { if (SetProperty(ref _selectedRaceFilter, value)) RebuildSearch(); } }
    public bool IsImportedStationsExpanded { get => _isImportedStationsExpanded; private set { if (SetProperty(ref _isImportedStationsExpanded, value)) OnPropertyChanged(nameof(ImportedStationsToggleGlyph)); } }
    public bool IsPlannedStationsExpanded { get => _isPlannedStationsExpanded; private set { if (SetProperty(ref _isPlannedStationsExpanded, value)) OnPropertyChanged(nameof(PlannedStationsToggleGlyph)); } }
    public string ImportedStationsToggleGlyph => IsImportedStationsExpanded ? "▲" : "▼";
    public string PlannedStationsToggleGlyph => IsPlannedStationsExpanded ? "▲" : "▼";
    public bool IsAutoAddedModulesExpanded { get => _isAutoAddedModulesExpanded; private set { if (SetProperty(ref _isAutoAddedModulesExpanded, value)) OnPropertyChanged(nameof(AutoAddedModulesToggleGlyph)); } }
    public bool IsSelectedModulesExpanded { get => _isSelectedModulesExpanded; private set { if (SetProperty(ref _isSelectedModulesExpanded, value)) OnPropertyChanged(nameof(SelectedModulesToggleGlyph)); } }
    public string AutoAddedModulesToggleGlyph => IsAutoAddedModulesExpanded ? "▲" : "▼";
    public string SelectedModulesToggleGlyph => IsSelectedModulesExpanded ? "▲" : "▼";
    public bool HasAutoAddedModules => AutoAddedModules.Count > 0;
    public string WorkforceAvailableText { get; private set; } = "0";
    public string WorkforceRequiredText { get; private set; } = "0";
    public string WorkforceCurrentRequiredText { get; private set; } = "0";
    public bool HasIncompleteProductionModules { get; private set; }
    public bool HasSingleWorkforceRequirement => !HasIncompleteProductionModules;
    public string WorkforceCoverageText { get; private set; } = "100%";
    public string WorkforceCapacityText { get; private set; } = "0";
    public string WorkforceGrowthPerCycleText { get; private set; } = "0";
    public string WorkforceNextGrowthTimeText { get; private set; } = "0d0h0m0s";
    public string WorkforceGrowthTimeText { get; private set; } = "0d0h0m0s";
    public string WorkforceBaseGrowthText { get; private set; } = "+20";
    public string WorkforcePopulationBonusText { get; private set; } = "+0%";
    public string WorkforceCapacityBonusText { get; private set; } = "+0%";
    public string WorkforceWelfareBonusText { get; private set; } = "+0%";
    public bool AreStandardWorkforceGrowthInfluencesVisible { get; private set; } = true;
    public string WorkforceGrowthConstraintLabel { get; private set; } = "居住环境拥挤";
    public string WorkforceGrowthConstraintText { get; private set; } = "0%";
    public string SectorPopulationBonusText { get; private set; } = "+0%";
    public bool HasWorkforceShiftTime { get; private set; }
    public string WorkforceShiftTimeText { get; private set; } = string.Empty;
    public bool IsWorkforceSufficient { get; private set; } = true;
    public bool CanAutoAddIntermediateProducts { get; private set; }
    public StationProductionRateUnit RateUnit
    {
        get => _rateUnit;
        private set
        {
            if (!SetProperty(ref _rateUnit, value)) return;
            OnPropertyChanged(nameof(RateUnitToggleText));
            OnPropertyChanged(nameof(OverviewRateDescription));
            foreach (var item in OverviewItems.Concat(CapacityItems)) item.RateUnit = value;
        }
    }
    public string RateUnitToggleText => RateUnit == StationProductionRateUnit.PerMinute ? "切换为 /小时" : "切换为 /分钟";
    public string OverviewRateDescription => $"按生产链路标记排序；数值为{(RateUnit == StationProductionRateUnit.PerMinute ? "每分钟" : "每小时")}净产能。";
    public bool IsInstantBuildAllPending
    {
        get => _isInstantBuildAllPending;
        private set
        {
            if (!SetProperty(ref _isInstantBuildAllPending, value)) return;
            OnPropertyChanged(nameof(InstantBuildAllButtonText));
        }
    }
    public string InstantBuildAllButtonText => IsInstantBuildAllPending ? "确认？" : "瞬间建造所有模块";

    private void BeginStationPlacement()
    {
        if (_selectedStationItem is not { IsPlanned: true } item)
            return;

        _placementTarget = item;
        OnPropertyChanged(nameof(IsPlacementActive));
        OnPropertyChanged(nameof(PendingPlacementStation));
        PlacementStateChanged?.Invoke(this, EventArgs.Empty);
        PlacementNavigationRequested?.Invoke(this,
            new StationMapPlacementNavigationEventArgs(StationMapPlacementNavigationTarget.StarMap));
    }

    public void CompletePlacement(
        SectorInfo sector,
        Vec3 sectorPosition,
        double displayX,
        double displayY)
    {
        ArgumentNullException.ThrowIfNull(sector);
        if (_placementTarget is not { IsPlanned: true } item ||
            !PlannedStations.Contains(item))
            return;
        if (!double.IsFinite(sectorPosition.X) || !double.IsFinite(sectorPosition.Z) ||
            !double.IsFinite(displayX) || !double.IsFinite(displayY))
        {
            RejectPlacement();
            return;
        }

        var station = item.Station;
        if (!string.Equals(station.SectorId, sector.Id, StringComparison.OrdinalIgnoreCase))
            station.GeneratedNameIndex = null;
        station.SectorId = sector.Id;
        station.ZoneId = string.Empty;
        station.SectorPosition = new Vec3(sectorPosition.X, 0, sectorPosition.Z);
        station.DisplayX = displayX;
        station.DisplayY = displayY;
        station.SolarEfficiencyPercent = (int)Math.Round(
            sector.SunlightFactor * 100,
            MidpointRounding.AwayFromZero);
        item.HasMapPlacement = true;

        _selectedStationItem = item;
        _stationSectorText = sector.SearchDisplayName;
        _placementTarget = null;
        IsOverview = false;
        OnPropertyChanged(nameof(SelectedStation));
        OnPropertyChanged(nameof(SelectedStationItem));
        OnPropertyChanged(nameof(CanChooseStationPlacement));
        OnPropertyChanged(nameof(StationSectorText));
        OnPropertyChanged(nameof(SolarEfficiencyPercent));
        OnPropertyChanged(nameof(IsPlacementActive));
        OnPropertyChanged(nameof(PendingPlacementStation));
        OnPropertyChanged(nameof(PlacedPlannedStations));
        RefreshStation(notifyPlacementState: false);
        StartTransportCalculation();
        PlacementStateChanged?.Invoke(this, EventArgs.Empty);
        PlacementNavigationRequested?.Invoke(this,
            new StationMapPlacementNavigationEventArgs(StationMapPlacementNavigationTarget.StationPlanning));
    }

    public void RejectPlacement() =>
        _showError?.Invoke("空间站必须放置到六边形扇区内部");

    public void SetStarMap(StarMapDB? starMap)
    {
        _starMap = starMap;
        _sectorGraph = starMap == null ? null : SectorGraph.Build(starMap);
        _transportEfficiencyCalculator = starMap == null
            ? null
            : new StationTransportEfficiencyCalculator(starMap);
        SectorNames = starMap?.Sectors.Values.Select(item => item.SearchDisplayName).Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.Ordinal).ToArray() ?? [];
        OnPropertyChanged(nameof(SectorNames));
        OnPropertyChanged(nameof(SolarEfficiencyMinimumPercent));
        OnPropertyChanged(nameof(SolarEfficiencyMaximumPercent));
        StartTransportCalculation();
    }

    public void SetStations(
        IReadOnlyList<Station> stations,
        double? savegameTimeSeconds = null,
        IEnumerable<string>? playerBlueprintWareIds = null)
    {
        _savegameTimeSeconds = savegameTimeSeconds;
        _playerBlueprintWareIds = (playerBlueprintWareIds ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var states = ImportedStations.Where(item => !string.IsNullOrWhiteSpace(item.Station.Id))
            .ToDictionary(item => item.Station.Id,
                item => (item.FillWorkforceCapacity, item.SkipWorkforceGrowth,
                    item.PreferredBuildMethod, item.PreferredRace, item.UseTeladianiumMaterials),
                StringComparer.OrdinalIgnoreCase);
        ImportedStations.Clear();
        foreach (var station in stations.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            ApplySectorSolarEfficiency(station);
            var item = CreateStationItem(station, false);
            if (states.TryGetValue(station.Id, out var state))
            {
                item.FillWorkforceCapacity = state.FillWorkforceCapacity;
                item.SkipWorkforceGrowth = state.SkipWorkforceGrowth;
                item.PreferredRace = state.PreferredRace;
                item.UseTeladianiumMaterials = state.UseTeladianiumMaterials;
                // 存档直属 build@method 是权威状态；只有存档缺失时才保留本页选择。
                if (string.IsNullOrWhiteSpace(station.BuildMethod))
                    item.PreferredBuildMethod = state.PreferredBuildMethod;
            }
            ImportedStations.Add(item);
        }
        SelectOverview();
        StartTransportCalculation();
    }

    public void Initialize() { RebuildFilterOptions(); RebuildSearch(); }
    private IEnumerable<StationPlanningStationItemViewModel> AllStations() => ImportedStations.Concat(PlannedStations);
    private StationPlanningStationItemViewModel CreateStationItem(Station station, bool isPlanned) =>
        new(station, isPlanned, DetermineDefaultStationDuty(station), OnStationItemChanged);

    private void OnStationItemChanged(StationPlanningStationItemViewModel item)
    {
        if (!ReferenceEquals(item, _selectedStationItem))
            return;
        OnPropertyChanged(nameof(FillWorkforceCapacity));
        OnPropertyChanged(nameof(SkipWorkforceGrowth));
        OnPropertyChanged(nameof(UseTeladianiumMaterials));
        OnPropertyChanged(nameof(PreferredRace));
        RefreshStation();
        StartTransportCalculation();
    }

    private void SelectOverview()
    {
        IsInstantBuildAllPending = false;
        ResetDeleteConfirmations();
        _selectedStationItem = null;
        OnPropertyChanged(nameof(SelectedStation)); OnPropertyChanged(nameof(SelectedStationItem));
        OnPropertyChanged(nameof(CanChooseStationPlacement));
        OnPropertyChanged(nameof(StationCode)); OnPropertyChanged(nameof(HasStationCode));
        OnPropertyChanged(nameof(FillWorkforceCapacity)); OnPropertyChanged(nameof(SkipWorkforceGrowth));
        OnPropertyChanged(nameof(UseTeladianiumMaterials));
        OnPropertyChanged(nameof(PreferredRace));
        OnPropertyChanged(nameof(ManagerStars)); OnPropertyChanged(nameof(StationDuty)); OnPropertyChanged(nameof(PreferredBuildMethod));
        StationName = string.Empty;
        IsOverview = true;
        OverviewItems.Clear();
        var contributions = AllStations().SelectMany(station =>
            CalculateCapacityContributions(station).Select(item =>
                (Station: station, item.Contribution, item.IsPlanned)));
        foreach (var group in contributions.GroupBy(item => item.Contribution.WareId, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Min(item => item.Contribution.Role))
                     .ThenBy(group => group.First().Contribution.WareName, StringComparer.Ordinal))
        {
            var first = group.First().Contribution;
            var sources = group.GroupBy(item => (item.Station, item.IsPlanned))
                .Select(source => new StationCapacitySourceItemViewModel(
                    source.Key.Station.Name,
                    source.Sum(item => item.Contribution.PerMinute),
                    source.Min(item => item.Contribution.Role),
                    source.Key.IsPlanned))
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .ThenBy(item => item.IsPlanned)
                .ToList();
            OverviewItems.Add(new StationCapacityProductItemViewModel(first.WareId, first.WareName,
                sources.Where(item => !item.IsPlanned).Sum(item => item.PerMinute),
                sources.Sum(item => item.PerMinute),
                group.Min(item => item.Contribution.Role), sources, RateUnit,
                forceTotalDisplay: group.Any(item => IsWorkforceContribution(item.Contribution))));
        }
        RefreshTransportView();
    }

    private void SelectStation(object? value)
    {
        var item = value as StationPlanningStationItemViewModel;
        if (item == null && value is Station station) item = AllStations().FirstOrDefault(candidate => ReferenceEquals(candidate.Station, station));
        if (item == null) return;
        IsInstantBuildAllPending = false;
        ResetDeleteConfirmations();
        _selectedStationItem = item;
        OnPropertyChanged(nameof(SelectedStation)); OnPropertyChanged(nameof(SelectedStationItem));
        OnPropertyChanged(nameof(CanChooseStationPlacement));
        OnPropertyChanged(nameof(StationCode)); OnPropertyChanged(nameof(HasStationCode));
        OnPropertyChanged(nameof(FillWorkforceCapacity)); OnPropertyChanged(nameof(SkipWorkforceGrowth));
        OnPropertyChanged(nameof(UseTeladianiumMaterials));
        OnPropertyChanged(nameof(PreferredRace));
        OnPropertyChanged(nameof(ManagerStars)); OnPropertyChanged(nameof(StationDuty)); OnPropertyChanged(nameof(PreferredBuildMethod));
        StationName = item.Name;
        _stationSectorText = ResolveSectorName(item.Station.SectorId);
        OnPropertyChanged(nameof(StationSectorText));
        OnPropertyChanged(nameof(SolarEfficiencyPercent));
        IsOverview = false;
        RefreshStation();
        RefreshTransportView();
    }

    private void CreateStation()
    {
        IsInstantBuildAllPending = false;
        ResetDeleteConfirmations();
        string name;
        do name = $"新建空间站 #{_nextPlannedStationNumber++:00}";
        while (AllStations().Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        var item = CreateStationItem(new Station { Id = Guid.NewGuid().ToString("N"), Name = name }, true);
        PlannedStations.Add(item);
        IsPlannedStationsExpanded = true;
        SelectStation(item);
    }

    private void DeleteStation(object? value)
    {
        if (value is not StationPlanningStationItemViewModel item) return;
        IsInstantBuildAllPending = false;
        if (!item.IsDeletePending)
        {
            ResetDeleteConfirmations();
            item.IsDeletePending = true;
            return;
        }

        var wasSelected = ReferenceEquals(_selectedStationItem, item);
        var removedImportedStation = ImportedStations.Remove(item);
        var removedPlacedPlannedStation = !removedImportedStation && item.HasMapPlacement;
        if (!removedImportedStation) PlannedStations.Remove(item);
        if (ReferenceEquals(_placementTarget, item))
        {
            _placementTarget = null;
            OnPropertyChanged(nameof(IsPlacementActive));
            OnPropertyChanged(nameof(PendingPlacementStation));
        }
        if (removedPlacedPlannedStation)
        {
            OnPropertyChanged(nameof(PlacedPlannedStations));
            PlacementStateChanged?.Invoke(this, EventArgs.Empty);
        }
        if (wasSelected || IsOverview) SelectOverview();
        if (removedImportedStation || removedPlacedPlannedStation) StartTransportCalculation();
    }

    private void AutoNameStation()
    {
        if (SelectedStation == null) return;
        var sector = FindSectorByName(StationSectorText);
        if (sector == null)
        {
            _showError?.Invoke("自动命名失败：请先选择有效扇区。");
            return;
        }

        SelectedStation.SectorId = sector.Id;
        StationNameGenerator.AssignGeneratedName(
            SelectedStation,
            sector.SearchDisplayName,
            AllStations().Select(item => item.Station),
            allocateIndexWhenMissing: _selectedStationItem?.IsPlanned == true);
        StationName = SelectedStation.Name;
    }

    private void AddModule(StationModuleDefinition? definition)
    {
        if (SelectedStation == null || definition == null) return;
        if (definition.Products.Count > 0)
        {
            var product = definition.Products[0];
            var ware = _gameData.FindByWareId(product.WareId);
            var recipe = ware?.Production?.FirstOrDefault(item => item.Method.Equals(product.Method, StringComparison.OrdinalIgnoreCase))
                         ?? ware?.Production?.FirstOrDefault(item => item.Method.Equals("default", StringComparison.OrdinalIgnoreCase))
                         ?? ware?.Production?.FirstOrDefault();
            if (ware != null && recipe != null)
            {
                var existing = SelectedStation.Modules.FirstOrDefault(item => !item.IsAutoAdded && item.ModuleId.Equals(definition.Id, StringComparison.OrdinalIgnoreCase));
                if (existing != null) existing.Count++;
                else SelectedStation.Modules.Add(new ProductionModule { ModuleId = definition.Id, WareId = ware.Id, Ware = ware,
                    Recipe = recipe, Method = recipe.Method, SelectedProductWareId = null });
            }
        }
        else
        {
            var existing = SelectedStation.AdditionalModules.FirstOrDefault(item => item.ModuleId.Equals(definition.Id, StringComparison.OrdinalIgnoreCase));
            if (existing != null) existing.Count++;
            else SelectedStation.AdditionalModules.Add(new StationModule { ModuleId = definition.Id, Kind = definition.Kind });
        }
        RefreshStation();
    }

    private void RemoveModule(StationPlanningModuleItem? item)
    {
        if (SelectedStation == null || item == null) return;
        if (item.Production != null)
        {
            SelectedStation.Modules.Remove(item.Production);
        }
        if (item.Additional != null)
        {
            SelectedStation.AdditionalModules.Remove(item.Additional);
        }
        RefreshStation();
        StartTransportCalculation();
    }

    private void InstantBuild()
    {
        if (_selectedStationItem == null) return;
        _selectedStationItem.MarkCurrentModulesBuilt();
        RefreshStation();
        StartTransportCalculation();
    }

    private void InstantBuildAll()
    {
        if (!IsInstantBuildAllPending)
        {
            ResetDeleteConfirmations();
            IsInstantBuildAllPending = true;
            return;
        }

        foreach (var item in AllStations()) item.MarkCurrentModulesBuilt();
        IsInstantBuildAllPending = false;
        SelectOverview();
        StartTransportCalculation();
    }

    private void SetModuleCount(StationPlanningModuleItem item, int requestedCount)
    {
        if (_selectedStationItem == null) return;
        var normalized = Math.Clamp(requestedCount, 1, 1_000_000);
        if (item.Production != null) item.Production.Count = normalized;
        if (item.Additional != null) item.Additional.Count = normalized;
        RefreshStation();
        StartTransportCalculation();
    }

    private void AutoAddIntermediateProducts()
    {
        if (SelectedStation == null || SelectedStation.Modules.Count == 0) return;
        var preservedOperatingModes = SelectedStation.Modules
            .Where(module => module.IsAutoAdded && !string.IsNullOrWhiteSpace(module.ModuleId))
            .GroupBy(module => module.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().OperatingMode, StringComparer.OrdinalIgnoreCase);
        SelectedStation.Modules.RemoveAll(module => module.IsAutoAdded);
        NormalizeTeladianiumProductionVariants();
        var unresolvedWareIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var pass = 0; pass < 512; pass++)
        {
            var deficit = _planner.CalculateBalance(SelectedStation, GetCurrentWorkforce(_selectedStationItem))
                .Where(item => item.Role != ProductionCatalogRole.Resource && item.PerMinute < -0.0000001 &&
                               !unresolvedWareIds.Contains(item.WareId))
                .OrderBy(item => item.Role)
                .ThenBy(item => item.WareName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (deficit == null) break;

            var addition = CreateAutoAddedModule(deficit.WareId, preservedOperatingModes);
            if (addition == null)
            {
                unresolvedWareIds.Add(deficit.WareId);
                continue;
            }
            var existing = SelectedStation.Modules.FirstOrDefault(module => module.IsAutoAdded &&
                module.ModuleId.Equals(addition.ModuleId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(module.SelectedProductWareId, addition.SelectedProductWareId, StringComparison.OrdinalIgnoreCase));
            var required = FindMinimumAdditionalModuleCount(addition, existing, deficit.WareId);
            if (required <= 0)
            {
                unresolvedWareIds.Add(deficit.WareId);
                continue;
            }
            if (existing == null)
            {
                addition.Count = required;
                SelectedStation.Modules.Add(addition);
            }
            else
            {
                existing.Count += required;
            }
        }

        RefreshStation();
        StartTransportCalculation();
    }

    private int FindMinimumAdditionalModuleCount(
        ProductionModule addition,
        ProductionModule? existing,
        string wareId)
    {
        if (SelectedStation == null) return 0;
        var originalCount = existing?.Count ?? 0;
        var candidate = existing ?? addition;
        if (existing == null) SelectedStation.Modules.Add(candidate);

        double GetBalance(int totalCount)
        {
            candidate.Count = totalCount;
            return _planner.CalculateBalance(
                    SelectedStation, GetCurrentWorkforce(_selectedStationItem))
                .FirstOrDefault(item => item.WareId.Equals(
                    wareId, StringComparison.OrdinalIgnoreCase))?.PerMinute ?? 0;
        }

        try
        {
            var low = originalCount + 1;
            var high = low;
            while (GetBalance(high) < -0.0000001)
            {
                if (high >= 1_000_000) return 0;
                high = Math.Min(1_000_000, high * 2);
            }

            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (GetBalance(middle) >= -0.0000001) high = middle;
                else low = middle + 1;
            }
            return low - originalCount;
        }
        finally
        {
            if (existing == null) SelectedStation.Modules.Remove(candidate);
            else candidate.Count = originalCount;
        }
    }

    private ProductionModule? CreateAutoAddedModule(
        string wareId,
        IReadOnlyDictionary<string, string> preservedOperatingModes)
    {
        if (SelectedStation == null) return null;
        var stationRaces = SelectedStation.Modules.Where(module => !module.IsAutoAdded)
            .Select(module => _gameData.StationModuleDefinitions.GetValueOrDefault(module.ModuleId)?.Race)
            .Where(race => !string.IsNullOrWhiteSpace(race))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in GetAutoAddCandidates(wareId)
                     .OrderBy(item => GetBuildMethodRank(item.Definition, item.Product, UseTeladianiumMaterials))
                     .ThenByDescending(item => stationRaces.Contains(item.Definition.Race))
                     .ThenByDescending(item => item.Definition.Race.Equals("argon", StringComparison.OrdinalIgnoreCase) || item.Definition.Race.Equals("default", StringComparison.OrdinalIgnoreCase))
                     .ThenBy(item => item.Definition.Name, StringComparer.Ordinal))
        {
            var definition = item.Definition;
            var product = item.Product;
            var ware = _gameData.FindByWareId(product.WareId);
            var recipe = ware?.Production?.FirstOrDefault(item => item.Method.Equals(product.Method, StringComparison.OrdinalIgnoreCase))
                         ?? ware?.Production?.FirstOrDefault(item => item.Method.Equals("default", StringComparison.OrdinalIgnoreCase))
                         ?? ware?.Production?.FirstOrDefault();
            if (ware == null || recipe == null) continue;
            return new ProductionModule
            {
                ModuleId = definition.Id,
                WareId = ware.Id,
                Ware = ware,
                Recipe = recipe,
                Method = recipe.Method,
                SelectedProductWareId = definition.Products.Count > 1 ? wareId : null,
                OperatingMode = preservedOperatingModes.GetValueOrDefault(definition.Id) ?? "full",
                IsAutoAdded = true
            };
        }
        return null;
    }

    private IEnumerable<(StationModuleDefinition Definition, StationModuleProductDefinition Product)>
        GetAutoAddCandidates(string wareId) =>
        _gameData.StationModuleDefinitions.Values
            .SelectMany(definition => definition.Products
                .Where(product => product.WareId.Equals(wareId, StringComparison.OrdinalIgnoreCase))
                .Select(product => (Definition: definition, Product: product)))
            .Where(item => !item.Product.Method.Equals("recycling", StringComparison.OrdinalIgnoreCase))
            .Where(item => UseTeladianiumMaterials || !IsTeladianiumProduction(item.Definition, item.Product));

    private int GetBuildMethodRank(
        StationModuleDefinition definition,
        StationModuleProductDefinition product,
        bool useTeladianiumMaterials)
    {
        var method = product.Method;
        if (useTeladianiumMaterials && IsTeladianiumProduction(definition, product)) return 0;
        if (method.Equals("default", StringComparison.OrdinalIgnoreCase)) return useTeladianiumMaterials ? 1 : 0;
        if (method.Equals("processing", StringComparison.OrdinalIgnoreCase)) return useTeladianiumMaterials ? 2 : 1;
        return 3;
    }

    private bool IsTeladianiumProduction(
        StationModuleDefinition definition,
        StationModuleProductDefinition product)
    {
        if (product.WareId.Equals("teladianium", StringComparison.OrdinalIgnoreCase)) return true;
        if (!product.Method.Equals("teladi", StringComparison.OrdinalIgnoreCase)) return false;
        var recipe = _gameData.FindByWareId(product.WareId)?.Production?
            .FirstOrDefault(item => item.Method.Equals(product.Method, StringComparison.OrdinalIgnoreCase));
        return recipe?.Consumption?.ContainsKey("teladianium") == true;
    }

    private void NormalizeTeladianiumProductionVariants()
    {
        if (SelectedStation == null || _selectedStationItem == null) return;
        foreach (var source in SelectedStation.Modules.Where(module => !module.IsAutoAdded).ToArray())
        {
            var sourceDefinition = _gameData.StationModuleDefinitions.GetValueOrDefault(source.ModuleId);
            var sourceProduct = sourceDefinition?.Products.FirstOrDefault(product =>
                product.WareId.Equals(source.WareId, StringComparison.OrdinalIgnoreCase));
            if (sourceDefinition == null || sourceProduct == null) continue;

            var sourceIsTeladianium = IsTeladianiumProduction(sourceDefinition, sourceProduct);
            if (UseTeladianiumMaterials == sourceIsTeladianium) continue;
            var desired = _gameData.StationModuleDefinitions.Values
                .SelectMany(definition => definition.Products
                    .Where(product => product.WareId.Equals(source.WareId, StringComparison.OrdinalIgnoreCase))
                    .Select(product => (Definition: definition, Product: product)))
                .Where(item => UseTeladianiumMaterials
                    ? IsTeladianiumProduction(item.Definition, item.Product)
                    : item.Product.Method.Equals("default", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Definition.Race.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                                           item.Definition.Race.Equals("generic", StringComparison.OrdinalIgnoreCase))
                .ThenBy(item => item.Definition.Name, StringComparer.Ordinal)
                .FirstOrDefault();
            if (desired.Definition == null || desired.Definition.Id.Equals(source.ModuleId, StringComparison.OrdinalIgnoreCase))
                continue;

            var movable = GetUnbuiltModuleCount(_selectedStationItem, source);
            if (movable <= 0) continue;
            var target = SelectedStation.Modules.FirstOrDefault(module => !module.IsAutoAdded &&
                module.ModuleId.Equals(desired.Definition.Id, StringComparison.OrdinalIgnoreCase));
            var ware = target == null ? _gameData.FindByWareId(desired.Product.WareId) : null;
            var recipe = ware?.Production?.FirstOrDefault(item =>
                item.Method.Equals(desired.Product.Method, StringComparison.OrdinalIgnoreCase));
            if (target == null && (ware == null || recipe == null)) continue;

            source.Count -= movable;
            if (source.Count == 0) SelectedStation.Modules.Remove(source);
            if (target != null)
            {
                target.Count += movable;
                continue;
            }

            SelectedStation.Modules.Add(new ProductionModule
            {
                ModuleId = desired.Definition.Id,
                WareId = ware!.Id,
                Ware = ware,
                Recipe = recipe!,
                Method = recipe!.Method,
                Count = movable
            });
        }
    }

    private void RefreshStation(bool notifyPlacementState = true)
    {
        Modules.Clear(); AutoAddedModules.Clear(); SelectedModules.Clear(); CapacityItems.Clear();
        if (SelectedStation == null) return;
        var calculationStation = CreateCalculationStation(_selectedStationItem!);
        var growth = CalculateWorkforceGrowth(_selectedStationItem, calculationStation);
        var workforce = _planner.CalculateWorkforce(calculationStation, growth.Current);
        var moduleWorkforceCoverage = calculationStation.Modules.Count == 0 ? 0d : workforce.Coverage;
        var allBuiltRequired = _planner.CalculateWorkforceRequirements(SelectedStation).AllBuiltRequired;
        var hasIncompleteProduction = SelectedStation.Modules.Any(module =>
            GetUnbuiltModuleCount(_selectedStationItem!, module) > 0);
        var workforceRequirements = new StationWorkforceRequirements(
            allBuiltRequired, workforce.Required, hasIncompleteProduction);
        _selectedStationItem?.RefreshIcon();
        _selectedStationItem?.RefreshDefaultStationDuty(DetermineDefaultStationDuty(SelectedStation));
        OnPropertyChanged(nameof(StationDuty));
        var items = new List<(StationPlanningModuleItem Item, int KindOrder)>();
        foreach (var module in SelectedStation.Modules)
        {
            var id = string.IsNullOrWhiteSpace(module.ModuleId) ? $"prod_gen_{module.WareId}_macro" : module.ModuleId;
            var definition = _gameData.StationModuleDefinitions.GetValueOrDefault(id);
            var kind = definition?.Kind ?? "production";
            var outputWare = definition?.Products.Select(product => _gameData.FindByWareId(product.WareId)).FirstOrDefault(ware => ware != null) ?? module.Ware;
            IReadOnlyList<StationPlanningProductFilterOption> filters = definition?.Products.Count > 1
                ? [new(AllFilterValue, "全部"), .. definition.Products.Select(product => new StationPlanningProductFilterOption(product.WareId, _gameData.FindByWareId(product.WareId)?.Name ?? product.WareId))]
                : [];
            IReadOnlyList<StationPlanningOperatingModeOption> operatingModes =
                IsScrapProcessor(id)
                    ? [new("full", "全速"), new("standard", "标准")]
                    : [];
            items.Add((new StationPlanningModuleItem(definition?.Name ?? module.Ware?.FactoryName ?? module.WareId,
                module.Count, module, null, _planner.GetModuleRole(module, outputWare), kind, filters, operatingModes,
                RefreshCapacityAndTransport, module.IsAutoAdded,
                _planner.CalculateModuleWorkforceBonus(module, moduleWorkforceCoverage),
                _selectedStationItem!.GetNewlyAddedModuleCount(module), SetModuleCount), GetKindSortOrder(kind)));
        }
        foreach (var module in SelectedStation.AdditionalModules)
        {
            var definition = _gameData.StationModuleDefinitions.GetValueOrDefault(module.ModuleId);
            var kind = definition?.Kind ?? string.Empty;
            items.Add((new StationPlanningModuleItem(definition?.Name ?? module.ModuleId, module.Count, null, module,
                kind: kind, unbuiltAmount: _selectedStationItem!.GetNewlyAddedModuleCount(module), setCount: SetModuleCount),
                GetKindSortOrder(kind)));
        }
        foreach (var item in items.OrderByDescending(item => item.Item.IsAutoAdded)
                     .ThenBy(item => item.KindOrder).ThenBy(item => item.Item.Name, StringComparer.Ordinal))
        {
            Modules.Add(item.Item);
            if (item.Item.IsAutoAdded) AutoAddedModules.Add(item.Item);
            else SelectedModules.Add(item.Item);
        }
        OnPropertyChanged(nameof(HasAutoAddedModules));
        RefreshCapacityItems();
        RefreshStorageWareItems();
        WorkforceAvailableText = $"{workforce.Current:N0}";
        WorkforceRequiredText = $"{workforceRequirements.AllBuiltRequired:N0}";
        WorkforceCurrentRequiredText = $"{workforceRequirements.CurrentRequired:N0}";
        HasIncompleteProductionModules = workforceRequirements.HasIncompleteProductionModules;
        WorkforceCoverageText = workforceRequirements.CurrentRequired <= 0
            ? "100%"
            : $"{Math.Min(100d, workforce.Current * 100d / workforceRequirements.CurrentRequired):0.#}%";
        WorkforceCapacityText = $"{workforce.Capacity:N0}";
        WorkforceGrowthPerCycleText = $"{growth.GrowthPerCycle:N0}";
        WorkforceNextGrowthTimeText = FormatDuration(growth.SecondsUntilNextGrowth);
        WorkforceGrowthTimeText = FormatDuration(growth.SecondsToTarget);
        WorkforceBaseGrowthText = $"+{growth.BaseGrowth:0.##}";
        WorkforcePopulationBonusText = FormatBonus(growth.PopulationBonus);
        WorkforceCapacityBonusText = FormatBonus(growth.CapacityBonus);
        WorkforceWelfareBonusText = FormatBonus(growth.WelfareBonus);
        AreStandardWorkforceGrowthInfluencesVisible =
            growth.ActiveConstraint != StationWorkforceGrowthConstraint.Layoff;
        WorkforceGrowthConstraintLabel = growth.ActiveConstraint switch
        {
            StationWorkforceGrowthConstraint.LimitedVacancies => "有限空缺",
            StationWorkforceGrowthConstraint.Layoff => "裁员",
            _ => "居住环境拥挤"
        };
        WorkforceGrowthConstraintText = FormatPenalty(growth.ActiveConstraintPenalty);
        SectorPopulationBonusText = WorkforcePopulationBonusText;
        var shiftSeconds = _selectedStationItem is { IsPlanned: false }
            ? StationWorkforceGrowthCalculator.CalculateRemainingSeconds(
                _savegameTimeSeconds, SelectedStation.WorkforceEfficiencyEndTimeSeconds)
            : null;
        HasWorkforceShiftTime = shiftSeconds.HasValue;
        WorkforceShiftTimeText = shiftSeconds.HasValue ? FormatMinutesSeconds(shiftSeconds.Value) : string.Empty;
        WorkforceCapacityItems.Clear();
        if (workforce.CapacityByRace.Count > 1)
            WorkforceCapacityItems.Add(new StationWorkforceCapacityItemViewModel(
                "总计", $"{workforce.Capacity:N0}", true));
        foreach (var capacity in workforce.CapacityByRace)
            WorkforceCapacityItems.Add(new StationWorkforceCapacityItemViewModel(
                GetWorkforceRaceCode(capacity.Race), $"{capacity.Capacity:N0}", false));
        if (WorkforceCapacityItems.Count == 0)
            WorkforceCapacityItems.Add(new StationWorkforceCapacityItemViewModel(string.Empty, "0", false));
        IsWorkforceSufficient = workforce.Current >= workforceRequirements.CurrentRequired;
        OnPropertyChanged(nameof(WorkforceAvailableText));
        OnPropertyChanged(nameof(WorkforceRequiredText));
        OnPropertyChanged(nameof(WorkforceCurrentRequiredText));
        OnPropertyChanged(nameof(HasIncompleteProductionModules));
        OnPropertyChanged(nameof(HasSingleWorkforceRequirement));
        OnPropertyChanged(nameof(WorkforceCoverageText));
        OnPropertyChanged(nameof(WorkforceCapacityText));
        OnPropertyChanged(nameof(WorkforceGrowthPerCycleText));
        OnPropertyChanged(nameof(WorkforceNextGrowthTimeText));
        OnPropertyChanged(nameof(WorkforceGrowthTimeText));
        OnPropertyChanged(nameof(WorkforceBaseGrowthText));
        OnPropertyChanged(nameof(WorkforcePopulationBonusText));
        OnPropertyChanged(nameof(WorkforceCapacityBonusText));
        OnPropertyChanged(nameof(WorkforceWelfareBonusText));
        OnPropertyChanged(nameof(AreStandardWorkforceGrowthInfluencesVisible));
        OnPropertyChanged(nameof(WorkforceGrowthConstraintLabel));
        OnPropertyChanged(nameof(WorkforceGrowthConstraintText));
        OnPropertyChanged(nameof(SectorPopulationBonusText));
        OnPropertyChanged(nameof(HasWorkforceShiftTime));
        OnPropertyChanged(nameof(WorkforceShiftTimeText));
        OnPropertyChanged(nameof(IsWorkforceSufficient));
        if (notifyPlacementState && _selectedStationItem?.HasMapPlacement == true)
        {
            OnPropertyChanged(nameof(PlacedPlannedStations));
            PlacementStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RefreshCapacityItems()
    {
        CapacityItems.Clear();
        if (SelectedStation == null) return;
        var calculationStation = CreateCalculationStation(_selectedStationItem!);
        var workforce = _planner.CalculateWorkforce(
            calculationStation, GetCurrentWorkforce(_selectedStationItem));
        var moduleWorkforceCoverage = calculationStation.Modules.Count == 0 ? 0d : workforce.Coverage;
        foreach (var item in Modules.Where(item => item.Production != null))
            item.SetWorkforceBonus(_planner.CalculateModuleWorkforceBonus(
                item.Production!, moduleWorkforceCoverage));
        foreach (var group in CalculateCapacityContributions(_selectedStationItem!)
                     .GroupBy(item => item.Contribution.WareId, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Min(item => item.Contribution.Role))
                     .ThenBy(group => group.First().Contribution.WareName, StringComparer.Ordinal))
        {
            var role = group.Min(item => item.Contribution.Role);
            var sources = group.GroupBy(item => (
                    item.IsPlanned,
                    ModuleId: item.Contribution.ModuleId.ToUpperInvariant()))
                .Select(source => new StationCapacitySourceItemViewModel(
                    source.First().Contribution.ModuleName,
                    source.Sum(item => item.Contribution.PerMinute),
                    source.Min(item => item.Contribution.Role),
                    isPlanned: source.Key.IsPlanned))
                .OrderBy(item => item.IsPlanned)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ToList();
            CapacityItems.Add(new StationCapacityProductItemViewModel(group.Key,
                group.First().Contribution.WareName,
                sources.Where(item => !item.IsPlanned).Sum(item => item.PerMinute),
                sources.Sum(item => item.PerMinute), role, sources, RateUnit,
                forceTotalDisplay: group.Any(item => IsWorkforceContribution(item.Contribution))));
        }
        CanAutoAddIntermediateProducts = SelectedStation.Modules.Count > 0 &&
            _planner.CalculateBalance(SelectedStation, GetCurrentWorkforce(_selectedStationItem)).Any(item =>
                item.Role != ProductionCatalogRole.Resource && item.PerMinute < -0.0000001 &&
                GetAutoAddCandidates(item.WareId).Any());
        OnPropertyChanged(nameof(CanAutoAddIntermediateProducts));
    }

    private void RefreshCapacityAndTransport()
    {
        RefreshCapacityItems();
        StartTransportCalculation();
    }

    private void RefreshStorageWareItems()
    {
        var expandedOrdinary = StorageWareItems.Where(item => item.IsExpanded && !item.IsStationSupply)
            .Select(item => item.WareId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expandedSupplies = StorageWareItems.Where(item => item.IsExpanded && item.IsStationSupply)
            .Select(item => item.WareId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        StorageWareItems.Clear();
        AvailableTradeWareOptions.Clear();
        SelectedTradeWareId = string.Empty;
        if (SelectedStation == null) return;

        var calculationStation = CreateCalculationStation(_selectedStationItem!);
        var contributions = _planner.CalculateModuleContributions(
            calculationStation, GetCurrentWorkforce(_selectedStationItem));
        var contributionGroups = contributions
            .GroupBy(item => item.WareId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var pricingProfiles = _planner.CalculateAutomaticOfferProductionProfiles(calculationStation);
        var roles = contributionGroups.ToDictionary(
            pair => pair.Key, pair => pair.Value.Min(item => item.Role), StringComparer.OrdinalIgnoreCase);
        var supplySettings = SelectedStation.WareSettings
            .Where(item => item.StationSupplyBuyOffer != null)
            .ToArray();
        var wareIds = SelectedStation.WareSettings.Where(HasOrdinaryWareCard).Select(item => item.WareId)
            .Concat(roles.Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        var ruleOptions = new List<StationTradeRuleOption> { new(null, "无贸易限制") };
        ruleOptions.AddRange(SelectedStation.TradeRules.Select(rule => new StationTradeRuleOption(rule.Id, rule.Name)));

        var wareIdList = wareIds.ToArray();
        var allocations = _storageAllocationCalculator.Calculate(
            calculationStation, wareIdList, operationalOnly: false,
            _playerBlueprintWareIds, PreferredBuildMethod);
        wareIdList = wareIdList.Concat(allocations.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var ware in wareIdList.Select(id => _gameData.FindByWareId(id)).OfType<Ware>())
            roles.TryAdd(ware.Id, _planner.GetStationWareRole(calculationStation, ware));
        foreach (var setting in supplySettings
                     .OrderBy(item => _gameData.FindByWareId(item.WareId)?.Name ?? item.WareId,
                         StringComparer.Ordinal))
        {
            var ware = _gameData.FindByWareId(setting.WareId);
            if (ware == null) continue;
            var item = new StationWareItemViewModel(ware, setting,
                _planner.GetStationWareRole(calculationStation, ware),
                0, 0, ruleOptions, 0, 0,
                transportSettingsChanged: StartTransportCalculation,
                isStationSupply: true)
            { IsExpanded = expandedSupplies.Contains(ware.Id) };
            StorageWareItems.Add(item);
        }
        foreach (var ware in wareIdList.Select(id => _gameData.FindByWareId(id)).OfType<Ware>()
                     .OrderBy(ware => roles[ware.Id])
                     .ThenBy(ware => ware.Name, StringComparer.Ordinal))
        {
            var setting = SelectedStation.WareSettings.FirstOrDefault(item =>
                item.WareId.Equals(ware.Id, StringComparison.OrdinalIgnoreCase));
            if (setting == null)
            {
                setting = new StationWareSetting { WareId = ware.Id };
                SelectedStation.WareSettings.Add(setting);
            }
            var allocation = allocations.GetValueOrDefault(ware.Id)
                ?? new StationWareStorageAllocation(ware.Id, ware.Transport, 0, 0, 0);
            var wareContributions = contributionGroups.GetValueOrDefault(ware.Id) ?? [];
            var pricingProfile = pricingProfiles.GetValueOrDefault(ware.Id);
            var item = new StationWareItemViewModel(ware, setting,
                roles[ware.Id],
                allocation.QuantityLimit, allocation.AllocatedVolume, ruleOptions,
                wareContributions.Where(entry => entry.PerMinute > 0).Sum(entry => entry.PerMinute),
                -wareContributions.Where(entry => entry.PerMinute < 0).Sum(entry => entry.PerMinute),
                pricingProfile?.ConsumingModuleCount ?? 0,
                pricingProfile?.ProductionBatchAmount,
                enableImplicitWorkforceBuyOffer: !_selectedStationItem!.IsPlanned && allocation.IsWorkforceConsumable,
                workforceAutomaticBuyAmount: allocation.WorkforceAutomaticBuyAmount,
                transportSettingsChanged: StartTransportCalculation)
            { IsExpanded = expandedOrdinary.Contains(ware.Id) };
            StorageWareItems.Add(item);
        }
        RefreshAvailableTradeWareOptions();
    }

    private void RefreshAvailableTradeWareOptions()
    {
        var visibleWareIds = StorageWareItems.Where(item => !item.IsStationSupply).Select(item => item.WareId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        AvailableTradeWareOptions.Clear();
        foreach (var ware in _gameData.Wares.Values
                     .Where(ware => ware.Volume > 0 &&
                                    ware.Tags.Contains("economy", StringComparer.OrdinalIgnoreCase) &&
                                    !visibleWareIds.Contains(ware.Id))
                     .OrderBy(ware => ware.Name, StringComparer.Ordinal))
        {
            AvailableTradeWareOptions.Add(new StationTradeWareOption(ware.Id, ware.Name));
        }
        OnPropertyChanged(nameof(CanAddTradeWare));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void AddTradeWare()
    {
        if (SelectedStation == null || !CanAddTradeWare) return;
        var setting = SelectedStation.WareSettings.FirstOrDefault(item =>
            item.WareId.Equals(SelectedTradeWareId, StringComparison.OrdinalIgnoreCase));
        if (setting != null && HasOrdinaryWareCard(setting)) return;
        setting ??= new StationWareSetting { WareId = SelectedTradeWareId };
        setting.IsTradeWare = true;
        setting.BuyEnabled = false;
        setting.SellEnabled = false;
        setting.UseStationBuyTradeRule = true;
        setting.UseStationSellTradeRule = true;
        setting.StorageAllocationStatus = StationStorageAllocationStatus.Automatic;
        if (!SelectedStation.WareSettings.Contains(setting)) SelectedStation.WareSettings.Add(setting);
        RefreshStorageWareItems();
        StartTransportCalculation();
    }

    private static bool HasOrdinaryWareCard(StationWareSetting setting) =>
        setting.StationSupplyBuyOffer == null ||
        setting.IsTradeWare || setting.BuyEnabled != null || setting.SellEnabled ||
        setting.BuyOffer != null || setting.SellOffer != null || setting.CurrentAmount != 0 ||
        setting.StorageAllocationStatus != StationStorageAllocationStatus.Unavailable ||
        setting.BuyPriceOverride != null || setting.SellPriceOverride != null;

    private Station CreateCalculationStation(StationPlanningStationItemViewModel item)
        => CreateCalculationStation(item, item.GetBuiltModuleCount);

    private Station CreatePlannedCapacityStation(StationPlanningStationItemViewModel item)
        => CreateCalculationStation(item, item.GetPlannedModuleCount,
            item.GetPlannedModuleCount);

    private Station CreateCalculationStation(
        StationPlanningStationItemViewModel item,
        Func<ProductionModule, int> productionCount,
        Func<StationModule, int>? additionalCount = null)
    {
        var source = item.Station;
        additionalCount ??= item.GetBuiltModuleCount;
        return new Station
        {
            Id = source.Id,
            Name = source.Name,
            SectorId = source.SectorId,
            SectorPosition = source.SectorPosition,
            SolarEfficiencyPercent = source.SolarEfficiencyPercent,
            BuildMethod = source.BuildMethod,
            WorkforceByRace = new Dictionary<string, long>(source.WorkforceByRace, StringComparer.OrdinalIgnoreCase),
            WorkforceLastUpdateTimeSeconds = source.WorkforceLastUpdateTimeSeconds,
            WorkforceEfficiencyEndTimeSeconds = source.WorkforceEfficiencyEndTimeSeconds,
            WorkforceEfficiencyBonus = source.WorkforceEfficiencyBonus,
            WareSettings = source.WareSettings,
            Modules = source.Modules.Select(module => new ProductionModule
            {
                ModuleId = module.ModuleId,
                WareId = module.WareId,
                Method = module.Method,
                Count = productionCount(module),
                IsAutoAdded = module.IsAutoAdded,
                SelectedProductWareId = module.SelectedProductWareId,
                OperatingMode = module.OperatingMode,
                Recipe = module.Recipe,
                Ware = module.Ware
            }).Where(module => module.Count > 0).ToList(),
            AdditionalModules = source.AdditionalModules.Select(module => new StationModule
            {
                ModuleId = module.ModuleId,
                Kind = module.Kind,
                Count = additionalCount(module)
            }).Where(module => module.Count > 0).ToList()
        };
    }

    private Station CreateWorkforceConsumptionStation(Station source, long currentWorkforce)
    {
        var weights = source.WorkforceByRace
            .Where(pair => pair.Value > 0)
            .ToDictionary(pair => pair.Key, pair => (double)pair.Value, StringComparer.OrdinalIgnoreCase);
        if (weights.Count == 0)
        {
            foreach (var module in source.AdditionalModules)
            {
                if (!_gameData.StationModuleDefinitions.TryGetValue(module.ModuleId, out var definition) ||
                    definition.WorkforceCapacity <= 0 || module.Count <= 0) continue;
                var race = string.IsNullOrWhiteSpace(definition.Race) ? "default" : definition.Race;
                weights[race] = weights.GetValueOrDefault(race) +
                                (double)definition.WorkforceCapacity * module.Count;
            }
        }
        if (weights.Count == 0 && currentWorkforce > 0) weights["default"] = 1;

        var population = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var totalWeight = weights.Values.Sum();
        var remaining = Math.Max(0, currentWorkforce);
        var ordered = weights.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key,
            StringComparer.OrdinalIgnoreCase).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var amount = index == ordered.Length - 1
                ? remaining
                : Math.Min(remaining, (long)Math.Floor(currentWorkforce * ordered[index].Value / totalWeight));
            population[ordered[index].Key] = amount;
            remaining -= amount;
        }

        return new Station
        {
            WorkforceByRace = population,
            AdditionalModules = source.AdditionalModules
        };
    }

    private void StartTransportCalculation()
    {
        var generation = ++_transportGeneration;
        var transportStations = ImportedStations
            .Concat(PlannedStations.Where(item => item.HasMapPlacement))
            .ToArray();
        CancellationTokenSource? cancellation = null;
        lock (_transportCancellationGate)
        {
            _transportCancellation?.Cancel();
            _transportCancellation = null;
            if (_sectorGraph != null && transportStations.Length > 0)
            {
                cancellation = new CancellationTokenSource();
                _transportCancellation = cancellation;
            }
        }

        if (cancellation == null)
        {
            _transportNetwork = new StationTransportNetwork([]);
            IsTransportLoading = false;
            TransportCalculationTask = Task.CompletedTask;
            RefreshTransportView();
            NotifyTransportMapStateChanged();
            return;
        }

        var seeds = transportStations.Select(item =>
        {
            var station = CreateCalculationStation(item);
            var offers = station.WareSettings.ToDictionary(
                setting => setting.WareId,
                setting => new StationTransportOfferDirections(
                    setting.BuyEnabled == true || setting.BuyOffer != null ||
                    setting.StationSupplyBuyOffer != null,
                    setting.SellEnabled || setting.SellOffer != null),
                StringComparer.OrdinalIgnoreCase);
            return new TransportCalculationSeed(
                station,
                ParseTransportDuty(item.StationDuty),
                item.ManagerStars,
                item.FillWorkforceCapacity,
                item.SkipWorkforceGrowth,
                _starMap?.FindSector(station.SectorId)?.Population ?? 0,
                _savegameTimeSeconds,
                offers);
        }).ToArray();
        var graph = _sectorGraph!;
        var context = SynchronizationContext.Current;
        IsTransportLoading = true;
        RefreshTransportView();
        NotifyTransportMapStateChanged();
        TransportCalculationTask = CalculateAndApplyTransportNetworkAsync(
            seeds, graph, generation, cancellation, context);
    }

    private async Task CalculateAndApplyTransportNetworkAsync(
        IReadOnlyList<TransportCalculationSeed> seeds,
        SectorGraph graph,
        int generation,
        CancellationTokenSource cancellation,
        SynchronizationContext? context)
    {
        var workerAcquired = false;
        try
        {
            await _transportWorkerGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            workerAcquired = true;
            var network = await Task.Run(() =>
            {
                var snapshots = seeds.Select(seed =>
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var currentWorkforce = CalculateTransportCurrentWorkforce(seed);
                    var capacity = _planner.CalculateModuleContributions(seed.Station, currentWorkforce)
                        .GroupBy(item => item.WareId, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.Sum(item => item.PerMinute),
                            StringComparer.OrdinalIgnoreCase);
                    var currentRequired = _planner.CalculateWorkforceRequirements(seed.Station).AllBuiltRequired;
                    var workforceConsumption = _workforceConsumptionCalculator.Calculate(
                        CreateWorkforceConsumptionStation(seed.Station, currentWorkforce),
                        currentRequired, currentRequired).CurrentPerMinute;
                    foreach (var (wareId, perMinute) in workforceConsumption)
                        capacity[wareId] = capacity.GetValueOrDefault(wareId) - perMinute;
                    return new StationTransportStationSnapshot(
                        seed.Station.Id, seed.Station.Name, seed.Station.SectorId,
                        seed.Duty, seed.ManagerStars, capacity, seed.Offers);
                }).ToArray();
                return _transportNetworkCalculator.Calculate(snapshots, graph, cancellation.Token);
            }, cancellation.Token).ConfigureAwait(false);

            await RunOnCapturedContextAsync(context, () =>
            {
                if (generation != _transportGeneration || cancellation.IsCancellationRequested) return;
                _transportNetwork = network;
                var retainedSelections = new Dictionary<string, StationTransportLinkSelection>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var route in network.Routes)
                {
                    var key = GetTransportRouteKey(route);
                    retainedSelections[key] = _transportSelections.GetValueOrDefault(key)
                        ?? new StationTransportLinkSelection(key, OnTransportSelectionChanged);
                }
                _transportSelections = retainedSelections;
                IsTransportLoading = false;
                RefreshTransportView();
                NotifyTransportMapStateChanged();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await RunOnCapturedContextAsync(context, () =>
            {
                if (generation != _transportGeneration) return;
                _transportNetwork = null;
                IsTransportLoading = false;
                TransportStatusText = $"运输链路计算失败：{ex.Message}";
                HasTransportStatus = true;
                HasTransportContent = false;
                NotifyTransportMapStateChanged();
            }).ConfigureAwait(false);
        }
        finally
        {
            if (workerAcquired) _transportWorkerGate.Release();
            lock (_transportCancellationGate)
            {
                if (ReferenceEquals(_transportCancellation, cancellation))
                    _transportCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private static Task RunOnCapturedContextAsync(SynchronizationContext? context, Action action)
    {
        if (context == null)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Post(_ =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }, null);
        return completion.Task;
    }

    private void RefreshTransportView()
    {
        IncomingTransportWareGroups.Clear();
        IncomingTransportStationGroups.Clear();
        OutgoingTransportWareGroups.Clear();
        OutgoingTransportStationGroups.Clear();
        HasTransportContent = false;
        HasTransportStatus = true;
        NotifyTransportEfficiencyChanged();

        if (_selectedStationItem == null)
        {
            TransportStatusText = "请选择一个空间站查看运输链路。";
            RefreshStationTransportStatistics();
            return;
        }
        if (_selectedStationItem is { IsPlanned: true, HasMapPlacement: false })
        {
            TransportStatusText = "请使用上方按钮选择放置位置";
            RefreshStationTransportStatistics();
            return;
        }
        if (IsTransportLoading)
        {
            TransportStatusText = "正在后台计算空间站运输链路；完成前可继续使用其他功能。";
            RefreshStationTransportStatistics();
            return;
        }
        if (_transportNetwork == null)
        {
            TransportStatusText = "运输链路尚未就绪。";
            RefreshStationTransportStatistics();
            return;
        }

        var incoming = _transportNetwork.Routes.Where(route => route.TargetStationId.Equals(
            _selectedStationItem.Station.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        var outgoing = _transportNetwork.Routes.Where(route => route.SourceStationId.Equals(
            _selectedStationItem.Station.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (incoming.Length == 0 && outgoing.Length == 0)
        {
            TransportStatusText = "当前管理员范围和供需条件下没有可用运输链路。";
            RefreshStationTransportStatistics();
            return;
        }

        AddTransportGroups(IncomingTransportWareGroups, incoming, incoming: true, groupByWare: true);
        AddTransportGroups(IncomingTransportStationGroups, incoming, incoming: true, groupByWare: false);
        AddTransportGroups(OutgoingTransportWareGroups, outgoing, incoming: false, groupByWare: true);
        AddTransportGroups(OutgoingTransportStationGroups, outgoing, incoming: false, groupByWare: false);
        HasTransportStatus = false;
        HasTransportContent = true;
        RefreshStationTransportStatistics();
        NotifyTransportEfficiencyChanged();
    }

    private void AddTransportGroups(
        ObservableCollection<StationTransportGroupItemViewModel> target,
        IReadOnlyList<StationTransportRoute> routes,
        bool incoming,
        bool groupByWare)
    {
        IEnumerable<IGrouping<string, StationTransportRoute>> groups = groupByWare
            ? routes.GroupBy(route => route.WareId, StringComparer.OrdinalIgnoreCase)
            : routes.GroupBy(route => incoming ? route.SourceStationId : route.TargetStationId,
                StringComparer.OrdinalIgnoreCase);
        groups = groupByWare
            ? groups.OrderBy(group => ResolveTransportWareRole(group.Key)).ThenBy(
                group => ResolveTransportWareName(group.Key), StringComparer.Ordinal)
            : groups.OrderBy(group => incoming
                    ? group.First().SourceStationName
                    : group.First().TargetStationName,
                StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var selections = group.Select(route => _transportSelections[GetTransportRouteKey(route)]).ToArray();
            var entries = group.Select(route =>
                {
                    var result = CalculateTransportEfficiency(route,
                        UseSmallTransportShips ? TransportShipSize.Small : TransportShipSize.Large);
                    return new StationTransportEntryItemViewModel(
                        groupByWare
                            ? incoming ? route.SourceStationName : route.TargetStationName
                            : ResolveTransportWareName(route.WareId),
                        groupByWare ? null : ResolveTransportWareRole(route.WareId),
                        _transportSelections[GetTransportRouteKey(route)],
                        result?.EfficiencyM3PerSecond,
                        result == null ? GetMissingTransportConfigurationText() : null);
                })
                .OrderBy(entry => entry.Name, StringComparer.Ordinal)
                .ToArray();
            target.Add(new StationTransportGroupItemViewModel(
                groupByWare
                    ? ResolveTransportWareName(group.Key)
                    : incoming ? group.First().SourceStationName : group.First().TargetStationName,
                groupByWare ? ResolveTransportWareRole(group.Key) : null,
                entries,
                selections));
        }
    }

    private void TransportShipConfigurationsOnConfigurationChanged(
        object? sender,
        TransportShipConfigurationChangedEventArgs e) => RefreshTransportView();

    public void Dispose()
    {
        _transportShipConfigurations.ConfigurationChanged -= TransportShipConfigurationsOnConfigurationChanged;
        lock (_transportCancellationGate)
        {
            _transportCancellation?.Cancel();
        }
    }

    private void OnTransportSelectionChanged()
    {
        NotifyTransportMapStateChanged();
        RefreshStationTransportStatistics();
        NotifyTransportEfficiencyChanged();
    }

    private void NotifyTransportEfficiencyChanged()
    {
        OnPropertyChanged(nameof(TransportEfficiencyM3PerSecond));
        OnPropertyChanged(nameof(TransportEfficiencyText));
        OnPropertyChanged(nameof(IncomingTransportEfficiencyM3PerSecond));
        OnPropertyChanged(nameof(IncomingTransportEfficiencyText));
        OnPropertyChanged(nameof(OutgoingTransportEfficiencyM3PerSecond));
        OnPropertyChanged(nameof(OutgoingTransportEfficiencyText));
    }

    private void RefreshStationTransportStatistics()
    {
        StationTransportStatistics.Clear();
        var selectedItem = _selectedStationItem;
        if (selectedItem == null)
        {
            IsStationTransportShipSizeFilterVisible = false;
            OnPropertyChanged(nameof(IsStationTransportShipSizeFilterVisible));
            SetStationTransportInfoState(false, string.Empty);
            return;
        }

        var builtStation = CreateCalculationStation(selectedItem);
        var capability = _transportCapabilityCalculator.Calculate(builtStation);
        IsStationTransportShipSizeFilterVisible = capability.HasPort;
        OnPropertyChanged(nameof(IsStationTransportShipSizeFilterVisible));
        if (!capability.HasPort)
        {
            SetStationTransportInfoState(true, "空间站缺少泊位/港口");
            return;
        }

        var supportsSelectedSize = SelectedStationInfoShipSize == TransportShipSize.Large
            ? capability.SupportsLargeShips
            : capability.SupportsSmallShips;
        if (!supportsSelectedSize)
        {
            SetStationTransportInfoState(true, "此类船只无法停靠到该空间站");
            return;
        }
        if (!capability.HasStorage)
        {
            SetStationTransportInfoState(true, "无仓储模块");
            return;
        }

        // 链路后台重算期间不把尚未提交的新网络或上一代网络解释成最终的“无可用链路”。
        // 港口、停靠尺寸和仓储能力来自当前模块，可以先行显示；链路统计等待本代网络提交后再刷新。
        if (IsTransportLoading || _transportNetwork == null)
        {
            SetStationTransportInfoState(false, string.Empty);
            return;
        }

        var routes = GetSelectedIncidentTransportRoutes()
            .Where(route => ResolveTransportStorageType(route.WareId) is { } storageType &&
                            capability.SupportsStorage(TransportShipConfigurationStore.StorageTag(storageType)))
            .ToArray();
        if (routes.Length == 0)
        {
            SetStationTransportInfoState(true, "当前管理员范围和供需条件下没有可用运输链路");
            return;
        }

        SetStationTransportInfoState(false, string.Empty);
        foreach (var storageType in OrderedTransportStorageTypes)
        {
            var storageTag = TransportShipConfigurationStore.StorageTag(storageType);
            if (!capability.SupportsStorage(storageTag)) continue;
            var storageRoutes = routes.Where(route => ResolveTransportStorageType(route.WareId) == storageType)
                .ToArray();
            if (storageRoutes.Length == 0) continue;

            var results = storageRoutes
                .Select(route => CalculateTransportEfficiency(route, SelectedStationInfoShipSize))
                .ToArray();
            if (results.Any(result => result == null))
            {
                StationTransportStatistics.Add(new StationTransportStorageStatisticItemViewModel(
                    storageType, GetTransportStorageName(storageType), "无舰船配置", "无舰船配置", false));
                continue;
            }

            var configuredResults = results.Select(result => result!).ToArray();
            var averageSeconds = configuredResults.Average(result => result.TotalSeconds);
            var averageEfficiencyM3PerHour = configuredResults.Average(
                result => result.EfficiencyM3PerSecond) * 3600d;
            StationTransportStatistics.Add(new StationTransportStorageStatisticItemViewModel(
                storageType,
                GetTransportStorageName(storageType),
                FormatMinutesSeconds((long)Math.Round(averageSeconds, MidpointRounding.AwayFromZero)),
                $"{averageEfficiencyM3PerHour:N0} m³/h",
                true));
        }
    }

    private void SetStationTransportInfoState(bool hasError, string errorText)
    {
        HasStationTransportInfoError = hasError;
        StationTransportInfoErrorText = errorText;
    }

    private IEnumerable<StationTransportRoute> GetSelectedIncidentTransportRoutes()
    {
        if (_selectedStationItem == null || _transportNetwork == null) return [];
        var stationId = _selectedStationItem.Station.Id;
        return _transportNetwork.Routes.Where(route =>
            (route.SourceStationId.Equals(stationId, StringComparison.OrdinalIgnoreCase) ||
             route.TargetStationId.Equals(stationId, StringComparison.OrdinalIgnoreCase)) &&
            _transportSelections.TryGetValue(GetTransportRouteKey(route), out var selection) &&
            selection.IsSelected);
    }

    private StationTransportEfficiencyResult? CalculateTransportEfficiency(
        StationTransportRoute route,
        TransportShipSize size)
    {
        if (_transportEfficiencyCalculator == null || route.Path == null) return null;
        var storageType = ResolveTransportStorageType(route.WareId);
        if (storageType == null ||
            !_transportShipConfigurations.TryGet(storageType.Value, size, out var stored) ||
            !_gameData.Ships.TryGetValue(stored.ShipId, out var ship) ||
            !_gameData.Engines.TryGetValue(stored.EngineId, out var engine) ||
            !_gameData.Thrusters.TryGetValue(stored.ThrusterId, out var thruster))
            return null;

        var source = AllStations().FirstOrDefault(item => item.Station.Id.Equals(
            route.SourceStationId, StringComparison.OrdinalIgnoreCase))?.Station;
        var target = AllStations().FirstOrDefault(item => item.Station.Id.Equals(
            route.TargetStationId, StringComparison.OrdinalIgnoreCase))?.Station;
        if (source == null || target == null) return null;

        return _transportEfficiencyCalculator.Calculate(
            route.Path,
            source.SectorPosition,
            target.SectorPosition,
            new StationTransportShipConfiguration(
                ship, engine, thruster, Math.Clamp(stored.PilotingStars, 0, 5)));
    }

    private TransportStorageType? ResolveTransportStorageType(string wareId)
    {
        var transport = _gameData.FindByWareId(wareId)?.Transport;
        return TransportShipConfigurationStore.TryParseStorageTag(transport, out var storageType)
            ? storageType
            : null;
    }

    private string GetMissingTransportConfigurationText() =>
        $"没有该仓储的{(UseSmallTransportShips ? "小型" : "大型")}舰船配置";

    private static readonly TransportStorageType[] OrderedTransportStorageTypes =
    [
        TransportStorageType.Container,
        TransportStorageType.Solid,
        TransportStorageType.Liquid,
        TransportStorageType.Condensate
    ];

    private static string GetTransportStorageName(TransportStorageType storageType) => storageType switch
    {
        TransportStorageType.Container => "集装",
        TransportStorageType.Solid => "固体",
        TransportStorageType.Liquid => "液体",
        TransportStorageType.Condensate => "冷凝",
        _ => throw new ArgumentOutOfRangeException(nameof(storageType))
    };

    private string ResolveTransportWareName(string wareId) => _gameData.FindByWareId(wareId)?.Name ?? wareId;

    private void NotifyTransportMapStateChanged()
    {
        TransportMapStateChanged?.Invoke(this, EventArgs.Empty);
        TransportOptimizationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private ProductionCatalogRole ResolveTransportWareRole(string wareId)
    {
        var ware = _gameData.FindByWareId(wareId);
        return ware == null || SelectedStation == null
            ? ProductionCatalogRole.Resource
            : _planner.GetStationWareRole(SelectedStation, ware);
    }

    private static StationTransportStationDuty ParseTransportDuty(string duty) => duty switch
    {
        "贸易" => StationTransportStationDuty.Trade,
        "终端" => StationTransportStationDuty.Terminal,
        _ => StationTransportStationDuty.Factory
    };

    private static string GetTransportRouteKey(StationTransportRoute route) =>
        $"{route.SourceStationId}\u001f{route.TargetStationId}\u001f{route.WareId}";

    private long CalculateTransportCurrentWorkforce(TransportCalculationSeed seed)
    {
        var growth = _workforceGrowthCalculator.Calculate(
            seed.Station, seed.Population, seed.Station.CurrentWorkforce,
            seed.FillWorkforceCapacity, seed.SavegameTimeSeconds,
            seed.Station.WorkforceLastUpdateTimeSeconds);
        return seed.SkipWorkforceGrowth
            ? _workforceGrowthCalculator.Calculate(
                seed.Station, seed.Population, growth.Target,
                seed.FillWorkforceCapacity, seed.SavegameTimeSeconds,
                seed.Station.WorkforceLastUpdateTimeSeconds).Current
            : growth.Current;
    }

    private sealed record TransportCalculationSeed(
        Station Station,
        StationTransportStationDuty Duty,
        int ManagerStars,
        bool FillWorkforceCapacity,
        bool SkipWorkforceGrowth,
        long Population,
        double? SavegameTimeSeconds,
        IReadOnlyDictionary<string, StationTransportOfferDirections> Offers);

    private IReadOnlyList<CapacityContributionSlice> CalculateCapacityContributions(
        StationPlanningStationItemViewModel item)
    {
        var builtStation = CreateCalculationStation(item);
        var plannedStation = CreatePlannedCapacityStation(item);
        var currentWorkforce = GetCurrentWorkforce(item);
        var builtWorkforce = _planner.CalculateWorkforce(builtStation, currentWorkforce);
        var coverage = builtStation.Modules.Count == 0
            ? 0d
            : Math.Clamp(builtStation.WorkforceEfficiencyBonus ?? builtWorkforce.Coverage, 0d, 1d);
        builtStation.WorkforceEfficiencyBonus = coverage;
        plannedStation.WorkforceEfficiencyBonus = coverage;

        var result = _planner.CalculateModuleContributions(builtStation, currentWorkforce)
            .Select(contribution => new CapacityContributionSlice(contribution, false))
            .Concat(_planner.CalculateModuleContributions(plannedStation, currentWorkforce)
                .Select(contribution => new CapacityContributionSlice(contribution, true)))
            .ToList();

        var currentRequired = _planner.CalculateWorkforceRequirements(builtStation).AllBuiltRequired;
        var allBuiltRequired = _planner.CalculateWorkforceRequirements(item.Station).AllBuiltRequired;
        var consumption = _workforceConsumptionCalculator.Calculate(
            CreateWorkforceConsumptionStation(item.Station, currentWorkforce),
            currentRequired, allBuiltRequired);
        foreach (var wareId in consumption.CurrentPerMinute.Keys
                     .Concat(consumption.AllBuiltPerMinute.Keys)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var ware = _gameData.FindByWareId(wareId);
            if (ware == null) continue;
            var role = _planner.GetStationWareRole(item.Station, ware);
            var current = consumption.CurrentPerMinute.GetValueOrDefault(wareId);
            var allBuilt = consumption.AllBuiltPerMinute.GetValueOrDefault(wareId);
            if (current > 0.0000001)
            {
                result.Add(new CapacityContributionSlice(new StationModuleContribution(
                    "workforce:current", "当前劳动力消耗", ware.Id, ware.Name, -current, role), false));
            }
            var adjustment = allBuilt - current;
            if (Math.Abs(adjustment) > 0.0000001)
            {
                result.Add(new CapacityContributionSlice(new StationModuleContribution(
                    "workforce:all-built-adjustment", "全部建成劳动力消耗调整",
                    ware.Id, ware.Name, -adjustment, role), true));
            }
        }
        return result;
    }

    private static bool IsWorkforceContribution(StationModuleContribution contribution) =>
        contribution.ModuleId.StartsWith("workforce:", StringComparison.OrdinalIgnoreCase);

    private static int GetUnbuiltModuleCount(
        StationPlanningStationItemViewModel item,
        ProductionModule module) => item.GetPlannedModuleCount(module);

    private static int GetUnbuiltModuleCount(
        StationPlanningStationItemViewModel item,
        StationModule module) => item.GetPlannedModuleCount(module);

    private long GetCurrentWorkforce(StationPlanningStationItemViewModel? stationItem)
    {
        return CalculateWorkforceGrowth(stationItem).Current;
    }

    private StationWorkforceGrowthSummary CalculateWorkforceGrowth(
        StationPlanningStationItemViewModel? stationItem,
        Station? calculationStation = null)
    {
        if (stationItem == null)
            return new StationWorkforceGrowthSummary(0, 0, 0, 0,
                new WorkforceGrowthParameters().CycleSeconds, 0, 0, 0,
                0, StationWorkforceGrowthConstraint.Overcrowding, 0, 0, 0);
        var population = _starMap?.FindSector(stationItem.Station.SectorId)?.Population ?? 0;
        var initialWorkforce = stationItem.IsPlanned ? 0 : stationItem.Station.CurrentWorkforce;
        calculationStation ??= CreateCalculationStation(stationItem);
        var growth = _workforceGrowthCalculator.Calculate(calculationStation, population,
            initialWorkforce, stationItem.FillWorkforceCapacity,
            stationItem.IsPlanned ? null : _savegameTimeSeconds,
            stationItem.IsPlanned ? null : stationItem.Station.WorkforceLastUpdateTimeSeconds);
        return stationItem.SkipWorkforceGrowth
            ? _workforceGrowthCalculator.Calculate(calculationStation, population,
                growth.Target, stationItem.FillWorkforceCapacity,
                stationItem.IsPlanned ? null : _savegameTimeSeconds,
                stationItem.IsPlanned ? null : stationItem.Station.WorkforceLastUpdateTimeSeconds)
            : growth;
    }

    private static string FormatBonus(double bonus) => $"+{bonus * 100:0.##}%";

    private static string FormatPenalty(double penalty) => penalty <= 0
        ? "0%"
        : $"-{Math.Round(penalty * 100, MidpointRounding.AwayFromZero):0}%";

    private static string FormatDuration(long totalSeconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return $"{(long)duration.TotalDays}d{duration.Hours}h{duration.Minutes}m{duration.Seconds}s";
    }

    private static string FormatMinutesSeconds(long totalSeconds)
    {
        totalSeconds = Math.Max(0, totalSeconds);
        return $"{totalSeconds / 60}m{totalSeconds % 60}s";
    }

    private static string GetWorkforceRaceCode(string race) => race.ToLowerInvariant() switch
    {
        "default" or "argon" => "ARG", "paranid" => "PAR", "teladi" => "TEL", "split" => "SPL",
        "terran" => "TER", "boron" => "BOR", _ => EquipmentDisplay.RaceCode(race)
    };

    private string DetermineDefaultStationDuty(Station station)
    {
        var additionalKinds = station.AdditionalModules.Select(module =>
                _gameData.StationModuleDefinitions.GetValueOrDefault(module.ModuleId)?.Kind ?? string.Empty)
            .ToArray();
        if (additionalKinds.Any(kind => kind.Equals("buildmodule", StringComparison.OrdinalIgnoreCase))) return "终端";
        var hasProduction = station.Modules.Count > 0;
        var hasStorage = additionalKinds.Any(kind => kind.Equals("storage", StringComparison.OrdinalIgnoreCase));
        var hasDocking = additionalKinds.Any(kind => kind.Equals("dockarea", StringComparison.OrdinalIgnoreCase)
                                                     || kind.Equals("pier", StringComparison.OrdinalIgnoreCase));
        return !hasProduction && hasStorage && hasDocking ? "贸易" : "工厂";
    }

    private SectorInfo? FindSectorByName(string text) => _starMap?.Sectors.Values.FirstOrDefault(
        item => item.Name.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase)
                || item.SearchDisplayName.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase)
                || item.SearchDisplayName.Split(['｜', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(part => part.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase)));
    private string ResolveSectorName(string sectorId) => _starMap?.FindSector(sectorId)?.SearchDisplayName ?? string.Empty;
    private void ApplySectorSolarEfficiency(Station station)
    {
        var sector = _starMap?.FindSector(station.SectorId);
        if (sector != null) station.SolarEfficiencyPercent = (int)Math.Round(sector.SunlightFactor * 100, MidpointRounding.AwayFromZero);
    }
    private void ResetDeleteConfirmations()
    {
        foreach (var item in AllStations()) item.IsDeletePending = false;
    }

    private void RebuildSearch()
    {
        SearchResults.Clear();
        var query = SearchText.Trim();
        foreach (var definition in _gameData.StationModuleDefinitions.Values
                     .Where(item => SelectedKindFilter == AllFilterValue || GetKindFilterKey(item.Kind) == SelectedKindFilter)
                     .Where(item => SelectedRaceFilter == AllFilterValue || item.Race.Equals(SelectedRaceFilter, StringComparison.OrdinalIgnoreCase))
                     .Where(item => string.IsNullOrEmpty(query) || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(item => item.Name, StringComparer.Ordinal)) SearchResults.Add(definition);
    }

    private void RebuildFilterOptions()
    {
        KindFilterOptions.Clear(); KindFilterOptions.Add(new(AllFilterValue, "全部"));
        var availableKinds = _gameData.StationModuleDefinitions.Values.Select(item => GetKindFilterKey(item.Kind)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var option in KindFilters.Where(option => availableKinds.Contains(option.Value))) KindFilterOptions.Add(option);
        RaceFilterOptions.Clear(); RaceFilterOptions.Add(new(AllFilterValue, "全部"));
        foreach (var race in _gameData.StationModuleDefinitions.Values.Select(item => item.Race).Where(race => !string.IsNullOrWhiteSpace(race))
                     .Distinct(StringComparer.OrdinalIgnoreCase).Select(race => new StationModuleFilterOption(race,
                         race.Equals("default", StringComparison.OrdinalIgnoreCase) ? "通用" : GetRaceDisplayName(race)))
                     .OrderBy(option => option.DisplayName, StringComparer.Ordinal)) RaceFilterOptions.Add(race);
    }

    private static string GetKindFilterKey(string kind) => kind.ToLowerInvariant() switch
    {
        "production" or "processingmodule" => "production", "storage" => "storage", "habitation" => "habitation",
        "dockarea" or "pier" => "docking", "buildmodule" => "construction", "defencemodule" => "defence",
        "connectionmodule" => "connection", _ => "other"
    };
    private static int GetKindSortOrder(string kind)
    {
        var key = GetKindFilterKey(kind);
        for (var index = 0; index < KindFilters.Count; index++) if (KindFilters[index].Value.Equals(key, StringComparison.OrdinalIgnoreCase)) return index;
        return KindFilters.Count;
    }
    private static bool IsScrapProcessor(string moduleId) =>
        moduleId.Equals("proc_gen_scrapworks_macro", StringComparison.OrdinalIgnoreCase)
        || moduleId.Equals("proc_gen_scrapworkskhaak_macro", StringComparison.OrdinalIgnoreCase);
    private static string GetRaceDisplayName(string race) => race.ToLowerInvariant() switch
    {
        "argon" => "Argon", "paranid" => "Paranid", "teladi" => "Teladi", "split" => "Split",
        "terran" => "Terran", "boron" => "Boron", _ => race
    };
}

internal sealed record CapacityContributionSlice(
    StationModuleContribution Contribution,
    bool IsPlanned);

public enum StationProductionRateUnit { PerMinute, PerHour }

public sealed record StationModuleFilterOption(string Value, string DisplayName) { public override string ToString() => DisplayName; }
public sealed record StationPlanningSelectionOption<T>(T Value, string DisplayName);
public sealed record StationPlanningProductFilterOption(string Value, string DisplayName);
public sealed record StationPlanningOperatingModeOption(string Value, string DisplayName);
public sealed record StationWorkforceCapacityItemViewModel(
    string RaceCode,
    string CapacityText,
    bool IsTotal);

public sealed class StationCapacityProductItemViewModel : ViewModelBase
{
    private bool _isExpanded;
    private StationProductionRateUnit _rateUnit;
    private readonly bool _forceTotalDisplay;
    public StationCapacityProductItemViewModel(string wareId, string name, double perMinute,
        double totalPerMinute, ProductionCatalogRole role,
        IReadOnlyList<StationCapacitySourceItemViewModel> sources,
        StationProductionRateUnit rateUnit = StationProductionRateUnit.PerMinute,
        bool forceTotalDisplay = false)
    { WareId = wareId; Name = name; PerMinute = perMinute; TotalPerMinute = totalPerMinute; Role = role; Sources = sources; RateUnit = rateUnit; _forceTotalDisplay = forceTotalDisplay; }
    public string WareId { get; }
    public string Name { get; }
    public double PerMinute { get; }
    public double TotalPerMinute { get; }
    public double PlannedPerMinute => TotalPerMinute - PerMinute;
    public ProductionCatalogRole Role { get; }
    public IReadOnlyList<StationCapacitySourceItemViewModel> Sources { get; }
    public bool IsExpanded { get => _isExpanded; set { if (SetProperty(ref _isExpanded, value)) OnPropertyChanged(nameof(ExpandGlyph)); } }
    public string ExpandGlyph => IsExpanded ? "▲" : "▼";
    public bool HasPlannedCapacity => _forceTotalDisplay || Sources.Any(source => source.IsPlanned);
    public StationProductionRateUnit RateUnit { get => _rateUnit; set { if (!SetProperty(ref _rateUnit, value)) return; OnPropertyChanged(nameof(DisplayAmount)); OnPropertyChanged(nameof(DisplayBuiltAmount)); OnPropertyChanged(nameof(DisplayTotalAmount)); OnPropertyChanged(nameof(DisplayUnit)); foreach (var source in Sources) source.RateUnit = value; } }
    public string DisplayBuiltAmount => FormatValue(PerMinute, RateUnit,
        HasPlannedCapacity && Normalize(PerMinute) == 0 ? Normalize(TotalPerMinute) : null);
    public string DisplayTotalAmount => FormatValue(TotalPerMinute, RateUnit);
    public string DisplayUnit => $" /{(RateUnit == StationProductionRateUnit.PerHour ? "小时" : "分钟")}";
    public string DisplayAmount => HasPlannedCapacity
        ? $"{DisplayBuiltAmount}（{DisplayTotalAmount}）{DisplayUnit}"
        : $"{DisplayBuiltAmount}{DisplayUnit}";
    public bool IsPositive => HasPlannedCapacity && Normalize(PerMinute) == 0
        ? Normalize(TotalPerMinute) >= 0
        : Normalize(PerMinute) >= 0;
    public bool IsTotalPositive => Normalize(TotalPerMinute) >= 0;

    private static string FormatValue(double perMinute, StationProductionRateUnit unit,
        double? zeroSignSource = null)
    {
        var value = Normalize(perMinute) * (unit == StationProductionRateUnit.PerHour ? 60 : 1);
        if (value == 0 && zeroSignSource < 0) return $"-{value:N1}";
        return $"{(value >= 0 ? "+" : "")}{value:N1}";
    }

    private static double Normalize(double value) => Math.Abs(value) < 0.0000001 ? 0d : value;
}

public sealed class StationCapacitySourceItemViewModel : ViewModelBase
{
    private StationProductionRateUnit _rateUnit;
    public StationCapacitySourceItemViewModel(string name, double perMinute, ProductionCatalogRole? role,
        bool isPlanned = false)
    { Name = name; PerMinute = perMinute; Role = role; IsPlanned = isPlanned; }
    public string Name { get; }
    public double PerMinute { get; }
    public ProductionCatalogRole? Role { get; }
    public bool IsPlanned { get; }
    public StationProductionRateUnit RateUnit { get => _rateUnit; set { if (SetProperty(ref _rateUnit, value)) OnPropertyChanged(nameof(DisplayAmount)); } }
    public string DisplayAmount => $"{(PerMinute >= 0 ? "+" : "")}{PerMinute * (RateUnit == StationProductionRateUnit.PerHour ? 60 : 1):N1} /{(RateUnit == StationProductionRateUnit.PerHour ? "小时" : "分钟")}";
    public bool IsPositive => PerMinute >= 0;
}

public sealed class StationPlanningModuleItem : ViewModelBase
{
    public StationPlanningModuleItem(string name, double amount, ProductionModule? production, StationModule? additional,
        ProductionCatalogRole? role = null, string kind = "", IReadOnlyList<StationPlanningProductFilterOption>? productFilters = null,
        IReadOnlyList<StationPlanningOperatingModeOption>? operatingModeFilters = null, Action? changed = null,
        bool isAutoAdded = false, double workforceBonus = 0, int unbuiltAmount = 0,
        Action<StationPlanningModuleItem, int>? setCount = null)
    { Name = name; Amount = amount; Production = production; Additional = additional; Role = role; Kind = kind; ProductFilters = productFilters ?? []; OperatingModeFilters = operatingModeFilters ?? []; Changed = changed; IsAutoAdded = isAutoAdded; _workforceBonus = workforceBonus; UnbuiltAmount = unbuiltAmount; _setCount = setCount; }
    private double _workforceBonus;
    private readonly Action<StationPlanningModuleItem, int>? _setCount;
    private Action? Changed { get; }
    public string Name { get; }
    public double Amount { get; }
    public ProductionModule? Production { get; }
    public StationModule? Additional { get; }
    public ProductionCatalogRole? Role { get; }
    public string Kind { get; }
    public bool IsAutoAdded { get; }
    public int UnbuiltAmount { get; }
    public bool HasUnbuiltAmount => UnbuiltAmount > 0;
    public double WorkforceBonus => _workforceBonus;
    public bool HasWorkforceBonus => _workforceBonus > 0.0000001;
    public string WorkforceBonusText => HasWorkforceBonus ? $"+{_workforceBonus * 100:0.#}%" : string.Empty;
    public IReadOnlyList<StationPlanningProductFilterOption> ProductFilters { get; }
    public bool HasProductFilter => ProductFilters.Count > 0;
    public IReadOnlyList<StationPlanningOperatingModeOption> OperatingModeFilters { get; }
    public bool HasOperatingModeFilter => OperatingModeFilters.Count > 0;
    public string SelectedProductFilter
    {
        get => string.IsNullOrWhiteSpace(Production?.SelectedProductWareId) ? "*" : Production.SelectedProductWareId;
        set
        {
            if (Production == null) return;
            var selected = value == "*" ? null : value;
            if (string.Equals(Production.SelectedProductWareId, selected, StringComparison.OrdinalIgnoreCase)) return;
            Production.SelectedProductWareId = selected;
            OnPropertyChanged();
            Changed?.Invoke();
        }
    }
    public string SelectedOperatingMode
    {
        get => Production?.OperatingMode ?? "full";
        set
        {
            if (Production == null || string.Equals(Production.OperatingMode, value, StringComparison.OrdinalIgnoreCase)) return;
            Production.OperatingMode = value;
            OnPropertyChanged();
            Changed?.Invoke();
        }
    }
    public bool HasRole => Role != null;
    public bool HasBalance => !CanRemove && Role != null;
    public bool IsPositive => Amount >= 0;
    public bool CanRemove => Production != null || Additional != null;
    public string ModuleCountText
    {
        get => CanRemove ? ((int)Amount).ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        set
        {
            if (!CanRemove || !int.TryParse(value, out var count))
            {
                OnPropertyChanged();
                return;
            }
            _setCount?.Invoke(this, count);
        }
    }
    public string UnbuiltAmountText => HasUnbuiltAmount ? $"（{UnbuiltAmount:N0}）" : string.Empty;

    public void SetModuleCount(int count) => _setCount?.Invoke(this, count);
    public string DisplayAmount => CanRemove
        ? $"× {Amount:N0}{(HasUnbuiltAmount ? $"（{UnbuiltAmount:N0}）" : string.Empty)}"
        : $"{(Amount >= 0 ? "+" : "")}{Amount:N1} /分钟";

    public void SetWorkforceBonus(double value)
    {
        if (Math.Abs(_workforceBonus - value) < 0.0000001) return;
        _workforceBonus = value;
        OnPropertyChanged(nameof(WorkforceBonus));
        OnPropertyChanged(nameof(HasWorkforceBonus));
        OnPropertyChanged(nameof(WorkforceBonusText));
    }
}
