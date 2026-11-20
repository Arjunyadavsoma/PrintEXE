namespace PrintShopCompanion.App.Tray;

using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using PrintShopCompanion.App.Services;
using PrintShopCompanion.App.Views;
using PrintShopCompanion.Core.Contracts;

/// <summary>
/// System-tray icon and context menu. The tray is the primary UI surface
/// for the shopkeeper — the companion "lives" in the tray and only shows
/// the full window on demand. The context menu has:
/// <list type="bullet">
///   <item>Open Dashboard</item>
///   <item>Send a test print</item>
///   <item>Pause / resume queue (HONEST — drives the real JobProcessor gate)</item>
///   <item>Settings</item>
///   <item>Quit</item>
/// </list>
///
/// Phase 5 (B-017): the tray now has honest CONNECTION STATES — the icon
/// carries a status dot (green = connected, gray = offline, red = error)
/// and the tooltip names the state, instead of the old static generic
/// SystemIcons.Application with a fixed "PrintShop Companion" tooltip.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    /// <summary>Honest connectivity states surfaced on the tray icon.</summary>
    public enum ConnectionState
    {
        /// <summary>Heartbeats succeeding / jobs flowing — device is known to the backend.</summary>
        Connected,
        /// <summary>Not paired yet, or heartbeats failing — no live backend link.</summary>
        Offline,
        /// <summary>Paired but the last exchange errored (auth rejected, backend down).</summary>
        Error,
    }

    private readonly TaskbarIcon _icon;
    private readonly IServiceProvider _services;
    private ConnectionState _state = ConnectionState.Offline;
    private MenuItem? _pauseItem;
    private readonly Icon _brandedIcon;
    private readonly Icon _connectedIcon;
    private readonly Icon _offlineIcon;
    private readonly Icon _errorIcon;

    public TrayIconHost(IServiceProvider services)
    {
        _services = services;
        _brandedIcon = LoadIcon("app.ico") ?? SystemIcons.Application;
        _connectedIcon = LoadIcon("tray-connected.png") ?? _brandedIcon;
        _offlineIcon = LoadIcon("tray-offline.png") ?? _brandedIcon;
        _errorIcon = LoadIcon("tray-error.png") ?? _brandedIcon;
        _icon = new TaskbarIcon
        {
            Icon = _offlineIcon,
            ToolTipText = ToolTipFor(_state),
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

    /// <summary>
    /// Surface the current connectivity honestly: switches the tray icon
    /// variant and the tooltip. Called from wherever connection state
    /// changes (pairing completed, heartbeat failed, auth rejected).
    /// </summary>
    public void SetState(ConnectionState state)
    {
        _state = state;
        _icon.Icon = state switch
        {
            ConnectionState.Connected => _connectedIcon,
            ConnectionState.Error => _errorIcon,
            _ => _offlineIcon,
        };
        _icon.ToolTipText = ToolTipFor(state);
    }

    private static string ToolTipFor(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "PrintShop Companion — connected",
        ConnectionState.Error => "PrintShop Companion — backend error (see logs)",
        _ => "PrintShop Companion — offline",
    };

    /// <summary>Load an icon asset from the app directory (copied at build time).
    /// .ico files load directly; PNG variants render through a bitmap's HICON.</summary>
    private static Icon? LoadIcon(string fileName)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", fileName);
            if (!File.Exists(path)) return null;
            if (Path.GetExtension(fileName).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                using var bmp = new Bitmap(path);
                return Icon.FromHandle(bmp.GetHicon());
            }
            using var stream = File.OpenRead(path);
            return new Icon(stream);
        }
        catch
        {
            return null; // fall back to the system icon
        }
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem
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
        menu.Items.Add(new MenuItem
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

        // Phase 5 (B-017 fix): this item used to be an empty stub while the
        // queue's pause gate was real — a lie in the primary UI surface. It
        // now drives IQueueController (the same gate the backend commands
        // use) and toggles its header to reflect the actual state.
        _pauseItem = new MenuItem { Header = "Pause queue" };
        _pauseItem.Click += async (_, _) =>
        {
            try
            {
                var queue = _services.GetRequiredService<IQueueController>();
                if (queue.IsPaused)
                {
                    await queue.ResumeAsync();
                    _pauseItem.Header = "Pause queue";
                }
                else
                {
                    await queue.PauseAsync();
                    _pauseItem.Header = "Resume queue (paused)";
                }
            }
            catch
            {
                // Queue controller unavailable — leave the header truthful:
                // the pause did NOT happen, so the label stays as-is.
            }
        };
        menu.Items.Add(_pauseItem);

        menu.Items.Add(new MenuItem
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
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
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
        // Phase 5: actually pass the severity (previously a severity icon was chosen
        // but never used, so every balloon looked the same).
        var kind = severity switch
        {
            Services.NotificationService.Severity.Success => H.NotifyIcon.Core.NotificationIcon.Info,
            Services.NotificationService.Severity.Warning => H.NotifyIcon.Core.NotificationIcon.Warning,
            Services.NotificationService.Severity.Error => H.NotifyIcon.Core.NotificationIcon.Error,
            _ => H.NotifyIcon.Core.NotificationIcon.Info,
        };
        _icon.ShowNotification(title, body, kind);
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
