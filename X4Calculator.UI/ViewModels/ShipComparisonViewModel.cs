using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using X4Calculator.Core.Calculation;
using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.UI.ViewModels;

/// <summary>
/// 货船对比页面的 ViewModel，管理舰船筛选、选择以及推进器/引擎装配与飞行属性计算。
/// </summary>
public class ShipComparisonViewModel : ViewModelBase
{
    private readonly GameDataDB _gameData;
    private readonly TransportShipConfigurationStore _transportShipConfigurations;
    private readonly Action<string>? _showError;
    private List<ShipInfo> _allShips = new();

    private string? _selectedRace;
    private string? _selectedSize;
    private ShipInfo? _selectedShip;
    private string? _shipAttributeSize; // 舰船属性区尺寸筛选框当前值（选船自动同步，点击行为待定）
    private string? _shipCargoType;     // 舰船属性区仓储筛选框当前值（未选船时可下拉）
    private string _shipCargoTypeText = "—"; // 选中舰船后"仓储"筛选框显示的文本
    private bool _isApplyingFilters;    // 筛选重建期间忽略 ListBox 因集合变化推送的 null（保留当前选择）

    // 运输效率排序栏（"计算所有舰船"）
    private bool _isSortExpanded;             // 排序栏是否展开
    private List<RoutePlanSegment>? _routePlan; // 当前航线飞行计划（排序栏实时计算用）
    private string _routeErrorText = "";
    private string _distanceErrorText = "";
    private string _selectionErrorText = "";
    private bool _hasAnyStations;
    private SortCalculationMode _sortCalculationMode;
    private IStationTransportOptimizationSource? _stationTransportOptimizationSource;

    // 推进器选择状态
    private ThrusterInfo? _selectedThruster;
    private bool _isThrusterExpanded;
    private bool _isThrusterSelected;

    // 引擎选择状态
    private bool _isEngineOptionsExpanded;
    private EngineInfo? _selectedEngine;
    private bool _isEngineSelected;

    // 引擎筛选状态（选择前按种族/风格过滤，缩小选项列表）
    private List<EngineInfo> _allShipEngines = new();
    private string? _selectedEngineRace = "全部";
    private string? _selectedEngineStyle = "全部";
    private int _selectedPilotingStars = 5;

    // 计算结果
    private double _forwardSpeed;
    private double _acceleration;
    private double _pitchRate;
    private double _yawRate;
    private double _rollRate;
    private double _travelSpeed;
    private double _travelCharge;
    private double _travelAttack;
    private FlightStats? _flightStats;   // 当前装配的飞行属性（运输效率耗时计算用）
    private bool _hasSelection;
    private bool _hasSelectedShip;
    private string _cargoLabel = string.Empty;
    private string _cargoValue = string.Empty;

    // 运输效率：航线输入
    private IReadOnlyList<string> _sectorNames = Array.Empty<string>();
    private string _startSectorText = string.Empty;
    private string _endSectorText = string.Empty;
    private int? _startGateDistanceKm;
    private int? _endGateDistanceKm;
    private IReadOnlyList<string> _startNextHopOptions = Array.Empty<string>();
    private IReadOnlyList<string> _endPreviousHopOptions = Array.Empty<string>();
    private string? _selectedStartNextHop;
    private string? _selectedEndPreviousHop;

    // 运输效率：计算（导航图 + 距离）
    private StarMapDB? _starMap;
    private SectorGraph? _sectorGraph;
    private IReadOnlyList<TransportDistanceRow> _distanceRows = Array.Empty<TransportDistanceRow>();
    private string _cruiseEngineCountText = "—";
    private string _jumpCountText = "—";
    private string _averageDistanceText = "—";
    private string _dockTimeText = "—";
    private string _undockTimeText = "—";
    private string _rotationTimeText = "—";
    private string _totalTimeText = "—";
    private string _efficiencyText = "—";
    private bool _includeUndockInTotal = true; // 离港耗时是否计入总耗时（经验公式，用户可勾选）
    private bool _includeDockInTotal = true;   // 进港耗时是否计入总耗时（经验公式，用户可勾选）
    private bool _isStartGateDistanceVisible = true;     // 本地运输（起=终）时隐藏"起始点→星门"输入框
    private string _endGateDistanceLabel = "星门→结束点"; // 本地运输时改为"起始点→结束点"

    public ShipComparisonViewModel(
        GameDataDB gameData,
        TransportShipConfigurationStore? transportShipConfigurations = null,
        Action<string>? showError = null)
    {
        _gameData = gameData;
        _transportShipConfigurations = transportShipConfigurations ?? new TransportShipConfigurationStore();
        _showError = showError;

        RaceOptions = new ObservableCollection<string>();
        SizeOptions = new ObservableCollection<string>();
        ShipAttributeSizeOptions = new ObservableCollection<string>();
        ShipCargoTypeOptions = new ObservableCollection<string> { "集装", "固体", "液体" };
        FilteredShips = new ObservableCollection<ShipInfo>();
        SortResults = new ObservableCollection<ShipEfficiencyRow>();

        CalculateAllCommand = new RelayCommand(() => CalculateAll());
        CalculateAllStationTransportOptimalCommand = new RelayCommand(
            CalculateAllStationTransportOptimal,
            () => HasAnyStations);
        ApplyConfigurationToAllStationsCommand = new RelayCommand(
            ApplyConfigurationToAllStations,
            () => HasAnyStations);
        ResetShipCommand = new RelayCommand(() => ResetShipSelection());

        ThrusterOptions = new ObservableCollection<ThrusterInfo>();
        EngineOptions = new ObservableCollection<EngineInfo>();
        PilotingStarOptions = new ObservableCollection<int> { 5, 4, 3, 2, 1, 0 };

        ExpandThrusterCommand = new RelayCommand(() =>
        {
            // 重置为初始选择状态：清空选择，仅展开推进器选项
            _selectedThruster = null;
            _isThrusterSelected = false;
            _isThrusterExpanded = true;
            OnPropertyChanged(nameof(SelectedThruster));
            OnPropertyChanged(nameof(IsThrusterSelected));
            OnPropertyChanged(nameof(IsThrusterExpanded));
            Recalculate(); // 清空部件后同步刷新运输效率及已展开的排序栏
        });
        ExpandEngineCommand = new RelayCommand(() =>
        {
            // 重置为初始选择状态：清空选择，重新加载并展开引擎选项（与初次选择逻辑一致）
            _selectedEngine = null;
            _isEngineSelected = false;
            _isEngineOptionsExpanded = true;
            OnPropertyChanged(nameof(SelectedEngine));
            OnPropertyChanged(nameof(IsEngineSelected));
            OnPropertyChanged(nameof(IsEngineOptionsExpanded));
            LoadEngineOptions();
            Recalculate(); // 清空部件后同步刷新运输效率及已展开的排序栏
        });

        SelectThrusterCommand = new RelayCommand(p => SelectedThruster = p as ThrusterInfo);
        SelectEngineCommand = new RelayCommand(p => SelectedEngine = p as EngineInfo);

        ResetRouteCommand = new RelayCommand(() =>
        {
            StartSectorText = string.Empty;
            EndSectorText = string.Empty;
            SelectedStartNextHop = null;
            SelectedEndPreviousHop = null;
        });
        ResetDistanceCommand = new RelayCommand(() =>
        {
            StartGateDistanceKm = null;
            EndGateDistanceKm = null;
        });
    }

    /// <summary>
    /// 所有可用种族选项。
    /// </summary>
    public ObservableCollection<string> RaceOptions { get; }

    /// <summary>
    /// 所有可用尺寸选项。
    /// </summary>
    public ObservableCollection<string> SizeOptions { get; }

    /// <summary>
    /// 筛选后的舰船列表。
    /// </summary>
    public ObservableCollection<ShipInfo> FilteredShips { get; }

    /// <summary>
    /// 当前选中的种族筛选。
    /// </summary>
    public string? SelectedRace
    {
        get => _selectedRace;
        set
        {
            if (SetProperty(ref _selectedRace, value))
                ApplyFilters();
        }
    }

    /// <summary>
    /// 当前选中的尺寸筛选。
    /// </summary>
    public string? SelectedSize
    {
        get => _selectedSize;
        set
        {
            if (SetProperty(ref _selectedSize, value))
                ApplyFilters();
        }
    }

    /// <summary>
    /// 当前选中的舰船。选中后加载该船可用的推进器/引擎。
    /// </summary>
    public ShipInfo? SelectedShip
    {
        get => _selectedShip;
        set
        {
            // 筛选重建期间 ListBox 会因集合变化把 SelectedItem 置 null → 忽略，保留当前选择（2026-08-15）
            if (_isApplyingFilters && value == null)
                return;
            var previous = _selectedShip;
            if (SetProperty(ref _selectedShip, value))
                LoadShipEquipment(previous);
        }
    }

    /// <summary>
    /// 舰船属性区尺寸筛选框选项（与左栏尺寸筛选相同，但去掉"全部"）；
    /// 独立控件，不与左栏尺寸筛选联动。
    /// </summary>
    public ObservableCollection<string> ShipAttributeSizeOptions { get; }

    /// <summary>
    /// 舰船属性区尺寸筛选框当前值：选择舰船后自动同步为舰船级别（无需用户点击）；
    /// 用户点击行为待定（后续接入）。
    /// </summary>
    public string? ShipAttributeSize
    {
        get => _shipAttributeSize;
        set
        {
            if (SetProperty(ref _shipAttributeSize, value))
            {
                if (SelectedShip == null)
                    LoadManualSizeThrusterOptions(value);
                if (IsSortExpanded)
                    RecomputeSortResults();
            }
        }
    }

    /// <summary>
    /// 舰船属性区仓储筛选框选项（集装/固体/液体）；独立控件，未选舰船时可下拉，点击行为待定。
    /// </summary>
    public ObservableCollection<string> ShipCargoTypeOptions { get; }

    /// <summary>
    /// 舰船属性区仓储筛选框当前值（未选舰船时下拉选择；选中舰船后由 ShipCargoTypeText 文本显示）。
    /// </summary>
    public string? ShipCargoType
    {
        get => _shipCargoType;
        set
        {
            if (SetProperty(ref _shipCargoType, value) && IsSortExpanded)
                RecomputeSortResults(); // 排序栏展开时实时重算
        }
    }

    /// <summary>
    /// 选中舰船后"仓储"筛选框显示的文本（舰船仓储类型，多类型斜杠连接，如"集装/固体"）。
    /// </summary>
    public string ShipCargoTypeText
    {
        get => _shipCargoTypeText;
        private set => SetProperty(ref _shipCargoTypeText, value);
    }

    // ============ 运输效率排序栏（"计算所有舰船"） ============

    /// <summary>排序栏是否展开（展开时隐藏左栏、实时重算；折叠后不再实时计算）。</summary>
    public bool IsSortExpanded
    {
        get => _isSortExpanded;
        set
        {
            if (SetProperty(ref _isSortExpanded, value))
            {
                OnPropertyChanged(nameof(IsLeftPanelVisible));
                OnPropertyChanged(nameof(HasNoSortResults));
                if (!value)
                    SortResults.Clear();
            }
        }
    }

    /// <summary>左栏"舰船选择"是否可见（排序栏展开时折叠隐藏）。</summary>
    public bool IsLeftPanelVisible => !IsSortExpanded;

    /// <summary>排序栏展开但无结果（用于空态提示）。</summary>
    public bool HasNoSortResults => IsSortExpanded && SortResults.Count == 0;

    /// <summary>运输效率排序结果行。</summary>
    public ObservableCollection<ShipEfficiencyRow> SortResults { get; }

    /// <summary>“计算所有舰船”命令：校验航线/距离/选择，成功后展开排序栏并计算。</summary>
    public RelayCommand CalculateAllCommand { get; }

    /// <summary>
    /// “计算所有空间站运输中的最优解”：按当前货类所有已勾选链路的平均效率排序。
    /// </summary>
    public RelayCommand CalculateAllStationTransportOptimalCommand { get; }

    /// <summary>
    /// 把当前完整装配写入对应的仓储类型 × 尺寸全局槽位。
    /// </summary>
    public RelayCommand ApplyConfigurationToAllStationsCommand { get; }

    /// <summary>“舰船配置”标题旁重置按钮：重置舰船选择 + 折叠排序栏。</summary>
    public RelayCommand ResetShipCommand { get; }

    /// <summary>产线编排中是否至少存在一个导入站或计划站；决定批量应用按钮是否显示。</summary>
    public bool HasAnyStations
    {
        get => _hasAnyStations;
        private set
        {
            if (!SetProperty(ref _hasAnyStations, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>由应用壳层桥接产线编排的站点集合状态。</summary>
    public void SetHasAnyStations(bool hasAnyStations) => HasAnyStations = hasAnyStations;

    /// <summary>由应用壳层桥接产线编排的只读运输优化输入。</summary>
    public void SetStationTransportOptimizationSource(IStationTransportOptimizationSource? source)
    {
        if (ReferenceEquals(_stationTransportOptimizationSource, source)) return;
        if (_stationTransportOptimizationSource != null)
            _stationTransportOptimizationSource.TransportOptimizationStateChanged -=
                StationTransportOptimizationSourceOnStateChanged;
        _stationTransportOptimizationSource = source;
        if (_stationTransportOptimizationSource != null)
            _stationTransportOptimizationSource.TransportOptimizationStateChanged +=
                StationTransportOptimizationSourceOnStateChanged;
        if (IsSortExpanded && _sortCalculationMode == SortCalculationMode.StationAverage)
            RecomputeSortResults();
    }

    /// <summary>航线错误提示（为空隐藏）。</summary>
    public string RouteErrorText { get => _routeErrorText; set => SetProperty(ref _routeErrorText, value); }

    /// <summary>距离错误提示（为空隐藏）。</summary>
    public string DistanceErrorText { get => _distanceErrorText; set => SetProperty(ref _distanceErrorText, value); }

    /// <summary>选择错误提示（为空隐藏）。</summary>
    public string SelectionErrorText { get => _selectionErrorText; set => SetProperty(ref _selectionErrorText, value); }

    // ============ 推进器 ============

    /// <summary>
    /// 当前舰船可用的推进器选项（按尺寸 + 实际存在过滤）。
    /// </summary>
    public ObservableCollection<ThrusterInfo> ThrusterOptions { get; }

    /// <summary>
    /// 当前选中的推进器。选中后折叠推进器选择区并重新计算。
    /// </summary>
    public ThrusterInfo? SelectedThruster
    {
        get => _selectedThruster;
        set
        {
            // 不短路：即使选择同一部件（SetProperty 返回 false）也执行折叠与计算
            SetProperty(ref _selectedThruster, value);
            if (value != null)
            {
                IsThrusterSelected = true;
                IsThrusterExpanded = false;
                Recalculate();
            }
        }
    }

    /// <summary>
    /// 推进器选择区是否展开（用于折叠/展开切换）。
    /// </summary>
    public bool IsThrusterExpanded
    {
        get => _isThrusterExpanded;
        set => SetProperty(ref _isThrusterExpanded, value);
    }

    /// <summary>
    /// 是否已选中推进器（折叠层显示依据）。
    /// </summary>
    public bool IsThrusterSelected
    {
        get => _isThrusterSelected;
        set => SetProperty(ref _isThrusterSelected, value);
    }

    /// <summary>
    /// 重新展开推进器选择区的命令。
    /// </summary>
    public RelayCommand ExpandThrusterCommand { get; }

    // ============ 引擎 ============

    /// <summary>
    /// 当前舰船可用的全部引擎选项（直接列出，不经过种族层）。
    /// </summary>
    public ObservableCollection<EngineInfo> EngineOptions { get; }

    /// <summary>
    /// AI 驾驶员技能星级选项，按界面显示顺序从 5 星到 0 星。
    /// </summary>
    public ObservableCollection<int> PilotingStarOptions { get; }

    /// <summary>
    /// 当前选择的 AI 驾驶员技能星级。默认 5 星。
    /// </summary>
    public int SelectedPilotingStars
    {
        get => _selectedPilotingStars;
        set
        {
            if (SetProperty(ref _selectedPilotingStars, Math.Clamp(value, 0, 5)))
                Recalculate();
        }
    }

    /// <summary>
    /// 引擎种族筛选可选内容（来自当前舰船可用引擎的种族，含"全部"）。
    /// 某些舰船只能装特定种族引擎，因此可选内容也会对应减少。
    /// ⚠️ 重建时整体替换实例（而非 Clear+Add），避免 ComboBox 容器复用导致下拉项重复。
    /// </summary>
    private ObservableCollection<string> _engineRaceOptions = new();
    public ObservableCollection<string> EngineRaceOptions
    {
        get => _engineRaceOptions;
        set => SetProperty(ref _engineRaceOptions, value);
    }

    /// <summary>
    /// 引擎风格筛选可选内容（来自当前舰船可用引擎的风格，含"全部"）。
    /// ⚠️ 重建时整体替换实例（而非 Clear+Add），避免 ComboBox 容器复用导致下拉项重复。
    /// </summary>
    private ObservableCollection<string> _engineStyleOptions = new();
    public ObservableCollection<string> EngineStyleOptions
    {
        get => _engineStyleOptions;
        set => SetProperty(ref _engineStyleOptions, value);
    }

    /// <summary>
    /// 当前选择的引擎种族筛选（"全部"=不过滤，选项仅在选择引擎前显示）。
    /// </summary>
    public string? SelectedEngineRace
    {
        get => _selectedEngineRace;
        set
        {
            if (SetProperty(ref _selectedEngineRace, value))
                UpdateEngineOptions();
        }
    }

    /// <summary>
    /// 当前选择的引擎风格筛选（"全部"=不过滤，选项仅在选择引擎前显示）。
    /// </summary>
    public string? SelectedEngineStyle
    {
        get => _selectedEngineStyle;
        set
        {
            if (SetProperty(ref _selectedEngineStyle, value))
                UpdateEngineOptions();
        }
    }

    /// <summary>
    /// 引擎选项区是否展开。
    /// </summary>
    public bool IsEngineOptionsExpanded
    {
        get => _isEngineOptionsExpanded;
        set => SetProperty(ref _isEngineOptionsExpanded, value);
    }

    /// <summary>
    /// 当前选中的引擎。选中后折叠引擎选项区并重新计算。
    /// </summary>
    public EngineInfo? SelectedEngine
    {
        get => _selectedEngine;
        set
        {
            // 不短路：即使选择同一引擎（SetProperty 返回 false）也执行折叠与计算
            SetProperty(ref _selectedEngine, value);
            if (value != null)
            {
                IsEngineSelected = true;
                IsEngineOptionsExpanded = false;
                Recalculate();
            }
        }
    }

    /// <summary>
    /// 是否已选中引擎（折叠层显示依据）。
    /// </summary>
    public bool IsEngineSelected
    {
        get => _isEngineSelected;
        set => SetProperty(ref _isEngineSelected, value);
    }

    /// <summary>
    /// 重新展开引擎选择区的命令。
    /// </summary>
    public RelayCommand ExpandEngineCommand { get; }

    /// <summary>
    /// 选择推进器的命令（参数为 ThrusterInfo）。
    /// </summary>
    public RelayCommand SelectThrusterCommand { get; }

    /// <summary>
    /// 选择引擎的命令（参数为 EngineInfo）。
    /// </summary>
    public RelayCommand SelectEngineCommand { get; }

    /// <summary>
    /// 清空航线（起始点/结束点扇区名）命令。
    /// </summary>
    public RelayCommand ResetRouteCommand { get; }

    /// <summary>
    /// 清空两端距离输入（起始点→星门 / 星门→结束点）命令。
    /// </summary>
    public RelayCommand ResetDistanceCommand { get; }

    // ============ 运输效率 ============

    /// <summary>
    /// 全部扇区英文名（运输效率航线自动补全候选）。
    /// 由 <see cref="SetStarMap"/> 从 StarMapDB 注入，加载前为空列表。
    /// </summary>
    public IReadOnlyList<string> SectorNames
    {
        get => _sectorNames;
        private set => SetProperty(ref _sectorNames, value);
    }

    /// <summary>
    /// 起始点扇区名（自动补全输入框文本）。变更后重算运输路线距离。
    /// </summary>
    public string StartSectorText
    {
        get => _startSectorText;
        set { if (SetProperty(ref _startSectorText, value)) { SelectedStartNextHop = null; UpdateLocalTransportUi(); UpdateTransportRoute(); } }
    }

    /// <summary>
    /// 结束点扇区名（自动补全输入框文本）。变更后重算运输路线距离。
    /// </summary>
    public string EndSectorText
    {
        get => _endSectorText;
        set { if (SetProperty(ref _endSectorText, value)) { SelectedEndPreviousHop = null; UpdateLocalTransportUi(); UpdateTransportRoute(); } }
    }

    /// <summary>当起点有多条可达首接口路线时，用户指定的下一跳扇区。</summary>
    public IReadOnlyList<string> StartNextHopOptions
    {
        get => _startNextHopOptions;
        private set { if (SetProperty(ref _startNextHopOptions, value)) OnPropertyChanged(nameof(IsStartNextHopSelectionVisible)); }
    }

    public bool IsStartNextHopSelectionVisible => StartNextHopOptions.Count > 1;

    public string? SelectedStartNextHop
    {
        get => _selectedStartNextHop;
        set { if (SetProperty(ref _selectedStartNextHop, value)) UpdateTransportRoute(); }
    }

    /// <summary>当终点有多条可达末接口路线时，用户指定的上一跳扇区。</summary>
    public IReadOnlyList<string> EndPreviousHopOptions
    {
        get => _endPreviousHopOptions;
        private set { if (SetProperty(ref _endPreviousHopOptions, value)) OnPropertyChanged(nameof(IsEndPreviousHopSelectionVisible)); }
    }

    public bool IsEndPreviousHopSelectionVisible => EndPreviousHopOptions.Count > 1;

    public string? SelectedEndPreviousHop
    {
        get => _selectedEndPreviousHop;
        set { if (SetProperty(ref _selectedEndPreviousHop, value)) UpdateTransportRoute(); }
    }

    /// <summary>
    /// 起始点到起始点星门距离（km，整数）。变更后重算运输路线距离。
    /// </summary>
    public int? StartGateDistanceKm
    {
        get => _startGateDistanceKm;
        set { if (SetProperty(ref _startGateDistanceKm, value)) UpdateTransportRoute(); }
    }

    /// <summary>
    /// 结束点星门到结束点距离（km，整数）。变更后重算运输路线距离。
    /// </summary>
    public int? EndGateDistanceKm
    {
        get => _endGateDistanceKm;
        set { if (SetProperty(ref _endGateDistanceKm, value)) UpdateTransportRoute(); }
    }

    /// <summary>"起始点→星门"距离输入框是否可见（本地运输起=终时隐藏，该框无意义）。</summary>
    public bool IsStartGateDistanceVisible
    {
        get => _isStartGateDistanceVisible;
        private set => SetProperty(ref _isStartGateDistanceVisible, value);
    }

    /// <summary>结束点距离框标签（跨星区 "星门→结束点"；本地运输 "起始点→结束点"）。</summary>
    public string EndGateDistanceLabel
    {
        get => _endGateDistanceLabel;
        private set => SetProperty(ref _endGateDistanceLabel, value);
    }

    /// <summary>
    /// 本地运输（起=终=同一扇区）UI 处理：
    /// 隐藏"起始点→星门"输入框并清空其值（避免残留值被当作直达段重复计）、
    /// 把"星门→结束点"标签改为"起始点→结束点"；
    /// 起/终不同或任一清空时恢复"起始点→星门"输入框与"星门→结束点"标签。
    /// 判断用字符串比较（不依赖星图解析，起/终被清空或改名时也能正确恢复）。
    /// </summary>
    private void UpdateLocalTransportUi()
    {
        bool isLocal = !string.IsNullOrWhiteSpace(StartSectorText)
            && !string.IsNullOrWhiteSpace(EndSectorText)
            && string.Equals(StartSectorText.Trim(), EndSectorText.Trim(), StringComparison.OrdinalIgnoreCase);
        if (isLocal)
        {
            IsStartGateDistanceVisible = false;
            // 清空起始→星门距离（避免残留值被当作直达段重复计）；用字段直接赋值 + 通知，避免递归重算
            if (_startGateDistanceKm.HasValue)
            {
                _startGateDistanceKm = null;
                OnPropertyChanged(nameof(StartGateDistanceKm));
            }
            EndGateDistanceLabel = "起始点→结束点";
        }
        else
        {
            IsStartGateDistanceVisible = true;
            EndGateDistanceLabel = "星门→结束点";
        }
    }

    // ============ 运输效率：结果 ============

    /// <summary>每次巡航引擎启动区间的距离行（用户输入起始段 + 自动巡航段 + 用户输入结束段）。</summary>
    public IReadOnlyList<TransportDistanceRow> DistanceRows
    {
        get => _distanceRows;
        private set => SetProperty(ref _distanceRows, value);
    }

    /// <summary>巡航引擎启动数（= 距离行数：每启动一次巡航飞一段）。</summary>
    public string CruiseEngineCountText
    {
        get => _cruiseEngineCountText;
        private set => SetProperty(ref _cruiseEngineCountText, value);
    }

    /// <summary>跳数（跨 cluster 星门/加速器穿越次数）。</summary>
    public string JumpCountText
    {
        get => _jumpCountText;
        private set => SetProperty(ref _jumpCountText, value);
    }

    /// <summary>平均距离（总距离 ÷ 巡航引擎启动数）。</summary>
    public string AverageDistanceText
    {
        get => _averageDistanceText;
        private set => SetProperty(ref _averageDistanceText, value);
    }

    /// <summary>进港耗时（从满常规速度刹停到 0 的制动时间；仅 L/XL 舰船，否则占位）。</summary>
    public string DockTimeText
    {
        get => _dockTimeText;
        private set => SetProperty(ref _dockTimeText, value);
    }

    /// <summary>离港耗时（实测拟合模型，独立于进港耗时、不合并；仅 L/XL 舰船，否则占位）。</summary>
    public string UndockTimeText
    {
        get => _undockTimeText;
        private set => SetProperty(ref _undockTimeText, value);
    }

    /// <summary>是否将离港耗时计入总耗时（离港耗时为经验公式，用户可自行勾选）。</summary>
    public bool IncludeUndockInTotal
    {
        get => _includeUndockInTotal;
        set
        {
            if (SetProperty(ref _includeUndockInTotal, value))
                UpdateTransportRoute(); // 勾选状态变化 → 重算总耗时
        }
    }

    /// <summary>是否将进港耗时计入总耗时（进港耗时为经验公式，用户可自行勾选）。</summary>
    public bool IncludeDockInTotal
    {
        get => _includeDockInTotal;
        set
        {
            if (SetProperty(ref _includeDockInTotal, value))
                UpdateTransportRoute(); // 勾选状态变化 → 重算总耗时
        }
    }

    /// <summary>
    /// 旋转耗时（= 180° ÷ 偏航旋转速度）：玩家通常在黄道面建设空间站，货船拿到货后
    /// 需旋转船体对齐星门；即便离港角度已对齐，再次对接时进港也要旋转 180°，故期望值为 180°。
    /// </summary>
    public string RotationTimeText
    {
        get => _rotationTimeText;
        private set => SetProperty(ref _rotationTimeText, value);
    }

    /// <summary>
    /// 总耗时 = 飞行耗时 + 旋转耗时 +（勾选 IncludeUndockInTotal / IncludeDockInTotal 时）离港/进港耗时；
    /// 恒以秒显示。
    /// </summary>
    public string TotalTimeText
    {
        get => _totalTimeText;
        private set => SetProperty(ref _totalTimeText, value);
    }

    /// <summary>运输效率 = 容量 ÷ 总耗时（单位 m³/s）。</summary>
    public string EfficiencyText
    {
        get => _efficiencyText;
        private set => SetProperty(ref _efficiencyText, value);
    }

    // ============ 计算结果 ============

    /// <summary>
    /// 是否已完成推进器+引擎选择（数据区是否显示数值）。
    /// </summary>
    public bool HasSelection
    {
        get => _hasSelection;
        set => SetProperty(ref _hasSelection, value);
    }

    /// <summary>
    /// 是否已选中舰船（引擎数显示依据）。
    /// </summary>
    public bool HasSelectedShip
    {
        get => _hasSelectedShip;
        set => SetProperty(ref _hasSelectedShip, value);
    }

    /// <summary>
    /// 货舱容量标签（如 "容量（集装）"），选中舰船后显示在舰船属性顶部。
    /// 括号内为仓储类型（集装/固体/液体），仅标注这三种类型（冷凝货物几乎无运输需求，即使船可运冷凝也不标注）。
    /// </summary>
    public string CargoLabel
    {
        get => _cargoLabel;
        set => SetProperty(ref _cargoLabel, value);
    }

    /// <summary>
    /// 货舱容量值（如 "57000m³"）。
    /// </summary>
    public string CargoValue
    {
        get => _cargoValue;
        set => SetProperty(ref _cargoValue, value);
    }

    /// <summary>
    /// 货舱类型显示名映射（container 集装 / solid 固体 / liquid 液体）。
    /// condensate（冷凝）不在映射中——其他船即使可运冷凝也不标注（用户要求，如阿斯特丽德）。
    /// </summary>
    private static readonly Dictionary<string, string> CargoTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["container"] = "集装",
        ["solid"] = "固体",
        ["liquid"] = "液体",
    };

    /// <summary>
    /// 生成货舱容量显示：标签恒为"容量"（仓储类型已移到舰船属性区"仓储"筛选框显示）+ 值（"57000m³"）。
    /// </summary>
    private static (string Label, string Value) BuildCargoDisplay(ShipInfo ship)
    {
        if (ship.CargoCapacity <= 0) return ("容量", "—");
        return ("容量", $"{ship.CargoCapacity:0}m³");
    }

    /// <summary>
    /// 舰船仓储类型显示文本（选中舰船后"仓储"筛选框显示）：多类型斜杠连接（如"集装/固体"），
    /// 过滤冷凝类型；无仓储类型时返回"—"。
    /// </summary>
    private static string GetCargoTypeText(ShipInfo ship)
    {
        var typeNames = ship.CargoTypes
            .Where(t => !string.Equals(t, "condensate", StringComparison.OrdinalIgnoreCase))
            .Select(t => CargoTypeNames.TryGetValue(t, out var n) ? n : t)
            .ToList();
        return typeNames.Count > 0 ? string.Join("/", typeNames) : "—";
    }

    /// <summary>
    /// 常规最大速度（m/s）。
    /// </summary>
    public double ForwardSpeed { get => _forwardSpeed; set => SetProperty(ref _forwardSpeed, value); }

    /// <summary>
    /// 常规加速度（m/s²）。
    /// </summary>
    public double Acceleration { get => _acceleration; set => SetProperty(ref _acceleration, value); }

    /// <summary>
    /// 俯仰转向速度（°/s）。
    /// </summary>
    public double PitchRate { get => _pitchRate; set => SetProperty(ref _pitchRate, value); }

    /// <summary>
    /// 偏航转向速度（°/s）。
    /// </summary>
    public double YawRate { get => _yawRate; set => SetProperty(ref _yawRate, value); }

    /// <summary>
    /// 翻滚转向速度（°/s）。
    /// </summary>
    public double RollRate { get => _rollRate; set => SetProperty(ref _rollRate, value); }

    /// <summary>
    /// 巡航最大速度（m/s）。
    /// </summary>
    public double TravelSpeed { get => _travelSpeed; set => SetProperty(ref _travelSpeed, value); }

    /// <summary>
    /// 巡航引擎启动时间（秒）。
    /// </summary>
    public double TravelCharge { get => _travelCharge; set => SetProperty(ref _travelCharge, value); }

    /// <summary>
    /// 巡航加速时间（秒）。
    /// </summary>
    public double TravelAttack { get => _travelAttack; set => SetProperty(ref _travelAttack, value); }

    /// <summary>
    /// 仅包含玩家可用的种族（排除 XEN、KHA 等）。
    /// </summary>
    private static readonly HashSet<string> PlayerRaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "argon", "paranid", "teladi", "boron", "split", "terran"
    };

    /// <summary>
    /// 玩家无法获取/安装引擎的非玩家种族（XEN 、KHA 、TFM 时间线航母等）。
    /// 此类引擎即使连接点标签可匹配玩家舰船插槽，也不出现在引擎选项中。
    /// 注意：使用黑名单而非白名单，以保留 gen（Astrid 等）通用引擎。
    /// </summary>
    private static readonly HashSet<string> NonPlayerRaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "xen", "kha", "tfm"
    };

    /// <summary>
    /// 种族显示名映射。
    /// </summary>
    private static readonly Dictionary<string, string> RaceDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["argon"] = "Argon",
        ["paranid"] = "Paranid",
        ["teladi"] = "Teladi",
        ["boron"] = "Boron",
        ["split"] = "Split",
        ["terran"] = "Terran",
    };

    /// <summary>
    /// 仅保留的舰船尺寸。
    /// </summary>
    private static readonly HashSet<string> ValidSizes = new() { "XL", "L", "M", "S" };

    /// <summary>
    /// 剧情/任务专用或已废弃、玩家无法正常获取的舰船宏 ID（列表中直接剔除）。
    /// </summary>
    private static readonly HashSet<string> ExcludedShipIds = new(StringComparer.OrdinalIgnoreCase)
    {
        // 剧情/任务专用变体
        "ship_pir_l_scavenger_01_a_storyhighcapacity_macro", // 巴巴罗萨·剧情高容量版（civilian，不可捕获）
        "ship_spl_s_trans_container_01_plot_01_macro",       // 大蜥蜴·Split 剧情版
        "ship_spl_s_scenariofighter_01_a_macro",             // 奇美拉·场景版
        // 废弃/未使用宏（游戏实际使用另一编号的同类舰船）
        "ship_arg_s_heavyfighter_01_a_macro",                // 日蚀 先锋型（实际使用 _02_a）
        // 特殊任务/奖励船（玩家最多各获 1~3 艘，不作为常规舰船列表）
        "ship_pir_xl_battleship_01_a_macro",                 // 妖王（Erlking，最多量产 3 条）
        "ship_gen_m_yacht_01_a_macro",                       // 阿斯特丽德（Astrid）
        "ship_ter_s_xperimental_01_a_macro",                 // 实验穿梭机（Timelines 奖励船）
        // 废弃宏（Split Vendetta 中从未实际加入游戏，仅在测试/剧情脚本引用）
        "ship_spl_xl_battleship_01_a_macro",                 // 巨蟒（Python）XL 战列舰
        "ship_spl_m_bomber_01_a_macro",                      // 蝮蛇（Viper）M 轰炸机
        // Timelines 竞速舰船（竞速型变体，玩家不可量产）
        "ship_arg_s_racer_01_a_macro",                       // 精英 竞速型
        "ship_tel_s_racer_01_a_macro",                       // 红隼 竞速型
        "ship_par_s_racer_01_a_macro",                       // 提修斯 竞速型
        "ship_gen_s_racer_01_a_macro",                       // 通用竞速舰船
        // 限量版（玩家不可量产）
        "ship_bor_m_corvette_02_a_macro",                    // 九头蛇 御用型（Hydra Regal，皇室定制限量版）
        // 冷凝货船（游戏内几乎没有运输冷凝货物的需求/船只，从列表过滤）
        "ship_pir_s_trans_condensate_01_a_macro",            // 罗利 冷凝态（Raleigh Condensate，pirate DLC）
    };

    /// <summary>
    /// 去掉舰船名开头的英文名和括号残留，如 "(Tethys Vanguard)）特提斯 先锋型" → "特提斯 先锋型"。
    /// </summary>
    private static string CleanShipName(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return rawName;

        // 如果以 '(' 开头，找到匹配的 ')' 并删除之前的所有内容
        int start = 0;
        if (rawName[0] == '(')
        {
            int depth = 1;
            for (int i = 1; i < rawName.Length; i++)
            {
                if (rawName[i] == '(') depth++;
                else if (rawName[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        start = i + 1;
                        break;
                    }
                }
            }
        }

        // 去掉开头残留的 ) 或 ）或空格
        var trimmed = rawName[start..].TrimStart(')', '\uFF09', ' ');
        return trimmed;
    }

    /// <summary>
    /// 判断舰船是否应排除（玩家不可用：剧情/任务专用、废弃宏、Timelines DLC size_xl 目录）。
    /// </summary>
    private static bool IsExcludedShip(ShipInfo s)
    {
        if (ExcludedShipIds.Contains(s.Id)) return true;

        // 排除 Timelines DLC size_xl 目录下的所有舰船（玩家不可用）
        if (!string.IsNullOrEmpty(s.SourceDir))
        {
            var sep = System.IO.Path.DirectorySeparatorChar;
            if (s.SourceDir.Contains("ego_dlc_timelines", StringComparison.OrdinalIgnoreCase)
                && s.SourceDir.Contains(sep + "size_xl" + sep, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 同名去重时保留的优先级：A 变体 > B 变体 > 其他（空）。
    /// </summary>
    private static int ShipVariantPriority(ShipInfo s) => s.Variant switch
    {
        "A" => 0,
        "B" => 1,
        _ => 2
    };

    /// <summary>
    /// 注入扇区数据：建立自动补全候选（扇区英文｜中文名）并构建导航图（SectorGraph）。
    /// 图构建后立即按当前航线输入重算运输距离。
    /// </summary>
    public void SetStarMap(StarMapDB starMap)
    {
        _starMap = starMap;
        _sectorGraph = starMap != null ? SectorGraph.Build(starMap) : null;
        SectorNames = starMap != null
            ? starMap.Sectors.Values
                .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                .Select(s => s.SearchDisplayName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : Array.Empty<string>();
        UpdateTransportRoute();
    }

    /// <summary>
    /// 根据当前航线输入重新计算运输路线距离明细。
    /// 每次巡航引擎启动 = 一段：用户输入起始段 + 各自动巡航段 + 用户输入结束段。
    /// 星门/SH 穿越为瞬时段，不计入巡航段列表（但计入跳数）；
    /// 非最终段（终点为星门/SH 接口）的耗时按过门段处理——末段用 6.5s 门固定时间
    /// 替代 release 减速段（2026-08-14 补充，见 FlightTimeCalculator 过门段简化）；
    /// 最终段（星门→目的地）保留到达减速段（endAtGate=false）。
    /// </summary>
    private void UpdateTransportRoute()
    {
        ClearTransportResult();
        // 仅在当前输入确实仍需要选择时再写错误；成功或输入变化后不能保留旧提示。
        RouteErrorText = "";
        if (_sectorGraph is null || _starMap is null) return;
        if (string.IsNullOrWhiteSpace(StartSectorText) || string.IsNullOrWhiteSpace(EndSectorText)) return;

        // 路径权重由飞行耗时决定。装配尚不完整时保留旧的可视化距离预览；
        // 一旦有实际飞行属性，结果和筛选框均改用耗时搜索。
        if (_flightStats is null)
        {
            StartNextHopOptions = Array.Empty<string>();
            EndPreviousHopOptions = Array.Empty<string>();
            var previewRoute = _sectorGraph.FindRoute(StartSectorText, EndSectorText);
            if (previewRoute is not { IsReachable: true }) return;
            BuildTransportRoute(previewRoute);
            return;
        }

        // 同一扇区是用户输入的直达末段，不经过接口，也不应显示接口筛选框。
        if (string.Equals(StartSectorText.Trim(), EndSectorText.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            StartNextHopOptions = Array.Empty<string>();
            EndPreviousHopOptions = Array.Empty<string>();
            var localRoute = _sectorGraph.FindFastestRoute(StartSectorText, EndSectorText, _flightStats);
            if (localRoute is { IsReachable: true }) BuildTransportRoute(localRoute);
            return;
        }

        // 两端输入是所选接口的实际距离。枚举有限的首/末接口组合并用同扇区接口间最大距离
        // 推导可补偿耗时，只有仍可能最优的组合才显示给用户。
        var choices = _sectorGraph.GetRouteChoiceOptions(StartSectorText, EndSectorText, _flightStats,
            StartGateDistanceKm ?? 0, EndGateDistanceKm ?? 0);
        var startOptions = choices.StartNextHopOptions;
        var endOptions = choices.EndPreviousHopOptions;
        StartNextHopOptions = startOptions;
        EndPreviousHopOptions = endOptions;
        if (!startOptions.Contains(SelectedStartNextHop ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            _selectedStartNextHop = null;
        if (!endOptions.Contains(SelectedEndPreviousHop ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            _selectedEndPreviousHop = null;

        // 用户输入的两端距离被视为“所选最优路线”的距离，不能拿它猜测其它门的位置。
        // 因而存在多个首/末接口时，先要求明确接口，再生成距离和耗时明细。
        if (IsStartNextHopSelectionVisible && string.IsNullOrWhiteSpace(SelectedStartNextHop))
        {
            RouteErrorText = "请选择起始点下一跳后显示路线";
            return;
        }
        if (IsEndPreviousHopSelectionVisible && string.IsNullOrWhiteSpace(SelectedEndPreviousHop))
        {
            RouteErrorText = "请选择结束点上一跳后显示路线";
            return;
        }

        var route = _sectorGraph.FindFastestRoute(StartSectorText, EndSectorText, _flightStats,
            SelectedStartNextHop, SelectedEndPreviousHop);
        if (route is not { IsReachable: true }) return;

        BuildTransportRoute(route);
    }

    /// <summary>将已确定的路线转换为距离行、飞行计划及运输效率结果。</summary>
    private void BuildTransportRoute(SectorRoute route)
    {
        var calc = new RouteDistanceCalculator(_starMap!);
        var result = calc.Calculate(route, StartGateDistanceKm ?? 0, EndGateDistanceKm ?? 0);

        // 每次巡航引擎启动 = 一段：用户输入起始段 + 各自动巡航段 + 用户输入结束段。
        // ⚠️ 距离为 0 的段（用户未输入/输入 0）表示该处不实际飞行，不计入巡航段：
        // 如同扇区运输（起=终=同扇区）只填起始段 100、结束段留空 → 仅 1 次巡航启动，
        // 而非把 0 距离结束段也算作一次启动（旧行为为 2）。
        var plan = new List<RoutePlanSegment>();
        var rows = new List<TransportDistanceRow>();
        double totalSeconds = 0;
        if (route.Sectors.Count == 1)
        {
            // 本地运输（起=终=同一扇区，零跳）：无星门/SH 穿越，不存在过门段。
            // 用户输入的"起始点-星门"距离实际是起点→终点的直达段，其终点为目的地，
            // 按最终段（endAtGate=false，保留 release 到达减速段）计算，不用 6.5s 门时间替代。
            plan.Add(new RoutePlanSegment(result.StartPointToGateKm, endAtGate: false));
            plan.Add(new RoutePlanSegment(result.EndGateToEndPointKm, endAtGate: false));
            AddDistanceRow(rows, route.Sectors[0].Name, result.StartPointToGateKm, _flightStats, ref totalSeconds);
            AddDistanceRow(rows, route.Sectors[0].Name, result.EndGateToEndPointKm, _flightStats, ref totalSeconds);
        }
        else
        {
            // 起始段（起点→星门）：非最终段，终点为星门 → 过门段，用 6.5s 门固定时间替代 release 减速段
            if (result.StartPointToGateKm > 0)
            {
                plan.Add(new RoutePlanSegment(result.StartPointToGateKm, endAtGate: true));
                AddDistanceRow(rows, route.Sectors[0].Name, result.StartPointToGateKm, _flightStats, ref totalSeconds, endAtGate: true);
            }
            else
            {
                // 跨星区且起始距离未输入/0：起点空间站就在星门 5km 内，进离港船只可直接过门。
                // 无巡航飞行段，但过门本身仍需 6.5s 门固定时间 → 单独列为 0 距离过门段（排序栏也要计入）。
                plan.Add(new RoutePlanSegment(0, endAtGate: true, isGateCrossing: true));
                if (_flightStats is not null)
                {
                    totalSeconds += FlightTimeCalculator.GateCrossingSeconds;
                    rows.Add(new TransportDistanceRow(route.Sectors[0].Name, 0, $"{FlightTimeCalculator.GateCrossingSeconds:0.#}"));
                }
            }
            // 自动巡航段（含起点/中间/终点 cluster 的所有区内飞行段）：终点为 SH 入口/星门接口 → 过门段，
            // 用 6.5s 门固定时间替代 release 减速段
            foreach (var leg in result.Legs)
                foreach (var seg in leg.Segments)
                {
                    plan.Add(new RoutePlanSegment(seg.DistanceKm, endAtGate: true));
                    AddDistanceRow(rows, seg.SectorName, seg.DistanceKm, _flightStats, ref totalSeconds, endAtGate: true);
                }
            // 用户输入结束段（星门→目的地）：最终段，终点为目的地 → 保留 release 到达减速段（endAtGate=false）
            plan.Add(new RoutePlanSegment(result.EndGateToEndPointKm, endAtGate: false));
            AddDistanceRow(rows, route.Sectors[^1].Name, result.EndGateToEndPointKm, _flightStats, ref totalSeconds);
        }
        _routePlan = plan;

        DistanceRows = rows;
        // 巡航引擎启动数 = 实际巡航飞行段数（DistanceKm > 0）；0 距离段（如跨星区起始直接过门的 6.5s）
        // 只过门不启动巡航，不计入启动数（明细仍保留该行以保证耗时相加 = 总耗时）
        int cruiseStartCount = rows.Count(r => r.DistanceKm > 0);
        CruiseEngineCountText = cruiseStartCount.ToString();
        JumpCountText = route.JumpCount.ToString();
        // 平均距离 = 总距离 ÷ 巡航启动数（只除以实际飞行段，0 距离段不拉低平均）
        AverageDistanceText = cruiseStartCount > 0 ? $"{result.TotalKm / cruiseStartCount:0.#} km" : "—";
        // 进港/离港耗时公式当前仅适用于 L/XL 舰船（S/M 暂不使用）。
        // 进港耗时 = 从满常规速度减到 0 速的制动时间（引擎倒车刹停，见 FlightTimeCalculator.CalculateDockTime）
        bool isLargeShip = SelectedShip?.SizeCategory is "L" or "XL";
        double dockSeconds = _flightStats != null && isLargeShip
            ? FlightTimeCalculator.CalculateDockTime(_flightStats) : 0;
        // 离港耗时 = 独立于进港耗时的离港模型（实测拟合：5.527 + 0.674 × 倒退一倍船长耗时），
        // 与进港耗时分别展示、不合并。
        double undockSeconds = _flightStats != null && isLargeShip && SelectedShip!.Length > 0
            ? FlightTimeCalculator.CalculateUndockTime(_flightStats, SelectedShip.Length) : 0;
        // 旋转耗时：静止起转、静止结束的 180° 偏航；按最高转速与角加速度的梯形/三角形候选模型计算。
        // 不含尚未实测验证的 jerk 与 steeringcurve。
        double rotationSeconds = _flightStats != null ? FlightTimeCalculator.CalculateRotationTime(_flightStats) : 0;

        DockTimeText = _flightStats != null && isLargeShip ? $"{dockSeconds:0.#} s" : "—";
        UndockTimeText = _flightStats != null && isLargeShip && SelectedShip!.Length > 0
            ? $"{undockSeconds:0.#} s" : "—";
        RotationTimeText = _flightStats != null && rotationSeconds > 0 ? $"{rotationSeconds:0.#} s" : "—";

        // 总耗时 = 飞行耗时（totalSeconds，各段四舍五入一位之和）+ 旋转耗时 +（勾选时）离港/进港耗时。
        // 各分项先各自四舍五入到一位再相加，保证显示的分项之和 = 总耗时（无舍入差）。
        // 离港/进港耗时为经验公式，是否计入由用户勾选（IncludeUndockInTotal / IncludeDockInTotal）控制。
        double totalSecondsFinal = _flightStats != null && SelectedShip != null
            ? ComputeTotalSeconds(_flightStats, SelectedShip, plan)
            : 0;
        TotalTimeText = _flightStats != null ? $"{totalSecondsFinal:0.#} s" : "—"; // 恒以秒显示
        // 运输效率 = 容量 ÷ 总耗时（单位 m³/s）；容量 = 舰船货舱容量，未装配或总耗时无效时为占位
        double cargoCapacity = SelectedShip?.CargoCapacity ?? 0;
        EfficiencyText = _flightStats != null && cargoCapacity > 0 && totalSecondsFinal > 0
            ? $"{cargoCapacity / totalSecondsFinal:0.#} m³/s"
            : "—";

        // 排序栏展开时实时重算（航线/距离/舰船/部件/勾选等变化都会经由此处）
        if (IsSortExpanded)
            RecomputeSortResults();
    }

    /// <summary>清空运输效率结果（航线无效/未注入星图时显示占位）。</summary>
    private void ClearTransportResult()
    {
        _routePlan = null;
        DistanceRows = Array.Empty<TransportDistanceRow>();
        CruiseEngineCountText = "—";
        JumpCountText = "—";
        AverageDistanceText = "—";
        DockTimeText = "—";
        UndockTimeText = "—";
        RotationTimeText = "—";
        TotalTimeText = "—";
        EfficiencyText = "—";
    }

    // ============ 运输效率排序栏（"计算所有舰船"） ============

    /// <summary>航线飞行计划中的一段（排序栏实时计算用）。</summary>
    private readonly struct RoutePlanSegment
    {
        public readonly double DistanceKm;
        public readonly bool EndAtGate;
        public readonly bool IsGateCrossing; // 0 距离跨星区起始过门段（固定 6.5s）
        public RoutePlanSegment(double distanceKm, bool endAtGate, bool isGateCrossing = false)
        {
            DistanceKm = distanceKm;
            EndAtGate = endAtGate;
            IsGateCrossing = isGateCrossing;
        }
    }

    /// <summary>
    /// "计算所有舰船"：校验航线/距离/选择，失败则在应用底部信息栏显示错误；成功则展开排序栏并计算。
    /// </summary>
    private void CalculateAll()
    {
        RouteErrorText = "";
        DistanceErrorText = "";
        SelectionErrorText = "";

        var errors = new List<string>(3);
        bool routeFilled = !string.IsNullOrWhiteSpace(StartSectorText) && !string.IsNullOrWhiteSpace(EndSectorText);
        if (!routeFilled)
            errors.Add("请输入航线");
        // 两输入框都填了但仍无飞行距离（仅可能是本地运输未填距离）→ 提示输入距离
        if (routeFilled && AverageDistanceText == "—")
            errors.Add("请输入距离");
        if (!HasShipOrSizeAndStorageSelection())
            errors.Add("请选择舰船或舰船尺寸和仓储类型");

        if (errors.Count > 0)
        {
            _showError?.Invoke(string.Join("；", errors));
            return;
        }

        // 校验通过：展开排序栏（隐藏左栏）并计算
        _sortCalculationMode = SortCalculationMode.ManualRoute;
        IsSortExpanded = true;
        RecomputeSortResults();
    }

    private void CalculateAllStationTransportOptimal()
    {
        SelectionErrorText = string.Empty;
        if (!HasShipOrSizeAndStorageSelection())
        {
            _showError?.Invoke("请选择舰船或舰船尺寸和仓储类型");
            return;
        }

        if (!TryGetSelectedTransportStorageType(out var storageType))
        {
            _showError?.Invoke("当前舰船没有唯一可用仓储类型");
            return;
        }

        var source = _stationTransportOptimizationSource;
        if (source?.IsTransportLoading == true)
        {
            _showError?.Invoke("空间站运输链路正在计算，请稍后重试");
            return;
        }
        if (source?.IsTransportNetworkReady != true)
        {
            _showError?.Invoke("空间站运输链路尚未就绪");
            return;
        }
        if (!source.SelectedTransportOptimizationLinks.Any(link => link.StorageType == storageType))
        {
            _showError?.Invoke("当前货物类别没有已勾选的可用运输链路");
            return;
        }

        _sortCalculationMode = SortCalculationMode.StationAverage;
        IsSortExpanded = true;
        RecomputeSortResults();
    }

    private bool HasShipOrSizeAndStorageSelection() =>
        HasSelectedShip || (ShipAttributeSize != null && ShipCargoType != null);

    private bool TryGetSelectedTransportStorageType(out TransportStorageType storageType)
    {
        if (SelectedShip != null)
        {
            var types = GetShipStorageTypes(SelectedShip)
                .Select(tag => TransportShipConfigurationStore.TryParseStorageTag(tag, out var parsed)
                    ? parsed
                    : (TransportStorageType?)null)
                .Where(type => type.HasValue)
                .Select(type => type!.Value)
                .Distinct()
                .ToArray();
            if (types.Length == 1)
            {
                storageType = types[0];
                return true;
            }
        }
        else if (CargoDisplayToInternal(ShipCargoType) is { } storageTag &&
                 TransportShipConfigurationStore.TryParseStorageTag(storageTag, out storageType))
        {
            return true;
        }

        storageType = default;
        return false;
    }

    private void ApplyConfigurationToAllStations()
    {
        var missingParts = new List<string>(3);
        if (SelectedShip == null) missingParts.Add("舰船");
        if (SelectedThruster == null) missingParts.Add("推进器");
        if (SelectedEngine == null) missingParts.Add("引擎");
        if (missingParts.Count > 0)
        {
            _showError?.Invoke($"还未选择{string.Join("/", missingParts)}");
            return;
        }

        var storageTypes = SelectedShip!.CargoTypes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(tag => TransportShipConfigurationStore.TryParseStorageTag(tag, out var type)
                ? type
                : (TransportStorageType?)null)
            .Where(type => type.HasValue)
            .Select(type => type!.Value)
            .Distinct()
            .ToArray();

        if (storageTypes.Length == 0)
        {
            _showError?.Invoke("当前舰船没有可用仓储类型");
            return;
        }

        // 当前选择列表没有真正的混合仓储运输船，因此一次操作只对应一个槽位。
        // 若游戏更新后开放多仓储类型舰船，必须先明确用户要覆盖哪个槽位；这里不静默覆盖多个槽。
        if (storageTypes.Length != 1)
        {
            _showError?.Invoke("当前舰船包含多个仓储类型，暂不能批量应用");
            return;
        }

        var shipSize = SelectedShip.SizeCategory is "L" or "XL"
            ? TransportShipSize.Large
            : TransportShipSize.Small;
        _transportShipConfigurations.Set(
            storageTypes[0],
            shipSize,
            new TransportShipConfiguration(
                SelectedShip.Id,
                SelectedThruster!.Id,
                SelectedEngine!.Id,
                SelectedPilotingStars));
    }

    /// <summary>
    /// "舰船配置"标题旁重置按钮：重置舰船选择；若排序栏展开则折叠（折叠后不再实时计算）。
    /// </summary>
    private void ResetShipSelection()
    {
        SelectedShip = null;
        ShipAttributeSize = null;
        ShipCargoType = null;
        ClearEquipmentForNoShip();
        Recalculate();
        if (IsSortExpanded)
            IsSortExpanded = false;
        _sortCalculationMode = SortCalculationMode.None;
    }

    /// <summary>
    /// 按飞行计划计算某舰船的等效总耗时（秒）：各段飞行（过门段含 6.5s 门固定时间）
    /// + 旋转耗时（180°÷偏航转速）+（勾选时）离港/进港耗时（仅 L/XL）。
    /// 各分项先四舍五入到一位再相加，与汇总区总耗时口径一致。
    /// </summary>
    private double ComputeTotalSeconds(FlightStats stats, ShipInfo ship, IReadOnlyList<RoutePlanSegment> plan)
    {
        var sharedPlan = plan.Select(segment => new StationTransportRoutePlanSegment(
            segment.DistanceKm, segment.EndAtGate, segment.IsGateCrossing)).ToArray();
        return StationTransportEfficiencyCalculator.CalculateTotalSeconds(
            stats,
            ship,
            sharedPlan,
            includeDock: IncludeDockInTotal,
            includeUndock: IncludeUndockInTotal);
    }

    /// <summary>
    /// 计算"运输效率排序"栏：按当前选择的推进器/引擎（未选的部件枚举全部可用组合）/尺寸档位（XL/L、M/S）/仓储类型，
    /// 列出所有候选舰船的运输效率并降序排序；选中舰船时其符合当前部件约束的配置组置顶，但保留实际效率名次。
    /// 仅在排序栏展开且当前模式有有效运输链路时执行。
    /// </summary>
    private void RecomputeSortResults()
    {
        SortResults.Clear();
        OnPropertyChanged(nameof(HasNoSortResults));
        if (!IsSortExpanded) return;

        IReadOnlyList<IReadOnlyList<RoutePlanSegment>> routePlans;
        bool useStationTiming;
        if (_sortCalculationMode == SortCalculationMode.ManualRoute)
        {
            if (_routePlan is null || _routePlan.Count == 0) return;
            routePlans = [_routePlan];
            useStationTiming = false;
        }
        else if (_sortCalculationMode == SortCalculationMode.StationAverage)
        {
            if (!TryGetSelectedTransportStorageType(out var storageType)) return;
            routePlans = BuildStationTransportRoutePlans(storageType);
            if (routePlans.Count == 0) return;
            useStationTiming = true;
        }
        else
        {
            return;
        }

        // 确定尺寸档位 + 仓储类型（内部类型 container/solid/liquid）
        string[] tierSizes;
        HashSet<string> storageTypes;
        bool hasSelectedShip = SelectedShip != null;

        if (hasSelectedShip)
        {
            // 已选舰船即可确定尺寸档位与仓储类型，不能要求推进器/引擎也都选完。
            tierSizes = SelectedShip!.SizeCategory is "L" or "XL" ? new[] { "XL", "L" } : new[] { "M", "S" };
            storageTypes = GetShipStorageTypes(SelectedShip!);
        }
        else
        {
            // 用尺寸/仓储筛选框（未选舰船时）
            if (string.IsNullOrEmpty(ShipAttributeSize)) return;
            tierSizes = ShipAttributeSize is "L" or "XL" ? new[] { "XL", "L" } : new[] { "M", "S" };
            string? internalType = CargoDisplayToInternal(ShipCargoType);
            if (internalType is null) return;
            storageTypes = new HashSet<string>(new[] { internalType }, StringComparer.OrdinalIgnoreCase);
        }

        // 候选舰船 = 尺寸档位内 + 仓储类型匹配
        var candidates = _allShips
            .Where(s => tierSizes.Contains(s.SizeCategory) && ShipCarriesAny(s, storageTypes))
            .ToList();

        var rows = new List<ShipEfficiencyRow>();
        // 已选的部件是跨同档位舰船的约束；未选部件则枚举该船全部可用组合。
        // 因而已选舰船但仅选了一项（或一项未选）时，也能和未选舰船一样得到有效效率排序。
        foreach (var ship in candidates)
        {
            var thrusters = _gameData.Thrusters.Values
                .Where(t => t.SizeCategory == ship.SizeCategory
                    && (SelectedThruster == null
                        || (t.Style == SelectedThruster.Style && t.Mk == SelectedThruster.Mk)))
                .ToList();
            var engines = _gameData.Engines.Values
                .Where(e => !NonPlayerRaces.Contains(e.Race) && IsEngineAvailableForShip(e, ship)
                    && (SelectedEngine == null
                        || (e.Race == SelectedEngine.Race && e.Style == SelectedEngine.Style && e.Mk == SelectedEngine.Mk)))
                .ToList();
            foreach (var thruster in thrusters)
            foreach (var engine in engines)
                AddEfficiencyRow(rows, ship, thruster, engine, routePlans, useStationTiming);
        }

        // 运输效率降序
        rows.Sort((a, b) => b.EfficiencyValue.CompareTo(a.EfficiencyValue));

        // 先按全体效率记录实际名次，随后才将选中舰船的配置组移到首行。
        // 这样“序”反映真实排序，而首组只承担用户配置的优先展示。
        for (int i = 0; i < rows.Count; i++)
            rows[i].Rank = i + 1;

        if (hasSelectedShip)
        {
            var selectedShipRows = rows.Where(r => r.ShipId == SelectedShip!.Id).ToList();
            if (selectedShipRows.Count > 0)
            {
                rows.RemoveAll(r => r.ShipId == SelectedShip!.Id);
                rows.InsertRange(0, selectedShipRows);
            }
        }

        // 填显示文本（名次已在置顶前按实际效率确定）
        foreach (var r in rows)
        {
            r.Efficiency = $"{r.EfficiencyValue:0.0} m³/s";
            SortResults.Add(r);
        }
        OnPropertyChanged(nameof(HasNoSortResults));
    }

    private IReadOnlyList<IReadOnlyList<RoutePlanSegment>> BuildStationTransportRoutePlans(
        TransportStorageType storageType)
    {
        if (_starMap == null || _stationTransportOptimizationSource?.IsTransportNetworkReady != true)
            return [];

        var distanceCalculator = new RouteDistanceCalculator(_starMap);
        return _stationTransportOptimizationSource.SelectedTransportOptimizationLinks
            .Where(link => link.StorageType == storageType)
            .Select(link =>
            {
                var distance = distanceCalculator.Calculate(
                    link.Route, link.SourceSectorPosition, link.TargetSectorPosition);
                return (IReadOnlyList<RoutePlanSegment>)StationTransportEfficiencyCalculator
                    .BuildRoutePlan(link.Route, distance)
                    .Select(segment => new RoutePlanSegment(
                        segment.DistanceKm, segment.EndAtGate, segment.IsGateCrossing))
                    .ToArray();
            })
            .ToArray();
    }

    /// <summary>为候选舰船+部件组合计算一条手动航线或多条空间站链路的平均效率。</summary>
    private void AddEfficiencyRow(
        List<ShipEfficiencyRow> rows,
        ShipInfo ship,
        ThrusterInfo thruster,
        EngineInfo engine,
        IReadOnlyList<IReadOnlyList<RoutePlanSegment>> routePlans,
        bool useStationTiming)
    {
        if (ship.CargoCapacity <= 0) return;
        var stats = FlightCalculator.Calculate(ship, engine, thruster, SelectedPilotingStars);
        var efficiencies = new double[routePlans.Count];
        for (int i = 0; i < routePlans.Count; i++)
        {
            double total = useStationTiming
                ? StationTransportEfficiencyCalculator.CalculateTotalSeconds(
                    stats,
                    ship,
                    routePlans[i].Select(segment => new StationTransportRoutePlanSegment(
                        segment.DistanceKm, segment.EndAtGate, segment.IsGateCrossing)).ToArray())
                : ComputeTotalSeconds(stats, ship, routePlans[i]);
            if (total <= 0) return;
            efficiencies[i] = ship.CargoCapacity / total;
        }
        rows.Add(new ShipEfficiencyRow
        {
            ShipId = ship.Id,
            ShipName = ship.Name,
            Size = ship.SizeCategory,
            Thruster = thruster.DisplayName,
            Engine = engine.DisplayName,
            Capacity = $"{ship.CargoCapacity:0}",
            EfficiencyValue = efficiencies.Average(),
        });
    }

    private void StationTransportOptimizationSourceOnStateChanged(object? sender, EventArgs e)
    {
        if (IsSortExpanded && _sortCalculationMode == SortCalculationMode.StationAverage)
            RecomputeSortResults();
    }

    private enum SortCalculationMode
    {
        None,
        ManualRoute,
        StationAverage
    }

    /// <summary>舰船仓储类型（内部类型集合，过滤冷凝）。</summary>
    private static HashSet<string> GetShipStorageTypes(ShipInfo ship)
        => new(ship.CargoTypes.Where(t => !string.Equals(t, "condensate", StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>舰船是否可装任一指定仓储类型。</summary>
    private static bool ShipCarriesAny(ShipInfo ship, HashSet<string> storageTypes)
        => ship.CargoTypes.Any(t => storageTypes.Contains(t));

    /// <summary>仓储显示名（集装/固体/液体）→ 内部类型（container/solid/liquid）。</summary>
    private static string? CargoDisplayToInternal(string? display)
    {
        if (string.IsNullOrEmpty(display)) return null;
        foreach (var kv in CargoTypeNames)
            if (string.Equals(kv.Value, display, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        return null;
    }

    /// <summary>
    /// 仅当距离 &gt; 0 时把该段加入巡航段列表，并按飞行模型计算其耗时。
    /// 0 距离 = 该处不实际飞行（用户未输入/输入 0），不计入巡航引擎启动数与总耗时。
    /// 未选引擎/推进器（<paramref name="stats"/> 为 null）时耗时列显示 "—"。
    /// <paramref name="endAtGate"/> = true 表示该段终点为星门/SH 接口（过门段）：
    /// 末段用固定门时间 6.5s 替代 release 减速段；false（默认）= 终点为目的地，保留到达减速段。
    /// </summary>
    private static void AddDistanceRow(
        List<TransportDistanceRow> rows, string sectorName, double distanceKm,
        FlightStats? stats, ref double totalSeconds, bool endAtGate = false)
    {
        if (distanceKm <= 0) return;
        string timeText;
        if (stats is not null)
        {
            // 每段耗时内部也保留一位小数（四舍五入）后再累加，保证明细各行耗时相加 = 总耗时（无舍入差）
            double seconds = Math.Round(FlightTimeCalculator.CalculateSegmentTimeKm(stats, distanceKm, endAtGate), 1, MidpointRounding.AwayFromZero);
            totalSeconds += seconds;
            timeText = $"{seconds:0.#}";
        }
        else
        {
            timeText = "—";
        }
        rows.Add(new TransportDistanceRow(sectorName, distanceKm, timeText));
    }

    /// <summary>
    /// 初始化数据，从 GameDataDB 加载舰船并建立筛选选项。
    /// </summary>
    public void Initialize()
    {
        try
        {
            // 只保留玩家种族 + 标准尺寸的舰船，并剔除玩家不可用/重复的舰船
            _allShips = _gameData.Ships.Values
                .Where(s => PlayerRaces.Contains(s.Race) && ValidSizes.Contains(s.SizeCategory))
                .Where(s => !IsExcludedShip(s))
                .AsEnumerable()
                .Select(s => new ShipInfo
                {
                    Id = s.Id,
                    Name = CleanShipName(s.Name),
                    Race = s.Race,
                    Races = new List<string>(s.Races),
                    SizeCategory = s.SizeCategory,
                    ShipType = s.ShipType,
                    Purpose = s.Purpose,
                    Variant = s.Variant,
                    EngineCount = s.EngineCount,
                    EngineSlotTags = new List<string>(s.EngineSlotTags),
                    ThrusterTag = s.ThrusterTag,
                    Mass = s.Mass,
                    InertiaPitch = s.InertiaPitch,
                    InertiaYaw = s.InertiaYaw,
                    InertiaRoll = s.InertiaRoll,
                    DragForward = s.DragForward,
                    DragReverse = s.DragReverse,
                    DragHorizontal = s.DragHorizontal,
                    DragVertical = s.DragVertical,
                    DragPitch = s.DragPitch,
                    DragYaw = s.DragYaw,
                    DragRoll = s.DragRoll,
                    AccFactorForward = s.AccFactorForward,
                    AccFactorReverse = s.AccFactorReverse,
                    AccFactorHorizontal = s.AccFactorHorizontal,
                    AccFactorVertical = s.AccFactorVertical,
                    Length = s.Length,
                    Width = s.Width,
                    CargoCapacity = s.CargoCapacity,
                    CargoTypes = new List<string>(s.CargoTypes)
                })
                // 同名舰船（A/B 变体等）只保留一个代表（变体 A 优先）
                .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(ShipVariantPriority).ThenBy(x => x.Id).First())
                .OrderBy(s => GetRaceOrder(s.Race))
                .ThenBy(s => GetSizeOrder(s.SizeCategory))
                .ThenBy(s => s.Name)
                .ToList();
        }
        catch
        {
            _allShips = new List<ShipInfo>();
        }

        // 种族选项（使用显示名）
        var races = _allShips
            .Select(s => s.Race)
            .Distinct()
            .OrderBy(r => GetRaceOrder(r))
            .ToList();

        RaceOptions.Clear();
        RaceOptions.Add("全部");
        foreach (var race in races)
            RaceOptions.Add(GetRaceDisplayName(race));

        // 尺寸选项
        SizeOptions.Clear();
        SizeOptions.Add("全部");
        SizeOptions.Add("XL");
        SizeOptions.Add("L");
        SizeOptions.Add("M");
        SizeOptions.Add("S");

        // 舰船属性区尺寸选项：与左栏尺寸筛选相同但去掉"全部"（独立控件，不与左栏联动）
        ShipAttributeSizeOptions.Clear();
        foreach (var size in SizeOptions.Skip(1))
            ShipAttributeSizeOptions.Add(size);

        // 默认选中"全部"
        _selectedRace = "全部";
        _selectedSize = "全部";
        OnPropertyChanged(nameof(SelectedRace));
        OnPropertyChanged(nameof(SelectedSize));

        ApplyFilters();
    }

    /// <summary>
    /// 获取种族显示名。
    /// </summary>
    private static string GetRaceDisplayName(string race)
    {
        return RaceDisplayNames.TryGetValue(race, out var display) ? display : race;
    }

    /// <summary>
    /// 选中舰船后加载该船可用的推进器与引擎选项。
    /// 切换舰船时尽量保留可复用的选择：若新舰船与旧舰船同尺寸，且当前引擎/推进器对新舰船仍可用，
    /// 则不清空引擎/推进器选择（保持折叠态并用新舰船重算飞行属性）；否则重置选择并展开选择区。
    /// </summary>
    private void LoadShipEquipment(ShipInfo? previousShip)
    {
        var ship = SelectedShip;
        HasSelectedShip = ship != null;
        // 舰船属性区尺寸筛选框自动同步为当前舰船级别（无需用户点击）
        ShipAttributeSize = ship?.SizeCategory;
        // 仓储筛选框：选中舰船后显示舰船仓储类型文本（多类型斜杠连接，如"集装/固体"）；未选船时清空下拉值
        ShipCargoTypeText = ship != null ? GetCargoTypeText(ship) : "—";
        if (ship == null)
            ShipCargoType = null;
        var (cargoLabel, cargoValue) = ship != null ? BuildCargoDisplay(ship) : ("", "");
        CargoLabel = cargoLabel;
        CargoValue = cargoValue;

        if (ship == null)
        {
            ClearEquipmentForNoShip();
            Recalculate();
            return;
        }

        // 是否保留当前引擎/推进器选择：需在清空之前判断（此时 SelectedThruster/SelectedEngine 仍为旧船选择）。
        // 条件 = 新舰船与旧舰船同尺寸，且当前引擎/推进器对新舰船仍可用。
        bool keepSelection =
            ship != null && previousShip != null
            && ship.SizeCategory == previousShip.SizeCategory
            && SelectedThruster != null && SelectedThruster.SizeCategory == ship.SizeCategory
            && SelectedEngine != null
            && !NonPlayerRaces.Contains(SelectedEngine.Race)
            && IsEngineAvailableForShip(SelectedEngine, ship);

        if (!keepSelection)
        {
            // 清空旧的部件选择
            SelectedThruster = null;
            SelectedEngine = null;
            EngineOptions.Clear();
            IsThrusterSelected = false;
            IsEngineSelected = false;
            HasSelection = false;
        }

        // 推进器：按舰船尺寸过滤（实际存在的档位，如 L/XL 只有均衡）

        // 引擎：直接列出该舰船可用的全部引擎（不经过种族层）；保留选择时不重置引擎、不展开
        LoadThrusterOptions(ship!.SizeCategory);
        LoadEngineOptions(keepSelection);

        if (!keepSelection)
        {
            // 默认展开选择区
            IsThrusterExpanded = true;
            IsEngineOptionsExpanded = true;
        }
        else
        {
            // 保留选择：保持折叠态，并用新舰船 + 保留的引擎/推进器重算飞行属性
            Recalculate();
        }
    }

    /// <summary>
    /// 加载当前舰船可用的全部引擎选项（直接列出，不经过种族层）。
    /// BOR L/XL 舰船只能使用 BOR 引擎与 TER 尖端引擎（游戏内 advanced 连接点机制）；
    /// 非 BOR 舰船排除 BOR 引擎（只给 BOR 船使用）与 TER 尖端引擎（仅 advanced 舰船可用）。
    /// 同时重建种族/风格筛选可选内容（只包含该舰船确实可用的值），并重置筛选为"全部"。
    /// <paramref name="preserveSelection"/> = true 时保留当前引擎选择与折叠态
    /// （切换同尺寸且引擎仍可用的舰船时，见 LoadShipEquipment）。
    /// </summary>
    private void LoadManualSizeThrusterOptions(string? size)
    {
        _selectedThruster = null;
        OnPropertyChanged(nameof(SelectedThruster));
        IsThrusterSelected = false;

        if (string.IsNullOrEmpty(size))
        {
            ThrusterOptions.Clear();
            IsThrusterExpanded = false;
            return;
        }

        LoadThrusterOptions(size);
        IsThrusterExpanded = ThrusterOptions.Count > 0;
    }

    private void LoadThrusterOptions(string size)
    {
        ThrusterOptions.Clear();
        foreach (var thruster in _gameData.Thrusters.Values
            .Where(t => t.SizeCategory == size)
            .OrderBy(t => StyleOrder(t.Style))
            .ThenBy(t => t.Mk))
        {
            ThrusterOptions.Add(thruster);
        }
    }

    private void ClearEquipmentForNoShip()
    {
        _selectedThruster = null;
        _selectedEngine = null;
        OnPropertyChanged(nameof(SelectedThruster));
        OnPropertyChanged(nameof(SelectedEngine));

        IsThrusterSelected = false;
        IsEngineSelected = false;
        IsThrusterExpanded = false;
        IsEngineOptionsExpanded = false;
        ThrusterOptions.Clear();
        EngineOptions.Clear();
        _allShipEngines.Clear();
        EngineRaceOptions = new ObservableCollection<string>();
        EngineStyleOptions = new ObservableCollection<string>();
        _selectedEngineRace = "全部";
        _selectedEngineStyle = "全部";
        OnPropertyChanged(nameof(SelectedEngineRace));
        OnPropertyChanged(nameof(SelectedEngineStyle));
    }

    private void LoadEngineOptions(bool preserveSelection = false)
    {
        if (!preserveSelection)
        {
            IsEngineSelected = false;
            SelectedEngine = null;
        }
        EngineOptions.Clear();

        var ship = SelectedShip;
        if (ship == null) return;

        // 该舰船可用的全部引擎（排序后保留，供筛选重建）
        _allShipEngines = _gameData.Engines.Values
            .Where(e => !NonPlayerRaces.Contains(e.Race) && IsEngineAvailableForShip(e, ship))
            .OrderBy(e => GetRaceOrder(e.Race))
            .ThenBy(e => StyleOrder(e.Style))
            .ThenBy(e => e.Mk)
            .ToList();

        // 切换舰船/重新展开时重置筛选为"全部"（筛选选项集合在 UpdateEngineOptions 中按当前筛选联动重建）
        _selectedEngineRace = "全部";
        _selectedEngineStyle = "全部";
        OnPropertyChanged(nameof(SelectedEngineRace));
        OnPropertyChanged(nameof(SelectedEngineStyle));

        UpdateEngineOptions();

        if (!preserveSelection)
            IsEngineOptionsExpanded = true;
    }

    /// <summary>
    /// 按当前种族/风格筛选联动重建筛选选项并过滤 EngineOptions。
    /// 风格选项 = 当前种族筛选后的风格集合；种族选项 = 当前风格筛选后的种族集合。
    /// 这样选非 TER 种族时风格里不会残留"尖端"（尖端引擎只属 TER），反之亦然；
    /// 若当前选中值因筛选不再可选，则自动重置为"全部"，避免空列表。
    /// </summary>
    private void UpdateEngineOptions()
    {
        // 1) 按当前种族筛选得到风格候选 → 重建风格选项（整体替换实例，避免下拉项重复）
        var styleCandidates = _allShipEngines
            .Where(e => string.IsNullOrEmpty(_selectedEngineRace) || _selectedEngineRace == "全部"
                        || string.Equals(GetRaceDisplayName(e.Race), _selectedEngineRace, StringComparison.OrdinalIgnoreCase))
            .ToList();
        EngineStyleOptions = new ObservableCollection<string>(
            new[] { "全部" }.Concat(styleCandidates
                .Select(e => e.Style)
                .Distinct()
                .OrderBy(StyleOrder)
                .Select(EquipmentDisplay.StyleName)));
        if (!string.IsNullOrEmpty(_selectedEngineStyle) && _selectedEngineStyle != "全部"
            && !EngineStyleOptions.Contains(_selectedEngineStyle))
        {
            _selectedEngineStyle = "全部";
            OnPropertyChanged(nameof(SelectedEngineStyle));
        }

        // 2) 按当前风格筛选得到种族候选 → 重建种族选项（整体替换实例，避免下拉项重复）
        var raceCandidates = _allShipEngines
            .Where(e => string.IsNullOrEmpty(_selectedEngineStyle) || _selectedEngineStyle == "全部"
                        || string.Equals(EquipmentDisplay.StyleName(e.Style), _selectedEngineStyle, StringComparison.OrdinalIgnoreCase))
            .ToList();
        EngineRaceOptions = new ObservableCollection<string>(
            new[] { "全部" }.Concat(raceCandidates
                .Select(e => e.Race)
                .Distinct()
                .OrderBy(GetRaceOrder)
                .Select(GetRaceDisplayName)));
        if (!string.IsNullOrEmpty(_selectedEngineRace) && _selectedEngineRace != "全部"
            && !EngineRaceOptions.Contains(_selectedEngineRace))
        {
            _selectedEngineRace = "全部";
            OnPropertyChanged(nameof(SelectedEngineRace));
        }

        // 3) 按双条件过滤填充 EngineOptions
        EngineOptions.Clear();
        foreach (var engine in _allShipEngines)
        {
            if (!string.IsNullOrEmpty(_selectedEngineRace) && _selectedEngineRace != "全部"
                && !string.Equals(GetRaceDisplayName(engine.Race), _selectedEngineRace, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrEmpty(_selectedEngineStyle) && _selectedEngineStyle != "全部"
                && !string.Equals(EquipmentDisplay.StyleName(engine.Style), _selectedEngineStyle, StringComparison.OrdinalIgnoreCase))
                continue;
            EngineOptions.Add(engine);
        }
    }

    /// <summary>
    /// 判断引擎是否可用于指定舰船（数据驱动，依据游戏内连接点归属标签）。
    /// 引擎可用于舰船 ⇔ 尺寸匹配 且 引擎.SlotTags 全量包含于舰船.EngineSlotTags。
    /// - 普通 M/S 引擎（advanced）→ 所有 M/S 舰船；普通 L/XL 引擎（standard）→ 所有 L/XL 舰船；
    /// - BOR L/XL 与 TER 尖端引擎（advanced）→ 仅 advanced 连接点的舰船（BOR L/XL 等）；
    /// - Envoy/Astrid 引擎（ship_gen_m_corvette_01 等专属标签）→ 仅对应专属舰船。
    /// </summary>
    private static bool IsEngineAvailableForShip(EngineInfo e, ShipInfo ship)
    {
        // 尺寸必须匹配
        if (e.SizeCategory != ship.SizeCategory) return false;

        // 舰船槽位无归属标签（罕见特殊船）时不做标签约束
        if (ship.EngineSlotTags.Count == 0) return true;

        return e.SlotTags.All(tag =>
            ship.EngineSlotTags.Contains(tag, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 根据当前推进器+引擎选择重新计算飞行属性。
    /// </summary>
    private void Recalculate()
    {
        var ship = SelectedShip;
        if (ship == null || SelectedThruster == null || SelectedEngine == null)
        {
            HasSelection = false;
            _flightStats = null;
            UpdateTransportRoute();  // 装配不完整时运输效率耗时恢复占位
            return;
        }

        var stats = FlightCalculator.Calculate(ship, SelectedEngine, SelectedThruster, SelectedPilotingStars);

        ForwardSpeed = stats.ForwardSpeed;
        Acceleration = stats.Acceleration;
        PitchRate = stats.PitchRate;
        YawRate = stats.YawRate;
        RollRate = stats.RollRate;
        TravelSpeed = stats.TravelSpeed;
        TravelCharge = stats.TravelCharge;
        TravelAttack = stats.TravelAttack;
        HasSelection = true;
        _flightStats = stats;
        UpdateTransportRoute();  // 引擎/推进器/舰船变化 → 刷新运输效率耗时
    }

    /// <summary>
    /// 根据当前筛选条件更新显示列表。
    /// </summary>
    private void ApplyFilters()
    {
        var filtered = _allShips.AsEnumerable();

        if (!string.IsNullOrEmpty(_selectedRace) && _selectedRace != "全部")
        {
            // 显示名 → 内部名 反向查找
            var internalRace = RaceDisplayNames
                .FirstOrDefault(kv => kv.Value == _selectedRace).Key;
            if (internalRace != null)
                // 按制造种族列表匹配（特使可在 argon/teladi 两族建造，两个筛选都包含它）
                filtered = filtered.Where(s => s.Races.Contains(internalRace, StringComparer.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(_selectedSize) && _selectedSize != "全部")
            filtered = filtered.Where(s => s.SizeCategory == _selectedSize);

        // 筛选变化保留当前选择的舰船（不清空）：重建期间忽略 ListBox 推送的 null（2026-08-15）
        var keptShip = SelectedShip;
        _isApplyingFilters = true;
        try
        {
            FilteredShips.Clear();
            foreach (var ship in filtered)
                FilteredShips.Add(ship);
        }
        finally
        {
            _isApplyingFilters = false;
        }

        // 若选中舰船仍在筛选结果中，重新推送以恢复 ListBox 高亮；
        // 不在结果中也保留选择（不清空），仅无法在列表中高亮。
        if (keptShip != null && FilteredShips.Contains(keptShip))
        {
            _selectedShip = keptShip; // 保险：确保字段值正确（若被忽略逻辑保护则已是同值）
            OnPropertyChanged(nameof(SelectedShip));
        }
    }

    private static int GetRaceOrder(string race)
    {
        // 与游戏内引擎种族顺序一致：ARG-BOR-PAR-SPL-TEL-TER
        return race.ToLowerInvariant() switch
        {
            "argon" => 0,
            "boron" => 1,
            "paranid" => 2,
            "split" => 3,
            "teladi" => 4,
            "terran" => 5,
            _ => 99
        };
    }

    private static int GetSizeOrder(string size)
    {
        return size switch
        {
            "XL" => 0,
            "L" => 1,
            "M" => 2,
            "S" => 3,
            _ => 99
        };
    }

    private static int StyleOrder(string style)
    {
        // 与游戏内引擎风格顺序一致：均衡 → 巡航 → 战斗；
        // TER 尖端(frontier) 排在均衡后、巡航前。
        return style.ToLowerInvariant() switch
        {
            "allround" => 0,
            "frontier" => 1,
            "travel" => 2,
            "combat" => 3,
            _ => 99
        };
    }
}

/// <summary>
/// 运输效率：单次巡航引擎启动区间（扇区名 + 距离 + 耗时占位）。
/// </summary>
public sealed class TransportDistanceRow
{
    /// <summary>所在扇区名（如 "Avarice I"；起/终段为起/终扇区名）。</summary>
    public string SectorName { get; }

    /// <summary>本段距离（km）。</summary>
    public double DistanceKm { get; }

    /// <summary>距离值显示文本（右对齐）：0 显示 "0"，非 0 精确到小数点后一位（如 "122.9"）。</summary>
    public string DistanceValueText { get; }

    /// <summary>耗时显示（第 4 步实现，当前占位 "—"）。</summary>
    public string TimeText { get; }

    public TransportDistanceRow(string sectorName, double distanceKm, string? timeText = null)
    {
        SectorName = sectorName;
        DistanceKm = distanceKm;
        DistanceValueText = $"{distanceKm:0.#}";
        TimeText = timeText ?? "—";
    }
}
