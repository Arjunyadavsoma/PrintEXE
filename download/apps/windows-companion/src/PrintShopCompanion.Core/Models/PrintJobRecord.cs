namespace PrintShopCompanion.Core.Models;

/// <summary>
/// Lifecycle of a print job as observed by the companion.
/// Transitions are append-only; the queue never moves a job backwards
/// except <see cref="Failed"/> -> <see cref="Pending"/> on a manual retry.
/// </summary>
public enum JobStatus
{
    /// <summary>Job accepted by the companion and persisted to the queue.</summary>
    Pending,

    /// <summary>Companion has fetched the PDF bytes from the backend.</summary>
    Downloaded,

    /// <summary>Job has been submitted to the Windows print spooler.</summary>
    Spooled,

    /// <summary>Spooler reports the document has physically printed.</summary>
    Printed,

    /// <summary>Companion has reported <see cref="Printed"/> back to the backend and got 2xx.</summary>
    ReportedPrinted,

    /// <summary>Permanent failure; will not auto-retry.</summary>
    Failed,

    /// <summary>Operator cancelled the job.</summary>
    Cancelled
}

/// <summary>
/// Persisted record of a print job. Maps 1:1 with the backend's <c>PrintJob</c> table.
/// </summary>
public sealed class PrintJobRecord
{
    /// <summary>Stable identifier from the backend. Used for idempotency.</summary>
    public required string JobId { get; init; }

    /// <summary>Foreign key to the shop that owns this job.</summary>
    public required string ShopId { get; init; }

    /// <summary>Foreign key to the PrintAgent/Device that claimed the job.</summary>
    public required string DeviceId { get; init; }

    /// <summary>Title shown in the UI and written to the spooler as the document name.</summary>
    public string Title { get; init; } = "PrintShop Job";

    /// <summary>Printer name as known to Windows (a PrintQueue.FullName).</summary>
    public string? PrinterName { get; init; }

    /// <summary>Number of copies. Default 1. Backend may override.</summary>
    public int Copies { get; init; } = 1;

    /// <summary>Page range, e.g. "1-3,5". Null = all pages.</summary>
    public string? PageRange { get; init; }

    /// <summary>Paper size name (e.g. "A4", "Letter"). Null = printer default.</summary>
    public string? PaperSize { get; init; }

    /// <summary>Color mode. Null = printer default.</summary>
    public bool? Color { get; init; }

    /// <summary>Duplex mode. Null = printer default.</summary>
    public string? Duplex { get; init; }

    /// <summary>Remote URL the companion should fetch the PDF from. Short-lived signed URL.</summary>
    public Uri? PdfUrl { get; init; }

    /// <summary>Local path where the PDF bytes are cached after download.</summary>
    public string? LocalPdfPath { get; set; }

    /// <summary>SHA-256 of the downloaded PDF bytes; used to detect mismatched redownloads.</summary>
    public string? PdfSha256 { get; set; }

    /// <summary>Current lifecycle status. Indexed in SQLite.</summary>
    public JobStatus Status { get; set; } = JobStatus.Pending;

    /// <summary>Windows spooler job ID once the document has been submitted.</summary>
    public int? SpoolerJobId { get; set; }

    /// <summary>Number of times the companion has attempted to print this job.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Last error message, if status is Failed. Cleared on successful retry.</summary>
    public string? LastError { get; set; }

    /// <summary>Backend timestamp. Used to discard jobs that are older than the queue's TTL.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Companion timestamp when the job was added to the local queue.</summary>
    public DateTimeOffset QueuedAt { get; set; }

    /// <summary>Companion timestamp when the job last changed status.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Companion timestamp when the spooler reported physical printing.</summary>
    public DateTimeOffset? PrintedAt { get; set; }
}
