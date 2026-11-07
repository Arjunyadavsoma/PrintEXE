namespace PrintShopCompanion.App.Views;

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using PrintShopCompanion.App.Services;
using PrintShopCompanion.Core.Contracts;

public partial class SettingsView : UserControl
{
    private readonly IServiceProvider _services;

    public SettingsView(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;

        // Always read the version from the assembly — never hardcode it.
        // The original SettingsView hardcoded "v0.4.1" which is why a successful
        // update still showed v0.4.1 in the UI even after the swap.
        VersionLabel.Text = AppVersion.Prefixed;
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PrintShop", "Companion", "logs");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Process.Start("explorer.exe", dir);
        }
        catch { }
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        var backend = _services.GetService<IShopBackend>();
        backend?.SignOutAsync();
        var nav = _services.GetRequiredService<INavigationService>();
        nav.NavigateTo("Login");
    }
}
