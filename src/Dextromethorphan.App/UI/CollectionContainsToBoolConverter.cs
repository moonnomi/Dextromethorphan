using System.Globalization;
using System.Windows.Data;

namespace Dextromethorphan.App.UI;

public sealed class CollectionContainsToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is System.Collections.IEnumerable items
        && parameter is string name
        && items.Cast<object>().Any(item => string.Equals(item?.ToString(), name, StringComparison.OrdinalIgnoreCase));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
