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
using PrintShopCompanion.Infrastructure.Queue;
using PrintShopCompanion.Infrastructure.Sync;
using PrintShopCompanion.Infrastructure.Update;
using Serilog;

public partial class App : Application
{
    private IHost? _host;
    private SingleInstance? _singleInstance;
    private TrayIconHost? _tray;

    // Single shared CancellationTokenSource for ALL background work. Cancelled
    // once on shutdown so every background task stops in a coordinated way
    // instead of each component owning its own CTS that fires at slightly
    // different times. The previous code had separate CTSes in SyncEngine,
    // JobProcessor, and the fire-and-forget startup Task.Run — leading to
    // 2s + 2s + 3s = 7s of shutdown latency in the worst case.
    private CancellationTokenSource? _appCts;

    // Background task references — needed so we can WaitAsync for them on
    // shutdown instead of letting them die mid-DB-write.
    private Task? _startupTask;
    private Task? _updateCheckTask;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // CLI args
        if (e.Args.Length > 0)
        {
            if (e.Args[0] == "--register-autostart") { RegisterAutoStart(); Shutdown(0); return; }
            if (e.Args[0] == "--unregister-autostart") { UnregisterAutoStart(); Shutdown(0); return; }
            if (e.Args[0] == "--version")
            {
                Console.WriteLine(AppVersion.Short);
                Shutdown(0);
                return;
            }
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

        Log.Information("=== PrintShop Companion v{Version} starting ===", AppVersion.Short);

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

            // Create the shared CTS NOW so every subsequent background task
            // shares it. We pass its Token to SyncEngine.Start,
            // JobProcessor.StartAsync, and the fire-and-forget startup Task.
            _appCts = new CancellationTokenSource();

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
                    services.AddTransient<SyncCenterViewModel>();
                    services.AddTransient<SettingsViewModel>();
                    services.AddTransient<LoginView>();
                    services.AddTransient<OverviewView>();
                    services.AddTransient<OrdersView>();
                    services.AddTransient<PrintersView>();
                    services.AddTransient<PrintQueueView>();
                    services.AddTransient<SyncCenterView>();
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

            // ALL ASYNC WORK happens AFTER the window is shown.
            // We capture the Task so OnExit can WaitAsync for it before disposing.
            _startupTask = Task.Run(async () =>
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

                    // Start background services — pass the shared token so they
                    // all stop together on shutdown.
                    var queue = _host.Services.GetRequiredService<IJobQueue>();
                    await queue.ReplayInFlightAsync();
                    _host.Services.GetRequiredService<JobProcessor>().StartAsync(_appCts.Token);
                    Log.Information("JobProcessor started");

                    var sync = _host.Services.GetRequiredService<SyncEngine>();
                    sync.Start(_appCts.Token);
                    Log.Information("SyncEngine started");
                }
                catch (OperationCanceledException) when (_appCts.IsCancellationRequested)
                {
                    Log.Information("Background startup cancelled by shutdown");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Background startup failed (non-fatal)");
                }
            }, _appCts.Token);

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

            // Update check (fire-and-forget but tracked for shutdown)
            _updateCheckTask = Task.Run(async () =>
            {
                try
                {
                    // First, check for a sentinel file from a previously failed
                    // .bat updater. If present, warn the user that the previous
                    // update failed.
                    var update = _host.Services.GetRequiredService<IUpdateService>();
                    if (update is GitHubUpdateService ghUpdate)
                    {
                        var failureReason = ghUpdate.CheckForFailedUpdateSentinel();
                        if (failureReason is not null)
                        {
                            Log.Warning("Previous update failed: {Reason}", failureReason);
                            Dispatcher.Invoke(() =>
                            {
                                MessageBox.Show(
                                    $"A previous auto-update failed ({failureReason}).\n\n" +
                                    "The app rolled back to the previous version. " +
                                    "You can retry from the in-app banner, or download manually from\n" +
                                    "https://github.com/Arjunyadavsoma/PrintEXE/releases/latest",
                                    "Previous update failed",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Warning);
                            });
                        }
                    }

                    if (await update.CheckForUpdatesAsync())
                    {
                        Log.Information("Update available: {Current} -> {Latest}", update.CurrentVersion, update.LatestVersion);
                        Dispatcher.Invoke(() => main.ShowUpdateBanner(update));
                    }
                }
                catch (OperationCanceledException) when (_appCts.IsCancellationRequested) { }
                catch (Exception ex) { Log.Warning(ex, "Update check failed"); }
            }, _appCts.Token);

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

        // 1. Release mutex FIRST so another instance can launch immediately
        //    while we clean up in the background.
        try { _singleInstance?.Dispose(); Log.Information("Mutex released"); } catch { }

        // 2. Signal ALL background work to stop
        try { _appCts?.Cancel(); Log.Information("Cancellation requested"); } catch { }

        // 3. Wait for the background startup task + update check task with a
        //    BOUNDED total shutdown of 5 seconds. We don't want a hung task
        //    to keep the process alive forever, but we also don't want to
        //    cut off a SQLite write mid-flight.
        var shutdownDeadline = TimeSpan.FromSeconds(5);
        try
        {
            var tasks = new List<Task>();
            if (_startupTask is not null) tasks.Add(_startupTask);
            if (_updateCheckTask is not null) tasks.Add(_updateCheckTask);

            if (tasks.Count > 0)
            {
                var allTask = Task.WhenAll(tasks);
                var winner = Task.WhenAny(allTask, Task.Delay(shutdownDeadline)).Result;
                if (winner != allTask)
                    Log.Warning("Background tasks did not finish within {Seconds}s — proceeding with shutdown",
                        shutdownDeadline.TotalSeconds);
                else
                    Log.Information("Background tasks finished cleanly");
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Background task wait failed (non-fatal)"); }

        // 4. Stop the JobProcessor and SyncEngine (now they should already
        //    be cancelled via the shared token; this call disposes their CTSes).
        if (_host != null)
        {
            try
            {
                var proc = _host.Services.GetRequiredService<JobProcessor>();
                proc.StopAsync().Wait(TimeSpan.FromSeconds(2));
            }
            catch { }

            try
            {
                var sync = _host.Services.GetRequiredService<SyncEngine>();
                sync.StopAsync().Wait(TimeSpan.FromSeconds(2));
            }
            catch { }

            // 5. Stop and dispose the DI host.
            try { _host.StopAsync(TimeSpan.FromSeconds(2)).Wait(); _host.Dispose(); }
            catch { }
        }

        // 6. Dispose the shared CTS.
        try { _appCts?.Dispose(); } catch { }

        // 7. Hide the tray icon.
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
