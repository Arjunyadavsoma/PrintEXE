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

    /// <summary>
    /// Three-state spooler confirmation poll (Phase 4 fix — the old boolean
    /// treated "job not found in any queue" as printed, a false positive for
    /// error-purged or never-actually-spooled jobs and a violation of the
    /// invariant "never report printed until the spooler confirms").
    /// <list type="bullet">
    /// <item><see cref="SpoolConfirmation.Printed"/> — the spooler reports the
    /// job as Printed/Completed, OR the job vanished after having been seen
    /// alive in an earlier poll (Windows purges printed jobs quickly; a
    /// vanished job that we previously observed in-flight is a purged print).</item>
    /// <item><see cref="SpoolConfirmation.Failed"/> — the spooler reports an
    /// error/deleted/aborted status: the job will never print.</item>
    /// <item><see cref="SpoolConfirmation.Unknown"/> — job in flight but not
    /// yet printed, or gone without ever having been seen. Callers keep
    /// polling until their deadline and then park the job as outcome-uncertain.</item>
    /// </list>
    /// </summary>
    Task<SpoolConfirmation> HasPrintedAsync(int spoolerJobId, CancellationToken cancellationToken = default);

    /// <summary>Submit a 1-page test PDF to the selected printer.</summary>
    Task<TestPrintResult> TestPrintAsync(string printerFullName, CancellationToken cancellationToken = default);
}

public sealed record SpoolSubmission(int? SpoolerJobId, string PrinterFullName, DateTimeOffset SubmittedAt);
// SpoolerJobId is null when the spooler did not report a job id: the physical
// outcome is then UNKNOWN and must never be reported as completed.

/// <summary>Honest three-state result of a spooler confirmation poll.</summary>
public enum SpoolConfirmation
{
    /// <summary>The spooler confirmed the document printed (or it purged after being seen alive).</summary>
    Printed,

    /// <summary>The spooler reports the job errored/deleted/aborted — it will never print.</summary>
    Failed,

    /// <summary>In flight but unconfirmed, or vanished without ever being seen — not evidence of printing.</summary>
    Unknown,
}
