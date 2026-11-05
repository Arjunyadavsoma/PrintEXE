namespace PrintShopCompanion.Infrastructure;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Backend;
using PrintShopCompanion.Infrastructure.Pairing;
using PrintShopCompanion.Infrastructure.Printers;
using PrintShopCompanion.Infrastructure.Queue;
using PrintShopCompanion.Infrastructure.Storage;
using System.Net.Http;

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
        // Honours BackendOptions.BypassTlsVerification — DEV ONLY. The companion logs
        // a loud warning if this is enabled.
        services.AddHttpClient<IPrintShopBackend, HttpPrintShopBackend>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptionsMonitor<BackendOptions>>().CurrentValue;
            if (opts.BypassTlsVerification && client.BaseAddress is not null)
            {
                // Bypass handler is wired in the HttpClient factory registration below.
                // (HttpClient itself does not have a direct TLS-bypass knob; we use
                //  a custom handler chain.) Just log a loud warning here.
                Console.Error.WriteLine(
                    "[WARNING] TLS verification is BYPASSED. This is for local mock backend testing only. " +
                    "NEVER enable Backend:BypassTlsVerification in production.");
            }
            client.Timeout = opts.DownloadTimeout;
        }).ConfigurePrimaryHttpMessageHandler(sp =>
        {
            var opts = sp.GetRequiredService<IOptionsMonitor<BackendOptions>>().CurrentValue;
            if (!opts.BypassTlsVerification)
            {
                return new HttpClientHandler();
            }
            // Bypass the TLS cert check — local mock only.
            return new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
        });

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

