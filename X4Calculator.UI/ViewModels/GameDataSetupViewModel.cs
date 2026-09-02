using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using X4Calculator.Core.Data;
using X4Calculator.UI.Services;

namespace X4Calculator.UI.ViewModels;

public enum GameDataSetupState
{
    DiscoveringInstall,
    AnalyzingContent,
    InstallNotFound,
    Entry,
    Reviewing,
    Extracting,
    Failed,
    Completed
}

public sealed class GameDataPackageSelectionViewModel : ViewModelBase
{
    private bool _isSelected = true;

    public GameDataPackageSelectionViewModel(X4ContentPackage package)
    {
        Package = package;
    }

    public X4ContentPackage Package { get; }
    public string Id => Package.Id;
    public string DisplayName => Package.DisplayName;
    public X4ContentKind Kind => Package.Kind;
    public bool IsRequired => Package.Kind == X4ContentKind.BaseGame;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (IsRequired && !value) return;
            SetProperty(ref _isSelected, value);
        }
    }
}

public sealed class GameDataSetupViewModel : ViewModelBase, IDisposable
{
    private readonly IX4DataPreparationService _preparationService;
    private readonly IX4GameDirectoryPicker _directoryPicker;
    private readonly string _destinationDirectory;
    private readonly Func<Task> _completed;
    private CancellationTokenSource? _operationCancellation;
    private IReadOnlyList<X4ContentPackage> _availablePackages = [];
    private X4ExtractionPlan? _reviewPlan;
    private GameDataSetupState _state = GameDataSetupState.DiscoveringInstall;
    private string _gameDirectory = string.Empty;
    private string _statusMessage = "正在查找本机 X4 Foundations…";
    private double _progressPercent;
    private bool _reviewIncludesMods;

    public GameDataSetupViewModel(
        IX4DataPreparationService preparationService,
        IX4GameDirectoryPicker directoryPicker,
        string destinationDirectory,
        Func<Task> completed)
    {
        _preparationService = preparationService;
        _directoryPicker = directoryPicker;
        _destinationDirectory = Path.GetFullPath(destinationDirectory);
        _completed = completed;

        BrowseCommand = new RelayCommand(async () => await BrowseAsync(), () => State != GameDataSetupState.Extracting);
        ChooseVanillaCommand = new RelayCommand(async () => await ReviewAsync(includeMods: false), () => State == GameDataSetupState.Entry);
        ChooseModsCommand = new RelayCommand(async () => await ReviewAsync(includeMods: true), () => State == GameDataSetupState.Entry);
        BackCommand = new RelayCommand(Back, () => State == GameDataSetupState.Reviewing);
        ConfirmCommand = new RelayCommand(async () => await ExtractAsync(), () => State == GameDataSetupState.Reviewing);
        CancelCommand = new RelayCommand(Cancel, () => State == GameDataSetupState.Extracting);
        RetryCommand = new RelayCommand(async () => await RetryAsync(), () => State == GameDataSetupState.Failed);
    }

    public ObservableCollection<GameDataPackageSelectionViewModel> ReviewPackages { get; } = new();
    public RelayCommand BrowseCommand { get; }
    public RelayCommand ChooseVanillaCommand { get; }
    public RelayCommand ChooseModsCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand ConfirmCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RetryCommand { get; }

    public GameDataSetupState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string GameDirectory
    {
        get => _gameDirectory;
        private set => SetProperty(ref _gameDirectory, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public bool ReviewIncludesMods
    {
        get => _reviewIncludesMods;
        private set => SetProperty(ref _reviewIncludesMods, value);
    }

    public async Task InitializeAsync()
    {
        CancelAndDisposeOperation();
        State = GameDataSetupState.DiscoveringInstall;
        StatusMessage = "正在查找本机 X4 Foundations…";
        _operationCancellation = new CancellationTokenSource();
        try
        {
            var directory = await _preparationService.FindGameDirectoryAsync(_operationCancellation.Token);
            if (string.IsNullOrWhiteSpace(directory))
            {
                State = GameDataSetupState.InstallNotFound;
                StatusMessage = "未自动找到 X4 Foundations，请手动选择游戏目录。";
                return;
            }
            LoadGameDirectory(directory);
        }
        catch (OperationCanceledException)
        {
            // 页面被新的发现或关闭操作替代。
        }
        catch (Exception ex)
        {
            State = GameDataSetupState.InstallNotFound;
            StatusMessage = $"自动查找失败：{ex.Message}";
        }
    }

    public async Task BrowseAsync()
    {
        var directory = _directoryPicker.PickGameDirectory(
            string.IsNullOrWhiteSpace(GameDirectory) ? null : GameDirectory);
        if (string.IsNullOrWhiteSpace(directory)) return;
        await Task.Yield();
        try
        {
            LoadGameDirectory(directory);
        }
        catch (Exception ex)
        {
            State = GameDataSetupState.InstallNotFound;
            StatusMessage = $"所选目录不是有效的 X4 游戏目录：{ex.Message}";
        }
    }

    public Task ReviewAsync(bool includeMods)
    {
        if (State != GameDataSetupState.Entry || string.IsNullOrWhiteSpace(GameDirectory))
            return Task.CompletedTask;

        _reviewPlan = null;
        ClearReviewPackages();
        foreach (var package in _availablePackages.Where(package =>
                     includeMods || package.Kind != X4ContentKind.Mod))
        {
            var selection = new GameDataPackageSelectionViewModel(package);
            selection.PropertyChanged += OnReviewPackagePropertyChanged;
            ReviewPackages.Add(selection);
        }
        ProgressPercent = 0;
        UpdateReviewSelectionStatus();
        State = GameDataSetupState.Reviewing;
        return Task.CompletedTask;
    }

    public async Task ExtractAsync()
    {
        if (State != GameDataSetupState.Reviewing) return;

        State = GameDataSetupState.AnalyzingContent;
        StatusMessage = "正在根据勾选内容生成导出计划…";
        try
        {
            var selectedPackageIds = ReviewPackages
                .Where(package => package.IsSelected)
                .Select(package => package.Id)
                .ToList();
            _reviewPlan = await Task.Run(() =>
                _preparationService.CreatePlan(GameDirectory, selectedPackageIds));
        }
        catch (Exception ex)
        {
            State = GameDataSetupState.Reviewing;
            StatusMessage = $"生成导出计划失败：{ex.Message}";
            return;
        }

        CancelAndDisposeOperation();
        _operationCancellation = new CancellationTokenSource();
        State = GameDataSetupState.Extracting;
        ProgressPercent = 0;
        StatusMessage = "正在导出游戏 XML…";

        var progress = new Progress<X4ExtractionProgress>(value =>
        {
            ProgressPercent = value.TotalBytes == 0
                ? 100
                : Math.Clamp(value.CompletedBytes * 100.0 / value.TotalBytes, 0, 100);
            StatusMessage = $"正在导出 {value.CompletedFiles}/{value.TotalFiles}：{value.CurrentFile}";
        });

        try
        {
            await _preparationService.ExtractAsync(
                _reviewPlan!, _destinationDirectory, progress, _operationCancellation.Token);
            ProgressPercent = 100;
            State = GameDataSetupState.Completed;
            StatusMessage = "游戏数据导出完成，正在加载…";
            await _completed();
        }
        catch (OperationCanceledException)
        {
            State = GameDataSetupState.Reviewing;
            StatusMessage = "已取消导出，原有 GameData 未改变。";
        }
        catch (Exception ex)
        {
            State = GameDataSetupState.Failed;
            StatusMessage = $"导出失败：{ex.Message}";
        }
    }

    private void LoadGameDirectory(string directory)
    {
        if (!_preparationService.IsGameDirectory(directory))
            throw new InvalidDataException("目录根层未找到配对的 X4 CAT/DAT 文件。");
        _availablePackages = _preparationService.DiscoverContent(directory);
        GameDirectory = Path.GetFullPath(directory);
        _reviewPlan = null;
        ClearReviewPackages();
        StatusMessage = $"已找到 {_availablePackages.Count(package => package.Kind == X4ContentKind.OfficialDlc)} 个官方 DLC、{_availablePackages.Count(package => package.Kind == X4ContentKind.Mod)} 个 MOD。";
        State = GameDataSetupState.Entry;
    }

    private void Back()
    {
        _reviewPlan = null;
        ClearReviewPackages();
        State = GameDataSetupState.Entry;
        StatusMessage = $"已找到 {_availablePackages.Count(package => package.Kind == X4ContentKind.OfficialDlc)} 个官方 DLC、{_availablePackages.Count(package => package.Kind == X4ContentKind.Mod)} 个 MOD。";
    }

    private void Cancel()
    {
        StatusMessage = "正在取消导出…";
        _operationCancellation?.Cancel();
    }

    private Task RetryAsync()
    {
        if (_reviewPlan == null) return InitializeAsync();
        State = GameDataSetupState.Reviewing;
        return ExtractAsync();
    }

    private void OnReviewPackagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GameDataPackageSelectionViewModel.IsSelected)) return;
        _reviewPlan = null;
        UpdateReviewSelectionStatus();
    }

    private void UpdateReviewSelectionStatus()
    {
        var selected = ReviewPackages.Where(package => package.IsSelected).ToList();
        var dlcCount = selected.Count(package => package.Kind == X4ContentKind.OfficialDlc);
        var modCount = selected.Count(package => package.Kind == X4ContentKind.Mod);
        ReviewIncludesMods = modCount > 0;
        StatusMessage = $"已选择原版游戏、{dlcCount} 个官方 DLC、{modCount} 个 MOD。";
    }

    private void ClearReviewPackages()
    {
        foreach (var package in ReviewPackages)
            package.PropertyChanged -= OnReviewPackagePropertyChanged;
        ReviewPackages.Clear();
        ReviewIncludesMods = false;
    }

    private void CancelAndDisposeOperation()
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = null;
    }

    public void Dispose()
    {
        ClearReviewPackages();
        CancelAndDisposeOperation();
    }

}
