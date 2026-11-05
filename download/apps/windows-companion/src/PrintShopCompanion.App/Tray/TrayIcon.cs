namespace PrintShopCompanion.App.Tray;

using System.Drawing;
using System.Windows;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using PrintShopCompanion.App.Services;

/// <summary>
/// System-tray icon and context menu. The tray is the primary UI surface
/// for the shopkeeper — the companion "lives" in the tray and only shows
/// the full window on demand. The context menu has:
/// <list type="bullet">
///   <item>Open Dashboard</item>
///   <item>Send a test print</item>
///   <item>Pause / resume queue</item>
///   <item>Settings</item>
///   <item>Quit</item>
/// </list>
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly IServiceProvider _services;

    public TrayIconHost(IServiceProvider services)
    {
        _services = services;
        _icon = new TaskbarIcon
        {
            Icon = SystemIcons.Application,
            ToolTipText = "PrintShop Companion",
            Visibility = Visibility.Visible,
            ContextMenu = BuildMenu(),
            DoubleClickCommand = new RelayCommand(() =>
            {
                var main = _services.GetRequiredService<MainWindow>();
                main.Activate();
                main.Show();
            })
        };
    }

    private System.Windows.Controls.ContextMenu BuildMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header = "Open Dashboard",
            Command = new RelayCommand(() =>
            {
                var nav = _services.GetRequiredService<INavigationService>();
                nav.NavigateTo("Dashboard");
                var main = _services.GetRequiredService<MainWindow>();
                main.Activate(); main.Show();
            })
        });
        menu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header = "Test print…",
            Command = new RelayCommand(() =>
            {
                var nav = _services.GetRequiredService<INavigationService>();
                nav.NavigateTo("Printers");
                var main = _services.GetRequiredService<MainWindow>();
                main.Activate(); main.Show();
            })
        });
        menu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header = "Pause queue",
            Command = new RelayCommand(() =>
            {
                // Phase 6: queue pause is wired here.
            })
        });
        menu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header = "Settings…",
            Command = new RelayCommand(() =>
            {
                var nav = _services.GetRequiredService<INavigationService>();
                nav.NavigateTo("Settings");
                var main = _services.GetRequiredService<MainWindow>();
                main.Activate(); main.Show();
            })
        });
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header = "Quit",
            Command = new RelayCommand(() => System.Windows.Application.Current.Shutdown())
        });
        return menu;
    }

    public void Show() => _icon.Visibility = Visibility.Visible;
    public void Hide() => _icon.Visibility = Visibility.Hidden;
    public void Dispose() => _icon.Dispose();
}

internal sealed class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action _action;
    public RelayCommand(Action action) => _action = action;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _action();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
