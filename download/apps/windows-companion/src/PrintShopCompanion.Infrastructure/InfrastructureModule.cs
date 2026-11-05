namespace PrintShopCompanion.Infrastructure;

using Microsoft.Extensions.DependencyInjection;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Infrastructure.Backend;
using PrintShopCompanion.Infrastructure.Pairing;
using PrintShopCompanion.Infrastructure.Printers;
using PrintShopCompanion.Infrastructure.Queue;
using PrintShopCompanion.Infrastructure.Storage;

/// <summary>
/// DI registration entry point. Call <c>services.AddPrintShopCompanionInfrastructure()</c>
/// from the App and the Worker to wire up the backend, queue, printer, and
/// pairing services.
/// </summary>
public static class InfrastructureModule
{
    public static IServiceCollection AddPrintShopCompanionInfrastructure(this IServiceCollection services)
    {
        // Backend (HttpClientFactory-backed so we get Polly retries, logging handler, etc.)
        services.AddHttpClient<IPrintShopBackend, HttpPrintShopBackend>();

        services.AddSingleton<IDeviceTokenStore, DeviceTokenStore>();
        services.AddSingleton<IJobQueue, SqliteJobQueue>();

        // Realtime (Socket.IO + HTTP poll fallback)
        services.AddHttpClient<HttpFallbackPoller>();
        services.AddSingleton<SocketIoRealtimeClient>();
        services.AddSingleton<HttpFallbackPoller>();

        // Pairing
        services.AddSingleton<IPairingService, MagicLinkPairingService>();

        // Printers — three renderers + composite that walks the fallback chain
        services.AddSingleton<PdfiumPdfRenderer>();
        services.AddSingleton<SumatraPdfPrinter>();
        services.AddSingleton<RawGdiPrinter>();
        services.AddSingleton<IPdfRenderer, CompositePdfRenderer>();
        services.AddSingleton<IPrinterService, WindowsPrinterService>();

        // Job processor (started by the App or Worker; not hosted automatically here)
        services.AddSingleton<JobProcessor>();

        return services;
    }
}
