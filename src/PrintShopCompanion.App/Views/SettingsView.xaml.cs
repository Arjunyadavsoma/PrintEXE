namespace PrintShopCompanion.App.Views;

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using PrintShopCompanion.Core.Contracts;

public partial class SettingsView : UserControl
{
    private readonly IServiceProvider _services;

    public SettingsView(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        try
        {
            var ver = typeof(SettingsView).Assembly.GetName().Version;
            VersionLabel.Text = "v" + (ver?.ToString() ?? "0.4.1");
        }
        catch { }
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
        var nav = _services.GetRequiredService<Services.INavigationService>();
        nav.NavigateTo("Login");
    }
}
