namespace PrintShopCompanion.Core.Options;

/// <summary>Backend connectivity settings. Bound from <c>appsettings.json</c> + environment.</summary>
public sealed class BackendOptions
{
    public const string SectionName = "Backend";

    /// <summary>Base URL of the PrintShop website. Set during pairing or via appsettings.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>API base path under <see cref="BaseUrl"/>.</summary>
    public string ApiBasePath { get; set; } = "/api/companion/v1";

    /// <summary>Socket.IO / SignalR endpoint path under <see cref="BaseUrl"/>.</summary>
    public string HubPath { get; set; } = "/companion-hub";

    /// <summary>Heartbeat interval.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>HTTP long-poll interval used as a fallback when the realtime socket is down.</summary>
    public TimeSpan PollFallbackInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Allowed origin(s) for the companion's local API (CORS).</summary>
    public string[] AllowedWebOrigins { get; set; } = Array.Empty<string>();

    /// <summary>JWT validation parameters for pairing tokens. Set from backend public key.</summary>
    public string? PairingIssuer { get; set; }

    /// <summary>Maximum time a single PDF download is allowed to take.</summary>
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(2);

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

    /// <summary>Path to the SQLite database file. Defaults to <c>%AppData%\PrintShop\Companion\queue.db</c>.</summary>
    public string? QueueDbPath { get; set; }

    /// <summary>Path to the directory used to cache downloaded PDFs.</summary>
    public string? PdfCacheDir { get; set; }

    /// <summary>Path to the directory used for Serilog rolling files.</summary>
    public string? LogDir { get; set; }

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
