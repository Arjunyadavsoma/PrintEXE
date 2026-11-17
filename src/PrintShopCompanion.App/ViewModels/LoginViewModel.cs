namespace PrintShopCompanion.App.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.App.Services;
using PrintShopCompanion.Infrastructure.Backend;

/// <summary>
/// Login view model. Supports THREE authentication paths (Phase 3 brief §4):
/// <para><b>1. Pairing-ticket pairing (RECOMMENDED, v1 contract §2):</b> the
/// shopkeeper goes to the web dashboard → Shop → Devices → Add Computer. The
/// website calls <c>POST /api/v1/companion/pairing/begin</c> and shows a
/// 64-hex-char pairing ticket + a deep link + a QR code (5-min TTL). The
/// shopkeeper pastes the ticket here (or opens the deep link, which is
/// handled by <c>Startup.UriActivation</c>). PrintEXE calls
/// <c>POST /api/v1/companion/pair</c> and gets the device token + shop
/// identity. This is the v1-contract flow — <see cref="PairWithTicketAsync"/>.</para>
/// <para><b>2. Paste-token pairing (legacy fallback):</b> the shopkeeper copies
/// a device token from the web dashboard and pastes it here —
/// <see cref="PairWithTokenAsync"/>. Kept for shops that haven't adopted the
/// pairing-ticket UI yet.</para>
/// <para><b>3. Direct email + password (legacy shopkeeper login):</b> used
/// when the companion connects to the shopkeeper REST API directly —
/// <see cref="SignInAsync"/>. NOT recommended for PrintEXE; kept for backward
/// compatibility.</para>
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

        // Deep-link pairing (integration spec §7.3): when the OS launched the
        // app with a pairing URI (printshop://pair?ticket=…&shop=… from the
        // website's Add Device dialog, or the legacy printshopcompanion://
        // token link), pre-fill the pairing form ONCE. The user still
        // confirms by clicking "Pair this computer" — a URI is never
        // auto-consumed, because an untrusted process can launch the app
        // with an arbitrary URI.
        if (UriActivator.TryConsumePendingPairingRequest(out var pending))
        {
            if (!string.IsNullOrEmpty(pending.Ticket))
            {
                PairingTicket = pending.Ticket;
            }
            else if (!string.IsNullOrEmpty(pending.LegacyToken))
            {
                DeviceToken = pending.LegacyToken;
            }
        }
    }

    // ──────────── Pairing-ticket flow (v1 contract, recommended) ────────────
    [ObservableProperty] private string _pairingTicket = "";
    [ObservableProperty] private string _deviceName = Environment.MachineName;
    [ObservableProperty] private string? _ticketErrorMessage;
    [ObservableProperty] private bool _isPairingWithTicket;
    [ObservableProperty] private string? _pairedShopName;

    /// <summary>
    /// Pair with a pairing ticket (v1 contract §2). The shopkeeper obtains the
    /// ticket from the web dashboard (or via the <c>printshop://pair?ticket=...</c>
    /// deep link). PrintEXE exchanges the ticket for a device token +
    /// shop identity via <c>POST /api/v1/companion/pair</c>.
    /// <para>Maps the contract's ticket-specific error codes to user-facing
    /// states via <see cref="PairingTicketException"/>:</para>
    /// <list type="bullet">
    ///   <item><c>VALIDATION_ERROR</c> → "Pairing code must be 64 hex characters."</item>
    ///   <item><c>UNAUTHORIZED</c> (expired) → "Pairing code expired. Generate a new one on the website."</item>
    ///   <item><c>UNAUTHORIZED</c> (unknown) → "Pairing code not recognized. Generate a new one."</item>
    ///   <item><c>PAIRING_TICKET_CONSUMED</c> → "Pairing code already used. Generate a new one."</item>
    ///   <item><c>PAIRING_TICKET_UNAVAILABLE</c> → "Pairing code is no longer active. Generate a new one."</item>
    ///   <item><c>NOT_FOUND</c> → "The shop for this pairing code no longer exists. Contact support."</item>
    ///   <item><c>RATE_LIMITED</c> → "Too many attempts. Wait a minute and try again."</item>
    /// </list>
    /// </summary>
    public async Task PairWithTicketAsync()
    {
        TicketErrorMessage = null;
        PairedShopName = null;
        if (string.IsNullOrWhiteSpace(PairingTicket))
        {
            TicketErrorMessage = "Enter the 64-character pairing code from the web dashboard.";
            return;
        }
        if (string.IsNullOrWhiteSpace(DeviceName))
        {
            TicketErrorMessage = "Enter a name for this computer (e.g. Shop-Front-PC).";
            return;
        }

        IsPairingWithTicket = true;
        try
        {
            var result = await _printShopBackend.PairWithTicketAsync(
                PairingTicket.Trim(),
                DeviceName.Trim());
            PairedShopName = result.ShopName;
            // Clear the ticket from memory — it's single-use and now consumed.
            // The device token is DPAPI-persisted by PairWithTicketAsync →
            // PairWithDeviceTokenAsync; we do NOT keep the ticket.
            PairingTicket = "";
            _nav.NavigateTo("Overview");
        }
        catch (PairingTicketException ex)
        {
            TicketErrorMessage = PairingTicketErrorMessages.Map(ex) ?? $"Pairing failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            TicketErrorMessage = $"Pairing failed: {ex.Message}";
        }
        finally
        {
            IsPairingWithTicket = false;
        }
    }

    /// <summary>Clear the pairing-ticket form (used by a "Clear" button).</summary>
    public void ClearTicketForm()
    {
        PairingTicket = "";
        TicketErrorMessage = null;
        PairedShopName = null;
    }

    // ──────────── Paste-token flow (legacy fallback) ────────────
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
    /// PairWithTicketAsync (v1 contract pairing-ticket flow).
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
