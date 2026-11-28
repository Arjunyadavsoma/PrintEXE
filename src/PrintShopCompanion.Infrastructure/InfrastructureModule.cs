namespace PrintShopCompanion.Infrastructure;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Infrastructure.Storage;
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
        // v1 contract Appendix C: bind the PrintShop:* section. Where PrintShop
        // fields are set, they override the historical Backend:* section.
        // Both sections stay bound; PrintShop wins where present.
        services.AddOptions<PrintShopOptions>().BindConfiguration(PrintShopOptions.SectionName);
        // Apply the PrintShop overrides onto BackendOptions via a post-configure
        // that can DI-resolve IOptions<PrintShopOptions>.
        services.AddSingleton<IPostConfigureOptions<BackendOptions>, BackendOptionsPostConfigure>();
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

        // v0.5: Local storage layout + SQLCipher key store + file retention manager.
        // These must be registered BEFORE SqliteJobQueue because SqliteJobQueue
        // depends on QueueKeyStore for its encryption key.
        services.AddSingleton<LocalStorageLayout>();
        services.AddSingleton<QueueKeyStore>();
        services.AddSingleton<FileRetentionManager>();

        // v0.7.4 fix: register the CONCRETE type first, then map the interface
        // to the SAME singleton. DurableCommandStore (and any future consumer)
        // takes SqliteJobQueue by its concrete type — with only the interface
        // registration, GetRequiredService<SqliteJobQueue>() threw
        // "Unable to resolve service for type 'SqliteJobQueue' while attempting
        // to activate 'DurableCommandStore'", which killed StartCloudConnection
        // (Socket.IO realtime + heartbeat timer + command poller never started;
        // the server marked the device offline 30s later). Mirrors the
        // JobProcessor/IQueueController pattern below.
        // Phase 9 (F-1): ONE recovery state shared by the queue, the job
        // processor, diagnostics and the UI — recovery outcome is visible
        // everywhere and the repeat guard is process-wide.
        services.AddSingleton<DatabaseRecoveryState>();
        services.AddSingleton<SqliteJobQueue>();
        services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<SqliteJobQueue>());

        // Realtime (Socket.IO + HTTP poll fallback)
        services.AddHttpClient<HttpFallbackPoller>();
        services.AddSingleton<SocketIoRealtimeClient>();
        services.AddSingleton<HttpFallbackPoller>();

        // Pairing — kept for backward compat with the v0.1 magic-link flow.
        // The v0.2 shopkeeper app uses email+password login via IShopBackend.LoginAsync,
        // but the magic-link flow is still wired so existing paired devices continue to work.
        services.AddSingleton<IPairingService, MagicLinkPairingService>();

        // Printers — SumatraPDF (primary, bundled .exe) + raw GDI fallback (ESC/POS / label printers).
        // The composite takes a name → renderer map so the whole printer engine stays
        // platform-neutral: on the neutral net10.0 build only Sumatra exists; the windows
        // TFM additionally registers the raw-GDI fallback and the spooler integration.
        services.AddSingleton<SumatraPdfPrinter>();
#if WINDOWS
        services.AddSingleton<RawGdiPrinter>();
        services.AddSingleton<IPrinterService, WindowsPrinterService>();
#endif
        services.AddSingleton<IPdfRenderer>(sp =>
        {
            var byName = new Dictionary<string, IPdfRenderer>(StringComparer.OrdinalIgnoreCase)
            {
                ["sumatra"] = sp.GetRequiredService<SumatraPdfPrinter>(),
#if WINDOWS
                ["gdi"] = sp.GetRequiredService<RawGdiPrinter>(),
#endif
            };
            return new CompositePdfRenderer(
                byName,
                sp.GetRequiredService<IOptionsMonitor<PrinterOptions>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CompositePdfRenderer>>());
        });

        // Managed PDF inspection (decision D-04) — validates documents before
        // they are claimed and spooled. Platform-neutral: PDFium runs on any OS.
        services.AddSingleton<Core.Contracts.IPdfInspector, Pdf.PdfDocumentInspector>();

        // Job processor (started by the App or Worker; not hosted automatically here).
        // Phase 3: also exposed as IQueueController so the CommandExecutor can
        // drive pause/resume without a DI cycle. Both registrations resolve to
        // the SAME singleton instance (the factory returns the JobProcessor).
        services.AddSingleton<JobProcessor>();
        services.AddSingleton<Core.Contracts.IQueueController>(sp => sp.GetRequiredService<JobProcessor>());

        // v1 contract §4 — remote command executor + poller + dedup store.
        // Phase 3 (brief §7): the in-memory LRU (CommandDedupStore) is kept as a
        // fast-path cache; the DurableCommandStore (SQLite, shares the queue DB)
        // is the durable guarantee against re-execution after restart.
        services.AddSingleton<Commands.CommandDedupStore>();
        services.AddSingleton<Commands.DurableCommandStore>();
        services.AddSingleton<Commands.CommandExecutor>();
        services.AddSingleton<Commands.CommandPoller>();

        // Auto-update — checks GitHub releases for newer versions
        services.AddHttpClient<IUpdateService, GitHubUpdateService>();

        // Sync engine — pushes pending ops, pulls updates
        services.AddSingleton<IPendingOpsQueue, SqlitePendingOpsQueue>();
        services.AddSingleton<SyncEngine>();

        return services;
    }
}

/// <summary>
/// v1 contract Appendix C — applies the <c>PrintShop:*</c> section's values
/// onto <see cref="BackendOptions"/> so the rest of the app (which reads
/// <c>IOptionsMonitor&lt;BackendOptions&gt;</c>) sees the contract values.
/// <c>PrintShop</c> wins where a field is set; <c>Backend</c> is the fallback.
/// </summary>
internal sealed class BackendOptionsPostConfigure : IPostConfigureOptions<BackendOptions>
{
    private readonly IOptions<PrintShopOptions> _printShop;

    public BackendOptionsPostConfigure(IOptions<PrintShopOptions> printShop)
    {
        _printShop = printShop;
    }

    public void PostConfigure(string? name, BackendOptions backend)
    {
        var ps = _printShop.Value;
        if (!string.IsNullOrWhiteSpace(ps.BaseUrl))
            backend.BaseUrl = ps.BaseUrl;
        if (!string.IsNullOrWhiteSpace(ps.SocketIoUrl))
            backend.SocketIoUrl = ps.SocketIoUrl;
        if (ps.HeartbeatInterval is { } hb && hb > TimeSpan.Zero)
        {
            backend.HeartbeatInterval = hb;
            backend.HeartbeatIntervalSeconds = (int)hb.TotalSeconds;
        }
        if (ps.PollingInterval is { } poll && poll > TimeSpan.Zero)
        {
            backend.PollFallbackInterval = poll;
            backend.PollIntervalSeconds = (int)poll.TotalSeconds;
        }
    }
}

