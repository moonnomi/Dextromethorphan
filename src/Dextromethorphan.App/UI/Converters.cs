using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Dextromethorphan.App.UI;

public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class RatingAtLeastConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int rating
        && int.TryParse(parameter?.ToString(), out var star)
        && rating >= star;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class BooleanToGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true) return new GridLength(0);

        var text = parameter?.ToString()?.Trim();
        if (string.IsNullOrEmpty(text) || text == "*") return new GridLength(1, GridUnitType.Star);
        if (text.EndsWith('*')
            && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var star))
            return new GridLength(Math.Max(0, star), GridUnitType.Star);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var pixels))
            return new GridLength(Math.Max(0, pixels));
        return new GridLength(1, GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
