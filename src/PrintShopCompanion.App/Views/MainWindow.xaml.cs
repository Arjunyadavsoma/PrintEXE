namespace PrintShopCompanion.App.Views;

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using PrintShopCompanion.App.Services;
using PrintShopCompanion.App.Tray;
using PrintShopCompanion.Core.Contracts;

public partial class MainWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly INavigationService _nav;
    private IUpdateService? _updateService;

    // Guards against the user double-clicking "Update Now" and triggering
    // multiple concurrent downloads. The log showed 6 download attempts in
    // 13 seconds, which means the button was clicked 6 times.
    private bool _updateInProgress;

    public MainWindow(IServiceProvider services, INavigationService nav)
    {
        _services = services;
        _nav = nav;
        InitializeComponent();

        // Read the version from the assembly so the sidebar label and
        // startup log always reflect the actually-running build, not a
        // hardcoded literal.
        VersionLabel.Text = AppVersion.Prefixed;

        _nav.ViewChanged += OnViewChanged;
        OnViewChanged();

        // CRITICAL: App.xaml sets ShutdownMode="OnExplicitShutdown" so the
        // process keeps running when the window closes (intentional — the
        // tray icon keeps the app alive). But this means clicking the X
        // button just hides the window without releasing the single-instance
        // mutex. The next launch hits "Another instance is already running"
        // and the user has to kill the process from Task Manager.
        //
        // Fix: wire the Closing event. When the user clicks X:
        //   - If MinimizeToTrayOnClose is true (the App option), minimize to
        //     tray instead of shutting down. This is the "app lives in tray"
        //     behavior some users want.
        //   - Otherwise, call Application.Shutdown() so OnExit fires, the
        //     mutex is released, and the process exits cleanly. The user
        //     can immediately relaunch.
        //
        // The default is to shut down — this fixes the "cannot reopen after
        // closing" bug. Users who want tray behavior can enable it in
        // Settings → Application → Minimize to tray on close.
        Closing += OnMainWindowClosing;
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        // Read the MinimizeToTrayOnClose option from the App options.
        // Default is false (close = exit), which fixes the "cannot reopen"
        // bug. If the user opts in, close = minimize to tray.
        try
        {
            var opts = _services.GetService<Microsoft.Extensions.Options.IOptionsMonitor<PrintShopCompanion.Core.Options.AppOptions>>();
            if (opts?.CurrentValue?.MinimizeToTrayOnClose == true)
            {
                // Minimize to tray instead of closing.
                e.Cancel = true;
                Hide();
                var tray = _services.GetService<TrayIconHost>();
                tray?.ShowBalloon("PrintShop Companion",
                    "Still running in the background. Click the tray icon to reopen.",
                    PrintShopCompanion.App.Services.NotificationService.Severity.Success);
                return;
            }
        }
        catch
        {
            // If option-reading fails, fall through to the default (close =
            // exit). Don't keep the user's window stuck open.
        }

        // Default: actually shut down the app so the mutex is released and
        // the user can reopen immediately.
        // We DON'T cancel the close — the window will close, but because
        // ShutdownMode is OnExplicitShutdown, the process would stay alive.
        // So we explicitly call Shutdown() here, which fires OnExit, which
        // releases the mutex.
        System.Windows.Application.Current.Shutdown();
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
            SidebarCol.Width = new GridLength(232);
            Sidebar.Visibility = Visibility.Visible;
        }

        // Highlight the active nav button (and demote the rest)
        foreach (var name in new[] { "Overview", "Orders", "PrintQueue", "Printers", "SyncCenter", "Settings" })
        {
            var btn = FindName("Nav" + name) as Button;
            if (btn == null) continue;
            var isActive = name == current;
            btn.Style = (Style)FindResource(isActive ? "NavItemActive" : "NavItem");
        }

        // Show the right view
        ContentArea.Content = current switch
        {
            "Login" => _services.GetRequiredService<LoginView>(),
            "Overview" => _services.GetRequiredService<OverviewView>(),
            "Orders" => _services.GetRequiredService<OrdersView>(),
            "PrintQueue" => _services.GetRequiredService<PrintQueueView>(),
            "Printers" => _services.GetRequiredService<PrintersView>(),
            "SyncCenter" => _services.GetRequiredService<SyncCenterView>(),
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
        UpdateBannerState(UpdateState.Available, $"A new version is available — v{updateService.CurrentVersion} → v{updateService.LatestVersion}");
        UpdateBanner.Visibility = Visibility.Visible;

        // Subscribe to state changes so the banner updates live as the
        // install progresses through Downloading -> Verifying -> Extracting
        // -> Installing -> Restarting.
        updateService.StateChanged += OnUpdateStateChanged;
    }

    private void OnUpdateStateChanged(UpdateState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnUpdateStateChanged(state));
            return;
        }
        UpdateBannerState(state, StateToLabel(state));
    }

    private static string StateToLabel(UpdateState state) => state switch
    {
        UpdateState.Idle => "Update check idle",
        UpdateState.Checking => "Checking for updates...",
        UpdateState.Available => "A new version is available",
        UpdateState.Downloading => "Downloading update...",
        UpdateState.Verifying => "Verifying download integrity...",
        UpdateState.Extracting => "Extracting files...",
        UpdateState.Installing => "Installing update...",
        UpdateState.Restarting => "Restarting...",
        UpdateState.UpToDate => "You're up to date",
        UpdateState.Failed => "Update failed — click Update Now to retry",
        _ => state.ToString()
    };

    private void UpdateBannerState(UpdateState state, string label)
    {
        UpdateText.Text = label;
        // Show progress bar for the in-flight states
        var showProgress = state is UpdateState.Downloading or UpdateState.Verifying
            or UpdateState.Extracting or UpdateState.Installing or UpdateState.Restarting;
        UpdateProgress.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressText.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;

        // Update button label
        UpdateButton.Content = state switch
        {
            UpdateState.Downloading => "Downloading...",
            UpdateState.Verifying => "Verifying...",
            UpdateState.Extracting => "Extracting...",
            UpdateState.Installing => "Installing...",
            UpdateState.Restarting => "Restarting...",
            UpdateState.Failed => "Retry",
            _ => "Update Now"
        };
        UpdateButton.IsEnabled = state is UpdateState.Available or UpdateState.Failed;

        // Color the state icon
        UpdateStateIcon.Foreground = state switch
        {
            UpdateState.Downloading or UpdateState.Verifying or UpdateState.Extracting
                => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06)),
            UpdateState.Installing or UpdateState.Restarting
                => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06)),
            UpdateState.Failed
                => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26)),
            _
                => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06))
        };
    }

    private void UpdateDismiss_Click(object sender, RoutedEventArgs e)
    {
        // Hide the banner. The next time the app checks for updates (on
        // restart), the banner will reappear if a new version is still
        // available.
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_updateService == null) return;

        // Hard guard — even if the user mashes the button, only one install
        // can run at a time.
        if (_updateInProgress) return;
        _updateInProgress = true;
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "Preparing...";
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgressText.Visibility = Visibility.Visible;
        UpdateProgressText.Text = "Starting download...";

        var progress = new Progress<int>(p =>
        {
            if (!Dispatcher.CheckAccess())
                Dispatcher.Invoke(() => UpdateProgressUI(p));
            else
                UpdateProgressUI(p);
        });

        try
        {
            await _updateService.InstallUpdateAsync(progress);
            // The install method calls Shutdown() at the end. If we somehow
            // reach here, the shutdown was deferred — fall through.
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"Update failed: {ex.Message}\n\nYou can download the latest version manually from:\nhttps://github.com/Arjunyadavsoma/PrintEXE/releases/latest",
                "Update failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            // Reset state so the user can try again later
            _updateInProgress = false;
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = "Update Now";
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateProgressText.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateProgressUI(int percent)
    {
        UpdateProgress.Value = percent;
        UpdateProgressText.Text = percent switch
        {
            < 10 => "Connecting to GitHub...",
            < 40 => "Downloading update...",
            < 50 => "Verifying SHA-256...",
            < 65 => "Extracting files...",
            < 85 => "Preparing updater...",
            _   => "Restarting..."
        };
        UpdateButton.Content = percent < 100 ? "Updating..." : "Restarting...";
    }
}
