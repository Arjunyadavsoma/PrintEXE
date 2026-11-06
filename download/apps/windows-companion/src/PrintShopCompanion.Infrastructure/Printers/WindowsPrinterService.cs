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
/// </summary>
public sealed class WindowsPrinterService : IPrinterService
{
    private readonly IPdfRenderer _renderer;
    private readonly IOptionsMonitor<PrinterOptions> _options;
    private readonly ILogger<WindowsPrinterService> _logger;
    private readonly Dictionary<int, (string JobId, DateTimeOffset SubmittedAt)> _spoolerTag = new();

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
                list.Add(new PrinterInfo
                {
                    FullName = q.FullName,
                    DisplayName = string.IsNullOrWhiteSpace(q.Description) ? q.Name : q.Description,
                    IsDefault = q.FullName.Equals(server.DefaultPrintQueue?.FullName, StringComparison.OrdinalIgnoreCase),
                    IsOffline = (q.QueueStatus & PrintQueueStatus.Offline) != 0,
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
        // Persisting the selected printer is done by the App via IOptionsMonitor + a JSON writer.
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
            duplex: job.Duplex,
            ct);

        if (spoolerJobId <= 0)
            throw new InvalidOperationException("Spooler returned an invalid job ID");

        lock (_spoolerTag) _spoolerTag[spoolerJobId] = (job.JobId, DateTimeOffset.UtcNow);
        _logger.LogInformation("Submitted {Title} -> {Printer} as spooler job #{Id} in {Ms}ms",
            job.Title, printerName, spoolerJobId, sw.ElapsedMilliseconds);
        return new SpoolSubmission(spoolerJobId, printerName, DateTimeOffset.UtcNow);
    }

    public async Task<bool> HasPrintedAsync(int spoolerJobId, CancellationToken ct = default)
    {
        var server = new LocalPrintServer();
        foreach (var q in server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections }))
        {
            var info = q.GetJob(spoolerJobId);
            if (info is null) continue;
            // JobStatus.Printed means the document has physically left the printer.
            return info.JobStatus.HasFlag(PrintJobStatus.Printed)
                || await Task.FromResult(info.JobStatus.HasFlag(PrintJobStatus.Completed));
        }
        // Job not found in any queue → already purged by the spooler. Treat as printed.
        _logger.LogInformation("Spooler job #{Id} not found; assuming completed and purged", spoolerJobId);
        return true;
    }

    public async Task<TestPrintResult> TestPrintAsync(string printerFullName, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Generate a 1-page test PDF in temp dir and print it.
            var testPdf = await TestPdfGenerator.GenerateAsync(ct);
            var spoolerId = await _renderer.PrintAsync(testPdf, printerFullName, ct);
            _logger.LogInformation("Test print spooled to {Printer} as job #{Id}", printerFullName, spoolerId);
            return new TestPrintResult { Success = true, SpoolerJobId = spoolerId, Duration = sw.Elapsed };
        }
        catch (Exception ex)
        {
            return new TestPrintResult { Success = false, ErrorMessage = ex.Message, Duration = sw.Elapsed };
        }
    }
}

/// <summary>Generates a minimal 1-page PDF for the test-print flow.</summary>
internal static class TestPdfGenerator
{
    public static async Task<string> GenerateAsync(CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), "PrintShop", "test-print.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // A tiny but valid PDF: 1 page with the text "PrintShop Companion test print".
        // This is hand-crafted so we don't depend on a PDF library at runtime; the
        // renderer (PDFium) opens it as a normal PDF.
        var content = """
            %PDF-1.4
            1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj
            2 0 obj << /Type /Pages /Count 1 /Kids [3 0 R] >> endobj
            3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842]
                       /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >> endobj
            4 0 obj << /Length 90 >> stream
            BT /F1 24 Tf 80 760 Td (PrintShop Companion test print) Tj ET
            endstream endobj
            5 0 obj << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> endobj
            xref
            0 6
            0000000000 65535 f 
            0000000010 00000 n 
            0000000060 00000 n 
            0000000110 00000 n 
            0000000260 00000 n 
            0000000390 00000 n 
            trailer << /Size 6 /Root 1 0 R >>
            startxref
            450
            %%EOF
            """;
        await File.WriteAllTextAsync(path, content, ct);
        return path;
    }
}
