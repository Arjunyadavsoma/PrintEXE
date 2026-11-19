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

/// <summary>
/// Paise → ₹ string, identical to the web's formatINRFromPaise. Used on every
/// money column (previously the raw paise integer was bound directly — "12500"
/// instead of "₹125.00").
/// </summary>
public sealed class PaiseToRupeesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            long p => Core.Presentation.Rupees.Format(p),
            int p => Core.Presentation.Rupees.Format(p),
            _ => "—",
        };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// OrderStatus → web label ("Awaiting payment", "Paid", "Printing",
/// "Ready for pickup", "Completed", "Cancelled").
/// </summary>
public sealed class OrderStatusLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Core.Presentation.StatusPresentation.ForString(value?.ToString()).Label;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// OrderStatus → pill background (web soft token) / border / foreground.
/// Pass "fg" or "border" as ConverterParameter for those brushes; default = background.
/// </summary>
public sealed class OrderStatusBrushConverter : IValueConverter
{
    /// <summary>Which style field to return: "bg" (default), "fg", or "border".
    /// A property (not ConverterParameter) so XAML can declare distinct instances.</summary>
    public string Mode { get; set; } = "bg";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = Core.Presentation.StatusPresentation.ForString(value?.ToString());
        var hex = Mode switch { "fg" => s.Foreground, "border" => s.Border, _ => s.Background };
        return new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// DateTimeOffset → local time "dd MMM, HH:mm" (the old bindings rendered the
/// raw UTC value — order timestamps looked 5½ hours off from the webpage).
/// </summary>
public sealed class UtcToLocalTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTimeOffset dto ? dto.ToLocalTime().ToString("dd MMM, HH:mm", culture) : "—";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Filter chip presentation: value[0] = the chip's filter name, value[1] =
/// the VM's SelectedFilter. Returns the active (indigo tint) brush when they
/// match, otherwise the neutral gray — so the selected chip is visible.
/// Parameter "fg" returns the text brush instead of the background brush.
/// </summary>
public sealed class FilterChipBrushConverter : System.Windows.Data.IMultiValueConverter
{
    /// <summary>"bg" (default) or "fg" — property-settable from XAML.</summary>
    public string Mode { get; set; } = "bg";

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var name = values.Length > 0 ? values[0]?.ToString() : null;
        var selected = values.Length > 1 ? values[1]?.ToString() : null;
        var active = !string.IsNullOrEmpty(selected) && string.Equals(name, selected, StringComparison.Ordinal);
        var hex = Mode switch
        {
            "fg" => active ? "#2858CD" : "#374151",
            _ => active ? "#E1EFFF" : "#F3F4F6",
        };
        return new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// bool → live-status brush: true = web success green, false = web danger red.
/// Used by the "● Live" pill (previously a static green regardless of state).
/// </summary>
public sealed class BoolToLiveBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => new System.Windows.Media.SolidColorBrush(
            value is true
                ? (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#007F3D")
                : (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#BE222A"));
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
