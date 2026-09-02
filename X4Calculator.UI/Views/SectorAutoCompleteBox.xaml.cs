using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace X4Calculator.UI.Views;

/// <summary>
/// 带自动补全的扇区名输入框。
/// - 支持英文和中文输入
/// - 用户开始输入时在下方弹出候选列表，按双语名称任一部分的前缀实时过滤
/// - 支持方向键选择、Enter 确认、Esc 关闭、失焦自动关闭
/// </summary>
public partial class SectorAutoCompleteBox : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource), typeof(IEnumerable), typeof(SectorAutoCompleteBox),
            new FrameworkPropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(SectorAutoCompleteBox),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnTextPropertyChanged));

    public static readonly DependencyProperty WatermarkProperty =
        DependencyProperty.Register(
            nameof(Watermark), typeof(string), typeof(SectorAutoCompleteBox),
            new FrameworkPropertyMetadata(string.Empty));

    /// <summary>内部输入框的稳定 UI 自动化标识。</summary>
    public static readonly DependencyProperty InputAutomationIdProperty =
        DependencyProperty.Register(
            nameof(InputAutomationId), typeof(string), typeof(SectorAutoCompleteBox),
            new FrameworkPropertyMetadata(string.Empty));

    /// <summary>候选列表的稳定 UI 自动化标识。</summary>
    public static readonly DependencyProperty SuggestionsAutomationIdProperty =
        DependencyProperty.Register(
            nameof(SuggestionsAutomationId), typeof(string), typeof(SectorAutoCompleteBox),
            new FrameworkPropertyMetadata(string.Empty));

    public SectorAutoCompleteBox()
    {
        InitializeComponent();
        UpdateWatermarkVisibility();
    }

    /// <summary>全部候选扇区名（IEnumerable&lt;string&gt;）。</summary>
    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>当前输入的文本（TwoWay，实时同步到源）。</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>空文本时的占位提示。</summary>
    public string Watermark
    {
        get => (string)GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    /// <summary>内部输入框的稳定 UI 自动化标识。</summary>
    public string InputAutomationId
    {
        get => (string)GetValue(InputAutomationIdProperty);
        set => SetValue(InputAutomationIdProperty, value);
    }

    /// <summary>候选列表的稳定 UI 自动化标识。</summary>
    public string SuggestionsAutomationId
    {
        get => (string)GetValue(SuggestionsAutomationIdProperty);
        set => SetValue(SuggestionsAutomationIdProperty, value);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((SectorAutoCompleteBox)d).RefreshSuggestions();
    }

    /// <summary>外部赋值 Text（如清空/重置）时同步到内部输入框。</summary>
    private static void OnTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (SectorAutoCompleteBox)d;
        box.SyncInputFromText();
        box.RefreshSuggestions();
    }

    private void SyncInputFromText()
    {
        // 值相同（用户输入路径）时不做赋值，避免触发循环
        if (InputBox.Text != Text)
            InputBox.Text = Text;
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        // 用户输入 → 推送到 Text 依赖属性（TwoWay 绑定会同步到源）
        if (InputBox.Text != Text)
            SetCurrentValue(TextProperty, InputBox.Text);
        UpdateWatermarkVisibility();
        RefreshSuggestions();
    }

    private void UpdateWatermarkVisibility()
    {
        WatermarkText.Visibility = string.IsNullOrEmpty(InputBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 按当前输入（忽略大小写前缀匹配）过滤候选并刷新下拉。
    /// 输入为空时关闭下拉；有候选时弹出并默认选中第一项。
    /// </summary>
    private void RefreshSuggestions()
    {
        var query = Text?.Trim() ?? string.Empty;
        List<string> matches = new();
        if (query.Length > 0 && ItemsSource != null)
        {
            foreach (var item in ItemsSource)
            {
                if (item is not string s || string.IsNullOrEmpty(s)) continue;
                if (s.Split(['｜', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(part => part.StartsWith(query, StringComparison.OrdinalIgnoreCase)))
                    matches.Add(s);
            }
        }

        SuggestionList.ItemsSource = matches;
        bool show = matches.Count > 0;
        SuggestionPopup.IsOpen = show;
        if (show)
        {
            SuggestionList.SelectedIndex = 0;
            SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
        }
    }

    /// <summary>
    /// 补全当前选中候选项。
    /// <paramref name="moveNext"/> 为 true（回车触发）时补全后跳转到下一个输入控件；
    /// 鼠标点击触发时焦点交还输入框，便于继续操作。
    /// </summary>
    private void ChooseSelected(bool moveNext = false)
    {
        if (SuggestionList.SelectedItem is string sel && !string.IsNullOrEmpty(sel))
        {
            InputBox.Text = sel; // 触发 TextChanged → 同步源
            InputBox.CaretIndex = sel.Length;
            SuggestionPopup.IsOpen = false;
            UpdateWatermarkVisibility();

            if (moveNext)
            {
                // 回车=结束输入 → 跳到下一个输入控件（按 Tab 顺序）
                InputBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            }
            else
            {
                // 鼠标点击补全：焦点交还输入框，保证后续键盘继续在输入框上工作
                Keyboard.Focus(InputBox);
            }
        }
    }

    // ============ 键盘 / 鼠标 / 焦点 ============

    /// <summary>
    /// 键盘导航（方向键/Enter/Esc）。必须用 PreviewKeyDown：WPF TextBox 类处理器会在 KeyDown
    /// 阶段把上/下方向键标记为已处理，导致普通 KeyDown 收不到方向键（Enter 不受影响）。
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!SuggestionPopup.IsOpen)
        {
            // 输入非空且有候选时，方向键打开下拉并定位到第一项
            if ((e.Key == Key.Down || e.Key == Key.Up) && SuggestionList.Items.Count > 0)
            {
                SuggestionPopup.IsOpen = true;
                SuggestionList.SelectedIndex = 0;
                SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
                e.Handled = true;
            }
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                // 回车=结束输入 → 补全后自动跳到下一个输入控件
                ChooseSelected(moveNext: true);
                e.Handled = true;
                break;
            case Key.Escape:
                SuggestionPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 移动候选选中项（越界钳制），并确保该项滚动可见。
    /// </summary>
    private void MoveSelection(int delta)
    {
        if (SuggestionList.Items.Count == 0) return;
        int newIndex = Math.Clamp(SuggestionList.SelectedIndex + delta, 0, SuggestionList.Items.Count - 1);
        SuggestionList.SelectedIndex = newIndex;
        SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
    }

    /// <summary>
    /// 鼠标按下候选框即补全：通过容器定位被点击项，避免 MouseUp 时 Popup 已关闭导致选择丢失。
    /// </summary>
    private void OnSuggestionPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox lb) return;
        var container = ItemsControl.ContainerFromElement(lb, (DependencyObject)e.OriginalSource) as ListBoxItem;
        if (container?.DataContext is string sel && !string.IsNullOrEmpty(sel))
        {
            SuggestionList.SelectedItem = sel;
            ChooseSelected();
        }
    }

    private void OnLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 延迟到 Input 优先级：让点击候选列表项的鼠标事件先完成选择，再关闭下拉
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!IsKeyboardFocusWithin)
                SuggestionPopup.IsOpen = false;
        }));
    }
}
