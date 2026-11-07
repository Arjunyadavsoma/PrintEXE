namespace PrintShopCompanion.App.Converters;

using System.Globalization;
using System.Windows;
using System.Windows.Data;

/// <summary>
/// Returns Visible when the bound value is "truthy":
///   - bool: true
///   - int / long: non-zero
///   - string: non-null/non-whitespace
///   - null: false
/// </summary>
/// <remarks>
/// This converter intentionally handles multiple types because XAML bindings
/// often pass `.Count` (int) or `ErrorMessage` (string) where a bool was expected.
/// The original implementation only checked `is bool`, which silently returned
/// Collapsed for every int/string value — meaning error panels and empty-state
/// messages NEVER appeared.
/// </remarks>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => IsTruthy(value) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;

    private static bool IsTruthy(object? value)
    {
        if (value is null) return false;
        if (value is bool b) return b;
        if (value is int i) return i != 0;
        if (value is long l) return l != 0;
        if (value is string s) return !string.IsNullOrWhiteSpace(s);
        return true; // non-null unknown type — assume truthy
    }
}

/// <summary>
/// Inverse of BoolToVisibilityConverter: Visible when the value is "falsy".
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => IsTruthy(value) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v != Visibility.Visible;

    private static bool IsTruthy(object? value)
    {
        if (value is null) return false;
        if (value is bool b) return b;
        if (value is int i) return i != 0;
        if (value is long l) return l != 0;
        if (value is string s) return !string.IsNullOrWhiteSpace(s);
        return true;
    }
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}

/// <summary>
/// Returns Visible when the bound string is non-null/non-empty/whitespace,
/// Collapsed otherwise. Used to show error message panels that should only
/// appear when there is actually an error message to display.
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return Visibility.Collapsed;
        if (value is string s) return !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Visible;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>
/// Inverse of StringToVisibilityConverter — Visible when the string IS
/// null/empty/whitespace. Used to hide empty-state messages when there
/// ARE items in the list.
/// </summary>
public sealed class InverseStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return Visibility.Visible;
        if (value is string s) return string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v != Visibility.Visible;
}
