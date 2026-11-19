namespace PrintShopCompanion.App.Services;

/// <summary>
/// Simple string-based navigation service with an optional per-navigation
/// parameter (e.g. the order id when drilling into OrderDetails).
/// </summary>
public interface INavigationService
{
    string CurrentView { get; }
    /// <summary>The parameter passed to the LAST NavigateTo call (null if none). Consumed by the target page.</summary>
    object? CurrentParam { get; }
    event Action? ViewChanged;
    void NavigateTo(string viewName);
    void NavigateTo(string viewName, object? param);
}

public sealed class NavigationService : INavigationService
{
    private string _current = "Login";
    private object? _param;

    public string CurrentView => _current;
    public object? CurrentParam => _param;
    public event Action? ViewChanged;

    public void NavigateTo(string viewName)
    {
        if (_current == viewName)
        {
            // Same view re-requested WITH a new param (e.g. opening a different
            // order's details from within OrderDetails) must still notify.
            if (_param is not null) ViewChanged?.Invoke();
            return;
        }
        _current = viewName;
        ViewChanged?.Invoke();
    }

    public void NavigateTo(string viewName, object? param)
    {
        _param = param;
        NavigateTo(viewName);
    }
}
