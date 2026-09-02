using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using X4Calculator.Core.Data;
using X4Calculator.Core.Models;
using X4Calculator.UI.Services;
using X4Calculator.UI.ViewModels;

namespace X4Calculator.UI.Views;

/// <summary>
/// 星区地图视图：以游戏内星图显示坐标（轴向网格布局）绘制六边形扇区、星门连接，
/// 支持玩家/NPC 站点切换、NPC 站点高亮、滚轮缩放（围绕鼠标）、拖拽平移和双击复位。
///
/// 所有元素（含扇区名称）都绘制在同一个世界画布上，随视图一起缩放——如同一张图片。
/// </summary>
public partial class StarMapView : UserControl
{
    public enum NpcStationHighlight
    {
        None,
        BlackMarketTrader,
        KhaakStation,
        OwnerlessShip,
        DataVault,
        ErlkingDataVault
    }

    public static readonly DependencyProperty MapProperty = DependencyProperty.Register(
        nameof(Map), typeof(StarMapDB), typeof(StarMapView),
        new PropertyMetadata(null, OnMapPropertyChanged));

    public static readonly DependencyProperty TransportSourceProperty = DependencyProperty.Register(
        nameof(TransportSource), typeof(IStationTransportMapSource), typeof(StarMapView),
        new PropertyMetadata(null, OnTransportSourcePropertyChanged));

    public static readonly DependencyProperty PlacementSourceProperty = DependencyProperty.Register(
        nameof(PlacementSource), typeof(IStationMapPlacementSource), typeof(StarMapView),
        new PropertyMetadata(null, OnPlacementSourcePropertyChanged));

    private StarMapDB? _db;
    private readonly StationTransportMapRouteProjector _transportProjector = new();
    private readonly StarMapPointerGesture _pointerGesture = new(
        SystemParameters.MinimumHorizontalDragDistance,
        SystemParameters.MinimumVerticalDragDistance);
    private IReadOnlyList<StationTransportMapCorridor> _projectedTransportCorridors = [];
    private string? _selectedTransportStationId;
    private bool _transportRefreshPending;
    private readonly List<FrameworkElement> _fixedScreenStationIcons = [];
    private FrameworkElement? _placementPreviewIcon;
    private string? _placementPreviewIconKey;
    private Point _lastPointerPosition;
    private bool _hasPointerPosition;
    private bool _showNpcStations;
    private NpcStationHighlight _npcHighlight;

    // 视口变换：screen = world * _scale + (_offsetX, _offsetY)
    private double _scale = 1.0;
    private double _baseScale = 1.0;
    private double _offsetX;
    private double _offsetY;

    // 世界包围盒（已按 WorldScale 放大）
    private double _minX, _maxX, _minY, _maxY;

    // 基准半径（世界单位，≈cluster 半径），用于门/连线尺寸
    private double _baseRadius = 1.0;

    // 平移状态
    private Point _lastMouse;

    // 是否已完成初次适配（FitView 成功）。Tab 未激活时尺寸为 0 会失败，需在激活后补适配。
    private bool _fitted;

    // 星图显示坐标（单位网格）放大为可绘制世界坐标的倍数（与游戏星图同数量级）
    private const double WorldScale = 10000.0;

    // 扇区名称统一字号（世界单位，随视图整体缩放；所有扇区一致）
    private const double SectorLabelFontSize = 1400.0;

    // 空间站图标保持固定屏幕尺寸；否则全图适配时会随世界画布缩成几乎不可见的像素点。
    private const double StationIconScreenSize = 22.0;

    // 星门/轨道加速器点统一半径（世界单位）：两者功能相同、视觉一致，且与扇区大小无关（避免连线两端一大一小）
    private double UniformGateRadius => _baseRadius * 0.05;

    private const double MinScaleFactor = 0.15;
    private const double MaxScaleFactor = 150.0;

    public StarMapView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

        RootGrid.MouseWheel += OnMouseWheel;
        RootGrid.MouseLeftButtonDown += OnMouseLeftButtonDown;
        RootGrid.MouseMove += OnMouseMove;
        RootGrid.MouseLeftButtonUp += OnMouseLeftButtonUp;
        RootGrid.LostMouseCapture += OnLostMouseCapture;
        RootGrid.MouseLeave += (_, _) =>
        {
            HideTransportHover();
            _hasPointerPosition = false;
            PlacementPreviewCanvas.Visibility = Visibility.Collapsed;
        };
        SizeChanged += OnSizeChanged;
        UpdateStationModeControls();
    }

    public StarMapDB? Map
    {
        get => (StarMapDB?)GetValue(MapProperty);
        set => SetValue(MapProperty, value);
    }

    public IStationTransportMapSource? TransportSource
    {
        get => (IStationTransportMapSource?)GetValue(TransportSourceProperty);
        set => SetValue(TransportSourceProperty, value);
    }

    public IStationMapPlacementSource? PlacementSource
    {
        get => (IStationMapPlacementSource?)GetValue(PlacementSourceProperty);
        set => SetValue(PlacementSourceProperty, value);
    }

    public string? SelectedTransportStationId => _selectedTransportStationId;
    public bool IsNpcStationMode => _showNpcStations;
    public NpcStationHighlight SelectedNpcHighlight => _npcHighlight;

    private static void OnMapPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (StarMapView)d;
        view.AttachMap(e.NewValue as StarMapDB ?? view.DataContext as StarMapDB);
    }

    private static void OnTransportSourcePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (StarMapView)d;
        if (e.OldValue is IStationTransportMapSource oldSource)
            WeakEventManager<IStationTransportMapSource, EventArgs>.RemoveHandler(
                oldSource, nameof(IStationTransportMapSource.TransportMapStateChanged), view.OnTransportMapStateChanged);
        if (e.NewValue is IStationTransportMapSource newSource)
            WeakEventManager<IStationTransportMapSource, EventArgs>.AddHandler(
                newSource, nameof(IStationTransportMapSource.TransportMapStateChanged), view.OnTransportMapStateChanged);
        view.QueueTransportRefresh();
    }

    private static void OnPlacementSourcePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (StarMapView)d;
        if (e.OldValue is IStationMapPlacementSource oldSource)
            WeakEventManager<IStationMapPlacementSource, EventArgs>.RemoveHandler(
                oldSource, nameof(IStationMapPlacementSource.PlacementStateChanged), view.OnPlacementStateChanged);
        if (e.NewValue is IStationMapPlacementSource newSource)
            WeakEventManager<IStationMapPlacementSource, EventArgs>.AddHandler(
                newSource, nameof(IStationMapPlacementSource.PlacementStateChanged), view.OnPlacementStateChanged);
        view.RefreshPlacementState();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (Map == null) AttachMap(e.NewValue as StarMapDB);
    }

    private void AttachMap(StarMapDB? map)
    {
        if (ReferenceEquals(_db, map))
        {
            TryInitialize();
            return;
        }
        if (_db != null)
            WeakEventManager<StarMapDB, EventArgs>.RemoveHandler(
                _db, nameof(StarMapDB.PlayerStationsChanged), OnPlayerStationsChanged);
        _db = map;
        if (_db != null)
            WeakEventManager<StarMapDB, EventArgs>.AddHandler(
                _db, nameof(StarMapDB.PlayerStationsChanged), OnPlayerStationsChanged);
        _fitted = false;
        if (_db == null)
        {
            WorldCanvas.Children.Clear();
            _fixedScreenStationIcons.Clear();
            ClearTransportSelection();
            return;
        }
        BuildMap();
        FitView();
    }

    private void OnPlayerStationsChanged(object? sender, EventArgs e)
    {
        if (_db == null) return;
        if (_selectedTransportStationId != null && FindTransportStation(_selectedTransportStationId) == null)
            _selectedTransportStationId = null;
        BuildMap();
        QueueTransportRefresh();
    }

    private void OnPlacementStateChanged(object? sender, EventArgs e)
    {
        if (PlacementSource?.IsPlacementActive == true && _showNpcStations)
        {
            _showNpcStations = false;
            UpdateStationModeControls();
        }
        if (_selectedTransportStationId != null && FindTransportStation(_selectedTransportStationId) == null)
            _selectedTransportStationId = null;
        RefreshPlacementState();
    }

    private void RefreshPlacementState()
    {
        if (PlacementSource?.IsPlacementActive == true && _showNpcStations)
            _showNpcStations = false;
        UpdateStationModeControls();
        if (_db != null) BuildMap();
        UpdatePlacementPreview(_hasPointerPosition ? _lastPointerPosition : null);
        UpdateHintText();
    }

    /// <summary>
    /// 当 DataContext 变为已加载的 <see cref="StarMapDB"/> 时初始化绘制。
    /// 若数据已加载但初次适配未完成（如当时 Tab 未激活导致尺寸为 0），在尺寸就绪后补适配。
    /// </summary>
    private void TryInitialize()
    {
        var db = Map ?? DataContext as StarMapDB;
        if (db == null || db.Clusters.Count == 0) return;

        if (_db != db)
        {
            AttachMap(db);
        }
        else if (!_fitted)
        {
            FitView();
        }
    }

    /// <summary>
    /// 视口尺寸变化时重新适配整张星图。这里只更新画布变换，不重新创建地图元素。
    /// </summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var db = Map ?? DataContext as StarMapDB;
        if (db == null || db.Clusters.Count == 0) return;

        if (_db != db)
            AttachMap(db);
        else
            FitView();
    }

    // ─── 绘制 ────────────────────────────────────────────────────

    private void BuildMap()
    {
        if (_db == null) return;
        WorldCanvas.Children.Clear();
        _fixedScreenStationIcons.Clear();

        ComputeBounds();
        _baseRadius = _db.Sectors.Values.Count > 0
            ? _db.Sectors.Values.Max(s => s.DisplayRadius) * WorldScale
            : 1.0;

        // 1. 多扇区星区的大六边形轮廓（最底层，包裹全部扇区）
        DrawClusterOutlines();

        // 2. 跨星区星门连线
        DrawGateLinks();

        // 3. 超级高速路（线 + 入口大点 + 出口小点）
        DrawSectorLinks();

        // 4. 扇区六边形
        foreach (var sector in _db.Sectors.Values.OrderBy(s => s.Id))
        {
            DrawSectorPolygon(sector);
        }

        // 5. 星门/加速器圆点
        foreach (var gate in _db.Gates.Values)
        {
            DrawGate(gate);
        }

        // 6. 玩家与 NPC 空间站严格按模式互斥显示。
        if (_showNpcStations)
        {
            foreach (var station in _db.NpcStations.Where(station => station.HasValidProjection))
                DrawNpcStation(station);

            if (GetSelectedMapObjectKind() is { } selectedKind)
            {
                foreach (var mapObject in _db.SaveMapObjects.Where(mapObject =>
                             mapObject.HasValidProjection && mapObject.Kind == selectedKind))
                    DrawSaveMapObject(mapObject);
            }
        }
        else
        {
            foreach (var station in _db.PlayerStations)
                DrawStation(station, "starmap.station");

            // 已放置计划站保持独立覆盖层，但与导入站一样支持运输链路点击选择。
            foreach (var station in PlacementSource?.PlacedPlannedStations ?? [])
                DrawStation(station, "starmap.planned-station");
        }

        // 7. 扇区名称（最顶层，确保不被六边形/连线/点覆盖）
        foreach (var sector in _db.Sectors.Values.OrderBy(s => s.Id))
        {
            DrawSectorLabel(sector);
        }

        RebuildTransportProjection();
    }

    private void OnTransportMapStateChanged(object? sender, EventArgs e) => QueueTransportRefresh();

    private void QueueTransportRefresh()
    {
        if (_transportRefreshPending) return;
        _transportRefreshPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _transportRefreshPending = false;
            RebuildTransportProjection();
        });
    }

    private void RebuildTransportProjection()
    {
        HideTransportHover();
        if (_showNpcStations || _db == null || TransportSource == null || string.IsNullOrWhiteSpace(_selectedTransportStationId) ||
            TransportSource.IsTransportLoading)
        {
            _projectedTransportCorridors = [];
            RefreshTransportScreenGeometry();
            UpdateHintText();
            return;
        }

        var selectedStation = FindTransportStation(_selectedTransportStationId);
        if (selectedStation == null)
        {
            ClearTransportSelection();
            return;
        }

        _projectedTransportCorridors = _transportProjector.Project(
            _db,
            selectedStation,
            TransportSource.SelectedTransportRoutes.Select(item =>
                new StationTransportMapRouteInput(item.Route, item.WareName)),
            PlacementSource?.PlacedPlannedStations);
        RefreshTransportScreenGeometry();
        UpdateHintText();
    }

    private void RefreshTransportScreenGeometry()
    {
        var corridors = new List<TransportFlowCorridor>();
        var hitSegments = new List<TransportFlowHitSegment>();
        var corridorIndex = 0;
        foreach (var corridor in _projectedTransportCorridors)
        {
            var screenFlows = corridor.Flows.Select(flow =>
            {
                var points = flow.Points
                    .Select(ToScreenPoint)
                    .ToArray();
                if (flow.Direction == StationTransportMapFlowDirection.Incoming)
                    Array.Reverse(points);
                return (flow.Direction, Points: points, Cargo: flow.Wares
                    .Select(ware => new TransportFlowCargo(ware.WareId, ware.Name)).ToArray());
            }).ToArray();
            foreach (var flowGroup in screenFlows.GroupBy(flow => CreateScreenGeometryKey(flow.Points)))
            {
                var outgoingCargo = flowGroup
                    .Where(flow => flow.Direction == StationTransportMapFlowDirection.Outgoing)
                    .SelectMany(flow => flow.Cargo)
                    .GroupBy(item => item.WareId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()).ToArray();
                var incomingCargo = flowGroup
                    .Where(flow => flow.Direction == StationTransportMapFlowDirection.Incoming)
                    .SelectMany(flow => flow.Cargo)
                    .GroupBy(item => item.WareId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()).ToArray();
                corridors.Add(new TransportFlowCorridor(
                    $"{corridor.OtherStationId}.{corridorIndex++}",
                    flowGroup.First().Points,
                    outgoingCargo,
                    incomingCargo));
            }

            foreach (var segment in corridor.Segments)
            {
                hitSegments.Add(new TransportFlowHitSegment(
                    ToScreenPoint(segment.Start),
                    ToScreenPoint(segment.End),
                    segment.OutgoingWares.Select(ware => new TransportFlowCargo(ware.WareId, ware.Name)).ToArray(),
                    segment.IncomingWares.Select(ware => new TransportFlowCargo(ware.WareId, ware.Name)).ToArray()));
            }
        }

        TransportOverlay.SetCorridors(corridors);
        TransportOverlay.SetHitSegments(hitSegments);
    }

    private static string CreateScreenGeometryKey(IEnumerable<Point> points) => string.Join(";",
        points.Select(point => $"{BitConverter.DoubleToInt64Bits(point.X):X16},{BitConverter.DoubleToInt64Bits(point.Y):X16}"));

    private Point ToScreenPoint(StationTransportMapPoint point) => new(
        point.X * WorldScale * _scale + _offsetX,
        point.Y * WorldScale * _scale + _offsetY);

    private void SelectTransportStation(string stationId)
    {
        if (_db == null || FindTransportStation(stationId) == null)
            return;
        _selectedTransportStationId = stationId;
        RebuildTransportProjection();
    }

    private Station? FindTransportStation(string stationId)
    {
        if (_db == null) return null;
        return _db.PlayerStations
            .Concat(PlacementSource?.PlacedPlannedStations ?? [])
            .FirstOrDefault(station => station.Id.Equals(stationId, StringComparison.OrdinalIgnoreCase));
    }

    private void ClearTransportSelection()
    {
        _selectedTransportStationId = null;
        _projectedTransportCorridors = [];
        TransportOverlay.SetCorridors(null);
        TransportOverlay.SetHitSegments(null);
        HideTransportHover();
        UpdateHintText();
    }

    private void UpdateHintText()
    {
        HintText.Text = PlacementSource?.IsPlacementActive == true
            ? "移动光标选择位置 · 拖拽平移 · 滚轮缩放 · 单击放置"
            : TransportSource?.IsTransportLoading == true &&
                        !string.IsNullOrWhiteSpace(_selectedTransportStationId)
            ? "正在后台计算运输链路；完成后会自动显示"
            : _showNpcStations
            ? "NPC 空间站显示 · 滚轮缩放 · 拖拽平移 · 双击复位"
            : "点击空间站查看运输 · 滚轮缩放 · 拖拽平移 · 双击复位";
    }

    private void DrawStation(Station station, string automationPrefix)
    {
        var icon = CreateStationIcon(station.IconKey);
        icon.Tag = station;

        System.Windows.Automation.AutomationProperties.SetAutomationId(icon, $"{automationPrefix}.{station.Id}");
        icon.ToolTip = CreateStationNameToolTip(icon, station);
        ToolTipService.SetInitialShowDelay(icon, 150);
        ToolTipService.SetBetweenShowDelay(icon, 0);
        ToolTipService.SetShowDuration(icon, 30000);
        Panel.SetZIndex(icon, 20);
        Canvas.SetLeft(icon, station.DisplayX * WorldScale - StationIconScreenSize / 2);
        Canvas.SetTop(icon, station.DisplayY * WorldScale - StationIconScreenSize / 2);
        WorldCanvas.Children.Add(icon);
        _fixedScreenStationIcons.Add(icon);
        ApplyStationIconScale(icon);
    }

    private void DrawNpcStation(NpcStation station)
    {
        if (_db == null) return;
        var normalTint = ParseColor(_db.ResolveFactionColorHex(station.Owner));
        var isHighlightTarget = _npcHighlight switch
        {
            NpcStationHighlight.BlackMarketTrader => station.HasBlackMarketTrader,
            NpcStationHighlight.KhaakStation => station.IsKhaakHighlightTarget,
            _ => false
        };
        var shouldLowlight = _npcHighlight switch
        {
            NpcStationHighlight.BlackMarketTrader or
                NpcStationHighlight.KhaakStation => !isHighlightTarget,
            NpcStationHighlight.OwnerlessShip or
                NpcStationHighlight.DataVault or
                NpcStationHighlight.ErlkingDataVault => true,
            _ => false
        };
        var tint = shouldLowlight
            ? Lerp(normalTint, Color.FromRgb(64, 64, 64), 0.7)
            : normalTint;
        var icon = CreateStationIcon(station.IconKey, tint);
        icon.Tag = station;
        icon.Opacity = 1;
        System.Windows.Automation.AutomationProperties.SetAutomationId(icon, $"starmap.npc-station.{station.Id}");
        icon.ToolTip = CreateNpcStationToolTip(icon, station);
        ToolTipService.SetInitialShowDelay(icon, 150);
        ToolTipService.SetBetweenShowDelay(icon, 0);
        ToolTipService.SetShowDuration(icon, 30000);
        // 高亮目标必须位于低亮 NPC 站之上，避免同坐标的武器平台遮住巢穴/虫巢。
        Panel.SetZIndex(icon, isHighlightTarget ? 22 : 20);
        Canvas.SetLeft(icon, station.DisplayX * WorldScale - StationIconScreenSize / 2);
        Canvas.SetTop(icon, station.DisplayY * WorldScale - StationIconScreenSize / 2);
        WorldCanvas.Children.Add(icon);
        _fixedScreenStationIcons.Add(icon);
        ApplyStationIconScale(icon);
    }

    private void DrawSaveMapObject(SaveMapObject mapObject)
    {
        FrameworkElement icon;
        if (mapObject.Kind == SaveMapObjectKind.OwnerlessShip)
        {
            icon = new Polygon
            {
                Points = new PointCollection
                {
                    new(StationIconScreenSize / 2, 1),
                    new(StationIconScreenSize - 2, StationIconScreenSize - 2),
                    new(StationIconScreenSize / 2, StationIconScreenSize * 0.72),
                    new(2, StationIconScreenSize - 2)
                },
                Width = StationIconScreenSize,
                Height = StationIconScreenSize,
                Fill = (Brush)FindResource("TextPrimaryBrush"),
                Stroke = Brushes.Black,
                StrokeThickness = 1,
                StrokeLineJoin = PenLineJoin.Round,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
        }
        else
        {
            var tint = mapObject.Kind == SaveMapObjectKind.DataVault
                ? ((SolidColorBrush)FindResource("AccentBlueBrush")).Color
                : ((SolidColorBrush)FindResource("AccentYellowBrush")).Color;
            icon = CreateStationIcon("mapob_vault_closed", tint);
        }

        icon.Tag = mapObject;
        System.Windows.Automation.AutomationProperties.SetAutomationId(
            icon, $"starmap.save-object.{GetMapObjectAutomationKind(mapObject.Kind)}.{mapObject.Id}");
        icon.ToolTip = CreateSaveMapObjectToolTip(icon, mapObject);
        ToolTipService.SetInitialShowDelay(icon, 150);
        ToolTipService.SetBetweenShowDelay(icon, 0);
        ToolTipService.SetShowDuration(icon, 30000);
        Panel.SetZIndex(icon, 21);
        Canvas.SetLeft(icon, mapObject.DisplayX * WorldScale - StationIconScreenSize / 2);
        Canvas.SetTop(icon, mapObject.DisplayY * WorldScale - StationIconScreenSize / 2);
        WorldCanvas.Children.Add(icon);
        _fixedScreenStationIcons.Add(icon);
        ApplyStationIconScale(icon);
    }

    private static FrameworkElement CreateStationIcon(string iconKey, Color? tint = null)
    {
        var bitmap = tint is Color color
            ? StationIconResources.Get(iconKey, color)
            : StationIconResources.Get(iconKey);
        if (bitmap != null)
        {
            return new Image
            {
                Source = bitmap,
                Width = StationIconScreenSize,
                Height = StationIconScreenSize,
                Stretch = Stretch.Uniform,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
        }

        return new Polygon
        {
            Points = new PointCollection
            {
                new(StationIconScreenSize / 2, 0), new(StationIconScreenSize, StationIconScreenSize / 2),
                new(StationIconScreenSize / 2, StationIconScreenSize), new(0, StationIconScreenSize / 2)
            },
            Width = StationIconScreenSize,
            Height = StationIconScreenSize,
            Fill = new SolidColorBrush(tint ?? Color.FromRgb(77, 255, 77)),
            Stroke = Brushes.Black,
            StrokeThickness = 1,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
    }

    private void UpdatePlacementPreview(Point? pointer)
    {
        var station = PlacementSource?.IsPlacementActive == true
            ? PlacementSource.PendingPlacementStation
            : null;
        if (station == null || pointer == null)
        {
            PlacementPreviewCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        if (_placementPreviewIcon == null ||
            !string.Equals(_placementPreviewIconKey, station.IconKey, StringComparison.OrdinalIgnoreCase))
        {
            PlacementPreviewCanvas.Children.Clear();
            _placementPreviewIcon = CreateStationIcon(station.IconKey);
            _placementPreviewIcon.Opacity = 0.82;
            System.Windows.Automation.AutomationProperties.SetAutomationId(
                _placementPreviewIcon, "starmap.placement-preview");
            PlacementPreviewCanvas.Children.Add(_placementPreviewIcon);
            _placementPreviewIconKey = station.IconKey;
        }

        Canvas.SetLeft(_placementPreviewIcon, pointer.Value.X - StationIconScreenSize / 2);
        Canvas.SetTop(_placementPreviewIcon, pointer.Value.Y - StationIconScreenSize / 2);
        PlacementPreviewCanvas.Visibility = Visibility.Visible;
    }

    private ToolTip CreateStationNameToolTip(FrameworkElement icon, Station station)
    {
        var label = new TextBlock
        {
            Text = station.Name,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 280
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(
            label, $"starmap.station-label.{station.Id}");

        return new ToolTip
        {
            Content = new Border
            {
                Background = (Brush)FindResource("BgSurfaceBrush"),
                BorderBrush = (Brush)FindResource("Surface1Brush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 5, 8, 5),
                Child = label
            },
            Placement = PlacementMode.Right,
            PlacementTarget = icon,
            HorizontalOffset = 8,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0)
        };
    }

    private ToolTip CreateNpcStationToolTip(FrameworkElement icon, NpcStation station)
    {
        var details = new List<string> { station.Name };
        if (!string.IsNullOrWhiteSpace(station.Code)) details[0] += $" ({station.Code})";
        if (station.HasBlackMarketTrader)
        {
            var state = station.IsBlackMarketTraderUnlocked ? "已解锁" : "未解锁";
            details.Add($"黑市商人：{state}");
        }
        var label = new TextBlock
        {
            Text = string.Join(Environment.NewLine, details),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 280
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(
            label, $"starmap.npc-station-label.{station.Id}");
        return new ToolTip
        {
            Content = new Border
            {
                Background = (Brush)FindResource("BgSurfaceBrush"),
                BorderBrush = (Brush)FindResource("Surface1Brush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 5, 8, 5),
                Child = label
            },
            Placement = PlacementMode.Right,
            PlacementTarget = icon,
            HorizontalOffset = 8,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0)
        };
    }

    private ToolTip CreateSaveMapObjectToolTip(FrameworkElement icon, SaveMapObject mapObject)
    {
        var title = mapObject.Kind switch
        {
            SaveMapObjectKind.OwnerlessShip => $"无主舰船：{mapObject.Name}",
            SaveMapObjectKind.DataVault => "数据保险库",
            SaveMapObjectKind.ErlkingDataVault => "妖王数据保险库",
            _ => mapObject.Name
        };
        if (!string.IsNullOrWhiteSpace(mapObject.Code)) title += $" ({mapObject.Code})";
        var details = new List<string> { title };
        if (mapObject.Kind == SaveMapObjectKind.OwnerlessShip &&
            mapObject.ObjectClass.StartsWith("ship_", StringComparison.OrdinalIgnoreCase))
            details.Add($"尺寸：{mapObject.ObjectClass[5..].ToUpperInvariant()}");

        var label = new TextBlock
        {
            Text = string.Join(Environment.NewLine, details),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 280
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(
            label, $"starmap.save-object-label.{GetMapObjectAutomationKind(mapObject.Kind)}.{mapObject.Id}");
        return new ToolTip
        {
            Content = new Border
            {
                Background = (Brush)FindResource("BgSurfaceBrush"),
                BorderBrush = (Brush)FindResource("Surface1Brush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 5, 8, 5),
                Child = label
            },
            Placement = PlacementMode.Right,
            PlacementTarget = icon,
            HorizontalOffset = 8,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0)
        };
    }

    private SaveMapObjectKind? GetSelectedMapObjectKind() => _npcHighlight switch
    {
        NpcStationHighlight.OwnerlessShip => SaveMapObjectKind.OwnerlessShip,
        NpcStationHighlight.DataVault => SaveMapObjectKind.DataVault,
        NpcStationHighlight.ErlkingDataVault => SaveMapObjectKind.ErlkingDataVault,
        _ => null
    };

    private static string GetMapObjectAutomationKind(SaveMapObjectKind kind) => kind switch
    {
        SaveMapObjectKind.OwnerlessShip => "ownerless-ship",
        SaveMapObjectKind.DataVault => "data-vault",
        SaveMapObjectKind.ErlkingDataVault => "erlking-data-vault",
        _ => "unknown"
    };

    private void ApplyStationIconScale(FrameworkElement icon)
    {
        var inverseScale = 1.0 / Math.Max(_scale, 1e-9);
        icon.RenderTransform = new ScaleTransform(inverseScale, inverseScale);
    }

    private void UpdateStationIconScales()
    {
        foreach (var icon in _fixedScreenStationIcons)
            ApplyStationIconScale(icon);
    }

    private void ComputeBounds()
    {
        if (_db == null) return;
        _minX = double.MaxValue; _maxX = double.MinValue;
        _minY = double.MaxValue; _maxY = double.MinValue;
        foreach (var s in _db.Sectors.Values)
        {
            double x = s.DisplayX * WorldScale;
            double y = s.DisplayY * WorldScale;
            _minX = Math.Min(_minX, x);
            _maxX = Math.Max(_maxX, x);
            _minY = Math.Min(_minY, y);
            _maxY = Math.Max(_maxY, y);
        }
        if (double.IsInfinity(_minX)) { _minX = -250000; _maxX = 250000; _minY = -250000; _maxY = 250000; }
    }

    /// <summary>绘制多扇区星区的大六边形轮廓（只描边，包裹全部扇区）。单扇区星区不需要。</summary>
    private void DrawClusterOutlines()
    {
        if (_db == null) return;
        foreach (var cluster in _db.Clusters.Values)
        {
            if (cluster.SectorIds.Count <= 1) continue;
            var brush = HexToBrush(cluster.OwnerColor);
            double cx = cluster.DisplayX * WorldScale;
            double cy = cluster.DisplayY * WorldScale;
            double radius = cluster.DisplayRadius * WorldScale;

            var points = new PointCollection();
            for (int i = 0; i < 6; i++)
            {
                double rad = (60 * i) * Math.PI / 180.0;
                points.Add(new Point(cx + radius * Math.Cos(rad), cy + radius * Math.Sin(rad)));
            }

            WorldCanvas.Children.Add(new Polygon
            {
                Points = points,
                Fill = new SolidColorBrush(Color.FromArgb(10, 255, 255, 255)),
                Stroke = brush,
                StrokeThickness = Math.Max(80, radius * 0.035),
                StrokeLineJoin = PenLineJoin.Round
            });
        }
    }

    /// <summary>绘制超级高速路：入口（大点，与星门同大小）→ 出口（小点）连线。</summary>
    private void DrawSectorLinks()
    {
        if (_db == null) return;
        var brush = new SolidColorBrush(Color.FromArgb(220, 29, 78, 216)); // #1d4ed8 蓝
        double thickness = Math.Max(60, _baseRadius * 0.035);

        foreach (var link in _db.SectorLinks)
        {
            double ax = link.DisplayFromX * WorldScale;
            double ay = link.DisplayFromY * WorldScale;
            double bx = link.DisplayToX * WorldScale;
            double by = link.DisplayToY * WorldScale;

            // 双向车道：垂直方向错开，避免两条反向线完全重叠
            if (link.LaneCount > 1)
            {
                double dx = bx - ax, dy = by - ay;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len > 1e-6)
                {
                    double px = -dy / len, py = dx / len;
                    double off = (link.LaneIndex - (link.LaneCount - 1) / 2.0) * _baseRadius * 0.10;
                    ax += px * off; ay += py * off;
                    bx += px * off; by += py * off;
                }
            }

            WorldCanvas.Children.Add(new Line
            {
                X1 = ax, Y1 = ay, X2 = bx, Y2 = by,
                Stroke = brush,
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });

            // 入口大点（与星门点大小一致）在 from 端，出口小点（约一半）在 to 端
            double gateR = SectorGateRadius(link.SectorAId);
            DrawDot(ax, ay, gateR, brush);
            DrawDot(bx, by, Math.Max(gateR * 0.5, 1), brush);
        }
    }

    private void DrawSectorPolygon(SectorInfo sector)
    {
        var brush = HexToBrush(sector.OwnerColor);
        var c = brush.Color;
        double cx = sector.DisplayX * WorldScale;
        double cy = sector.DisplayY * WorldScale;
        double radius = sector.DisplayRadius * WorldScale;

        var points = new PointCollection();
        for (int i = 0; i < 6; i++)
        {
            // flat-top 六边形：顶点在左右两侧、顶边朝上（0° 起，与游戏星图一致）
            double rad = (60 * i) * Math.PI / 180.0;
            points.Add(new Point(cx + radius * Math.Cos(rad), cy + radius * Math.Sin(rad)));
        }

        WorldCanvas.Children.Add(new Polygon
        {
            Points = points,
            Fill = new SolidColorBrush(Color.FromArgb(36, c.R, c.G, c.B)),
            Stroke = brush,
            StrokeThickness = Math.Max(80, radius * 0.05),
            StrokeLineJoin = PenLineJoin.Round
        });
    }

    /// <summary>
    /// 扇区名称：统一字号，水平居中，顶部边下方对齐（位于六边形顶部）。
    /// 长名称允许略微超出六边形范围。名称最后绘制，保证在最顶层不被覆盖。
    /// </summary>
    private void DrawSectorLabel(SectorInfo sector)
    {
        double cx = sector.DisplayX * WorldScale;
        double cy = sector.DisplayY * WorldScale;
        double radius = sector.DisplayRadius * WorldScale;

        var label = new TextBlock
        {
            Text = sector.Name,
            FontSize = SectorLabelFontSize,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(150, 17, 17, 27))
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textW = label.DesiredSize.Width;

        // flat-top 六边形顶部边 y = cy - r·√3/2；名称顶部对齐到顶部边内侧
        double topEdgeY = cy - radius * 0.8660254037844386;
        Canvas.SetLeft(label, cx - textW / 2);
        Canvas.SetTop(label, topEdgeY + radius * 0.02);
        WorldCanvas.Children.Add(label);
    }

    private void DrawGate(GateInfo gate)
    {
        // 轨道加速器与星门功能相同，统一为白色实心点（不按类型分色）
        var brush = (Brush)FindResource("TextPrimaryBrush");
        DrawDot(gate.DisplayX * WorldScale, gate.DisplayY * WorldScale, UniformGateRadius, brush);
    }

    /// <summary>星门/加速器统一点半径（世界单位），超级高速路入口大点与其一致。</summary>
    private double SectorGateRadius(string sectorId)
    {
        _ = sectorId;
        return UniformGateRadius;
    }

    /// <summary>在世界画布中心 (cx, cy) 绘制半径为 r 的圆点。</summary>
    private void DrawDot(double cx, double cy, double r, Brush brush)
    {
        var dot = new Ellipse
        {
            Width = r * 2,
            Height = r * 2,
            Fill = brush,
            Stroke = new SolidColorBrush(Color.FromArgb(150, 11, 11, 18)),
            StrokeThickness = Math.Max(50, r * 0.18)
        };
        Canvas.SetLeft(dot, cx - r);
        Canvas.SetTop(dot, cy - r);
        WorldCanvas.Children.Add(dot);
    }

    private void DrawGateLinks()
    {
        if (_db == null) return;
        var gates = _db.Gates.Values.ToList();
        var drawn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lineBrush = new SolidColorBrush(Color.FromArgb(150, 205, 214, 244));
        double thickness = Math.Max(100, _baseRadius * 0.05);

        foreach (var gate in gates)
        {
            if (string.IsNullOrEmpty(gate.TargetClusterId)) continue;
            GateInfo? target = null;
            double best = double.MaxValue;
            foreach (var other in gates)
            {
                if (!string.Equals(other.ClusterId, gate.TargetClusterId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(other.TargetClusterId, gate.ClusterId, StringComparison.OrdinalIgnoreCase)) continue;
                double dx = gate.DisplayX - other.DisplayX;
                double dy = gate.DisplayY - other.DisplayY;
                var d = Math.Sqrt(dx * dx + dy * dy);
                if (d < best) { best = d; target = other; }
            }
            if (target == null) continue;

            var key = string.Compare(gate.Id, target.Id, StringComparison.OrdinalIgnoreCase) < 0
                ? $"{gate.Id}|{target.Id}"
                : $"{target.Id}|{gate.Id}";
            if (!drawn.Add(key)) continue;

            var line = new Line
            {
                X1 = gate.DisplayX * WorldScale,
                Y1 = gate.DisplayY * WorldScale,
                X2 = target.DisplayX * WorldScale,
                Y2 = target.DisplayY * WorldScale,
                Stroke = lineBrush,
                StrokeThickness = thickness,
                StrokeDashArray = new DoubleCollection { 2, 1.5 }
            };
            WorldCanvas.Children.Add(line);
        }
    }

    private static SolidColorBrush HexToBrush(string hex)
    {
        if (ColorConverter.ConvertFromString(hex) is Color c) return new SolidColorBrush(c);
        return Brushes.Gray;
    }

    private static Color ParseColor(string hex) =>
        ColorConverter.ConvertFromString(hex) is Color color ? color : Color.FromRgb(102, 102, 102);

    private static Color Lerp(Color source, Color target, double strength)
    {
        strength = Math.Clamp(strength, 0, 1);
        return Color.FromArgb(
            255,
            (byte)Math.Round(source.R + (target.R - source.R) * strength),
            (byte)Math.Round(source.G + (target.G - source.G) * strength),
            (byte)Math.Round(source.B + (target.B - source.B) * strength));
    }

    // ─── 视口变换 ────────────────────────────────────────────────

    private void ApplyTransform()
    {
        HideTransportHover();
        WorldCanvas.RenderTransform = new MatrixTransform(_scale, 0, 0, _scale, _offsetX, _offsetY);
        UpdateStationIconScales();
        RefreshTransportScreenGeometry();
        ZoomText.Text = $"{_scale / Math.Max(_baseScale, 1e-9) * 100:F0}%";
    }

    private void FitView()
    {
        if (ActualWidth < 10 || ActualHeight < 10)
        {
            _fitted = false; // 布局未完成（如 Tab 未激活），等待后续 SizeChanged 再适配
            return;
        }

        double worldW = _maxX - _minX;
        double worldH = _maxY - _minY;
        if (worldW <= 0 || worldH <= 0) return;

        double pad = 60;
        double scale = Math.Min((ActualWidth - pad * 2) / worldW, (ActualHeight - pad * 2) / worldH);
        scale = Math.Clamp(scale, 1e-6, double.MaxValue);

        _baseScale = scale;
        _scale = scale;
        double cx = (_minX + _maxX) / 2;
        double cy = (_minY + _maxY) / 2;
        _offsetX = ActualWidth / 2 - cx * scale;
        _offsetY = ActualHeight / 2 - cy * scale;
        _fitted = true;

        ApplyTransform();
    }

    private void ZoomAt(Point screen, double factor)
    {
        double newScale = Math.Clamp(_scale * factor, MinScaleFactor * _baseScale, MaxScaleFactor * _baseScale);
        factor = newScale / _scale;
        if (Math.Abs(factor - 1) < 1e-6) return;

        // 保持鼠标下的世界点不动
        double wx = (screen.X - _offsetX) / _scale;
        double wy = (screen.Y - _offsetY) / _scale;
        _scale = newScale;
        _offsetX = screen.X - wx * _scale;
        _offsetY = screen.Y - wy * _scale;
        ApplyTransform();
    }

    // ─── 交互 ────────────────────────────────────────────────────

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var pos = e.GetPosition(RootGrid);
        RememberPointer(pos);
        ZoomAt(pos, e.Delta > 0 ? 1.18 : 1 / 1.18);
        e.Handled = true;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && PlacementSource?.IsPlacementActive != true)
        {
            _pointerGesture.Cancel();
            FitView();
            e.Handled = true;
            return;
        }

        var position = e.GetPosition(RootGrid);
        RememberPointer(position);
        var stationId = PlacementSource?.IsPlacementActive == true
            ? null
            : FindStation(e.OriginalSource as DependencyObject)?.Id;
        _pointerGesture.Begin(position.X, position.Y, stationId);
        _lastMouse = position;
        RootGrid.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(RootGrid);
        RememberPointer(pos);
        if (_pointerGesture.IsActive)
        {
            if (_pointerGesture.Update(pos.X, pos.Y))
            {
                Mouse.OverrideCursor = Cursors.Hand;
                _offsetX += pos.X - _lastMouse.X;
                _offsetY += pos.Y - _lastMouse.Y;
                _lastMouse = pos;
                ApplyTransform();
                HideTransportHover();
            }
            e.Handled = true;
            return;
        }

        UpdateTransportHover(pos);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pointerGesture.IsActive) return;
        var position = e.GetPosition(RootGrid);
        var result = _pointerGesture.Complete(position.X, position.Y);
        RootGrid.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
        if (PlacementSource?.IsPlacementActive == true)
        {
            if (result.Action != StarMapPointerGestureAction.PreserveSelection)
                CompletePlacementAt(position);
            e.Handled = true;
            return;
        }
        if (result.Action == StarMapPointerGestureAction.SelectStation && result.StationId != null)
            SelectTransportStation(result.StationId);
        else if (result.Action == StarMapPointerGestureAction.ClearSelection &&
                 TransportOverlay.FindHit(position) == null)
            ClearTransportSelection();
        e.Handled = true;
    }

    private void RememberPointer(Point pointer)
    {
        _lastPointerPosition = pointer;
        _hasPointerPosition = true;
        UpdatePlacementPreview(pointer);
    }

    private void CompletePlacementAt(Point screenPoint)
    {
        if (_db == null || PlacementSource == null) return;

        var displayX = (screenPoint.X - _offsetX) / _scale / WorldScale;
        var displayY = (screenPoint.Y - _offsetY) / _scale / WorldScale;
        var sector = _db.Sectors.Values
            .Where(candidate => IsInsideFlatTopHex(candidate, displayX, displayY))
            .OrderBy(candidate =>
                (candidate.DisplayX - displayX) * (candidate.DisplayX - displayX) +
                (candidate.DisplayY - displayY) * (candidate.DisplayY - displayY))
            .ThenBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (sector != null &&
            _db.TryUnprojectSectorPosition(sector.Id, displayX, displayY, out var sectorPosition))
        {
            PlacementSource.CompletePlacement(sector, sectorPosition, displayX, displayY);
            return;
        }

        PlacementSource.RejectPlacement();
    }

    private static bool IsInsideFlatTopHex(SectorInfo sector, double x, double y)
    {
        var radius = sector.DisplayRadius;
        if (!double.IsFinite(radius) || radius <= 0) return false;
        var normalizedX = Math.Abs((x - sector.DisplayX) / radius);
        var normalizedY = Math.Abs((y - sector.DisplayY) / radius);
        var sqrt3 = Math.Sqrt(3.0);
        const double tolerance = 1e-9;
        return normalizedY <= sqrt3 / 2.0 + tolerance &&
               sqrt3 * normalizedX + normalizedY <= sqrt3 + tolerance;
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        _pointerGesture.Cancel();
        Mouse.OverrideCursor = null;
    }

    private static Station? FindStation(DependencyObject? source)
    {
        for (var current = source; current != null; current = GetParent(current))
        {
            if (current is FrameworkElement { Tag: Station station }) return station;
        }
        return null;
    }

    private static DependencyObject? GetParent(DependencyObject current) => current switch
    {
        Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(current),
        FrameworkContentElement content => content.Parent,
        _ => null
    };

    private void UpdateTransportHover(Point pointer)
    {
        var hit = TransportOverlay.FindHit(pointer);
        if (hit == null)
        {
            HideTransportHover();
            return;
        }

        ShowHoverBorder(
            OutgoingHoverBorder,
            OutgoingHoverText,
            hit.OutgoingCargo,
            pointer,
            upperRight: true);
        ShowHoverBorder(
            IncomingHoverBorder,
            IncomingHoverText,
            hit.IncomingCargo,
            pointer,
            upperRight: false);
    }

    private void ShowHoverBorder(
        Border border,
        TextBlock text,
        IReadOnlyList<TransportFlowCargo> cargo,
        Point pointer,
        bool upperRight)
    {
        if (cargo.Count == 0)
        {
            border.Visibility = Visibility.Collapsed;
            return;
        }

        text.Text = string.Join(Environment.NewLine, cargo.Select(item => item.WareName));
        border.Visibility = Visibility.Visible;
        border.Measure(new Size(Math.Max(0, ActualWidth - 20), Math.Max(0, ActualHeight - 20)));
        var width = border.DesiredSize.Width;
        var height = border.DesiredSize.Height;
        var left = upperRight ? pointer.X + 14 : pointer.X - width - 14;
        var top = upperRight ? pointer.Y - height - 14 : pointer.Y + 14;
        Canvas.SetLeft(border, Math.Clamp(left, 8, Math.Max(8, ActualWidth - width - 8)));
        Canvas.SetTop(border, Math.Clamp(top, 8, Math.Max(8, ActualHeight - height - 8)));
    }

    private void HideTransportHover()
    {
        OutgoingHoverBorder.Visibility = Visibility.Collapsed;
        IncomingHoverBorder.Visibility = Visibility.Collapsed;
    }

    private void OnStationModeToggleClick(object sender, RoutedEventArgs e)
    {
        if (PlacementSource?.IsPlacementActive == true) return;
        _showNpcStations = !_showNpcStations;
        ClearTransportSelection();
        UpdateStationModeControls();
        BuildMap();
    }

    private void OnNpcHighlightSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _npcHighlight = NpcHighlightComboBox.SelectedIndex switch
        {
            1 => NpcStationHighlight.BlackMarketTrader,
            2 => NpcStationHighlight.KhaakStation,
            3 => NpcStationHighlight.OwnerlessShip,
            4 => NpcStationHighlight.DataVault,
            5 => NpcStationHighlight.ErlkingDataVault,
            _ => NpcStationHighlight.None
        };
        if (_showNpcStations && _db != null) BuildMap();
    }

    private void UpdateStationModeControls()
    {
        if (StationModeToggleButton == null || NpcHighlightPanel == null) return;
        StationModeToggleButton.Content = _showNpcStations
            ? "切换为玩家站点显示"
            : "切换为NPC站点显示";
        StationModeToggleButton.IsEnabled = PlacementSource?.IsPlacementActive != true;
        NpcHighlightPanel.Visibility = _showNpcStations ? Visibility.Visible : Visibility.Collapsed;
        UpdateHintText();
    }
}
