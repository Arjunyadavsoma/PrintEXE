namespace PrintShopCompanion.App.Views;

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrintShopCompanion.App.Services;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Options;

public partial class MainWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly INavigationService _nav;

    public MainWindow(IServiceProvider services, INavigationService nav)
    {
        _services = services;
        _nav = nav;
        InitializeComponent();
        VersionLabel.Text = "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "0.0.0");

        _nav.ViewChanged += OnViewChanged;
        OnViewChanged();
        Closing += OnClosingHandler;
    }

    private void OnViewChanged()
    {
        var current = _nav.CurrentView;
        foreach (var name in new[] { "Overview", "Orders", "PrintQueue", "Printers", "Reports", "Settings", "Account" })
        {
            var btn = (Button)FindName("Nav" + name);
            if (btn == null) continue;
            btn.FontWeight = name == current ? FontWeights.SemiBold : FontWeights.Normal;
            btn.Background = name == current
                ? (System.Windows.Media.Brush)FindResource("ControlAltBrush")
                : System.Windows.Media.Brushes.Transparent;
        }

        ContentArea.Content = current switch
        {
            "Login"       => _services.GetRequiredService<LoginView>(),
            "Overview"     => _services.GetRequiredService<OverviewView>(),
            "Orders"       => _services.GetRequiredService<OrdersView>(),
            "PrintQueue"   => _services.GetRequiredService<PrintQueueView>(),
            "Printers"     => _services.GetRequiredService<PrintersView>(),
            "Settings"     => _services.GetRequiredService<SettingsView>(),
            _              => _services.GetRequiredService<OverviewView>()
        };
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string s) _nav.NavigateTo(s);
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        var backend = _services.GetService<IShopBackend>();
        backend?.SignOutAsync();
        _nav.NavigateToLogin();
    }

    private void OnClosingHandler(object? sender, CancelEventArgs e)
    {
        var opts = _services.GetRequiredService<IOptionsMonitor<AppOptions>>().CurrentValue;
        if (opts.MinimizeToTrayOnClose && !RealClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    public bool RealClose { get; set; }
}
