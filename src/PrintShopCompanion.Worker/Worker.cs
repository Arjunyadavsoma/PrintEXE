namespace PrintShopCompanion.Worker;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure;
using PrintShopCompanion.Infrastructure.Backend;
using PrintShopCompanion.Infrastructure.Diagnostics;
using PrintShopCompanion.Infrastructure.Queue;
using Serilog;
using Serilog.Events;

/// <summary>
/// Headless worker. Same engine as the WPF app, minus the UI. Designed to be
/// installed as a Windows Service so the companion can keep processing print
/// jobs even when no one is logged in (kiosk / unattended-shop deployments).
///
/// <para>Service install (admin one-time):</para>
/// <code>
/// sc.exe create PrintShopCompanionWorker binPath= "C:\Program Files\PrintShop\Companion\PrintShopCompanion.Worker.exe" start= auto
/// sc.exe description PrintShopCompanionWorker "Background print-job processor for PrintShop Companion"
/// sc.exe start PrintShopCompanionWorker
/// </code>
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var logDir = SystemDiagnostics.CompanionLogDir();
        System.IO.Directory.CreateDirectory(logDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                System.IO.Path.Combine(logDir, "worker-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .WriteTo.EventLog("PrintShop Companion Worker")
            .CreateLogger();

        try
        {
            Log.Information("PrintShop Companion Worker starting");
            await Host.CreateDefaultBuilder(args)
                .UseWindowsService(o => o.ServiceName = "PrintShopCompanionWorker")
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

                    services.AddPrintShopCompanionInfrastructure();
                    services.AddHostedService<Worker>();
                })
                .Build()
                .RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Worker startup failed");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}

/// <summary>
/// Background worker. Restores the device token, replays in-flight jobs,
/// starts the Socket.IO realtime client (with HTTP poll fallback), and
/// runs the durable job processor loop. All are stopped cleanly on shutdown.
/// </summary>
public sealed class Worker : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly HttpPrintShopBackend? _backend;
    private readonly IJobQueue _queue;
    private readonly SocketIoRealtimeClient _realtime;
    private readonly JobProcessor _processor;
    private readonly IOptions<StorageOptions> _storage;

    public Worker(IServiceProvider services, IJobQueue queue, SocketIoRealtimeClient realtime, JobProcessor processor, IOptions<StorageOptions> storage)
    {
        _services = services;
        // HttpPrintShopBackend is registered as a singleton via HttpClientFactory, but we
        // reach for it through IPrintShopBackend to call RestoreAsync.
        _backend = services.GetService<IPrintShopBackend>() as HttpPrintShopBackend;
        _queue = queue;
        _realtime = realtime;
        _processor = processor;
        _storage = storage;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_backend is not null && await _backend.RestoreAsync(cancellationToken))
            Log.Information("Worker restored device token");
        else
            Log.Warning("Worker has no device token; will not process jobs until paired");

        await _queue.ReplayInFlightAsync(cancellationToken);
        await _queue.PurgeTerminalAsync(_storage.Value.TerminalJobTtl, cancellationToken);

        await _realtime.StartAsync(cancellationToken);
        await _processor.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _processor.StopAsync(cancellationToken);
        await _realtime.StopAsync();
    }
}
