namespace X4Calculator.UI.Views;
public partial class StationPlanningView : System.Windows.Controls.UserControl
{
    public StationPlanningView() => InitializeComponent();

    private void IntegerTextBox_OnPreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox) return;
        var proposed = textBox.Text.Remove(textBox.SelectionStart, textBox.SelectionLength)
            .Insert(textBox.SelectionStart, e.Text);
        e.Handled = proposed.Length == 0 || proposed.Any(character => character is < '0' or > '9');
    }

    private void IntegerTextBox_OnPasting(object sender, System.Windows.DataObjectPastingEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox ||
            !e.SourceDataObject.GetDataPresent(System.Windows.DataFormats.UnicodeText))
        {
            e.CancelCommand();
            return;
        }

        var pasted = e.SourceDataObject.GetData(System.Windows.DataFormats.UnicodeText) as string ?? string.Empty;
        var proposed = textBox.Text.Remove(textBox.SelectionStart, textBox.SelectionLength)
            .Insert(textBox.SelectionStart, pasted);
        if (proposed.Length == 0 || proposed.Any(character => character is < '0' or > '9'))
            e.CancelCommand();
    }

    private void IntegerTextBox_OnLostFocus(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox) return;
        var binding = textBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty);
        binding?.UpdateSource();
        binding?.UpdateTarget();
    }

    private void ModuleCountTextBox_OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || sender is not System.Windows.Controls.TextBox textBox ||
            textBox.DataContext is not ViewModels.StationPlanningModuleItem item)
            return;
        var current = int.TryParse(textBox.Text, out var pending) ? pending : (int)item.Amount;
        current = System.Math.Clamp(current, 1, 1_000_000);
        item.SetModuleCount(e.Delta > 0 ? current + 1 : current - 1);
        e.Handled = true;
    }
}
