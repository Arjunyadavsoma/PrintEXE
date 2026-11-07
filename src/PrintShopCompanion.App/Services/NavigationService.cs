namespace PrintShopCompanion.App.Services;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// View-first navigation. Keeps a string <see cref="CurrentView"/> and an
/// optional <see cref="Parameter"/> (e.g. the order ID for Order Details).
/// The MainWindow swaps the ContentControl bound to the active View.
/// </summary>
public interface INavigationService
{
    string CurrentView { get; }
    string? Parameter { get; }
    event Action? ViewChanged;
    void NavigateTo(string viewName);
    void NavigateTo(string viewName, string? parameter);
}

public sealed class NavigationService : INavigationService
{
    private string _current = "Login";
    private string? _parameter;

    public string CurrentView => _current;
    public string? Parameter => _parameter;
    public event Action? ViewChanged;

    public void NavigateTo(string viewName) => NavigateTo(viewName, null);

    public void NavigateTo(string viewName, string? parameter)
    {
        if (_current == viewName && _parameter == parameter) return;
        _current = viewName;
        _parameter = parameter;
        ViewChanged?.Invoke();
    }
}

public static class ViewNames
{
    public const string Login         = "Login";
    public const string Overview       = "Overview";
    public const string Orders        = "Orders";
    public const string OrderDetails   = "OrderDetails";
    public const string PrintQueue     = "PrintQueue";
    public const string Printers       = "Printers";
    public const string Reports        = "Reports";
    public const string Settings       = "Settings";
    public const string Account        = "Account";
}

public static class NavigationServiceExtensions
{
    public static void NavigateToOrderDetails(this INavigationService nav, string orderId)
        => nav.NavigateTo(ViewNames.OrderDetails, orderId);

    public static void NavigateToOverview(this INavigationService nav) => nav.NavigateTo(ViewNames.Overview);
    public static void NavigateToOrders(this INavigationService nav) => nav.NavigateTo(ViewNames.Orders);
    public static void NavigateToPrintQueue(this INavigationService nav) => nav.NavigateTo(ViewNames.PrintQueue);
    public static void NavigateToPrinters(this INavigationService nav) => nav.NavigateTo(ViewNames.Printers);
    public static void NavigateToReports(this INavigationService nav) => nav.NavigateTo(ViewNames.Reports);
    public static void NavigateToSettings(this INavigationService nav) => nav.NavigateTo(ViewNames.Settings);
    public static void NavigateToAccount(this INavigationService nav) => nav.NavigateTo(ViewNames.Account);
    public static void NavigateToLogin(this INavigationService nav) => nav.NavigateTo(ViewNames.Login);
}
