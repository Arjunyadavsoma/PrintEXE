namespace PrintShopCompanion.Core.Options;

/// <summary>Backend connectivity settings. Bound from <c>appsettings.json</c> + environment.</summary>
public sealed class BackendOptions
{
    public const string SectionName = "Backend";

    /// <summary>Base URL of the PrintShop website. Set during pairing or via appsettings.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>API base path under <see cref="BaseUrl"/>. Default <c>/api/v1</c> per the production backend.</summary>
    public string ApiBasePath { get; set; } = "/api/v1";

    /// <summary>
    /// Full Socket.IO URL including the Caddy gateway query parameter.
    /// Default <c>https://printshop-saas-three.vercel.app/?XTransformPort=3003</c>.
    /// The <c>XTransformPort=3003</c> query is REQUIRED — without it Caddy routes
    /// to the Next.js app (port 3000) which doesn't serve Socket.IO.
    /// </summary>
    public string? SocketIoUrl { get; set; } = "https://printshop-saas-three.vercel.app/?XTransformPort=3003";

    /// <summary>
    /// Socket.IO path. Kept for backward compatibility — prefer <see cref="SocketIoUrl"/>.
    /// If <see cref="SocketIoUrl"/> is set, it wins.
    /// </summary>
    public string HubPath { get; set; } = "/?XTransformPort=3003";

    /// <summary>Heartbeat interval (POST /agents/heartbeat). Default 60s per the integration guide.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Heartbeat interval in seconds. Used by code that prefers seconds over TimeSpan.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 60;

    /// <summary>HTTP long-poll interval used as a fallback when the realtime socket is down. Default 2s.</summary>
    public TimeSpan PollFallbackInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Poll interval in seconds. Used by code that prefers seconds over TimeSpan.</summary>
    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>Allowed origin(s) for the companion's local API (CORS).</summary>
    public string[] AllowedWebOrigins { get; set; } = Array.Empty<string>();

    /// <summary>JWT validation parameters for pairing tokens. Set from backend public key.</summary>
    public string? PairingIssuer { get; set; }

    /// <summary>Maximum time a single PDF download is allowed to take. Default 5 minutes per integration guide.</summary>
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Download timeout in seconds. Used by code that prefers seconds over TimeSpan.</summary>
    public int DownloadTimeoutSeconds { get; set; } = 300;

    /// <summary>Maximum number of attempts per print job before marking it Failed. Default 5.</summary>
    public int MaxPrintAttempts { get; set; } = 5;

    /// <summary>True to skip TLS verification. Local mock only. Never enable in production.</summary>
    public bool BypassTlsVerification { get; set; } = false;
}

/// <summary>Printer selection and print behaviour.</summary>
public sealed class PrinterOptions
{
    public const string SectionName = "Printer";

    /// <summary>Last-selected printer FullName. Empty = use Windows default.</summary>
    public string? SelectedPrinter { get; set; }

    /// <summary>PDF rendering engine. One of <c>sumatra</c>, <c>gdi</c>. Default <c>sumatra</c>.</summary>
    public string Renderer { get; set; } = "sumatra";

    /// <summary>Fallback chain if the primary renderer fails. Empty = no fallback.</summary>
    public string[] FallbackRenderers { get; set; } = new[] { "gdi" };

    /// <summary>Path to the bundled SumatraPDF.exe. Resolved relative to the app dir if not absolute.</summary>
    public string? SumatraExePath { get; set; }

    /// <summary>Seconds to wait for the spooler to confirm a job has physically printed.</summary>
    public TimeSpan PrintConfirmTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Maximum number of attempts per job before marking it Failed.</summary>
    public int MaxAttempts { get; set; } = 5;
}

/// <summary>Local SQLite queue + cache paths.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Path to the SQLite database file. Defaults to <c>%LocalAppData%\PrintShop\Companion\Database\queue.db</c>.</summary>
    public string? QueueDbPath { get; set; }

    /// <summary>Path to the directory used to cache downloaded PDFs.</summary>
    public string? PdfCacheDir { get; set; }

    /// <summary>Path to the directory used for Serilog rolling files.</summary>
    public string? LogDir { get; set; }

    /// <summary>
    /// Path to the DPAPI-encrypted file that stores the device token.
    /// Defaults to <c>%LocalAppData%\PrintShop\Companion\Security\device.dat</c>.
    /// </summary>
    public string? DeviceTokenPath { get; set; }

    /// <summary>
    /// Local PDF retention in minutes after a terminal printing state
    /// (completed/failed/cancelled). The FileRetentionManager deletes the
    /// local PDF when localDeleteAfter &lt; now. Default 60.
    /// </summary>
    public int LocalRetentionMinutes { get; set; } = 60;

    /// <summary>
    /// Cloud PDF retention in minutes after verified local receipt. The
    /// server-side cron worker deletes the Supabase object when
    /// cloudDeleteAfter &lt; now. Default 15. (Server-side config; this
    /// is for the UI display only.)
    /// </summary>
    public int CloudRetentionMinutes { get; set; } = 15;

    /// <summary>
    /// Maximum total bytes for the Jobs/ directory. If exceeded, the
    /// companion aggressively cleans up completed files regardless of
    /// localDeleteAfter. Default 1 GB.
    /// </summary>
    public long MaxLocalStorageBytes { get; set; } = 1_000_000_000;

    /// <summary>Jobs older than this in a terminal state are purged on startup.</summary>
    public TimeSpan TerminalJobTtl { get; set; } = TimeSpan.FromDays(7);
}

/// <summary>Tray + auto-start behaviour.</summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>True to minimise to tray on close. Default FALSE — clicking X closes the app.
    /// The shopkeeper can still use the tray menu's "Open Dashboard" to restore a minimized window.
    /// Setting this to false prevents the "cannot reopen" issue where the user thinks they closed
    /// the app but it's still running in the tray.</summary>
    public bool MinimizeToTrayOnClose { get; set; } = false;

    /// <summary>True to launch on Windows startup. Default true.</summary>
    public bool AutoStartWithWindows { get; set; } = true;

    /// <summary>True to follow the Windows system light/dark preference. Default true.</summary>
    public bool FollowSystemTheme { get; set; } = true;

    /// <summary>Light or dark. Used when <see cref="FollowSystemTheme"/> is false.</summary>
    public string Theme { get; set; } = "system";
}
