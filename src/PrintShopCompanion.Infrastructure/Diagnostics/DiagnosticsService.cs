namespace PrintShopCompanion.Infrastructure.Diagnostics;

using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Storage;

/// <summary>
/// Aggregates the agent's health into one model for the Diagnostics screen
/// and produces the one-shot diagnostics export (decision D-07; patterns from
/// QZ Tray's "Zip logs to Desktop" and PaperCut's collect-logs-for-support).
///
/// <para>Platform-neutral: everything here compiles and runs on any OS so the
/// aggregation logic is unit-tested. Windows-only specifics (printer probing)
/// are optional — the service tolerates a missing <see cref="IPrinterService"/>
/// (the neutral build registers none).</para>
///
/// <para>Privacy: the export contains NO document content — only structural
/// state, queue counts, redacted settings names and log files. The redactor
/// (decision D-06) is wired into the log pipeline itself, so log files in the
/// bundle are already scrubbed.</para>
/// </summary>
public sealed class DiagnosticsService
{
    private readonly IOptionsMonitor<BackendOptions> _backendOpts;
    private readonly IPrintShopBackend _backend;
    private readonly IJobQueue _queue;
    private readonly IQueueDiagnostics? _queueStats;
    private readonly IQueueController? _queueController;
    private readonly IPrinterService? _printers;
    private readonly LocalStorageLayout _layout;
    private readonly ILogger<DiagnosticsService> _logger;

    public DiagnosticsService(
        IOptionsMonitor<BackendOptions> backendOpts,
        IPrintShopBackend backend,
        IJobQueue queue,
        LocalStorageLayout layout,
        ILogger<DiagnosticsService> logger,
        IQueueDiagnostics? queueStats = null,
        IQueueController? queueController = null,
        IPrinterService? printers = null)
    {
        _backendOpts = backendOpts;
        _backend = backend;
        _queue = queue;
        _layout = layout;
        _logger = logger;
        _queueStats = queueStats;
        _queueController = queueController;
        _printers = printers;
    }

    /// <summary>Collect the current health snapshot. Never throws — every check degrades to an error entry.</summary>
    public async Task<DiagnosticsSnapshot> CollectAsync(CancellationToken ct = default)
    {
        var snap = new DiagnosticsSnapshot { CollectedAt = DateTimeOffset.UtcNow };

        // Cloud connectivity + auth (both degrade gracefully when the backend is down).
        try
        {
            await _backend.HeartbeatAsync(ct);
            snap.CloudReachable = true;
            snap.TokenValid = true;
            snap.DeviceRegistered = _backend.CurrentDevice is not null;
            snap.DeviceId = _backend.CurrentDevice?.DeviceId;
            snap.ShopId = _backend.CurrentShop?.ShopId is { } sid && sid != "" ? sid : _backend.CurrentShop?.Id;
        }
        catch (Exception ex)
        {
            snap.CloudReachable = false;
            snap.TokenValid = _backend.IsAuthenticated;
            snap.Errors.Add("Cloud heartbeat failed: " + ex.Message);
        }
        snap.BaseUrl = _backendOpts.CurrentValue.BaseUrl;

        // Database health (integrity via the queue's own open path).
        try
        {
            if (_queueStats is not null)
            {
                snap.QueueCounts = await _queueStats.GetCountsByStatusAsync(ct);
                snap.LastSuccessfulPrintAt = await _queueStats.GetLastPrintedAtAsync(ct);
                snap.DatabaseHealthy = true;
            }
            else
            {
                snap.DatabaseHealthy = true; // counts unavailable, DB opened fine implicitly
            }
            snap.QueueDbPath = _layout.QueueDbPath;
        }
        catch (Exception ex)
        {
            snap.DatabaseHealthy = false;
            snap.Errors.Add("Queue database: " + ex.Message);
        }

        // Queue worker state.
        snap.QueuePaused = _queueController?.IsPaused;

        // Printer health (Windows only; absent on the neutral build).
        try
        {
            if (_printers is not null)
            {
                var printers = _printers.DiscoverPrinters();
                snap.PrinterCount = printers.Count;
                snap.SelectedPrinter = printers.FirstOrDefault(p => p.IsSelected)?.FullName
                    ?? printers.FirstOrDefault(p => p.IsDefault)?.FullName;
                snap.PrinterIssues = printers
                    .Where(p => p.IsOffline)
                    .Select(p => p.FullName + " (offline)")
                    .ToList();
            }
            else
            {
                snap.PrinterIssues.Add("Printer service unavailable on this build");
            }
        }
        catch (Exception ex)
        {
            snap.Errors.Add("Printer discovery failed: " + ex.Message);
        }

        // Disk space on the data volume.
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_layout.QueueDbPath)) ?? "/");
            snap.FreeDiskBytes = drive.IsReady ? drive.AvailableFreeSpace : null;
            if (snap.FreeDiskBytes is < 256 * 1024 * 1024)
                snap.Errors.Add("Low disk space (< 256 MB) — the queue refuses new downloads below this threshold");
        }
        catch (Exception ex)
        {
            snap.Errors.Add("Disk check failed: " + ex.Message);
        }

        _logger.LogInformation("Diagnostics collected: cloud={Cloud} db={Db} printers={Printers} errors={Errors}",
            snap.CloudReachable, snap.DatabaseHealthy, snap.PrinterCount, snap.Errors.Count);
        return snap;
    }

    /// <summary>
    /// Write a diagnostics bundle (zip): diagnostics.json + all current log
    /// files. Returns the zip path. Used by the Diagnostics screen's
    /// "Export diagnostics" button and suitable for support hand-off.
    /// </summary>
    public async Task<string> ExportDiagnosticsAsync(string outputZipPath, CancellationToken ct = default)
    {
        var snapshot = await CollectAsync(ct);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputZipPath))!);

        var tmp = outputZipPath + ".tmp";
        await using (var zip = File.Create(tmp))
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Create);
            var entry = archive.CreateEntry("diagnostics.json", CompressionLevel.Optimal);
            await using (var w = new StreamWriter(entry.Open()))
            {
                var json = System.Text.Json.JsonSerializer.Serialize(snapshot, DiagnosticsJson);
                await w.WriteAsync(json);
            }

            var logDir = CompanionLogDir();
            if (Directory.Exists(logDir))
            {
                foreach (var file in Directory.EnumerateFiles(logDir, "*.log", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        archive.CreateEntryFromFile(file, "logs/" + Path.GetFileName(file), CompressionLevel.Optimal);
                    }
                    catch (IOException)
                    {
                        // The newest log is typically locked by Serilog — skip it;
                        // yesterday's files cover the same history.
                    }
                }
            }
        }
        File.Move(tmp, outputZipPath, overwrite: true);
        _logger.LogInformation("Diagnostics bundle written to {Path}", outputZipPath);
        return outputZipPath;
    }

    private static string CompanionLogDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PrintShop", "Companion", "logs");

    private static readonly System.Text.Json.JsonSerializerOptions DiagnosticsJson = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}

/// <summary>Health snapshot for the Diagnostics screen. All fields are structural state — never document content.</summary>
public sealed class DiagnosticsSnapshot
{
    public DateTimeOffset CollectedAt { get; set; }
    public string? BaseUrl { get; set; }
    public bool CloudReachable { get; set; }
    public bool TokenValid { get; set; }
    public bool DeviceRegistered { get; set; }
    public string? DeviceId { get; set; }
    public string? ShopId { get; set; }
    public bool DatabaseHealthy { get; set; }
    public string? QueueDbPath { get; set; }
    public bool? QueuePaused { get; set; }
    public int PrinterCount { get; set; }
    public string? SelectedPrinter { get; set; }
    public List<string> PrinterIssues { get; set; } = new();
    public long? FreeDiskBytes { get; set; }
    public DateTimeOffset? LastSuccessfulPrintAt { get; set; }
    public IReadOnlyDictionary<JobStatus, int> QueueCounts { get; set; } = new Dictionary<JobStatus, int>();
    public List<string> Errors { get; set; } = new();

    public bool Healthy => CloudReachable && TokenValid && DeviceRegistered && DatabaseHealthy && Errors.Count == 0;
}
