using System.Globalization;
using System.Windows.Data;
using X4Calculator.UI.Services;

namespace X4Calculator.UI.Converters;

public sealed class StationIconKeyToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string iconKey ? StationIconResources.Get(iconKey) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
