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
            if (e.Args[0] == "--version") { Console.WriteLine("0.4.1"); Shutdown(0); return; }
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

        Log.Information("=== PrintShop Companion v0.4.1 starting ===");

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
            Log.Information("Mutex acquired");

            // DI — build synchronously, no async calls here
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

            // StartAsync is synchronous-safe (it doesn't use the Dispatcher)
            _host.StartAsync().Wait();
            Log.Information("DI host started");

            // CHECK for saved credentials SYNCHRONOUSLY (no async, no .Result, no deadlock)
            // Just check if the file exists — we'll load it in the background after the window shows
            var credPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PrintShop", "Companion", "device.dat");
            bool hasSavedCredentials = File.Exists(credPath);
            Log.Information("Saved credentials: {Has}", hasSavedCredentials ? "FOUND" : "NOT FOUND");

            // Navigate to the right view
            var nav = _host.Services.GetRequiredService<INavigationService>();
            if (hasSavedCredentials)
                nav.NavigateTo("Overview");  // Will try to restore session in background
            else
                nav.NavigateTo("Login");
            Log.Information("Navigated to: {View}", nav.CurrentView);

            // SHOW THE WINDOW IMMEDIATELY — before any async work
            var main = _host.Services.GetRequiredService<MainWindow>();
            main.Show();
            Log.Information("Main window shown");

            // ALL ASYNC WORK happens AFTER the window is shown (fire-and-forget, no blocking)
            _ = Task.Run(async () =>
            {
                try
                {
                    // Restore credentials
                    if (hasSavedCredentials)
                    {
                        var store = _host.Services.GetRequiredService<ICredentialStore>();
                        var creds = await store.LoadAsync();
                        if (creds?.Session != null)
                        {
                            var shopBackend = _host.Services.GetRequiredService<IShopBackend>() as HttpShopBackend;
                            if (shopBackend != null)
                            {
                                shopBackend.RestoreSession(creds.Session);
                                Log.Information("Session restored in background");
                            }
                        }
                        else
                        {
                            // Credentials file exists but is corrupt/empty — go to login
                            Dispatcher.Invoke(() => nav.NavigateTo("Login"));
                            Log.Information("Credentials invalid — redirected to Login");
                        }
                    }

                    // Start background services
                    var queue = _host.Services.GetRequiredService<IJobQueue>();
                    await queue.ReplayInFlightAsync();
                    _host.Services.GetRequiredService<JobProcessor>().StartAsync();
                    Log.Information("JobProcessor started");

                    var sync = _host.Services.GetRequiredService<Infrastructure.Sync.SyncEngine>();
                    sync.Start();
                    Log.Information("SyncEngine started");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Background startup failed (non-fatal)");
                }
            });

            // Tray (synchronous — safe)
            try
            {
                _tray = _host.Services.GetRequiredService<TrayIconHost>();
                _tray.Show();
                Log.Information("Tray shown");
            }
            catch (Exception ex) { Log.Warning(ex, "Tray setup failed"); }

            // Auto-start (synchronous — safe)
            try { _host.Services.GetRequiredService<AutoStartService>().SyncFromOptions(); } catch { }

            // Update check (fire-and-forget — no blocking)
            _ = Task.Run(async () =>
            {
                try
                {
                    var update = _host.Services.GetRequiredService<IUpdateService>();
                    if (await update.CheckForUpdatesAsync())
                    {
                        Log.Information("Update available: {Current} -> {Latest}", update.CurrentVersion, update.LatestVersion);
                        Dispatcher.Invoke(() => main.ShowUpdateBanner(update));
                    }
                }
                catch (Exception ex) { Log.Warning(ex, "Update check failed"); }
            });

            Log.Information("=== Startup complete ===");
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

        // 1. Release mutex FIRST
        try { _singleInstance?.Dispose(); Log.Information("Mutex released"); } catch { }

        // 2. Stop background services
        if (_host != null)
        {
            try
            {
                var sync = _host.Services.GetRequiredService<Infrastructure.Sync.SyncEngine>();
                sync.StopAsync().Wait(TimeSpan.FromSeconds(2));
            }
            catch { }

            try
            {
                var proc = _host.Services.GetRequiredService<JobProcessor>();
                proc.StopAsync().Wait(TimeSpan.FromSeconds(2));
            }
            catch { }

            try { _host.StopAsync(TimeSpan.FromSeconds(3)).Wait(); _host.Dispose(); }
            catch { }
        }

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
            var script = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut(\"{Path.Combine(dir, "PrintShop Companion.lnk")}\");$s.TargetPath=\"{exe}\";$s.Save()";
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
