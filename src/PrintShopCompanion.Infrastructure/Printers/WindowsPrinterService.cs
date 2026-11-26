namespace PrintShopCompanion.Infrastructure.Printers;

using System.Printing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;

/// <summary>
/// Discovers installed Windows printers via the <see cref="System.Printing"/> API and
/// submits jobs to the local spooler. Discovery and spooler ID tracking are the only
/// two responsibilities here — actual PDF rasterisation is delegated to an
/// <see cref="IPdfRenderer"/> implementation.
///
/// <para>Phase 0-audit fix (decision D-03): implements <see cref="ISpoolerMonitor"/>
/// with the honest tri-state model. The old boolean <see cref="HasPrintedAsync"/>
/// returned <c>true</c> when a spooler job was "not found in any queue", which
/// mis-classified never-spooled or failed jobs as printed. The tracker now
/// distinguishes "observed then purged" (normal Windows behaviour, maps to printed)
/// from "never observed" (keeps polling; the confirm-loop timeout parks the job as
/// UncertainSpooled).</para>
/// </summary>
public sealed class WindowsPrinterService : IPrinterService, ISpoolerMonitor
{
    private readonly IPdfRenderer _renderer;
    private readonly IOptionsMonitor<PrinterOptions> _options;
    private readonly ILogger<WindowsPrinterService> _logger;
    private readonly Dictionary<int, (string JobId, DateTimeOffset SubmittedAt)> _spoolerTag = new();
    private readonly SpoolerOutcomeTracker _outcomeTracker = new();

    public WindowsPrinterService(IPdfRenderer renderer, IOptionsMonitor<PrinterOptions> options, ILogger<WindowsPrinterService> logger)
    {
        _renderer = renderer;
        _options = options;
        _logger = logger;
    }

    public IReadOnlyList<PrinterInfo> DiscoverPrinters()
    {
        var server = new LocalPrintServer();
        var queues = server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections });
        var selected = _options.CurrentValue.SelectedPrinter;
        var list = new List<PrinterInfo>();
        foreach (var q in queues)
        {
            try
            {
                q.Refresh();
                var caps = q.GetPrintCapabilities();
                // v0.7.17: real status, not a green-for-everyone dot. The
                // mapping lives in PrinterStatusMapper (pure, tested);
                // precedence Offline > Error > Busy(paused) > Ready.
                var status = PrintShopCompanion.Core.Printers.PrinterStatusMapper.Map(
                    isOffline: (q.QueueStatus & PrintQueueStatus.Offline) != 0,
                    isPaused: (q.QueueStatus & PrintQueueStatus.Paused) != 0,
                    isError: (q.QueueStatus & PrintQueueStatus.Error) != 0,
                    isBusy: (q.QueueStatus & PrintQueueStatus.Busy) != 0,
                    isPrinting: (q.QueueStatus & PrintQueueStatus.Printing) != 0,
                    isInitializing: (q.QueueStatus & PrintQueueStatus.Initializing) != 0,
                    isPendingDeletion: (q.QueueStatus & PrintQueueStatus.PendingDeletion) != 0);
                list.Add(new PrinterInfo
                {
                    FullName = q.FullName,
                    DisplayName = string.IsNullOrWhiteSpace(q.Description) ? q.Name : q.Description,
                    IsDefault = q.FullName.Equals(server.DefaultPrintQueue?.FullName, StringComparison.OrdinalIgnoreCase),
                    IsOffline = (q.QueueStatus & PrintQueueStatus.Offline) != 0,
                    Status = status,
                    StatusText = PrintShopCompanion.Core.Printers.PrinterStatusMapper.TextFor(status),
                    IsNetwork = q.FullName.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)
                                || q.FullName.Contains("http", StringComparison.OrdinalIgnoreCase)
                                || q.FullName.Contains("WSD", StringComparison.OrdinalIgnoreCase),
                    SupportedPaperSizes = caps?.PageMediaSizeCapability?.Select(p => p.ToString()).ToArray() ?? Array.Empty<string>(),
                    SupportedDuplexModes = caps?.DuplexingCapability?.Select(d => d.ToString()).ToArray() ?? Array.Empty<string>(),
                    SupportsColor = caps?.OutputColorCapability?.Contains(OutputColor.Color) == true,
                    LastTestPrintAt = null,
                    IsSelected = string.Equals(selected, q.FullName, StringComparison.OrdinalIgnoreCase)
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping printer {Name} (caps probe failed)", q.FullName);
            }
        }
        return list;
    }

    public PrinterInfo? GetSelectedPrinter()
    {
        var selected = _options.CurrentValue.SelectedPrinter;
        if (string.IsNullOrWhiteSpace(selected))
        {
            // Fall back to Windows default.
            var server = new LocalPrintServer();
            var def = server.DefaultPrintQueue;
            return def is null ? null : DiscoverPrinters().FirstOrDefault(p => p.FullName == def.FullName);
        }
        return DiscoverPrinters().FirstOrDefault(p => p.FullName == selected);
    }

    public async Task SelectPrinterAsync(string printerFullName, CancellationToken ct = default)
    {
        _options.CurrentValue.SelectedPrinter = printerFullName;
        // Persist the selection to the per-user override file so it survives
        // restart (Phase 0 audit: the selection was in-memory only — every
        // restart re-fell back to the Windows default printer). The heartbeat
        // also picks this up so the website shows the correct printer.
        try
        {
            Storage.LocalSettingsStore.Set("Printer:SelectedPrinter", printerFullName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist selected printer (non-fatal — selection stays active for this session)");
        }
        await Task.CompletedTask;
    }

    public async Task<SpoolSubmission> SubmitAsync(PrintJobRecord job, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(job.LocalPdfPath) || !File.Exists(job.LocalPdfPath))
            throw new InvalidOperationException("LocalPdfPath is missing or file does not exist");

        var printerName = job.PrinterName ?? _options.CurrentValue.SelectedPrinter;
        if (string.IsNullOrWhiteSpace(printerName))
        {
            var server = new LocalPrintServer();
            printerName = server.DefaultPrintQueue?.FullName
                ?? throw new InvalidOperationException("No printer selected and no Windows default printer");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var spoolerJobId = await _renderer.PrintAsync(
            job.LocalPdfPath,
            printerName,
            pageRange: job.PageRange,
            copies: job.Copies,
            color: job.Color,
            paperSize: job.PaperSize,
            duplex: job.Duplex == true ? "duplexlong" : job.Duplex == false ? "simplex" : null,
            cancellationToken: ct);

        // Phase 5: an id of 0 (or negative) means the spooler did not report
        // one — the caller must treat the physical outcome as UNKNOWN, not
        // printed. (Previously this THREW, failing the whole job even though
        // the paper may actually have gone through; now it degrades honestly.)
        if (spoolerJobId > 0)
            lock (_spoolerTag) _spoolerTag[spoolerJobId] = (job.JobId, DateTimeOffset.UtcNow);
        _logger.LogInformation("Submitted {Title} -> {Printer} as spooler job #{Id} in {Ms}ms",
            job.Title, printerName, spoolerJobId > 0 ? spoolerJobId.ToString() : "unknown", sw.ElapsedMilliseconds);
        return new SpoolSubmission(spoolerJobId > 0 ? spoolerJobId : null, printerName, DateTimeOffset.UtcNow);
    }

    public async Task<bool> HasPrintedAsync(int spoolerJobId, CancellationToken ct = default)
    {
        var outcome = await GetOutcomeAsync(spoolerJobId, ct);
        return outcome is SpoolerOutcome.Printed or SpoolerOutcome.PurgedAfterSeen;
    }

    /// <summary>
    /// Tri-state spooler outcome (decision D-03). See <see cref="SpoolerOutcomeTracker"/>
    /// for the state machine; this method supplies the observations from the spooler.
    /// </summary>
    public async Task<SpoolerOutcome> GetOutcomeAsync(int spoolerJobId, CancellationToken ct = default)
    {
        var server = new LocalPrintServer();
        foreach (var q in server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections }))
        {
            var info = q.GetJob(spoolerJobId);
            if (info is null) continue;
            // JobStatus.Printed means the document has physically left the printer.
            var printed = info.JobStatus.HasFlag(PrintJobStatus.Printed)
                          || info.JobStatus.HasFlag(PrintJobStatus.Completed);
            return await Task.FromResult(_outcomeTracker.Observe(spoolerJobId, foundInQueue: true, reportedPrinted: printed));
        }
        // Not found in any queue this poll. The tracker decides honestly:
        // seen-before → purged after success; never-seen → keep polling.
        return await Task.FromResult(_outcomeTracker.Observe(spoolerJobId, foundInQueue: false, reportedPrinted: false));
    }

    public async Task<TestPrintResult> TestPrintAsync(string printerFullName, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Generate a spec-correct 1-page test PDF in temp dir and print it
            // (decision D-04: xref offsets are computed, /Length is exact).
            var testPdf = await Pdf.TestPagePdf.WriteAsync(
                Path.Combine(Path.GetTempPath(), "PrintShop", "test-print.pdf"), ct);
            var spoolerId = await _renderer.PrintAsync(testPdf, printerFullName, cancellationToken: ct);
            _logger.LogInformation("Test print spooled to {Printer} as job #{Id}", printerFullName, spoolerId);
            return new TestPrintResult { Success = true, SpoolerJobId = spoolerId, Duration = sw.Elapsed };
        }
        catch (Exception ex)
        {
            return new TestPrintResult { Success = false, ErrorMessage = ex.Message, Duration = sw.Elapsed };
        }
    }
}
