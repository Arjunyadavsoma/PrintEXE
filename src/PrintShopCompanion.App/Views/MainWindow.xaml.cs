namespace PrintShopCompanion.App.Views;

using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using PrintShopCompanion.App.Services;
using PrintShopCompanion.Core.Contracts;

public partial class MainWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly INavigationService _nav;
    private IUpdateService? _updateService;

    public MainWindow(IServiceProvider services, INavigationService nav)
    {
        _services = services;
        _nav = nav;
        InitializeComponent();
        _nav.ViewChanged += OnViewChanged;
        OnViewChanged();
    }

    private void OnViewChanged()
    {
        var current = _nav.CurrentView;

        // Show/hide sidebar based on view
        if (current == "Login")
        {
            SidebarCol.Width = new GridLength(0);
            Sidebar.Visibility = Visibility.Collapsed;
        }
        else
        {
            SidebarCol.Width = new GridLength(220);
            Sidebar.Visibility = Visibility.Visible;
        }

        // Highlight active nav button
        foreach (var name in new[] { "Overview", "Orders", "PrintQueue", "Printers", "Settings" })
        {
            var btn = FindName("Nav" + name) as Button;
            if (btn == null) continue;
            btn.FontWeight = name == current ? FontWeights.SemiBold : FontWeights.Normal;
            btn.Background = name == current
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFE, 0xF3, 0xC7))
                : System.Windows.Media.Brushes.Transparent;
        }

        // Show the right view
        ContentArea.Content = current switch
        {
            "Login" => _services.GetRequiredService<LoginView>(),
            "Overview" => _services.GetRequiredService<OverviewView>(),
            "Orders" => _services.GetRequiredService<OrdersView>(),
            "PrintQueue" => _services.GetRequiredService<PrintQueueView>(),
            "Printers" => _services.GetRequiredService<PrintersView>(),
            "Settings" => _services.GetRequiredService<SettingsView>(),
            _ => _services.GetRequiredService<OverviewView>()
        };
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string s) _nav.NavigateTo(s);
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        var backend = _services.GetService<IShopBackend>();
        backend?.SignOutAsync();
        _nav.NavigateTo("Login");
    }

    public void ShowUpdateBanner(IUpdateService updateService)
    {
        _updateService = updateService;
        UpdateText.Text = $"Update available: v{updateService.CurrentVersion} -> v{updateService.LatestVersion}";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_updateService == null) return;
        try
        {
            await _updateService.InstallUpdateAsync();
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"Update failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
