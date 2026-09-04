using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using X4Calculator.Core.Models;
using X4Calculator.UI.Services;

namespace X4Calculator.UI.ViewModels;

public sealed class SaveImportViewModel : ViewModelBase
{
    private readonly Func<IReadOnlyList<SavegameFileInfo>> _locateSaves;
    private readonly ISaveFilePicker _filePicker;
    private readonly Func<string, CancellationToken, Task<SavegameImportResult>> _parseSavegame;
    private readonly Action<SavegameImportResult> _applySavegame;
    private readonly Func<bool> _isReady;
    private readonly Action<string>? _reportStatus;
    private readonly TimeProvider _timeProvider;
    private CancellationTokenSource? _importCancellation;
    private long _importGeneration;
    private string? _pendingImportPath;
    private bool _isEntryPage = true;
    private bool _isDefaultPage;
    private bool _isResultPage;
    private bool _isImporting;
    private bool _importSucceeded;
    private string _statusMessage = string.Empty;
    private string _importedFileName = string.Empty;
    private int _importedStationCount;

    public SaveImportViewModel(
        Func<IReadOnlyList<SavegameFileInfo>> locateSaves,
        ISaveFilePicker filePicker,
        Func<string, CancellationToken, Task<SavegameImportResult>> parseSavegame,
        Action<SavegameImportResult> applySavegame,
        Func<bool> isReady,
        Action<string>? reportStatus = null,
        TimeProvider? timeProvider = null)
    {
        _locateSaves = locateSaves;
        _filePicker = filePicker;
        _parseSavegame = parseSavegame;
        _applySavegame = applySavegame;
        _isReady = isReady;
        _reportStatus = reportStatus;
        _timeProvider = timeProvider ?? TimeProvider.System;

        ShowDefaultSavesCommand = new RelayCommand(ShowDefaultSaves);
        ManualImportCommand = new RelayCommand(async () => await PickAndImportAsync());
        ImportSaveCommand = new RelayCommand(async value => await ImportSelectedAsync(value));
        BackCommand = new RelayCommand(ShowEntryPage, () => !IsImporting);
        CancelImportCommand = new RelayCommand(CancelImport, () => IsResultPage);
    }

    public ObservableCollection<SavegameFileInfo> Saves { get; } = new();
    public RelayCommand ShowDefaultSavesCommand { get; }
    public RelayCommand ManualImportCommand { get; }
    public RelayCommand ImportSaveCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand CancelImportCommand { get; }
    public bool IsEntryPage { get => _isEntryPage; private set => SetProperty(ref _isEntryPage, value); }
    public bool IsDefaultPage { get => _isDefaultPage; private set => SetProperty(ref _isDefaultPage, value); }
    public bool IsResultPage
    {
        get => _isResultPage;
        private set
        {
            if (SetProperty(ref _isResultPage, value)) CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool IsImporting
    {
        get => _isImporting;
        private set
        {
            if (SetProperty(ref _isImporting, value)) CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool ImportSucceeded
    {
        get => _importSucceeded;
        private set
        {
            if (SetProperty(ref _importSucceeded, value)) CommandManager.InvalidateRequerySuggested();
        }
    }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string ImportedFileName { get => _importedFileName; private set => SetProperty(ref _importedFileName, value); }
    public int ImportedStationCount { get => _importedStationCount; private set => SetProperty(ref _importedStationCount, value); }
    public bool HasPendingImport => !string.IsNullOrWhiteSpace(_pendingImportPath);

    public void ShowDefaultSaves()
    {
        Saves.Clear();
        foreach (var save in _locateSaves()) Saves.Add(save);
        StatusMessage = Saves.Count == 0 ? "默认存档位置中没有找到 .gz 文件" : string.Empty;
        ShowPage(entry: false, defaults: true, result: false);
    }

    public async Task PickAndImportAsync()
    {
        var path = _filePicker.PickGzipSave();
        if (!string.IsNullOrWhiteSpace(path)) await ImportAsync(path);
    }

    public async Task ImportAsync(string path)
    {
        if (!_isReady())
        {
            InvalidateCurrentImport();
            _pendingImportPath = path;
            ImportSucceeded = false;
            ImportedFileName = Path.GetFileName(path);
            ImportedStationCount = 0;
            StatusMessage = "已选择存档，游戏数据加载完成后将自动导入";
            _reportStatus?.Invoke(StatusMessage);
            ShowPage(entry: false, defaults: false, result: true);
            return;
        }

        _pendingImportPath = null;
        InvalidateCurrentImport();
        var generation = _importGeneration;
        var cancellation = new CancellationTokenSource();
        _importCancellation = cancellation;
        ImportSucceeded = false;
        IsImporting = true;
        ImportedFileName = Path.GetFileName(path);
        ImportedStationCount = 0;
        StatusMessage = "正在导入存档…";
        _reportStatus?.Invoke(StatusMessage);
        ShowPage(entry: false, defaults: false, result: true);
        var importStartedAt = _timeProvider.GetTimestamp();

        try
        {
            var result = await _parseSavegame(path, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != _importGeneration) return;
            _applySavegame(result);
            var elapsed = _timeProvider.GetElapsedTime(importStartedAt);
            ImportedStationCount = result.Stations.Count;
            ImportSucceeded = true;
            StatusMessage = $"导入完成！用时{elapsed.TotalSeconds:F1}秒找到{result.Stations.Count}个玩家空间站！";
            _reportStatus?.Invoke(StatusMessage);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (generation != _importGeneration) return;
            StatusMessage = "已取消导入";
            _reportStatus?.Invoke(StatusMessage);
        }
        catch (Exception ex)
        {
            if (generation != _importGeneration) return;
            StatusMessage = $"导入失败：{ex.Message}";
            _reportStatus?.Invoke(StatusMessage);
        }
        finally
        {
            if (generation == _importGeneration)
            {
                IsImporting = false;
                if (ReferenceEquals(_importCancellation, cancellation))
                    _importCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    public async Task OnDataReadyAsync()
    {
        var pendingPath = _pendingImportPath;
        _pendingImportPath = null;
        if (!string.IsNullOrWhiteSpace(pendingPath))
        {
            await ImportAsync(pendingPath);
            return;
        }

        if (IsDefaultPage) ShowDefaultSaves();
    }

    public void ResetForGameDataChange()
    {
        InvalidateCurrentImport();
        _pendingImportPath = null;
        _applySavegame(SavegameImportResult.Empty);
        IsImporting = false;
        ImportSucceeded = false;
        ImportedStationCount = 0;
        ImportedFileName = string.Empty;
        StatusMessage = string.Empty;
        ShowEntryPage();
    }

    private void InvalidateCurrentImport()
    {
        _importGeneration++;
        _importCancellation?.Cancel();
        _importCancellation = null;
    }

    private Task ImportSelectedAsync(object? value) => value is SavegameFileInfo save
        ? ImportAsync(save.FullPath)
        : Task.CompletedTask;

    private void CancelImport()
    {
        if (IsImporting)
        {
            StatusMessage = "正在取消导入…";
            _importCancellation?.Cancel();
            return;
        }

        if (HasPendingImport)
        {
            _pendingImportPath = null;
            ImportedStationCount = 0;
            ImportedFileName = string.Empty;
            StatusMessage = "已取消导入";
            _reportStatus?.Invoke(StatusMessage);
            ShowEntryPage();
            return;
        }

        if (ImportSucceeded)
        {
            _applySavegame(SavegameImportResult.Empty);
            ImportSucceeded = false;
            ImportedStationCount = 0;
            ImportedFileName = string.Empty;
            StatusMessage = "已取消导入";
            _reportStatus?.Invoke(StatusMessage);
        }
        ShowEntryPage();
    }

    private void ShowEntryPage() => ShowPage(entry: true, defaults: false, result: false);

    private void ShowPage(bool entry, bool defaults, bool result)
    {
        IsEntryPage = entry;
        IsDefaultPage = defaults;
        IsResultPage = result;
    }
}
