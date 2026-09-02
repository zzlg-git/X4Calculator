using System.Globalization;
using X4Calculator.Core.Calculation;
using X4Calculator.Core.Models;

namespace X4Calculator.UI.ViewModels;

/// <summary>仓储视图中一件货物的可编辑买卖报价状态。</summary>
public sealed class StationWareItemViewModel : ViewModelBase
{
    private readonly Ware _ware;
    private readonly StationWareSetting _setting;
    private readonly Action? _transportSettingsChanged;
    private bool _isExpanded;
    private bool _isAutomaticAllocation = true;
    private bool _isAutomaticBuyAmount;
    private bool _isAutomaticSellAmount;
    private bool _isAutomaticBuyPrice;
    private bool _isAutomaticSellPrice;
    private bool _isBuyOfferEnabled;
    private bool _isSellOfferEnabled;
    private long _manualBuyAmount;
    private long _manualSellAmount;

    public StationWareItemViewModel(
        Ware ware,
        StationWareSetting setting,
        ProductionCatalogRole role,
        long quantityLimit,
        long allocatedStorageVolume,
        IReadOnlyList<StationTradeRuleOption> tradeRuleOptions,
        double productionPerMinute,
        double consumptionPerMinute,
        int consumingModuleCount = 0,
        long? productionBatchAmount = null,
        bool enableImplicitWorkforceBuyOffer = false,
        long? workforceAutomaticBuyAmount = null,
        Action? transportSettingsChanged = null,
        bool isStationSupply = false)
    {
        _ware = ware;
        _setting = setting;
        _transportSettingsChanged = transportSettingsChanged;
        IsStationSupply = isStationSupply;
        Role = role;
        QuantityLimit = quantityLimit;
        AllocatedStorageVolume = allocatedStorageVolume;
        ProductionPerMinute = Math.Max(0, productionPerMinute);
        ConsumptionPerMinute = Math.Max(0, consumptionPerMinute);
        ConsumingModuleCount = Math.Max(0, consumingModuleCount);
        ProductionBatchAmount = productionBatchAmount;
        WorkforceAutomaticBuyAmount = workforceAutomaticBuyAmount;
        TradeRuleOptions = tradeRuleOptions;
        _isAutomaticAllocation = setting.StorageAllocationStatus != StationStorageAllocationStatus.Manual;
        _isAutomaticBuyAmount = CanUseAutomaticBuyAmount;
        _isAutomaticSellAmount = CanUseAutomaticSellAmount;
        _isAutomaticBuyPrice = IsStationSupply || setting.BuyPriceOverride == null;
        _isAutomaticSellPrice = setting.SellPriceOverride == null;
        _manualBuyAmount = EffectiveBuyOffer?.DesiredAmount ?? AutomaticBuyAmount;
        _manualSellAmount = setting.SellOffer?.DesiredAmount ?? AutomaticSellAmount;
        if (!IsStationSupply && setting.BuyEnabled == null && enableImplicitWorkforceBuyOffer)
            setting.BuyEnabled = true;
        _isBuyOfferEnabled = IsStationSupply || setting.BuyOffer != null || setting.BuyEnabled == true;
        _isSellOfferEnabled = !IsStationSupply && (setting.SellOffer != null || setting.SellEnabled);
        ToggleExpandedCommand = new RelayCommand(_ => IsExpanded = !IsExpanded);
        ToggleBuyOfferCommand = new RelayCommand(_ => IsBuyOfferEnabled = !IsBuyOfferEnabled);
        ToggleSellOfferCommand = new RelayCommand(_ => IsSellOfferEnabled = !IsSellOfferEnabled);
    }

    public string WareId => _ware.Id;
    public string Name => _ware.Name;
    public bool IsStationSupply { get; }
    private StationTradeOffer? EffectiveBuyOffer => IsStationSupply
        ? _setting.StationSupplyBuyOffer
        : _setting.BuyOffer;
    public ProductionCatalogRole Role { get; }
    public double ProductionPerMinute { get; }
    public double ConsumptionPerMinute { get; }
    public int ConsumingModuleCount { get; }
    public long? ProductionBatchAmount { get; }
    public long? WorkforceAutomaticBuyAmount { get; }
    public bool HasProduction => ProductionPerMinute > 0.0000001;
    public bool HasConsumption => ConsumptionPerMinute > 0.0000001;
    public bool IsPureTradeWare => !HasProduction && !HasConsumption;
    public long CurrentAmount => _setting.CurrentAmount;
    public long QuantityLimit { get; }
    public long AllocatedStorageVolume { get; }
    public string StorageText => $"{CurrentAmount:N0} / {QuantityLimit:N0}";
    public string StorageCapacityExplanation => $"自动分配仓储 {AllocatedStorageVolume:N0} m³；数量上限已除以单件货物体积。";
    public RelayCommand ToggleExpandedCommand { get; }
    public RelayCommand ToggleBuyOfferCommand { get; }
    public RelayCommand ToggleSellOfferCommand { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (SetProperty(ref _isExpanded, value)) OnPropertyChanged(nameof(ExpandGlyph)); }
    }
    public string ExpandGlyph => IsExpanded ? "▲" : "▼";

    public bool IsAutomaticAllocation
    {
        get => _isAutomaticAllocation;
        set
        {
            if (!SetProperty(ref _isAutomaticAllocation, value)) return;
            _setting.StorageAllocationStatus = value ? StationStorageAllocationStatus.Automatic : StationStorageAllocationStatus.Manual;
            _setting.StorageAllocationOverride = value ? null : QuantityLimit;
        }
    }

    public bool IsBuyOfferEnabled
    {
        get => _isBuyOfferEnabled;
        set
        {
            if (IsStationSupply) return;
            if (_isBuyOfferEnabled == value) return;
            _isBuyOfferEnabled = value;
            _setting.BuyEnabled = value;
            if (!value) _setting.BuyOffer = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BuyOfferButtonText));
            NotifyPricesChanged();
            _transportSettingsChanged?.Invoke();
        }
    }
    public string BuyOfferButtonText => IsBuyOfferEnabled ? "删除购买报价" : "创建购买报价";

    public bool IsSellOfferEnabled
    {
        get => _isSellOfferEnabled;
        set
        {
            if (_isSellOfferEnabled == value) return;
            _isSellOfferEnabled = value;
            _setting.SellEnabled = value;
            if (!value) _setting.SellOffer = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SellOfferButtonText));
            NotifyPricesChanged();
            _transportSettingsChanged?.Invoke();
        }
    }
    public string SellOfferButtonText => IsSellOfferEnabled ? "删除出售报价" : "创建出售报价";

    public bool CanUseAutomaticBuyAmount => WorkforceAutomaticBuyAmount.HasValue || HasConsumption || IsPureTradeWare;
    public bool CanUseAutomaticSellAmount => HasProduction || IsPureTradeWare;

    public bool IsAutomaticBuyAmount
    {
        get => _isAutomaticBuyAmount;
        set
        {
            var normalized = CanUseAutomaticBuyAmount && value;
            if (!SetProperty(ref _isAutomaticBuyAmount, normalized)) return;
            OnPropertyChanged(nameof(BuyAmountText));
            OnPropertyChanged(nameof(CanEditBuyAmount));
            OnPropertyChanged(nameof(AutomaticPriceText));
            NotifyPricesChanged();
        }
    }
    public bool CanEditBuyAmount => !IsAutomaticBuyAmount;
    public string BuyAmountText
    {
        get => (IsAutomaticBuyAmount ? AutomaticBuyAmount : _manualBuyAmount).ToString("N0", CultureInfo.CurrentCulture);
        set
        {
            if (IsAutomaticBuyAmount) return;
            if (!TryParseAmount(value, out var parsed)) return;
            var normalized = Math.Clamp(parsed, 0, QuantityLimit);
            if (_manualBuyAmount == normalized) return;
            _manualBuyAmount = normalized;
            OnPropertyChanged();
            NotifyPricesChanged();
        }
    }

    public bool IsAutomaticSellAmount
    {
        get => _isAutomaticSellAmount;
        set
        {
            var normalized = CanUseAutomaticSellAmount && value;
            if (!SetProperty(ref _isAutomaticSellAmount, normalized)) return;
            OnPropertyChanged(nameof(SellAmountText));
            OnPropertyChanged(nameof(CanEditSellAmount));
            OnPropertyChanged(nameof(AutomaticSellPriceText));
            NotifyPricesChanged();
        }
    }
    public bool CanEditSellAmount => !IsAutomaticSellAmount;
    public string SellAmountText
    {
        get => (IsAutomaticSellAmount ? AutomaticSellAmount : _manualSellAmount).ToString("N0", CultureInfo.CurrentCulture);
        set
        {
            if (IsAutomaticSellAmount) return;
            if (!TryParseAmount(value, out var parsed)) return;
            var normalized = Math.Clamp(parsed, 0, QuantityLimit);
            if (_manualSellAmount == normalized) return;
            _manualSellAmount = normalized;
            OnPropertyChanged();
            NotifyPricesChanged();
        }
    }

    public bool IsAutomaticBuyPrice
    {
        get => _isAutomaticBuyPrice;
        set
        {
            if (IsStationSupply) return;
            if (!SetProperty(ref _isAutomaticBuyPrice, value)) return;
            if (value) _setting.BuyPriceOverride = null;
            else _setting.BuyPriceOverride ??= AutomaticBuyPriceCredits;
            OnPropertyChanged(nameof(IsAutomaticPrice));
            NotifyPricesChanged();
        }
    }

    // 保留旧名称供现有绑定与调用方使用。
    public bool IsAutomaticPrice
    {
        get => IsAutomaticBuyPrice;
        set => IsAutomaticBuyPrice = value;
    }

    public bool IsAutomaticSellPrice
    {
        get => _isAutomaticSellPrice;
        set
        {
            if (!SetProperty(ref _isAutomaticSellPrice, value)) return;
            if (value) _setting.SellPriceOverride = null;
            else _setting.SellPriceOverride ??= AutomaticSellPriceCredits;
            NotifyPricesChanged();
        }
    }

    public bool CanEditBuyPrice => !IsAutomaticBuyPrice;
    public bool CanEditSellPrice => !IsAutomaticSellPrice;

    public string BuyPriceText
    {
        get => FormatPrice(IsAutomaticBuyPrice ? AutomaticBuyPriceCredits : _setting.BuyPriceOverride ?? AutomaticBuyPriceCredits);
        set
        {
            if (IsAutomaticBuyPrice || !TryParsePrice(value, out var parsed)) return;
            var upper = IsSellOfferEnabled && IsAutomaticSellPrice ? Math.Max(_ware.PriceMin, _ware.PriceMax - 1m) : _ware.PriceMax;
            _setting.BuyPriceOverride = Math.Clamp(parsed, _ware.PriceMin, upper);
            NotifyPricesChanged();
        }
    }

    public string SellPriceText
    {
        get => FormatPrice(IsAutomaticSellPrice ? AutomaticSellPriceCredits : _setting.SellPriceOverride ?? AutomaticSellPriceCredits);
        set
        {
            if (IsAutomaticSellPrice || !TryParsePrice(value, out var parsed)) return;
            var lower = IsBuyOfferEnabled && IsAutomaticBuyPrice ? Math.Min(_ware.PriceMax, _ware.PriceMin + 1m) : _ware.PriceMin;
            _setting.SellPriceOverride = Math.Clamp(parsed, lower, _ware.PriceMax);
            NotifyPricesChanged();
        }
    }

    public string AutomaticPriceText => HasConsumption
        ? $"生产购买按原生 Q=max(库存-A0,0)、T=min(仓储目标,A40-A0) 进入三次价格曲线；A0 为单消费者模块小时消耗，A40 为 40 倍全站小时消耗。规划器无待处理预约时按 0 处理；范围 {_ware.PriceMin:N0}–{_ware.PriceMax:N0} Cr，导入报价优先显示存档有效价格。"
        : $"该方向没有自然生产消耗，按纯贸易 Q=库存、T=仓储目标进入原生三次价格曲线估算；范围 {_ware.PriceMin:N0}–{_ware.PriceMax:N0} Cr，导入报价优先显示存档有效价格。";
    public string AutomaticSellPriceText => HasProduction
        ? $"生产出售按原生 Q=库存、T=min(5,000,000 Cr÷均价,仓储目标-单批产量) 进入三次价格曲线。规划器无待处理预约时按 0 处理；{ProductionBatchBoundaryText}双向自动报价至少相差 1 Cr，范围 {_ware.PriceMin:N0}–{_ware.PriceMax:N0} Cr，导入报价优先显示存档有效价格。"
        : $"该方向没有自然生产产出，按纯贸易 Q=库存、T=仓储目标进入原生三次价格曲线估算；双向自动报价至少相差 1 Cr，范围 {_ware.PriceMin:N0}–{_ware.PriceMax:N0} Cr，导入报价优先显示存档有效价格。";

    private string ProductionBatchBoundaryText => ProductionBatchAmount.HasValue
        ? string.Empty
        : "当前存在不同批量配方或缺少可确认批量，单批扣减按 0 估算；";

    public bool UseStationTradeRule
    {
        get => _setting.UseStationBuyTradeRule;
        set
        {
            if (_setting.UseStationBuyTradeRule == value) return;
            _setting.UseStationBuyTradeRule = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSelectTradeRule));
        }
    }
    public bool CanSelectTradeRule => !UseStationTradeRule;
    public IReadOnlyList<StationTradeRuleOption> TradeRuleOptions { get; }
    public int? SelectedTradeRuleId
    {
        get => _setting.BuyTradeRuleId;
        set
        {
            if (_setting.BuyTradeRuleId == value) return;
            _setting.BuyTradeRuleId = value;
            OnPropertyChanged();
        }
    }

    public bool UseStationSellTradeRule
    {
        get => _setting.UseStationSellTradeRule;
        set
        {
            if (_setting.UseStationSellTradeRule == value) return;
            _setting.UseStationSellTradeRule = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSelectSellTradeRule));
        }
    }
    public bool CanSelectSellTradeRule => !UseStationSellTradeRule;
    public int? SelectedSellTradeRuleId
    {
        get => _setting.SellTradeRuleId;
        set
        {
            if (_setting.SellTradeRuleId == value) return;
            _setting.SellTradeRuleId = value;
            OnPropertyChanged();
        }
    }

    private long AutomaticBuyAmount => CalculateAutomaticThreshold(isBuy: true);
    private long AutomaticSellAmount => CalculateAutomaticThreshold(isBuy: false);

    private long CalculateAutomaticThreshold(bool isBuy)
    {
        if (WorkforceAutomaticBuyAmount is { } workforceAmount && HasProduction && !HasConsumption)
            return workforceAmount;
        if (isBuy && WorkforceAutomaticBuyAmount is { } purchaseAmount && !HasProduction)
            return purchaseAmount;
        if (HasProduction && HasConsumption)
        {
            var ratio = Math.Clamp(ConsumptionPerMinute / ProductionPerMinute, 0d, 1d);
            return Math.Clamp((long)(QuantityLimit * ratio), 0, QuantityLimit);
        }
        if (IsPureTradeWare) return isBuy ? QuantityLimit : 0;
        if (isBuy && HasConsumption) return QuantityLimit;
        return 0;
    }

    private decimal AutomaticBuyPriceCredits
    {
        get
        {
            if (IsSellOfferEnabled && !IsAutomaticSellPrice)
                return Math.Clamp((_setting.SellPriceOverride ?? BaseAutomaticBuyPriceCredits) - 1m, _ware.PriceMin, _ware.PriceMax);
            if (EffectiveBuyOffer != null) return EffectiveBuyOffer.PriceCredits;
            if (IsSellOfferEnabled && IsAutomaticSellPrice)
            {
                if (_setting.SellOffer != null)
                    return Math.Clamp(_setting.SellOffer.PriceCredits - 1m, _ware.PriceMin, _ware.PriceMax);
                return AutomaticCandidatePrices.BuyPriceCredits;
            }
            return BaseAutomaticBuyPriceCredits;
        }
    }

    private decimal AutomaticSellPriceCredits
    {
        get
        {
            if (IsBuyOfferEnabled && !IsAutomaticBuyPrice)
                return Math.Clamp((_setting.BuyPriceOverride ?? BaseAutomaticBuyPriceCredits) + 1m, _ware.PriceMin, _ware.PriceMax);
            if (_setting.SellOffer != null) return _setting.SellOffer.PriceCredits;
            if (IsBuyOfferEnabled && IsAutomaticBuyPrice)
            {
                if (EffectiveBuyOffer != null)
                    return Math.Clamp(EffectiveBuyOffer.PriceCredits + 1m, _ware.PriceMin, _ware.PriceMax);
                return AutomaticCandidatePrices.SellPriceCredits;
            }
            return BaseAutomaticSellPriceCredits;
        }
    }

    private decimal BaseAutomaticBuyPriceCredits
    {
        get
        {
            var storageTarget = Math.Max(0L, QuantityLimit);
            var inputs = HasConsumption
                ? StationAutomaticOfferPriceCalculator.CreateProductionBuyCurveInputs(
                    CurrentAmount, storageTarget, ConsumptionPerMinute * 60d, ConsumingModuleCount)
                : StationAutomaticOfferPriceCalculator.CreateTradeCurveInputs(CurrentAmount, storageTarget);
            return CalculateBasePrice(inputs);
        }
    }

    private decimal BaseAutomaticSellPriceCredits
    {
        get
        {
            var inputs = HasProduction
                ? StationAutomaticOfferPriceCalculator.CreateProductionSellCurveInputs(
                    CurrentAmount, QuantityLimit, ProductionBatchAmount ?? 0L, _ware.PriceAvg)
                : StationAutomaticOfferPriceCalculator.CreateTradeCurveInputs(CurrentAmount, QuantityLimit);
            return CalculateBasePrice(inputs);
        }
    }

    private StationAutomaticOfferPricePair AutomaticCandidatePrices =>
        StationAutomaticOfferPriceCalculator.ApplyMinimumSpread(
            BaseAutomaticBuyPriceCredits, BaseAutomaticSellPriceCredits,
            _ware.PriceMin, _ware.PriceMax);

    private decimal CalculateBasePrice(StationAutomaticOfferCurveInputs inputs) =>
        StationAutomaticOfferPriceCalculator.CalculateBasePriceCredits(
            inputs, _ware.PriceMin, _ware.PriceAvg, _ware.PriceMax);

    private static bool TryParseAmount(string value, out long parsed)
        => long.TryParse(value, NumberStyles.Integer | NumberStyles.AllowThousands,
            CultureInfo.CurrentCulture, out parsed);

    private static bool TryParsePrice(string value, out decimal parsed)
    {
        var normalized = value.Replace("Cr", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed);
    }

    private static string FormatPrice(decimal priceCredits) => $"{priceCredits:0.##} Cr";

    private void NotifyPricesChanged()
    {
        OnPropertyChanged(nameof(BuyPriceText));
        OnPropertyChanged(nameof(SellPriceText));
        OnPropertyChanged(nameof(CanEditBuyPrice));
        OnPropertyChanged(nameof(CanEditSellPrice));
    }
}

public sealed record StationTradeRuleOption(int? Id, string Name);
