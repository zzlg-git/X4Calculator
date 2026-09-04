using X4Calculator.Core.Calculation;
using X4Calculator.Core.Models;

namespace X4Calculator.UI.ViewModels;

/// <summary>产线编排中的空间站条目；保存仅属于本页的规划倍数。</summary>
public sealed class StationPlanningStationItemViewModel : ViewModelBase
{
    private readonly Action<StationPlanningStationItemViewModel>? _changed;
    // 唯一的已建口径：导入时由 operational 状态初始化，点击“瞬间建造”后由当前方案完整替换。
    private readonly Dictionary<string, int> _builtModuleCountSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private int _managerStars;
    private string _preferredBuildMethod = "default";
    private string _preferredRace = "argon";
    private string _stationDuty;
    private bool _stationDutyWasSelected;
    private bool _isDeletePending;
    private bool _fillWorkforceCapacity;
    private bool _skipWorkforceGrowth;
    private bool _hasMapPlacement;

    public StationPlanningStationItemViewModel(
        Station station,
        bool isPlanned,
        string initialStationDuty,
        Action<StationPlanningStationItemViewModel>? changed = null)
    {
        Station = station;
        IsPlanned = isPlanned;
        InitializeBuiltModuleCountSnapshot();
        if (isPlanned) Station.IconKey = StationIconClassifier.Classify(station);
        // 新建规划站从 5 星开始；导入站按存档 management 原始值每 3 点一星，缺少管理员为 0。
        _managerStars = isPlanned
            ? 5
            : Math.Clamp(station.Manager?.ManagementSkill / 3 ?? 0, 0, 5);
        _preferredBuildMethod = isPlanned ? "default" : NormalizePreferredBuildMethod(station.BuildMethod);
        _stationDuty = initialStationDuty;
        _skipWorkforceGrowth = isPlanned;
        _changed = changed;
    }

    public Station Station { get; }
    public bool IsPlanned { get; }
    public bool HasMapPlacement
    {
        get => _hasMapPlacement;
        internal set => SetProperty(ref _hasMapPlacement, value);
    }
    internal int GetBuiltModuleCount(ProductionModule target) =>
        AllocateCount(GetEffectiveBuiltCount(ProductionBuildKey(target)), target.Count,
            Station.Modules.TakeWhile(module => !ReferenceEquals(module, target))
                .Where(module => ProductionBuildKey(module).Equals(
                    ProductionBuildKey(target), StringComparison.OrdinalIgnoreCase))
                .Sum(module => module.Count));

    internal int GetBuiltModuleCount(StationModule target) =>
        AllocateCount(GetEffectiveBuiltCount(AdditionalBuildKey(target)), target.Count,
            Station.AdditionalModules.TakeWhile(module => !ReferenceEquals(module, target))
                .Where(module => AdditionalBuildKey(module).Equals(
                    AdditionalBuildKey(target), StringComparison.OrdinalIgnoreCase))
                .Sum(module => module.Count));

    internal int GetNewlyAddedModuleCount(ProductionModule target) =>
        target.Count - AllocateCount(GetNewModuleBoundary(ProductionBuildKey(target)), target.Count,
            Station.Modules.TakeWhile(module => !ReferenceEquals(module, target))
                .Where(module => ProductionBuildKey(module).Equals(
                    ProductionBuildKey(target), StringComparison.OrdinalIgnoreCase))
                .Sum(module => module.Count));

    internal int GetNewlyAddedModuleCount(StationModule target) =>
        target.Count - AllocateCount(GetNewModuleBoundary(AdditionalBuildKey(target)), target.Count,
            Station.AdditionalModules.TakeWhile(module => !ReferenceEquals(module, target))
                .Where(module => AdditionalBuildKey(module).Equals(
                    AdditionalBuildKey(target), StringComparison.OrdinalIgnoreCase))
                .Sum(module => module.Count));

    internal int GetPlannedModuleCount(ProductionModule target) =>
        target.Count - GetBuiltModuleCount(target);

    internal int GetPlannedModuleCount(StationModule target) =>
        target.Count - GetBuiltModuleCount(target);

    internal void MarkCurrentModulesBuilt()
    {
        _builtModuleCountSnapshot.Clear();
        foreach (var group in Station.Modules.GroupBy(ProductionBuildKey, StringComparer.OrdinalIgnoreCase))
            _builtModuleCountSnapshot[group.Key] = group.Sum(module => module.Count);
        foreach (var group in Station.AdditionalModules.GroupBy(AdditionalBuildKey, StringComparer.OrdinalIgnoreCase))
            _builtModuleCountSnapshot[group.Key] = group.Sum(module => module.Count);
    }
    public string IconKey => Station.IconKey;

    internal void RefreshIcon()
    {
        if (IsPlanned) Station.IconKey = StationIconClassifier.Classify(Station);
        OnPropertyChanged(nameof(IconKey));
    }

    public string Name
    {
        get => Station.Name;
        set
        {
            if (string.Equals(Station.Name, value, StringComparison.Ordinal)) return;
            Station.Name = value;
            OnPropertyChanged();
            _changed?.Invoke(this);
        }
    }

    public bool FillWorkforceCapacity
    {
        get => _fillWorkforceCapacity;
        set
        {
            if (!SetProperty(ref _fillWorkforceCapacity, value)) return;
            _changed?.Invoke(this);
        }
    }

    public bool SkipWorkforceGrowth
    {
        get => _skipWorkforceGrowth;
        set
        {
            if (!SetProperty(ref _skipWorkforceGrowth, value)) return;
            _changed?.Invoke(this);
        }
    }

    public int ManagerStars
    {
        get => _managerStars;
        set
        {
            if (!SetProperty(ref _managerStars, Math.Clamp(value, 0, 5))) return;
            _changed?.Invoke(this);
        }
    }

    public string PreferredBuildMethod
    {
        get => _preferredBuildMethod;
        set => SetProperty(ref _preferredBuildMethod, NormalizePreferredBuildMethod(value));
    }

    public string PreferredRace
    {
        get => _preferredRace;
        set => SetProperty(ref _preferredRace, NormalizePreferredRace(value));
    }

    public string StationDuty
    {
        get => _stationDuty;
        set
        {
            if (!SetProperty(ref _stationDuty, value)) return;
            _stationDutyWasSelected = true;
            _changed?.Invoke(this);
        }
    }

    internal void RefreshDefaultStationDuty(string stationDuty)
    {
        if (_stationDutyWasSelected) return;
        SetProperty(ref _stationDuty, stationDuty, nameof(StationDuty));
    }

    public bool IsDeletePending
    {
        get => _isDeletePending;
        set
        {
            if (!SetProperty(ref _isDeletePending, value)) return;
            OnPropertyChanged(nameof(DeleteButtonText));
        }
    }

    public string DeleteButtonText => IsDeletePending ? "确认？" : "❌";

    private void InitializeBuiltModuleCountSnapshot()
    {
        if (IsPlanned) return;

        if (Station.ModuleConstructions.Count == 0)
        {
            // 旧存档或精简测试数据没有施工序列时，聚合模块本身就是唯一的已建事实。
            foreach (var group in Station.Modules.GroupBy(ProductionBuildKey, StringComparer.OrdinalIgnoreCase))
            {
                var count = group.Sum(module => module.Count);
                _builtModuleCountSnapshot[group.Key] = count;
            }
            foreach (var group in Station.AdditionalModules
                         .GroupBy(AdditionalBuildKey, StringComparer.OrdinalIgnoreCase))
            {
                var count = group.Sum(module => module.Count);
                _builtModuleCountSnapshot[group.Key] = count;
            }
            return;
        }

        var remainingByModuleId = Station.ModuleConstructions
            .Where(module => module.State == StationModuleConstructionState.Operational)
            .GroupBy(module => module.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var module in Station.Modules.Where(module => !module.IsAutoAdded))
            AllocateImportedOperationalCount(ProductionBuildKey(module), module.ModuleId, module.Count, remainingByModuleId);
        foreach (var module in Station.AdditionalModules)
            AllocateImportedOperationalCount(AdditionalBuildKey(module), module.ModuleId, module.Count, remainingByModuleId);
    }

    private void AllocateImportedOperationalCount(
        string key,
        string moduleId,
        int totalCount,
        Dictionary<string, int> remainingByModuleId)
    {
        var builtCount = Math.Min(totalCount, remainingByModuleId.GetValueOrDefault(moduleId));
        if (builtCount > 0)
            _builtModuleCountSnapshot[key] = _builtModuleCountSnapshot.GetValueOrDefault(key) + builtCount;
        remainingByModuleId[moduleId] = Math.Max(0, remainingByModuleId.GetValueOrDefault(moduleId) - builtCount);
    }

    private int GetEffectiveBuiltCount(string key) => _builtModuleCountSnapshot.GetValueOrDefault(key);

    private int GetNewModuleBoundary(string key) => _builtModuleCountSnapshot.GetValueOrDefault(key);

    private static int AllocateCount(int availableCount, int totalCount, int precedingCount) =>
        Math.Clamp(availableCount - precedingCount, 0, totalCount);

    private static string ProductionBuildKey(ProductionModule module) =>
        module.IsAutoAdded
            ? $"production:auto:{module.ModuleId}:{module.SelectedProductWareId ?? "*"}"
            : $"production:selected:{module.ModuleId}";

    private static string AdditionalBuildKey(StationModule module) => $"additional:{module.ModuleId}";

    private static string NormalizePreferredBuildMethod(string method)
    {
        if (string.Equals(method, "terran", StringComparison.OrdinalIgnoreCase)) return "terran";
        if (string.Equals(method, "closedloop", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(method, "recycling", StringComparison.OrdinalIgnoreCase)) return "recycling";
        return "default";
    }

    private static string NormalizePreferredRace(string race) => race.ToLowerInvariant() switch
    {
        "argon" or "boron" or "paranid" or "split" or "teladi" or "terran" => race.ToLowerInvariant(),
        _ => "argon"
    };
}
