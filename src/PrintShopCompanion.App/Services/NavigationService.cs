namespace PrintShopCompanion.App.Services;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// Minimal view-first navigation service. Keeps a string CurrentView and
/// lets the MainWindow swap the ContentControl bound to the active View.
/// </summary>
public interface INavigationService
{
    string CurrentView { get; }
    event Action? ViewChanged;
    void NavigateTo(string viewName);
}

public sealed class NavigationService : INavigationService
{
    private string _current = "Setup";

    public string CurrentView => _current;
    public event Action? ViewChanged;

    public void NavigateTo(string viewName)
    {
        if (_current == viewName) return;
        _current = viewName;
        ViewChanged?.Invoke();
    }
}
