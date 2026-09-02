using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using X4Calculator.Core.Calculation;
using X4Calculator.Core.Data;
using X4Calculator.Core.Parsing;
using X4Calculator.UI.Services;

namespace X4Calculator.UI.ViewModels;

/// <summary>主窗口 ViewModel，管理启动加载和功能页面状态。</summary>
public class MainViewModel : ViewModelBase, IDisposable
{
    private const int StationPlanningTabIndex = 2;
    private const int StarMapTabIndex = 4;
    private GameDataDB _gameData;
    private ProductionCalculator _calculator;
    private string _statusMessage = "就绪";
    private int _selectedTabIndex;
    private bool _dataLoaded;
    private bool _isLoading;
    private bool _hasLoadError;
    private AppTopMessage? _topMessage;
    private bool _isTopMessageFading;
    private CancellationTokenSource? _topMessageCancellation;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly string _gameDataPath;
    private readonly TransportShipConfigurationStore _transportShipConfigurations = new();
    private ShipComparisonViewModel _shipComparison;
    private ProductionChainViewModel _productionChain;
    private StationPlanningViewModel _stationPlanning = null!;
    private StarMapDB _starMap = new();
    private bool _needsGameDataSetup;

    public MainViewModel()
        : this((delay, cancellationToken) => Task.Delay(delay, cancellationToken), null, null, null)
    {
    }

    /// <summary>供确定性测试注入延时实现；应用运行时使用默认构造函数。</summary>
    public MainViewModel(Func<TimeSpan, CancellationToken, Task> delayAsync)
        : this(delayAsync, null, null, null)
    {
    }

    /// <summary>供测试注入数据目录和首次运行准备服务。</summary>
    public MainViewModel(
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        string? gameDataPath,
        IX4DataPreparationService? dataPreparationService,
        IX4GameDirectoryPicker? gameDirectoryPicker)
    {
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        _gameDataPath = Path.GetFullPath(gameDataPath ?? GetDefaultGameDataDirectory());
        _gameData = new GameDataDB();
        _calculator = new ProductionCalculator(_gameData);
        _shipComparison = new ShipComparisonViewModel(
            _gameData, _transportShipConfigurations, ShowError);
        _productionChain = new ProductionChainViewModel(_gameData);
        StationPlanning = CreateStationPlanningViewModel(_gameData);
        SaveImport = new SaveImportViewModel(
            () => new SavegameLocator(
                locationNameResolver: locationReference =>
                    StarMap.ResolveLocalizedLocationName(locationReference)).FindDefaultSaves(),
            new SaveFilePicker(),
            async (path, cancellationToken) =>
                await new SavegameParser(
                        _gameData,
                        sectorId => StarMap.FindSector(sectorId)?.SearchDisplayName)
                    .ParseAsync(path, cancellationToken),
            result =>
            {
                StarMap.ApplySavegameData(result);
                StationPlanning.SetStations(
                    result.Stations, result.GameTimeSeconds, result.PlayerBlueprintWareIds);
            },
            () => DataLoaded,
            message =>
            {
                StatusMessage = message;
                if (message.StartsWith("导入失败：", StringComparison.Ordinal))
                    ShowError(message);
            });

        GameDataSetup = new GameDataSetupViewModel(
            dataPreparationService ?? new X4DataPreparationService(),
            gameDirectoryPicker ?? new X4GameDirectoryPicker(),
            _gameDataPath,
            OnGameDataSetupCompletedAsync);
        GameDataSetup.PropertyChanged += OnGameDataSetupPropertyChanged;

        LoadDataCommand = new RelayCommand(async _ => await LoadDataAsync(), _ => !IsLoading);
        RetryLoadCommand = new RelayCommand(async _ => await LoadDataAsync(), _ => !IsLoading);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>标题栏中央的瞬时错误或帮助信息；普通进度状态只写入 StatusMessage。</summary>
    public AppTopMessage? TopMessage
    {
        get => _topMessage;
        private set
        {
            if (SetProperty(ref _topMessage, value))
                OnPropertyChanged(nameof(HasTopMessage));
        }
    }

    public bool HasTopMessage => TopMessage != null;

    /// <summary>由视图触发淡出动画；错误显示十秒后变为 true。</summary>
    public bool IsTopMessageFading
    {
        get => _isTopMessageFading;
        private set => SetProperty(ref _isTopMessageFading, value);
    }

    public void ShowError(string message) => ShowTopMessage(message, AppTopMessageKind.Error, autoDismiss: true);

    public void ShowHelp(string message) => ShowTopMessage(message, AppTopMessageKind.Help, autoDismiss: false);

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    public RelayCommand LoadDataCommand { get; }
    public RelayCommand RetryLoadCommand { get; }
    public SaveImportViewModel SaveImport { get; }
    public GameDataSetupViewModel GameDataSetup { get; }

    public bool NeedsGameDataSetup
    {
        get => _needsGameDataSetup;
        private set => SetProperty(ref _needsGameDataSetup, value);
    }

    /// <summary>运输效率页与产线编排页共享的会话级八槽舰船配置。</summary>
    public TransportShipConfigurationStore TransportShipConfigurations => _transportShipConfigurations;

    public bool DataLoaded
    {
        get => _dataLoaded;
        private set => SetProperty(ref _dataLoaded, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!SetProperty(ref _isLoading, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasLoadError
    {
        get => _hasLoadError;
        private set => SetProperty(ref _hasLoadError, value);
    }

    public ShipComparisonViewModel ShipComparison
    {
        get => _shipComparison;
        private set
        {
            if (ReferenceEquals(_shipComparison, value)) return;
            _shipComparison.SetStationTransportOptimizationSource(null);
            if (!SetProperty(ref _shipComparison, value)) return;
            if (_stationPlanning != null)
            {
                value.SetStationTransportOptimizationSource(_stationPlanning);
                UpdateHasAnyStations(_stationPlanning);
            }
        }
    }

    public ProductionChainViewModel ProductionChain
    {
        get => _productionChain;
        private set => SetProperty(ref _productionChain, value);
    }

    public StationPlanningViewModel StationPlanning
    {
        get => _stationPlanning;
        private set
        {
            if (ReferenceEquals(_stationPlanning, value)) return;
            DetachStationCountBridge(_stationPlanning);
            if (!SetProperty(ref _stationPlanning, value)) return;
            AttachStationCountBridge(value);
        }
    }

    public StarMapDB StarMap
    {
        get => _starMap;
        private set => SetProperty(ref _starMap, value);
    }

    public async Task InitializeAsync()
    {
        if (!GameDataDirectory.IsUsable(_gameDataPath))
        {
            NeedsGameDataSetup = true;
            DataLoaded = false;
            HasLoadError = false;
            StatusMessage = "需要先从本机 X4 安装目录导出游戏数据";
            await GameDataSetup.InitializeAsync();
            return;
        }

        await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        if (IsLoading) return;

        DataLoaded = false;
        HasLoadError = false;
        if (!GameDataDirectory.IsUsable(_gameDataPath))
        {
            NeedsGameDataSetup = true;
            StatusMessage = "需要先从本机 X4 安装目录导出游戏数据";
            await GameDataSetup.InitializeAsync();
            return;
        }

        NeedsGameDataSetup = false;
        IsLoading = true;
        StatusMessage = "正在后台准备游戏数据...";

        try
        {
            // 这两个加载器只访问文件和普通 CLR 对象。保持顺序可避免同时扫描同一数据目录。
            var (gameData, starMap) = await Task.Run(async () =>
            {
                var loadedGameData = new GameDataDB();
                await loadedGameData.LoadAsync(_gameDataPath);

                var loadedStarMap = new StarMapDB();
                await loadedStarMap.LoadAsync(_gameDataPath);
                return (loadedGameData, loadedStarMap);
            });

            // await 后回到 WPF UI 线程；只在此时替换页面会读取的数据和 ViewModel。
            _gameData = gameData;
            _calculator = new ProductionCalculator(_gameData);
            ShipComparison = new ShipComparisonViewModel(
                _gameData, _transportShipConfigurations, ShowError);
            ProductionChain = new ProductionChainViewModel(_gameData);
            StationPlanning = CreateStationPlanningViewModel(_gameData);
            StarMap = starMap;

            ShipComparison.Initialize();
            ProductionChain.Initialize();
            StationPlanning.Initialize();
            ShipComparison.SetStarMap(starMap);
            StationPlanning.SetStarMap(starMap);

            StatusMessage = $"游戏数据加载完成：{_gameData.Wares.Count} 种商品，{_gameData.Ships.Count} 艘舰船";
            DataLoaded = true;
            if (SaveImport.IsDefaultPage) SaveImport.ShowDefaultSaves();
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败：{ex.Message}";
            ShowError(StatusMessage);
            HasLoadError = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ShowTopMessage(string message, AppTopMessageKind kind, bool autoDismiss)
    {
        CancelTopMessageTimeout();
        IsTopMessageFading = false;
        TopMessage = new AppTopMessage(message, kind);

        if (!autoDismiss)
            return;

        var cancellation = new CancellationTokenSource();
        _topMessageCancellation = cancellation;
        _ = FadeAndClearErrorAsync(TopMessage, cancellation);
    }

    private async Task FadeAndClearErrorAsync(AppTopMessage message, CancellationTokenSource cancellation)
    {
        try
        {
            await _delayAsync(TimeSpan.FromSeconds(10), cancellation.Token);
            if (!ReferenceEquals(TopMessage, message)) return;

            IsTopMessageFading = true;
            await _delayAsync(TimeSpan.FromMilliseconds(750), cancellation.Token);
            if (!ReferenceEquals(TopMessage, message)) return;

            TopMessage = null;
            IsTopMessageFading = false;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 新消息替换旧消息时，旧计时任务正常结束。
        }
        finally
        {
            if (ReferenceEquals(_topMessageCancellation, cancellation))
                _topMessageCancellation = null;
            cancellation.Dispose();
        }
    }

    private void CancelTopMessageTimeout()
    {
        var cancellation = _topMessageCancellation;
        _topMessageCancellation = null;
        cancellation?.Cancel();
    }

    private StationPlanningViewModel CreateStationPlanningViewModel(GameDataDB gameData)
    {
        var viewModel = new StationPlanningViewModel(
            gameData, ShowError, _transportShipConfigurations);
        viewModel.PlacementNavigationRequested += OnPlacementNavigationRequested;
        return viewModel;
    }

    private async Task OnGameDataSetupCompletedAsync()
    {
        NeedsGameDataSetup = false;
        await LoadDataAsync();
    }

    private void OnGameDataSetupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameDataSetupViewModel.StatusMessage) && NeedsGameDataSetup)
            StatusMessage = GameDataSetup.StatusMessage;
    }

    public void Dispose()
    {
        CancelTopMessageTimeout();
        GameDataSetup.PropertyChanged -= OnGameDataSetupPropertyChanged;
        GameDataSetup.Dispose();
        DetachStationCountBridge(_stationPlanning);
    }

    private void AttachStationCountBridge(StationPlanningViewModel viewModel)
    {
        viewModel.ImportedStations.CollectionChanged += OnStationCollectionChanged;
        viewModel.PlannedStations.CollectionChanged += OnStationCollectionChanged;
        ShipComparison.SetStationTransportOptimizationSource(viewModel);
        UpdateHasAnyStations(viewModel);
    }

    private void DetachStationCountBridge(StationPlanningViewModel? viewModel)
    {
        if (viewModel == null) return;
        viewModel.ImportedStations.CollectionChanged -= OnStationCollectionChanged;
        viewModel.PlannedStations.CollectionChanged -= OnStationCollectionChanged;
        ShipComparison.SetStationTransportOptimizationSource(null);
        viewModel.Dispose();
    }

    private void OnStationCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        UpdateHasAnyStations(StationPlanning);

    private void UpdateHasAnyStations(StationPlanningViewModel viewModel) =>
        ShipComparison.SetHasAnyStations(
            viewModel.ImportedStations.Count > 0 || viewModel.PlannedStations.Count > 0);

    private void OnPlacementNavigationRequested(
        object? sender,
        StationMapPlacementNavigationEventArgs e)
    {
        SelectedTabIndex = e.Target == StationMapPlacementNavigationTarget.StarMap
            ? StarMapTabIndex
            : StationPlanningTabIndex;
    }

    private static string GetDefaultGameDataDirectory()
    {
        var testOverride = Environment.GetEnvironmentVariable("X4CALCULATOR_GAMEDATA");
        return string.IsNullOrWhiteSpace(testOverride)
            ? Path.Combine(AppContext.BaseDirectory, "GameData")
            : testOverride;
    }

}
