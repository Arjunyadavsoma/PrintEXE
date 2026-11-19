namespace PrintShopCompanion.App.Views;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PrintShopCompanion.App.ViewModels;

public partial class LoginView : UserControl
{
    private static readonly SolidColorBrush ActiveBorder = new(Color.FromRgb(0x28, 0x58, 0xCD));
    private static readonly SolidColorBrush IdleBorder = new(Color.FromRgb(0xD1, 0xD5, 0xDB));

    private readonly LoginViewModel _vm;
    private bool _suppressRevealSync;

    public LoginView(LoginViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        // Stamp the panels with the actually-running build so the login page
        // never lies about which version the user is on. The hardcoded
        // "v0.4.1" was the original symptom of the auto-update bug.
        VersionLabel.Text = AppVersion.Prefixed;
        BuildStamp.Text = $"PrintShop Companion  ·  {AppVersion.Prefixed}";

        // Deep link (printshop://pair?ticket=…) pre-filled the form — open the
        // matching panel so the shopkeeper sees the code waiting for them.
        if (!string.IsNullOrEmpty(_vm.PairingTicket)) OpenPanels(PairingPanel, TokenPanel, PairingToggle, TokenToggle);
        else if (!string.IsNullOrEmpty(_vm.DeviceToken)) OpenPanels(TokenPanel, PairingPanel, TokenToggle, PairingToggle);

        // The SessionRestoreState singleton outlives this transient view model —
        // unhook when the view goes away (e.g. after pairing navigates to Overview).
        Unloaded += (_, _) => _vm.Detach();
    }

    // ── Secondary-option accordions ─────────────────────────────────────
    // Email+password is the PRIMARY form; the pairing-code and device-token
    // flows live behind these small icon buttons and expand on demand.

    private void TogglePairingPanel_Click(object sender, RoutedEventArgs e)
        => TogglePanels(PairingPanel, TokenPanel, PairingToggle, TokenToggle);

    private void ToggleTokenPanel_Click(object sender, RoutedEventArgs e)
        => TogglePanels(TokenPanel, PairingPanel, TokenToggle, PairingToggle);

    private void TogglePanels(StackPanel target, StackPanel other, Button targetBtn, Button otherBtn)
    {
        if (target.Visibility == Visibility.Visible)
        {
            // Clicking the active option's button collapses it again.
            target.Visibility = Visibility.Collapsed;
            targetBtn.BorderBrush = IdleBorder;
        }
        else
        {
            OpenPanels(target, other, targetBtn, otherBtn);
            // Stale errors from the other flow would otherwise show in the
            // newly opened panel — clear both when switching.
            _vm.ErrorMessage = null;
            _vm.TicketErrorMessage = null;
        }
    }

    private static void OpenPanels(StackPanel target, StackPanel other, Button targetBtn, Button otherBtn)
    {
        target.Visibility = Visibility.Visible;
        other.Visibility = Visibility.Collapsed;
        targetBtn.BorderBrush = ActiveBorder;
        otherBtn.BorderBrush = IdleBorder;
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _vm.Password = PasswordBox.Password;
        // Mirror the typed text into the overlay reveal box (when visible).
        if (!_suppressRevealSync)
            PasswordRevealBox.Text = PasswordBox.Password;
    }

    private void PasswordRevealBox_TextChanged(object sender, RoutedEventArgs e)
    {
        // The reveal box is read-only — this handler only fires when we
        // programmatically set its text from the PasswordBox. We don't
        // sync back to PasswordBox to avoid feedback loops.
    }

    private void TogglePassword_Click(object sender, RoutedEventArgs e)
    {
        var showing = TogglePasswordBtn.Content as string == "Hide";
        if (showing)
        {
            // Hide — switch back to PasswordBox
            TogglePasswordBtn.Content = "Show";
            PasswordRevealBox.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Visible;
            PasswordBox.Focus();
        }
        else
        {
            // Show — switch to reveal box
            TogglePasswordBtn.Content = "Hide";
            _suppressRevealSync = true;
            PasswordRevealBox.Text = PasswordBox.Password;
            _suppressRevealSync = false;
            PasswordRevealBox.Visibility = Visibility.Visible;
            PasswordBox.Visibility = Visibility.Collapsed;
            PasswordRevealBox.Focus();
        }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        await _vm.SignInAsync();
    }

    /// <summary>
    /// Pair with the device token. The recommended flow per the integration
    /// guide section 2.1: the shopkeeper generates a device token on the
    /// web dashboard's Printer Setup page, copies it, and pastes it here.
    /// </summary>
    private async void PairWithToken_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.Content = "Connecting...";
        ConnectButton.IsEnabled = false;
        try
        {
            await _vm.PairWithTokenAsync();
        }
        finally
        {
            ConnectButton.Content = "Connect this PC";
            // The ViewModel sets IsPairing=true while pairing is in flight;
            // the XAML binds IsEnabled to !IsPairing. We don't need to
            // re-enable here because if pairing succeeded, we navigate to
            // Overview and this view is gone. If pairing failed, IsPairing
            // flips back to false and the binding re-enables the button.
        }
    }

    /// <summary>
    /// Phase 3 (brief §4) — Pair with a pairing TICKET (the v1-contract recommended
    /// flow). The shopkeeper gets a 64-hex-char code from the web dashboard's
    /// Devices → Add Computer dialog (5-min TTL, single-use) and pastes it here.
    /// </summary>
    private async void PairWithTicket_Click(object sender, RoutedEventArgs e)
    {
        PairWithTicketButton.Content = "Pairing...";
        try
        {
            await _vm.PairWithTicketAsync();
        }
        finally
        {
            PairWithTicketButton.Content = "Pair this computer";
            // Same IsEnabled-via-binding pattern as PairWithToken_Click:
            // IsPairingWithTicket flips back to false on completion (success or
            // failure), and the XAML binding re-enables the button. On success
            // we navigate to Overview and this view is gone.
        }
    }
}
