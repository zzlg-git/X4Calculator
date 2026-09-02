using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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

    public ProductionChainView()
    {
        InitializeComponent();
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
            _nodeElements.Remove(wareId);
        }
        QueueDrawConnections();
    }

    private void ChainNode_SizeChanged(object sender, SizeChangedEventArgs e) => QueueDrawConnections();

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

            ConnectionCanvas.Children.Add(new Path
            {
                Data = geometry,
                Stroke = (Brush)FindResource("GraphConnectorBrush"),
                StrokeThickness = 1.5
            });
            ConnectionCanvas.Children.Add(new Polygon
            {
                Fill = (Brush)FindResource("GraphArrowBrush"),
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
