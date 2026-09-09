using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using X4Calculator.UI.ViewModels;

namespace X4Calculator.UI.Views;

public partial class ProductionChainView : UserControl
{
    private readonly Dictionary<string, FrameworkElement> _nodeElements =
        new(StringComparer.OrdinalIgnoreCase);
    private ProductionChainViewModel? _viewModel;
    private bool _drawQueued;
    private string? _hoveredWareId;
    private FrameworkElement? _pressedNode;
    private Point _pressPoint;
    private Point _nodeOrigin;
    private bool _dragging;
    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    public static readonly DependencyProperty IsHighlightedProperty = DependencyProperty.RegisterAttached(
        "IsHighlighted", typeof(bool), typeof(ProductionChainView), new PropertyMetadata(false));

    public static bool GetIsHighlighted(DependencyObject element) => (bool)element.GetValue(IsHighlightedProperty);
    public static void SetIsHighlighted(DependencyObject element, bool value) => element.SetValue(IsHighlightedProperty, value);

    public ProductionChainView()
    {
        InitializeComponent();
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
            if (_pressedNode == null || Mouse.LeftButton != MouseButtonState.Pressed)
            {
                EndDrag();
                return;
            }
            _dragging = true;
            _pressedNode.Cursor = Cursors.SizeAll;
            // 提升每个条目容器的层级，使卡片能够跨越层级和行边界。
            for (DependencyObject child = _pressedNode; child != ChainColumnsControl;)
            {
                var parent = VisualTreeHelper.GetParent(child);
                if (parent == null) break;
                if (parent is Panel panel && child is UIElement element)
                {
                    foreach (UIElement sibling in panel.Children) Panel.SetZIndex(sibling, 0);
                    Panel.SetZIndex(element, 1);
                }
                child = parent;
            }
        };
        Unloaded += (_, _) => ResetInteraction();
        Loaded += (_, _) => BindViewModel();
        SizeChanged += (_, _) => QueueDrawConnections();
        DataContextChanged += (_, _) => BindViewModel();
        ChainGraphRoot.LayoutUpdated += (_, _) =>
        {
            if (_viewModel?.HasChain == true && ConnectionCanvas.Children.Count == 0)
                QueueDrawConnections();
        };
    }

    private void BindViewModel()
    {
        var nextViewModel = DataContext as ProductionChainViewModel;
        if (ReferenceEquals(_viewModel, nextViewModel))
        {
            QueueDrawConnections();
            return;
        }

        if (_viewModel != null)
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ResetInteraction();
        _viewModel = nextViewModel;
        if (_viewModel != null)
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _nodeElements.Clear();
        QueueDrawConnections();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProductionChainViewModel.ChainEdges) or nameof(ProductionChainViewModel.HasChain))
        {
            ResetInteraction();
            QueueDrawConnections();
        }
    }

    private void ChainNode_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is string wareId)
            _nodeElements[wareId] = element;
        QueueDrawConnections();
    }

    private void ChainNode_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is string wareId &&
            _nodeElements.TryGetValue(wareId, out var registered) && ReferenceEquals(registered, element))
        {
            if (ReferenceEquals(_pressedNode, element)) EndDrag();
            _nodeElements.Remove(wareId);
        }
        QueueDrawConnections();
    }

    private void ChainNode_SizeChanged(object sender, SizeChangedEventArgs e) => QueueDrawConnections();

    private void ChainNode_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_pressedNode != null) return;
        _hoveredWareId = (sender as FrameworkElement)?.Tag as string;
        QueueDrawConnections();
    }

    private void ChainNode_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_pressedNode != null) return;
        _hoveredWareId = null;
        QueueDrawConnections();
    }

    private void ChainNode_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        EndDrag();
        if (sender is not FrameworkElement node || !node.CaptureMouse()) return;
        _pressedNode = node;
        _pressPoint = e.GetPosition(ChainGraphRoot);
        _nodeOrigin = node.TranslatePoint(new Point(), ChainGraphRoot);
        _hoveredWareId = node.Tag as string;
        _holdTimer.Start();
        e.Handled = true;
    }

    private void ChainNode_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedNode == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }
        var delta = e.GetPosition(ChainGraphRoot) - _pressPoint;
        if (!_dragging)
        {
            // 长按完成前发生移动时取消本次手势。
            if (Math.Abs(delta.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(delta.Y) > SystemParameters.MinimumVerticalDragDistance)
                EndDrag();
            return;
        }
        var node = _pressedNode;
        var current = node.TranslatePoint(new Point(), ChainGraphRoot);
        var transform = node.RenderTransform as TranslateTransform;
        if (transform == null)
            node.RenderTransform = transform = new TranslateTransform();
        var x = Math.Clamp(_nodeOrigin.X + delta.X, 0, Math.Max(0, ChainGraphRoot.ActualWidth - node.ActualWidth));
        var y = Math.Clamp(_nodeOrigin.Y + delta.Y, 24, Math.Max(24, ChainGraphRoot.ActualHeight - node.ActualHeight - 6));
        transform.X += x - current.X;
        transform.Y += y - current.Y;
        QueueDrawConnections();
        e.Handled = true;
    }

    private void ChainNode_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressedNode == null) return;
        EndDrag();
        e.Handled = true;
    }

    private void ChainNode_LostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

    private void EndDrag()
    {
        _holdTimer.Stop();
        var node = _pressedNode;
        _pressedNode = null;
        _dragging = false;
        if (node == null) return;
        node.ClearValue(CursorProperty);
        if (node.IsMouseCaptured) node.ReleaseMouseCapture();
        _hoveredWareId = _nodeElements.Values.FirstOrDefault(element => element.IsMouseOver)?.Tag as string;
        QueueDrawConnections();
    }

    private void ResetInteraction()
    {
        EndDrag();
        _hoveredWareId = null;
        foreach (var node in _nodeElements.Values)
        {
            node.ClearValue(RenderTransformProperty);
            SetIsHighlighted(node, false);
        }
    }

    private void ChainScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0)
            QueueDrawConnections();
    }

    private void QueueDrawConnections()
    {
        if (_drawQueued || !IsLoaded) return;
        _drawQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _drawQueued = false;
            DrawConnections();
        });
    }

    private void DrawConnections()
    {
        ConnectionCanvas.Children.Clear();
        if (_viewModel == null || !_viewModel.HasChain) return;
        _nodeElements.Clear();
        CollectNodeElements(ChainGraphRoot);

        var highlightedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_hoveredWareId != null)
        {
            highlightedIds.Add(_hoveredWareId);
            foreach (var edge in _viewModel.ChainEdges.Where(edge =>
                         string.Equals(edge.TargetWareId, _hoveredWareId, StringComparison.OrdinalIgnoreCase)))
                highlightedIds.Add(edge.SourceWareId);
        }
        foreach (var (id, node) in _nodeElements)
            SetIsHighlighted(node, highlightedIds.Contains(id));

        foreach (var edge in _viewModel.ChainEdges)
        {
            if (!_nodeElements.TryGetValue(edge.SourceWareId, out var source) ||
                !_nodeElements.TryGetValue(edge.TargetWareId, out var target) ||
                source.ActualWidth <= 0 || target.ActualWidth <= 0)
            {
                continue;
            }

            var start = source.TranslatePoint(new Point(source.ActualWidth, source.ActualHeight / 2), ChainGraphRoot);
            var end = target.TranslatePoint(new Point(0, target.ActualHeight / 2), ChainGraphRoot);
            var controlOffset = Math.Max(20, (end.X - start.X) * 0.45);
            var geometry = new PathGeometry([
                new PathFigure(start,
                [
                    new BezierSegment(
                        new Point(start.X + controlOffset, start.Y),
                        new Point(end.X - controlOffset, end.Y),
                        end,
                        true)
                ], false)
            ]);

            var highlighted = string.Equals(edge.TargetWareId, _hoveredWareId, StringComparison.OrdinalIgnoreCase);
            ConnectionCanvas.Children.Add(new Path
            {
                Tag = edge,
                Data = geometry,
                Stroke = (Brush)FindResource(highlighted ? "AccentBlueBrush" : "GraphConnectorBrush"),
                StrokeThickness = highlighted ? 3 : 1.5
            });
            ConnectionCanvas.Children.Add(new Polygon
            {
                Tag = edge,
                Fill = (Brush)FindResource(highlighted ? "AccentBlueBrush" : "GraphArrowBrush"),
                Points = [end, new Point(end.X - 8, end.Y - 4), new Point(end.X - 8, end.Y + 4)]
            });
        }
    }

    private void CollectNodeElements(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement { Tag: string wareId } element)
                _nodeElements[wareId] = element;
            CollectNodeElements(child);
        }
    }
}

/// <summary>向桌面自动化公开的图形卡片，包含变换后的边界。</summary>
public sealed class ProductionChainCard : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
