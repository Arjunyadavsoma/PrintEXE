namespace PrintShopCompanion.App.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.App.Services;

/// <summary>
/// The Login screen — shopkeeper signs in with email + password (the same
/// credentials they use on the website). Backed by POST /api/v1/auth/login.
/// On success, navigates to Overview.
/// </summary>
public partial class LoginViewModel : ObservableObject
{
    private readonly IShopBackend _backend;
    private readonly ICredentialStore _store;
    private readonly INavigationService _nav;

    public LoginViewModel(IShopBackend backend, ICredentialStore store, INavigationService nav)
    {
        _backend = backend;
        _store = store;
        _nav = nav;
    }

    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;

    [RelayCommand]
    public async Task SignInAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Enter your email and password.";
            return;
        }
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var session = await _backend.LoginAsync(Email, Password);
            await _store.SaveAsync(new StoredCredentials { Session = session });
            _nav.NavigateToOverview();
        }
        catch (PrintShopApiException ex)
        {
            ErrorMessage = ex.ErrorCode == "UNAUTHORIZED"
                ? "Invalid email or password."
                : ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally { IsLoading = false; }
    }
}
