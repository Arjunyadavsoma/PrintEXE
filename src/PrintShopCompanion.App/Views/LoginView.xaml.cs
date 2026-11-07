namespace PrintShopCompanion.App.Views;

using System.Windows;
using System.Windows.Controls;
using PrintShopCompanion.App.ViewModels;

public partial class LoginView : UserControl
{
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
}
