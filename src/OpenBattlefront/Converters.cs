using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OpenBattlefront;

/// <summary>value == parameter, for radio buttons bound to an enum or string. Unchecking does nothing.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string text ? (targetType.IsEnum ? Enum.Parse(targetType, text) : text) : Binding.DoNothing;
}

/// <summary>Visible when the value matches the parameter, or one of "A|B".</summary>
public sealed class MatchToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string current = value?.ToString() ?? "";
        bool match = (parameter?.ToString() ?? "").Split('|').Any(p => string.Equals(p, current, StringComparison.OrdinalIgnoreCase));
        return match ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
