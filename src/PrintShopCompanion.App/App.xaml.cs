namespace PrintShopCompanion.App;

using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PrintShopCompanion.App.Native;
using PrintShopCompanion.App.Services;
using PrintShopCompanion.App.Tray;
using PrintShopCompanion.App.ViewModels;
using PrintShopCompanion.App.Views;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure;
using PrintShopCompanion.Infrastructure.Backend;
using PrintShopCompanion.Infrastructure.Diagnostics;
using PrintShopCompanion.Infrastructure.Queue;
using PrintShopCompanion.Infrastructure.Sync;
using Serilog;

public partial class App : Application
{
    private IHost? _host;
    private SingleInstance? _singleInstance;
    private TrayIconHost? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // CLI args
        if (e.Args.Length > 0)
        {
            if (e.Args[0] == "--register-autostart") { RegisterAutoStart(); Shutdown(0); return; }
            if (e.Args[0] == "--unregister-autostart") { UnregisterAutoStart(); Shutdown(0); return; }
            if (e.Args[0] == "--version") { Console.WriteLine("0.4.0"); Shutdown(0); return; }
        }

        // Serilog
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PrintShop", "Companion", "logs");
        Directory.CreateDirectory(logDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(logDir, "companion-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();

        Log.Information("=== PrintShop Companion v0.4.0 starting ===");

        try
        {
            // Single instance — acquire mutex
            _singleInstance = new SingleInstance("PrintShop-Companion-Mutex-v4");
            if (!_singleInstance.AcquireOrForward(e.Args))
            {
                Log.Information("Another instance is already running — exiting");
                Shutdown(0);
                return;
            }

            // DI
            _host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureAppConfiguration((ctx, cfg) =>
                {
                    cfg.SetBasePath(AppContext.BaseDirectory);
                    cfg.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                })
                .ConfigureServices((ctx, services) =>
                {
                    services.Configure<BackendOptions>(ctx.Configuration.GetSection(BackendOptions.SectionName));
                    services.Configure<PrinterOptions>(ctx.Configuration.GetSection(PrinterOptions.SectionName));
                    services.Configure<StorageOptions>(ctx.Configuration.GetSection(StorageOptions.SectionName));
                    services.Configure<AppOptions>(ctx.Configuration.GetSection(AppOptions.SectionName));

                    services.AddPrintShopCompanionInfrastructure();

                    services.AddSingleton<INavigationService, NavigationService>();
                    services.AddSingleton<MainWindow>();
                    services.AddTransient<LoginViewModel>();
                    services.AddTransient<OverviewViewModel>();
                    services.AddTransient<OrdersViewModel>();
                    services.AddTransient<PrintersViewModel>();
                    services.AddTransient<PrintQueueViewModel>();
                    services.AddTransient<SettingsViewModel>();
                    services.AddTransient<LoginView>();
                    services.AddTransient<OverviewView>();
                    services.AddTransient<OrdersView>();
                    services.AddTransient<PrintersView>();
                    services.AddTransient<PrintQueueView>();
                    services.AddTransient<SettingsView>();
                    services.AddSingleton<TrayIconHost>();
                })
                .Build();

            _host.StartAsync().Wait();
            Log.Information("DI host started");

            // Restore saved session (check if we have valid credentials)
            var creds = _host.Services.GetRequiredService<ICredentialStore>().LoadAsync().Result;
            bool hasSession = creds?.Session != null;

            // Build + show window
            var nav = _host.Services.GetRequiredService<INavigationService>();
            if (hasSession)
            {
                // Try to restore the session
                var shopBackend = _host.Services.GetRequiredService<IShopBackend>() as HttpShopBackend;
                if (shopBackend != null)
                {
                    shopBackend.RestoreSession(creds.Session);
                    nav.NavigateTo("Overview");
                    Log.Information("Session restored — going to Overview");
                }
                else
                {
                    nav.NavigateTo("Login");
                }
            }
            else
            {
                nav.NavigateTo("Login");
                Log.Information("No session — going to Login");
            }

            var main = _host.Services.GetRequiredService<MainWindow>();
            main.Show();
            Log.Information("Main window shown");

            // Start background services (non-fatal)
            try
            {
                var queue = _host.Services.GetRequiredService<IJobQueue>();
                queue.ReplayInFlightAsync().Wait();
                _host.Services.GetRequiredService<JobProcessor>().StartAsync();
                Log.Information("JobProcessor started");

                var sync = _host.Services.GetRequiredService<SyncEngine>();
                sync.Start();
                Log.Information("SyncEngine started");
            }
            catch (Exception bgEx)
            {
                Log.Warning(bgEx, "Background service start failed (non-fatal)");
            }

            // Tray
            _tray = _host.Services.GetRequiredService<TrayIconHost>();
            _tray.Show();

            // Auto-start
            try { _host.Services.GetRequiredService<AutoStartService>().SyncFromOptions(); } catch { }

            // Check for updates
            try
            {
                var update = _host.Services.GetRequiredService<IUpdateService>();
                if (update.CheckForUpdatesAsync().Result)
                {
                    Log.Information("Update available: {Current} -> {Latest}", update.CurrentVersion, update.LatestVersion);
                    Dispatcher.Invoke(() => main.ShowUpdateBanner(update));
                }
            }
            catch (Exception ex) { Log.Warning(ex, "Update check failed"); }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            MessageBox.Show($"PrintShop Companion could not start.\n\n{ex.Message}\n\nLogs: {logDir}",
                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("=== Shutdown starting ===");

        // 1. Release mutex FIRST — even if everything else hangs, the app can reopen
        try { _singleInstance?.Dispose(); Log.Information("Mutex released"); } catch { }

        // 2. Stop background services
        if (_host != null)
        {
            try
            {
                var sync = _host.Services.GetRequiredService<SyncEngine>();
                sync.StopAsync().Wait(TimeSpan.FromSeconds(2));
                Log.Information("SyncEngine stopped");
            }
            catch { }

            try
            {
                var proc = _host.Services.GetRequiredService<JobProcessor>();
                proc.StopAsync().Wait(TimeSpan.FromSeconds(2));
                Log.Information("JobProcessor stopped");
            }
            catch { }

            try { _host.StopAsync(TimeSpan.FromSeconds(3)).Wait(); _host.Dispose(); Log.Information("Host disposed"); }
            catch { }
        }

        // 3. Tray
        try { _tray?.Hide(); } catch { }

        Log.Information("=== Shutdown complete ===");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static void RegisterAutoStart()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
            Directory.CreateDirectory(dir);
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            // Simple shortcut via PowerShell
            var script = $"(New-Object -ComObject WScript.Shell).CreateShortcut(\"{Path.Combine(dir, "PrintShop Companion.lnk")}\");$s.TargetPath=\"{exe}\";$s.Save()";
            System.Diagnostics.Process.Start("powershell.exe", $"-NoProfile -Command \"{script}\"")?.WaitForExit(3000);
        }
        catch { }
    }

    private static void UnregisterAutoStart()
    {
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", "Startup", "PrintShop Companion.lnk");
            if (File.Exists(p)) File.Delete(p);
        }
        catch { }
    }
}
