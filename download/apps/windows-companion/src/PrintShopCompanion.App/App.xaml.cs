namespace PrintShopCompanion.App;

using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrintShopCompanion.App.Services;
using PrintShopCompanion.App.Tray;
using PrintShopCompanion.App.ViewModels;
using PrintShopCompanion.App.Views;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure;
using PrintShopCompanion.Infrastructure.Diagnostics;
using PrintShopCompanion.Infrastructure.Queue;
using Serilog;

/// <summary>
/// Application entry point. Sets up DI, Serilog, single-instance guard,
/// magic-link URI handler, tray icon, and the main window.
/// </summary>
public partial class App : Application
{
    private IHost? _host;
    private SingleInstance? _singleInstance;
    private TrayIconHost? _tray;
    private UriActivator? _uriActivator;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance guard. If a second instance is launched (e.g. the OS passes
        // us a magic-link URL while the app is already running), forward the URL and exit.
        _singleInstance = new SingleInstance("PrintShop-Companion-Mutex");
        if (!_singleInstance.AcquireOrForward(e.Args))
        {
            Shutdown(0);
            return;
        }
        _singleInstance.UrlForwarded += (_, url) => _ = HandleMagicLinkAsync(new Uri(url));

        // Serilog bootstrap. Rolling file under %AppData%\PrintShop\Companion\logs\.
        var logDir = SystemDiagnostics.CompanionLogDir();
        Directory.CreateDirectory(logDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .Enrich.With<DeviceTokenRedactor>()
            .WriteTo.File(
                Path.Combine(logDir, "companion-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .WriteTo.EventLog(source: "PrintShop Companion", logName: "Application", manageEventSource: true)
            .CreateLogger();

        try
        {
            Log.Information("PrintShop Companion starting up (v{Version})", typeof(App).Assembly.GetName().Version);

            _host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureAppConfiguration((ctx, cfg) =>
                {
                    cfg.SetBasePath(AppContext.BaseDirectory);
                    cfg.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                    cfg.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
                })
                .ConfigureServices((ctx, services) =>
                {
                    services.Configure<BackendOptions>(ctx.Configuration.GetSection(BackendOptions.SectionName));
                    services.Configure<PrinterOptions>(ctx.Configuration.GetSection(PrinterOptions.SectionName));
                    services.Configure<StorageOptions>(ctx.Configuration.GetSection(StorageOptions.SectionName));
                    services.Configure<AppOptions>(ctx.Configuration.GetSection(AppOptions.SectionName));

                    services.AddPrintShopCompanionInfrastructure();

                    // App-level services
                    services.AddSingleton<ThemeService>();
                    services.AddSingleton<AutoStartService>();
                    services.AddSingleton<INavigationService, NavigationService>();
                    services.AddSingleton<MainWindow>();
                    services.AddSingleton<MainViewModel>();
                    services.AddTransient<SetupViewModel>();
                    services.AddTransient<DashboardViewModel>();
                    services.AddTransient<PrintersViewModel>();
                    services.AddTransient<PrintQueueViewModel>();
                    services.AddTransient<SettingsViewModel>();
                    services.AddTransient<SetupView>();
                    services.AddTransient<DashboardView>();
                    services.AddTransient<PrintersView>();
                    services.AddTransient<PrintQueueView>();
                    services.AddTransient<SettingsView>();

                    services.AddSingleton<TrayIconHost>();
                    services.AddSingleton<UriActivator>();
                })
                .Build();

            await _host.StartAsync();

            // Restore device token if we have one.
            var backend = _host.Services.GetRequiredService<IPrintShopBackend>() as HttpPrintShopBackend;
            if (backend is not null && await backend.RestoreAsync())
                Log.Information("Restored device token; companion is paired");
            else
                Log.Information("Companion is not paired; will start on Setup screen");

            // Replay any in-flight jobs that were left after the previous run.
            var queue = _host.Services.GetRequiredService<IJobQueue>();
            await queue.ReplayInFlightAsync();
            await queue.PurgeTerminalAsync(_host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value.TerminalJobTtl);

            // Start the job processor.
            _host.Services.GetRequiredService<JobProcessor>().StartAsync();

            // Apply theme + auto-start.
            _host.Services.GetRequiredService<ThemeService>().ApplyFromOptions();
            _host.Services.GetRequiredService<AutoStartService>().SyncFromOptions();

            // Tray icon.
            _tray = _host.Services.GetRequiredService<TrayIconHost>();
            _tray.Show();

            // URI activator — handles printshopcompanion:// URLs passed by the OS.
            _uriActivator = _host.Services.GetRequiredService<UriActivator>();

            // If we were launched by the OS with a magic-link URL, handle it now.
            if (e.Args.Length > 0 && e.Args[0].StartsWith("printshopcompanion://", StringComparison.OrdinalIgnoreCase))
                await HandleMagicLinkAsync(new Uri(e.Args[0]));

            // Show main window (or hide to tray if already paired and configured to start minimised).
            var main = _host.Services.GetRequiredService<MainWindow>();
            main.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Companion startup failed");
            MessageBox.Show(
                "PrintShop Companion could not start. Logs have been written to " + logDir,
                "PrintShop Companion", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task HandleMagicLinkAsync(Uri url)
    {
        if (_host is null) return;
        var pairing = _host.Services.GetRequiredService<IPairingService>();
        var nav = _host.Services.GetRequiredService<INavigationService>();
        nav.NavigateTo("Setup");
        var ok = await pairing.HandleMagicLinkAsync(url);
        if (ok)
        {
            nav.NavigateTo("Dashboard");
            var main = _host.Services.GetRequiredService<MainWindow>();
            main.Activate();
            main.Show();
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                var processor = _host.Services.GetRequiredService<JobProcessor>();
                await processor.StopAsync();
                await _host.StopAsync(TimeSpan.FromSeconds(5));
            }
            _tray?.Hide();
            _singleInstance?.Dispose();
            Log.Information("PrintShop Companion shutting down");
            Log.CloseAndFlush();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during shutdown");
        }
        base.OnExit(e);
    }
}
