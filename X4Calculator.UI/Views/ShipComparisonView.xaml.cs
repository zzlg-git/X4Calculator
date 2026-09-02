using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using X4Calculator.UI.ViewModels;

namespace X4Calculator.UI.Views;

public partial class ShipComparisonView : UserControl
{
    /// <summary>非数字字符（运输效率距离输入：仅允许整数，不含小数点）。</summary>
    private static readonly Regex NonDigitRegex = new("[^0-9]+", RegexOptions.Compiled);

    private ShipComparisonViewModel? _viewModel;

    public ShipComparisonView()
    {
        InitializeComponent();
        Loaded += (_, _) => { HookListBoxScroll(); BindViewModel(); };
        DataContextChanged += (_, _) => BindViewModel();
    }

    /// <summary>
    /// 订阅 ViewModel 的 IsSortExpanded 变化，动态调整左栏/排序栏列宽：
    /// 排序栏展开时左栏列宽折叠为 0（其余栏展开到实际可用宽度）、排序栏弹性展开；
    /// 折叠时左栏恢复 380、排序栏为 0（不占位）。
    /// 不使用 x:Reference/ElementName 绑定，避免 ColumnDefinition 上的循环依赖崩溃。
    /// </summary>
    private void BindViewModel()
    {
        if (_viewModel != null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = DataContext as ShipComparisonViewModel;
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdatePanelWidths();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShipComparisonViewModel.IsSortExpanded))
            UpdatePanelWidths();
    }

    private void UpdatePanelWidths()
    {
        bool expanded = _viewModel?.IsSortExpanded ?? false;
        // 列 0 = 左栏（舰船选择），列 3 = 排序栏（运输效率排序）
        MainGrid.ColumnDefinitions[0].Width = expanded ? new GridLength(0) : new GridLength(380);
        MainGrid.ColumnDefinitions[3].Width = expanded ? new GridLength(1.6, GridUnitType.Star) : new GridLength(0);
    }

    /// <summary>
    /// 距离输入框：回车=结束输入，自动跳到下一个输入控件（最底部的数字框不绑定此事件，回车不再跳转）。
    /// </summary>
    private void DistanceBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            (sender as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
    }

    /// <summary>
    /// 距离输入框：仅允许数字（整数），键盘输入时拦截非数字字符。
    /// </summary>
    private void NumericOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = NonDigitRegex.IsMatch(e.Text);
    }

    /// <summary>
    /// 距离输入框：粘贴时拦截含非数字字符的文本。
    /// </summary>
    private void NumericOnly_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(typeof(string)))
        {
            var text = (string)e.DataObject.GetData(typeof(string))!;
            if (NonDigitRegex.IsMatch(text))
                e.CancelCommand();
        }
        else
        {
            e.CancelCommand();
        }
    }

    private void HookListBoxScroll()
    {
        ShipListBox.PreviewMouseWheel += ShipListBox_PreviewMouseWheel;
    }

    private void ShipListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scrollViewer = FindScrollViewer(ShipListBox);
        if (scrollViewer == null) return;

        // 每次滚轮 tick（Delta=±120）只滚动 2 行，而不是直接按像素/原始值滚动
        int deltaLines = Math.Max(1, Math.Abs(e.Delta) / 120) * 2;
        scrollViewer.ScrollToVerticalOffset(
            scrollViewer.VerticalOffset - Math.Sign(e.Delta) * deltaLines);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject dep)
    {
        if (dep is ScrollViewer sv) return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(dep); i++)
        {
            var result = FindScrollViewer(VisualTreeHelper.GetChild(dep, i));
            if (result != null) return result;
        }
        return null;
    }
}
