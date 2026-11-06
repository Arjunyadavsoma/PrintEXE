namespace PrintShopCompanion.App.Views;

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrintShopCompanion.App.Services;
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
        VersionLabel.Text = "PrintShop Companion v" +
            (typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "0.1.0");

        _nav.ViewChanged += OnViewChanged;
        OnViewChanged();
        Closing += OnClosingHandler;
    }

    private void OnViewChanged()
    {
        var current = _nav.CurrentView;
        foreach (var name in new[] { "Setup", "Dashboard", "Printers", "PrintQueue", "Settings" })
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
            "Setup"     => _services.GetRequiredService<SetupView>(),
            "Dashboard" => _services.GetRequiredService<DashboardView>(),
            "Printers"  => _services.GetRequiredService<PrintersView>(),
            "PrintQueue"=> _services.GetRequiredService<PrintQueueView>(),
            "Settings"  => _services.GetRequiredService<SettingsView>(),
            _           => _services.GetRequiredService<SetupView>()
        };
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string s) _nav.NavigateTo(s);
    }

    private void OnClosing(object sender, CancelEventArgs e) { /* XAML event hookup; routed */ }
    private void OnClosingHandler(object? sender, CancelEventArgs e)
    {
        var opts = _services.GetRequiredService<IOptionsMonitor<AppOptions>>().CurrentValue;
        if (opts.MinimizeToTrayOnClose && !RealClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>Set to true just before shutdown to bypass the "minimise to tray" guard.</summary>
    public bool RealClose { get; set; }
}
