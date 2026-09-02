using System.ComponentModel;
using System.Windows;
using X4Calculator.Core.Calculation;
using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.UI.ViewModels;

public sealed record StationTradeWareOption(string WareId, string Name);

/// <summary>星图消费的一条只读运输路线；选择状态仍由产线编排内部的共享 selection 维护。</summary>
public sealed record StationTransportMapRoute(StationTransportRoute Route, string WareName);

/// <summary>空间站信息栏中一类仓储的运输统计。</summary>
public sealed record StationTransportStorageStatisticItemViewModel(
    TransportStorageType StorageType,
    string StorageName,
    string TransportTimeText,
    string ThroughputText,
    bool HasConfiguration)
{
    public string TransportTimeDisplayText => $"{StorageName}：{TransportTimeText}";
    public string ThroughputDisplayText => $"{StorageName}：{ThroughputText}";
}

/// <summary>向星图提供后台运输网络的只读、已勾选路线投影。</summary>
public interface IStationTransportMapSource
{
    bool IsTransportLoading { get; }
    IReadOnlyList<StationTransportMapRoute> SelectedTransportRoutes { get; }
    event EventHandler? TransportMapStateChanged;
}

/// <summary>舰船排序页消费的一条已勾选空间站运输链路快照。</summary>
public sealed record StationTransportOptimizationLink(
    TransportStorageType StorageType,
    SectorRoute Route,
    Vec3 SourceSectorPosition,
    Vec3 TargetSectorPosition);

/// <summary>向舰船排序页提供后台运输网络的只读优化输入。</summary>
public interface IStationTransportOptimizationSource
{
    bool IsTransportLoading { get; }
    bool IsTransportNetworkReady { get; }
    IReadOnlyList<StationTransportOptimizationLink> SelectedTransportOptimizationLinks { get; }
    event EventHandler? TransportOptimizationStateChanged;
}

/// <summary>一条运输边唯一的用户选择；两种分类投影共享同一实例。</summary>
public sealed class StationTransportLinkSelection : ViewModelBase
{
    private readonly Action? _changed;
    private bool _isSelected = true;

    public StationTransportLinkSelection(string key, Action? changed = null)
    {
        Key = key;
        _changed = changed;
    }

    public string Key { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                _changed?.Invoke();
        }
    }
}

public sealed class StationTransportEntryItemViewModel : ViewModelBase
{
    private readonly StationTransportLinkSelection _selection;

    public StationTransportEntryItemViewModel(
        string name,
        ProductionCatalogRole? role,
        StationTransportLinkSelection selection,
        double? efficiencyM3PerSecond = null,
        string? efficiencyErrorText = null)
    {
        Name = name;
        Role = role;
        EfficiencyM3PerSecond = efficiencyM3PerSecond;
        EfficiencyErrorText = efficiencyErrorText ?? string.Empty;
        _selection = selection;
        PropertyChangedEventManager.AddHandler(
            _selection, SelectionOnPropertyChanged, nameof(StationTransportLinkSelection.IsSelected));
    }

    public string Name { get; }
    public ProductionCatalogRole? Role { get; }
    public bool HasRole => Role.HasValue;
    public double? EfficiencyM3PerSecond { get; }
    public string EfficiencyErrorText { get; }
    public bool HasEfficiencyError => !string.IsNullOrWhiteSpace(EfficiencyErrorText);
    public string EfficiencyText => HasEfficiencyError
        ? EfficiencyErrorText
        : EfficiencyM3PerSecond is double efficiency
            ? $"{efficiency:0.#} m³/s"
            : string.Empty;

    public bool IsSelected
    {
        get => _selection.IsSelected;
        set => _selection.IsSelected = value;
    }

    private void SelectionOnPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        OnPropertyChanged(nameof(IsSelected));
}

public sealed class StationTransportGroupItemViewModel : ViewModelBase
{
    private bool _isExpanded;

    public StationTransportGroupItemViewModel(
        string name,
        ProductionCatalogRole? role,
        IReadOnlyList<StationTransportEntryItemViewModel> entries,
        IReadOnlyList<StationTransportLinkSelection> selections)
    {
        Name = name;
        Role = role;
        Entries = entries;
        Selections = selections.Distinct().ToArray();
        ToggleExpandedCommand = new RelayCommand(_ => IsExpanded = !IsExpanded);
        foreach (var selection in Selections)
        {
            PropertyChangedEventManager.AddHandler(
                selection, SelectionOnPropertyChanged, nameof(StationTransportLinkSelection.IsSelected));
        }
    }

    public string Name { get; }
    public ProductionCatalogRole? Role { get; }
    public bool HasRole => Role.HasValue;
    public IReadOnlyList<StationTransportEntryItemViewModel> Entries { get; }
    public IReadOnlyList<StationTransportLinkSelection> Selections { get; }
    public RelayCommand ToggleExpandedCommand { get; }
    public double EfficiencyM3PerSecond => Entries
        .Where(entry => entry.IsSelected)
        .Sum(entry => entry.EfficiencyM3PerSecond ?? 0d);
    public string EfficiencyText => $"{EfficiencyM3PerSecond:0.#} m³/s";

    public bool? IsSelected
    {
        get
        {
            if (Selections.Count == 0) return false;
            var selected = Selections.Count(item => item.IsSelected);
            return selected == 0 ? false : selected == Selections.Count ? true : null;
        }
        set
        {
            if (!value.HasValue) return;
            foreach (var selection in Selections) selection.IsSelected = value.Value;
            OnPropertyChanged();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
                OnPropertyChanged(nameof(ExpandGlyph));
        }
    }

    public string ExpandGlyph => IsExpanded ? "▲" : "▼";

    private void SelectionOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(EfficiencyM3PerSecond));
        OnPropertyChanged(nameof(EfficiencyText));
    }
}
