namespace PrintShopCompanion.App.Tray;

using System.Drawing;
using System.Windows;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using PrintShopCompanion.App.Services;
using PrintShopCompanion.App.Views;

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
        // Decision D-09: the pause item now drives the real queue controller
        // (IQueueController — the same singleton the remote `pause`/`resume`
        // commands use). Cooperative semantics: an in-flight job finishes;
        // new dequeues block until resumed. The header reflects live state.
        var queueController = _services.GetRequiredService<Core.Contracts.IQueueController>();
        var pauseItem = new System.Windows.Controls.MenuItem
        {
            Header = queueController.IsPaused ? "Resume queue" : "Pause queue",
        };
        pauseItem.Click += (_, _) =>
        {
            if (queueController.IsPaused)
            {
                queueController.ResumeAsync();
                pauseItem.Header = "Pause queue";
            }
            else
            {
                queueController.PauseAsync();
                pauseItem.Header = "Resume queue";
            }
        };
        menu.Items.Add(pauseItem);
        menu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header = "Diagnostics…",
            Command = new RelayCommand(() =>
            {
                var nav = _services.GetRequiredService<INavigationService>();
                nav.NavigateTo("Diagnostics");
                var main = _services.GetRequiredService<MainWindow>();
                main.Activate(); main.Show();
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

    /// <summary>Show a balloon notification in the Windows notification area.</summary>
    public void ShowBalloon(string title, string body, Services.NotificationService.Severity severity)
    {
        // H.NotifyIcon exposes ShowNotification which uses the Win32 NOTIFYICONDATA balloon API.
        var icon = severity switch
        {
            Services.NotificationService.Severity.Success => System.Drawing.SystemIcons.Information,
            Services.NotificationService.Severity.Warning => System.Drawing.SystemIcons.Warning,
            Services.NotificationService.Severity.Error => System.Drawing.SystemIcons.Error,
            _ => System.Drawing.SystemIcons.Information,
        };
        _icon.ShowNotification(title, body);
    }

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
