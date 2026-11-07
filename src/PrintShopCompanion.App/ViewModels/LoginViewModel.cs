namespace PrintShopCompanion.App.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.App.Services;
using PrintShopCompanion.Infrastructure.Backend;

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
    [ObservableProperty] private string? _errorMessage;

    public async Task SignInAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Enter your email and password.";
            return;
        }

        ErrorMessage = null;
        try
        {
            var session = await _backend.LoginAsync(Email, Password);
            // Save credentials so the user doesn't need to log in every time
            await _store.SaveAsync(new StoredCredentials { Session = session });
            _nav.NavigateTo("Overview");
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
    }
}
