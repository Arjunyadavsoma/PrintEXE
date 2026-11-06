namespace PrintShopCompanion.Core.Contracts;

using PrintShopCompanion.Core.Models;

/// <summary>
/// Discovers installed Windows printers and submits print jobs.
/// Implementations must be thread-safe; one instance is shared by the
/// WPF app, the worker service, and the local API.
/// </summary>
public interface IPrinterService
{
    /// <summary>Enumerate all printers known to the local Windows spooler.</summary>
    IReadOnlyList<PrinterInfo> DiscoverPrinters();

    /// <summary>Returns the printer that the user has explicitly selected, or the Windows default.</summary>
    PrinterInfo? GetSelectedPrinter();

    /// <summary>Persists the selected printer name in the companion's local config.</summary>
    Task SelectPrinterAsync(string printerFullName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Submit a PDF to the spooler. Returns the spooler job ID so the caller can poll status.
    /// Implementations MUST:
    ///  <list type="bullet">
    ///   <item>Tag the spooler job with the PrintShop JobId so status updates can be matched back.</item>
    ///   <item>Throw on spooler rejection; never return a zero spooler ID silently.</item>
    ///  </list>
    /// </summary>
    Task<SpoolSubmission> SubmitAsync(PrintJobRecord job, CancellationToken cancellationToken = default);

    /// <summary>True once the spooler reports the document has physically left the printer.</summary>
    Task<bool> HasPrintedAsync(int spoolerJobId, CancellationToken cancellationToken = default);

    /// <summary>Submit a 1-page test PDF to the selected printer.</summary>
    Task<TestPrintResult> TestPrintAsync(string printerFullName, CancellationToken cancellationToken = default);
}

public sealed record SpoolSubmission(int SpoolerJobId, string PrinterFullName, DateTimeOffset SubmittedAt);
