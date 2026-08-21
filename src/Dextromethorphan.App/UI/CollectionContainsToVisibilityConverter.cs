using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Dextromethorphan.App.UI;

public sealed class CollectionContainsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is System.Collections.IEnumerable items && parameter is string name && items.Cast<object>().Any(item => string.Equals(item?.ToString(), name, StringComparison.OrdinalIgnoreCase)) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
