using System.Collections.ObjectModel;
using X4Calculator.Core.Calculation;
using X4Calculator.Core.Data;

namespace X4Calculator.UI.ViewModels;

public sealed class ProductionChainViewModel : ViewModelBase
{
    private readonly ProductionChainPlanner _planner;
    private ProductionCategory _activeCategory = ProductionCategory.Industrial;
    private string _activeLineage = "default";
    private string _selectedWareId = string.Empty;
    private string _selectedProductTitle = "请在上方选择最终产品";
    private string _chainStatusMessage = string.Empty;
    private bool _hasChain;

    public ProductionChainViewModel(GameDataDB gameData)
    {
        _planner = new ProductionChainPlanner(gameData);
        SelectCategoryCommand = new RelayCommand(SelectCategory);
        SelectLineageCommand = new RelayCommand(SelectLineage);
        SelectWareCommand = new RelayCommand(SelectWare);

        TierColumns = CreateColumns();
        ChainColumns = CreateColumns();
        RebuildLineageOptions();
    }

    public ObservableCollection<ProductionLineageOptionViewModel> LineageOptions { get; } = new();
    public ObservableCollection<ProductionTierColumnViewModel> TierColumns { get; }
    public ObservableCollection<ProductionTierColumnViewModel> ChainColumns { get; }

    public RelayCommand SelectCategoryCommand { get; }
    public RelayCommand SelectLineageCommand { get; }
    public RelayCommand SelectWareCommand { get; }

    public IReadOnlyList<ProductionChainEdgeViewModel> ChainEdges { get; private set; }
        = Array.Empty<ProductionChainEdgeViewModel>();

    public bool IsIndustrialSelected => _activeCategory == ProductionCategory.Industrial;
    public bool IsAgriculturalSelected => _activeCategory == ProductionCategory.Agricultural;

    public string SelectedProductTitle
    {
        get => _selectedProductTitle;
        private set => SetProperty(ref _selectedProductTitle, value);
    }

    public string ChainStatusMessage
    {
        get => _chainStatusMessage;
        private set => SetProperty(ref _chainStatusMessage, value);
    }

    public bool HasChain
    {
        get => _hasChain;
        private set => SetProperty(ref _hasChain, value);
    }

    public void Initialize()
    {
        RebuildCandidates();
    }

    private void SelectCategory(object? parameter)
    {
        if (parameter is not string categoryId) return;
        var next = string.Equals(categoryId, "agricultural", StringComparison.OrdinalIgnoreCase)
            ? ProductionCategory.Agricultural
            : ProductionCategory.Industrial;
        if (_activeCategory == next) return;

        _activeCategory = next;
        OnPropertyChanged(nameof(IsIndustrialSelected));
        OnPropertyChanged(nameof(IsAgriculturalSelected));
        _activeLineage = ProductionChainPlanner.GetLineages(next)[0].Id;
        RebuildLineageOptions();
        RebuildCandidates();
    }

    private void SelectLineage(object? parameter)
    {
        if (parameter is not ProductionLineageOptionViewModel option || option.Id == _activeLineage)
            return;

        _activeLineage = option.Id;
        foreach (var item in LineageOptions)
            item.IsSelected = item.Id == _activeLineage;
        RebuildCandidates();
    }

    private void SelectWare(object? parameter)
    {
        if (parameter is not ProductionWareItemViewModel item) return;

        _selectedWareId = item.Id;
        foreach (var column in TierColumns)
        foreach (var ware in column.Wares)
            ware.IsSelected = ware.Id == _selectedWareId;

        try
        {
            var plan = _planner.BuildChain(item.Id, _activeCategory, _activeLineage);
            foreach (var column in ChainColumns) column.Wares.Clear();
            var cardWidths = plan.Nodes
                .GroupBy(node => node.Tier)
                .ToDictionary(group => group.Key, group => CalculateCardWidth(group.Select(node => node.Ware.Name)));

            foreach (var node in plan.Nodes)
            {
                ChainColumns[node.Tier].Wares.Add(new ProductionWareItemViewModel(
                    node.Ware.Id,
                    node.Ware.Name,
                    node.Tier,
                    GetNodeSubtitle(node),
                    GetMethodLabel(node.FacilityRace, node.Recipe?.Method),
                    node.IsTarget,
                    cardWidths[node.Tier]));
            }

            ChainEdges = plan.Edges
                .Select(edge => new ProductionChainEdgeViewModel(edge.SourceWareId, edge.TargetWareId))
                .ToList();
            OnPropertyChanged(nameof(ChainEdges));
            SelectedProductTitle = item.Name;
            ChainStatusMessage = string.Empty;
            HasChain = true;
        }
        catch (Exception ex)
        {
            ClearChain();
            ChainStatusMessage = ex.Message;
        }
    }

    private void RebuildLineageOptions()
    {
        LineageOptions.Clear();
        foreach (var option in ProductionChainPlanner.GetLineages(_activeCategory))
        {
            LineageOptions.Add(new ProductionLineageOptionViewModel(
                option.Id, option.DisplayName, option.Id == _activeLineage));
        }
    }

    private void RebuildCandidates()
    {
        foreach (var column in TierColumns) column.Wares.Clear();
        ClearChain();
        _selectedWareId = string.Empty;

        try
        {
            foreach (var item in _planner.GetCandidateWares(_activeCategory, _activeLineage))
            {
                TierColumns[item.Tier].Wares.Add(new ProductionWareItemViewModel(
                    item.Ware.Id, item.Ware.Name, item.Tier, catalogRole: item.Role));
            }
            ChainStatusMessage = TierColumns.Sum(column => column.Wares.Count) == 0
                ? "当前生产体系没有可用商品"
                : string.Empty;
        }
        catch (Exception ex)
        {
            ChainStatusMessage = ex.Message;
        }
    }

    private void ClearChain()
    {
        foreach (var column in ChainColumns) column.Wares.Clear();
        ChainEdges = Array.Empty<ProductionChainEdgeViewModel>();
        OnPropertyChanged(nameof(ChainEdges));
        SelectedProductTitle = "请在上方选择最终产品";
        HasChain = false;
    }

    private static ObservableCollection<ProductionTierColumnViewModel> CreateColumns() =>
    [
        new(0, "基础资源"),
        new(1, "初级加工"),
        new(2, "中间产品"),
        new(3, "最终产品")
    ];

    private static string GetNodeSubtitle(ProductionDependencyNode node)
    {
        if (node.IsResource) return "基础资源";
        return string.IsNullOrWhiteSpace(node.Ware.FactoryName)
            ? "生产模块"
            : node.Ware.FactoryName;
    }

    private static string GetMethodLabel(string facilityRace, string? recipeMethod)
    {
        var key = !string.Equals(facilityRace, "default", StringComparison.OrdinalIgnoreCase)
            ? facilityRace
            : recipeMethod ?? "default";
        return key.ToLowerInvariant() switch
        {
            "argon" => "ARG",
            "boron" => "BOR",
            "paranid" => "PAR",
            "split" => "SPL",
            "teladi" => "TEL",
            "terran" => "TER",
            "recycling" or "processing" => "回收",
            "resource" => "RES",
            _ => "GEN"
        };
    }

    private static double CalculateCardWidth(IEnumerable<string> names)
    {
        var longestUnits = names.DefaultIfEmpty(string.Empty).Max(name =>
            name.Sum(character => character <= 0x7f ? 0.58 : 1.0));
        return Math.Clamp(72 + longestUnits * 14, 130, 230);
    }
}

public sealed class ProductionLineageOptionViewModel : ViewModelBase
{
    private bool _isSelected;

    public ProductionLineageOptionViewModel(string id, string displayName, bool isSelected)
    {
        Id = id;
        DisplayName = displayName;
        _isSelected = isSelected;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class ProductionTierColumnViewModel
{
    public ProductionTierColumnViewModel(int tier, string description)
    {
        Tier = tier;
        Description = description;
    }

    public int Tier { get; }
    public string Description { get; }
    public ObservableCollection<ProductionWareItemViewModel> Wares { get; } = new();
}

public sealed class ProductionWareItemViewModel : ViewModelBase
{
    private bool _isSelected;

    public ProductionWareItemViewModel(
        string id,
        string name,
        int tier,
        string subtitle = "",
        string methodLabel = "",
        bool isTarget = false,
        double cardWidth = double.NaN,
        ProductionCatalogRole catalogRole = ProductionCatalogRole.Terminal)
    {
        Id = id;
        Name = name;
        Tier = tier;
        Subtitle = subtitle;
        MethodLabel = methodLabel;
        IsTarget = isTarget;
        CardWidth = cardWidth;
        CatalogRole = catalogRole;
    }

    public string Id { get; }
    public string Name { get; }
    public int Tier { get; }
    public string Subtitle { get; }
    public string MethodLabel { get; }
    public bool HasDetails => !string.IsNullOrEmpty(Subtitle);
    public bool IsTarget { get; }
    public double CardWidth { get; }
    public ProductionCatalogRole CatalogRole { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed record ProductionChainEdgeViewModel(string SourceWareId, string TargetWareId);
