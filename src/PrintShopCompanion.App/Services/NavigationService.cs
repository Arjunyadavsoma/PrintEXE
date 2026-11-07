namespace PrintShopCompanion.App.Services;

/// <summary>
/// Simple string-based navigation service.
/// </summary>
public interface INavigationService
{
    string CurrentView { get; }
    event Action? ViewChanged;
    void NavigateTo(string viewName);
}

public sealed class NavigationService : INavigationService
{
    private string _current = "Login";

    public string CurrentView => _current;
    public event Action? ViewChanged;

    public void NavigateTo(string viewName)
    {
        if (_current == viewName) return;
        _current = viewName;
        ViewChanged?.Invoke();
    }
}
