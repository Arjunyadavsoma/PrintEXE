namespace PrintShopCompanion.App.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.App.Services;
using PrintShopCompanion.Infrastructure.Backend;

/// <summary>
/// Login view model. Supports TWO authentication paths:
/// <para><b>1. Paste-token pairing (recommended for PrintEXE):</b> the
/// shopkeeper logs into the web dashboard, goes to Printer Setup → Register
/// new agent, copies the device token, and pastes it into PrintEXE. This
/// is the v0.4.7+ flow per the integration guide section 2.1.</para>
/// <para><b>2. Direct email + password (legacy shopkeeper login):</b> used
/// when the companion connects to the shopkeeper REST API (the
/// /api/v1/shops/me/* endpoints) directly. This is NOT the recommended
/// flow for PrintEXE — the device-token flow is. Kept for backward
/// compatibility with shops that haven't yet set up a print agent.</para>
/// </summary>
public partial class LoginViewModel : ObservableObject
{
    private readonly IShopBackend _shopBackend;
    private readonly IPrintShopBackend _printShopBackend;
    private readonly ICredentialStore _store;
    private readonly INavigationService _nav;

    public LoginViewModel(
        IShopBackend shopBackend,
        IPrintShopBackend printShopBackend,
        ICredentialStore store,
        INavigationService nav)
    {
        _shopBackend = shopBackend;
        _printShopBackend = printShopBackend;
        _store = store;
        _nav = nav;
    }

    // ──────────── Paste-token flow ────────────
    [ObservableProperty] private string _deviceToken = "";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isPairing;

    /// <summary>
    /// Pair with the device token obtained from the web dashboard's
    /// "Register new agent" flow. The shopkeeper pastes the token here;
    /// PrintEXE validates it via heartbeat + stores it DPAPI-encrypted.
    /// </summary>
    public async Task PairWithTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(DeviceToken))
        {
            ErrorMessage = "Paste the device token from the web dashboard's Printer Setup page.";
            return;
        }

        ErrorMessage = null;
        IsPairing = true;
        try
        {
            await _printShopBackend.PairWithDeviceTokenAsync(DeviceToken.Trim());
            _nav.NavigateTo("Overview");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsPairing = false;
        }
    }

    // ──────────── Legacy email+password flow (kept for backward compat) ────────────
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _password = "";

    /// <summary>
    /// Direct shopkeeper login (legacy). Used by HttpShopBackend against
    /// /api/v1/auth/login. Not the recommended flow for PrintEXE — prefer
    /// PairWithTokenAsync (paste-token pairing).
    /// </summary>
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
            var session = await _shopBackend.LoginAsync(Email, Password);
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
