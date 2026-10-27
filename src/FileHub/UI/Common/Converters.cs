using System.Globalization;
using System.Windows;
using System.Windows.Data;
using FileHub.Core.Search;

namespace FileHub.UI.Common;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is Visibility.Visible;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool has = value != null && !(value is string s && s.Length == 0);
        if (Invert) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is long l ? SizeRange.Format(l) : string.Empty;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null && parameter != null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true && parameter != null ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}

public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToDoubleConverter : IValueConverter
{
    public double True { get; set; } = 1;
    public double False { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? True : False;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
