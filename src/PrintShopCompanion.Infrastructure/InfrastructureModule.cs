namespace PrintShopCompanion.Infrastructure;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Backend;
using PrintShopCompanion.Infrastructure.Pairing;
using PrintShopCompanion.Infrastructure.Printers;
using PrintShopCompanion.Infrastructure.Update;
using PrintShopCompanion.Infrastructure.Sync;
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
        // Shopkeeper backend (JWT-authed) — for the Dashboard, Orders, Analytics, etc.
        services.AddHttpClient<IShopBackend, HttpShopBackend>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptionsMonitor<BackendOptions>>().CurrentValue;
            if (opts.BypassTlsVerification)
            {
                Console.Error.WriteLine(
                    "[WARNING] TLS verification is BYPASSED. This is for local mock backend testing only. " +
                    "NEVER enable Backend:BypassTlsVerification in production.");
            }
            client.Timeout = opts.DownloadTimeout;
        }).ConfigurePrimaryHttpMessageHandler(sp =>
        {
            var opts = sp.GetRequiredService<IOptionsMonitor<BackendOptions>>().CurrentValue;
            if (!opts.BypassTlsVerification) return new HttpClientHandler();
            return new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
        });

        // Print-agent backend (device-token-authed) — for the background print engine.
        services.AddHttpClient<IPrintShopBackend, HttpPrintShopBackend>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptionsMonitor<BackendOptions>>().CurrentValue;
            client.Timeout = opts.DownloadTimeout;
        }).ConfigurePrimaryHttpMessageHandler(sp =>
        {
            var opts = sp.GetRequiredService<IOptionsMonitor<BackendOptions>>().CurrentValue;
            if (!opts.BypassTlsVerification) return new HttpClientHandler();
            return new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
        });

        // Credential store — DPAPI-encrypted, holds the shopkeeper session + device token.
        services.AddSingleton<ICredentialStore, DeviceTokenStore>();
        services.AddSingleton<IJobQueue, SqliteJobQueue>();

        // Realtime (Socket.IO + HTTP poll fallback)
        services.AddHttpClient<HttpFallbackPoller>();
        services.AddSingleton<SocketIoRealtimeClient>();
        services.AddSingleton<HttpFallbackPoller>();

        // Pairing — kept for backward compat with the v0.1 magic-link flow.
        // The v0.2 shopkeeper app uses email+password login via IShopBackend.LoginAsync,
        // but the magic-link flow is still wired so existing paired devices continue to work.
        services.AddSingleton<IPairingService, MagicLinkPairingService>();

        // Printers — SumatraPDF (primary, bundled .exe) + raw GDI fallback (ESC/POS / label printers)
        services.AddSingleton<SumatraPdfPrinter>();
        services.AddSingleton<RawGdiPrinter>();
        services.AddSingleton<IPdfRenderer, CompositePdfRenderer>();
        services.AddSingleton<IPrinterService, WindowsPrinterService>();

        // Job processor (started by the App or Worker; not hosted automatically here)
        services.AddSingleton<JobProcessor>();

        // Auto-update — checks GitHub releases for newer versions
        services.AddHttpClient<IUpdateService, GitHubUpdateService>();

        // Sync engine — pushes pending ops, pulls updates
        services.AddSingleton<IPendingOpsQueue, SqlitePendingOpsQueue>();
        services.AddSingleton<SyncEngine>();

        return services;
    }
}

