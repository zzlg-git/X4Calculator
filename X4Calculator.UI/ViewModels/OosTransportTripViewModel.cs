using System.Collections.ObjectModel;
using System.ComponentModel;
using X4Calculator.Core.Calculation;
using X4Calculator.Core.Data;
using X4Calculator.Core.Models;

namespace X4Calculator.UI.ViewModels;

/// <summary>任意两座已导入站点的条件单程入口；后台计算且输入变化会取消旧结果。</summary>
public sealed class OosTransportTripViewModel : ViewModelBase, IDisposable
{
    private readonly GameDataDB _db;
    private readonly ShipComparisonViewModel _ship;
    private CancellationTokenSource? _cancellation;
    private Station? _source, _target;
    private Ware? _ware;
    private string _sourceBerth = "全部源泊位（分别计算）";
    private string _refresh = "两种刷新分别计算";
    private string _departureMode = "常规自动贸易";
    private bool _isBusy;
    private string _result = "选择两个已导入站点和货物，使用左侧船只配置计算。";
    private string _details = "";
    private int _generation;

    public OosTransportTripViewModel(GameDataDB db, ShipComparisonViewModel ship)
    {
        _db = db; _ship = ship;
        CalculateCommand = new RelayCommand(async () => await CalculateAsync(), () => !IsBusy);
        CancelCommand = new RelayCommand(() => Invalidate("已取消。"), () => IsBusy);
        _ship.PropertyChanged += OnShipChanged;
    }
    public ObservableCollection<Station> Stations { get; } = new();
    public ObservableCollection<Ware> Wares { get; } = new();
    public ObservableCollection<string> SourceBerths { get; } = new();
    public IReadOnlyList<string> RefreshChoices { get; } = ["两种刷新分别计算", "闭星图周期刷新", "连续刷新"];
    public IReadOnlyList<string> DepartureChoices { get; } = ["常规自动贸易", "非阻塞运输（实验订单）"];
    public RelayCommand CalculateCommand { get; }
    public RelayCommand CancelCommand { get; }
    public Station? Source { get => _source; set { if (SetProperty(ref _source, value)) { RefreshBerths(); Invalidate(); } } }
    public Station? Target { get => _target; set { if (SetProperty(ref _target, value)) Invalidate(); } }
    public Ware? Ware { get => _ware; set { if (SetProperty(ref _ware, value)) Invalidate(); } }
    public string SourceBerth { get => _sourceBerth; set { if (SetProperty(ref _sourceBerth, value)) Invalidate(); } }
    public string Refresh { get => _refresh; set { if (SetProperty(ref _refresh, value)) Invalidate(); } }
    public string DepartureMode { get => _departureMode; set { if (SetProperty(ref _departureMode, value)) Invalidate(); } }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string Result { get => _result; private set => SetProperty(ref _result, value); }
    public string Details { get => _details; private set => SetProperty(ref _details, value); }
    public string ConditionsText => "条件估计：满载、五星船员、靠泊辅助 Mk2；站点贸易无人机全部可用、每架运量 4,000 m³，无排队或其他障碍，区域交接位置近似。不同源泊位分别报告；需要未实现清离动作时返回 UNKNOWN。";

    public void SetStations(IEnumerable<Station> stations)
    {
        var items = stations.ToArray();
        if (Stations.SequenceEqual(items)) return;
        var sourceId = Source?.Id; var targetId = Target?.Id;
        Invalidate(); Stations.Clear();
        foreach (var station in items) Stations.Add(station);
        Source = Stations.FirstOrDefault(s => s.Id == sourceId) ?? Stations.FirstOrDefault();
        Target = Stations.FirstOrDefault(s => s.Id == targetId) ?? Stations.FirstOrDefault(s => s != Source);
        Wares.Clear();
        foreach (var ware in _db.Wares.Values.Where(w => w.Volume > 0).OrderBy(w => w.Name)) Wares.Add(ware);
        Ware = Wares.FirstOrDefault(w => w.Id == Ware?.Id) ?? Wares.FirstOrDefault(w => w.Id == "ore") ?? Wares.FirstOrDefault();
    }

    public async Task CalculateAsync()
    {
        Invalidate();
        if (Source?.TransportTopology is not { } source || Target?.TransportTopology is not { } target || Source.Id == Target.Id ||
            Ware is not { } ware || _ship.SelectedShip is not { } ship || _ship.SelectedEngine is not { } engine || _ship.SelectedThruster is not { } thruster)
        { Result = "UNKNOWN：需要两座不同站点的完整存档拓扑、货物和船只装配。"; return; }
        var generation = _generation;
        var cts = _cancellation = new();
        var selectedBerth = SourceBerth;
        var departureOrder = DepartureMode == DepartureChoices[0] ? "TradeRoutine" : "DockAndWait";
        var regimes = Refresh == RefreshChoices[1] ? new[] { OosTransportRefreshRegime.Periodic } :
            Refresh == RefreshChoices[2] ? [OosTransportRefreshRegime.Continuous] :
            new[] { OosTransportRefreshRegime.Periodic, OosTransportRefreshRegime.Continuous };
        var configuration = new StationTransportShipConfiguration(ship, engine, thruster, _ship.SelectedPilotingStars);
        IsBusy = true; Result = "正在计算各源泊位的条件均值…";
        try
        {
            var prediction = await Task.Run(() =>
            {
                var resolution = new OosTransportShipProfileResolver(_db).Resolve(new(configuration, ware.Id, null, 5, 0, "software_dockmk2", false));
                var profile = resolution.Profile ?? throw new NotSupportedException(string.Join("; ", resolution.UnsupportedReasons));
                var preparer = new OosTransportEndpointPreparer(_db);
                var eligibleSource = source.Berths.Where(b => b.SupportsCapitalTwoPointTemplate &&
                    (selectedBerth == "全部源泊位（分别计算）" || b.SaveId == selectedBerth)).Select(b => b.SaveId).ToArray();
                var eligibleTarget = target.Berths.Where(b => b.SupportsCapitalTwoPointTemplate).Select(b => b.SaveId).ToArray();
                // 容量和顺序始终作为可见的情景假设，不视为导入的预约状态。
                var src = preparer.Prepare(source, profile, new(eligibleSource, 4000, 0, true, departureOrder));
                var dst = preparer.Prepare(target, profile, new(eligibleTarget, 4000, 0, true, departureOrder));
                var scenario = new OosTransportTripScenario(new(OosTransportRefreshRegime.Periodic, null, .5f,
                    new(1, 0, true), new(true, true, true)), 0, 0,
                    NoOtherConstraints: true, NoBerthQueue: true, NoOtherZoneCandidates: true, NoNavigationTransitionWait: true);
                return OosStationTransportTripCalculator.Calculate(profile, src, dst, eligibleSource, regimes, scenario,
                    samplesPerCondition: 32, cancellationToken: cts.Token);
            }, cts.Token);
            if (generation != _generation) return;
            if (prediction.Status == OosTransportPhaseStatus.Unknown)
            {
                Result = "UNKNOWN：至少一个必要条件尚不支持，不能合并为平均时间。";
                Details = string.Join(Environment.NewLine, prediction.UnknownReasons);
                return;
            }
            var min = prediction.MinimumConditionalMeanSeconds!.Value; var max = prediction.MaximumConditionalMeanSeconds!.Value;
            Result = prediction.Conditions.Count == 1 ? $"条件平均单程：{min:F1} 秒" : $"各条件平均单程：{min:F1}–{max:F1} 秒";
            Details = "装货完成 → 目的站卸货完成；区间为条件均值范围，不是单次上下界。\n" +
                $"离港订单条件：{departureOrder}；单架贸易无人机运量 4,000 m³。\n" +
                string.Join(Environment.NewLine, prediction.Conditions.Select(c =>
                    $"{c.SourceBerthId} / {(c.Refresh == OosTransportRefreshRegime.Periodic ? "周期刷新" : "连续刷新")}：{c.MeanSeconds:F1} 秒，抽样标准误 {c.StandardErrorSeconds:F1} 秒（{c.SampleCount} 次）"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == _generation) { Result = "UNKNOWN：当前输入不满足已验证模型。"; Details = ex.Message; }
        }
        finally
        {
            if (generation == _generation) { IsBusy = false; _cancellation = null; }
            cts.Dispose();
        }
    }

    private void RefreshBerths()
    {
        SourceBerths.Clear(); SourceBerths.Add("全部源泊位（分别计算）");
        foreach (var berth in Source?.TransportTopology?.Berths ?? [])
            if (berth.SupportsCapitalTwoPointTemplate) SourceBerths.Add(berth.SaveId);
        SourceBerth = SourceBerths[0];
    }
    private void OnShipChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShipComparisonViewModel.SelectedShip) or nameof(ShipComparisonViewModel.SelectedEngine)
            or nameof(ShipComparisonViewModel.SelectedThruster) or nameof(ShipComparisonViewModel.SelectedPilotingStars)) Invalidate();
    }
    private void Invalidate(string message = "输入已更新，请计算条件单程。")
    {
        _generation++; _cancellation?.Cancel(); _cancellation = null;
        IsBusy = false; Result = message; Details = "";
    }
    public void Dispose() { Invalidate(); _ship.PropertyChanged -= OnShipChanged; }
}
