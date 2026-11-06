namespace PrintShopCompanion.App.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Options;

/// <summary>
/// Setup wizard. Walks the shopkeeper through:
/// 1. Welcome — explains what the companion does.
/// 2. Pairing — instructs the user to click "Add this PC" on the website.
/// 3. Done — shows the paired shop name and routes to Dashboard.
/// </summary>
public partial class SetupViewModel : ObservableObject
{
    private readonly IPairingService _pairing;
    private readonly IPrintShopBackend _backend;
    private readonly IOptionsMonitor<BackendOptions> _options;

    [ObservableProperty]
    private int _currentStep = 1;

    [ObservableProperty]
    private string _statusText = "Open PrintShop on your web browser, sign in, and click \"Add this PC\" in Shop → Printers.";

    [ObservableProperty]
    private bool _isWaitingForMagicLink;

    [ObservableProperty]
    private string? _lastError;

    public SetupViewModel(IPairingService pairing, IPrintShopBackend backend, IOptionsMonitor<BackendOptions> options)
    {
        _pairing = pairing;
        _backend = backend;
        _options = options;
    }

    [RelayCommand]
    private async Task BeginPairingAsync()
    {
        LastError = null;
        IsWaitingForMagicLink = true;
        StatusText = "Waiting for the website to send the pairing link…";
        await _pairing.StartAwaitingAsync();
    }

    [RelayCommand]
    private Task CancelPairingAsync()
    {
        return _pairing.CancelAsync();
    }

    [RelayCommand]
    private void Next() => CurrentStep = Math.Min(3, CurrentStep + 1);

    [RelayCommand]
    private void Back() => CurrentStep = Math.Max(1, CurrentStep - 1);

    public void OnPairingSucceeded(string shopName)
    {
        IsWaitingForMagicLink = false;
        StatusText = $"Connected to {shopName}";
        CurrentStep = 3;
    }
}
