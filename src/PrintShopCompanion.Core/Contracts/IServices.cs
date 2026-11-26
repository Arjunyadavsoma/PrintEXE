namespace PrintShopCompanion.Core.Contracts;

using PrintShopCompanion.Core.Models;

/// <summary>
/// Magic-link pairing flow. The companion registers a custom URL scheme
/// <c>printshopcompanion://</c> with Windows during install. The website
/// generates a one-time signed pairing URL of the form
/// <c>printshopcompanion://pair?token=…&amp;shop=…&amp;expires=…</c>;
/// the OS launches the companion with that URL and pairing completes
/// without any manual code entry.
/// </summary>
public interface IPairingService
{
    /// <summary>True while the companion is waiting for a magic-link callback.</summary>
    bool IsAwaitingMagicLink { get; }

    /// <summary>Begin a listening session. The companion shows a "click 'Add this PC' on the website" hint.</summary>
    Task StartAwaitingAsync(CancellationToken cancellationToken = default);

    /// <summary>Invoked by the OS protocol-handler when Windows passes the companion a <c>printshopcompanion://</c> URL.</summary>
    Task<bool> HandleMagicLinkAsync(Uri url, CancellationToken cancellationToken = default);

    /// <summary>Cancel the current awaiting session.</summary>
    Task CancelAsync(CancellationToken cancellationToken = default);
}

/// <summary>Minimal PDF rendering / printing abstraction.</summary>
public interface IPdfRenderer
{
    /// <summary>Print the PDF at <paramref name="pdfPath"/> to <paramref name="printerFullName"/> with the supplied options.</summary>
    Task<int> PrintAsync(
        string pdfPath,
        string printerFullName,
        string? pageRange = null,
        int copies = 1,
        bool? color = null,
        string? paperSize = null,
        string? duplex = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Secure credential storage. Stores the shopkeeper session (access + refresh
/// token) and the device token (for the background print agent) DPAPI-encrypted.
/// </summary>
public interface ICredentialStore
{
    Task<StoredCredentials?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(StoredCredentials credentials, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Phase 3 (brief §7) — minimal control surface over the local print queue's
/// processing loop, exposed so the <c>pause</c>/<c>resume</c> remote commands
/// can drive the queue without the CommandExecutor taking a direct dependency
/// on the concrete JobProcessor (which would create a DI cycle). The pause is
/// cooperative: an in-flight job finishes; subsequent dequeues block until
/// <see cref="ResumeAsync"/> is called.
/// </summary>
public interface IQueueController
{
    /// <summary>True while the processor is paused (won't dequeue new jobs). In-flight jobs finish.</summary>
    bool IsPaused { get; }

    /// <summary>Pause job processing. Cooperative — the in-flight job, if any, finishes. Idempotent.</summary>
    Task PauseAsync(CancellationToken cancellationToken = default);

    /// <summary>Resume job processing after a pause. Idempotent.</summary>
    Task ResumeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a managed inspection of a downloaded PDF, performed BEFORE the
/// job is claimed and spooled (decision D-04). The companion must know what a
/// document is before it prints it; an unusable file is failed permanently
/// with a precise reason instead of producing garbage paper.
/// </summary>
public enum PdfInspectionError
{
    /// <summary>The document opened cleanly.</summary>
    None = 0,

    /// <summary>File missing or empty (0 bytes).</summary>
    Empty = 1,

    /// <summary>PDFium could not parse the bytes — corrupt/truncated/HTML-error-page.</summary>
    Corrupt = 2,

    /// <summary>The document is password-protected; the companion has no password.</summary>
    Encrypted = 3,

    /// <summary>Not a PDF (missing %PDF- header).</summary>
    Unsupported = 4,

    /// <summary>Larger than the configured maximum.</summary>
    Oversized = 5,

    /// <summary>Zero pages after successful parse.</summary>
    NoPages = 6
}

public sealed record PdfInspectionResult
{
    public bool IsValid => Error == PdfInspectionError.None;

    /// <summary>Classification when the document cannot be printed safely.</summary>
    public PdfInspectionError Error { get; init; } = PdfInspectionError.None;

    /// <summary>Human-readable detail for logs and the UI (no document content).</summary>
    public string? Message { get; init; }

    /// <summary>Number of pages, when the document parsed (0 otherwise).</summary>
    public int PageCount { get; init; }

    /// <summary>First-page width in PDF points (1/72 in), when parsed.</summary>
    public double? PageWidthPt { get; init; }

    /// <summary>First-page height in PDF points (1/72 in), when parsed.</summary>
    public double? PageHeightPt { get; init; }
}

/// <summary>Managed (in-process) PDF inspection. Implementations must be thread-safe: PDFium serializes internally.</summary>
public interface IPdfInspector
{
    /// <summary>Open and classify the PDF at <paramref name="pdfPath"/>. Never prints; never modifies the file.</summary>
    Task<PdfInspectionResult> InspectAsync(string pdfPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Tri-state physical outcome of a spooler job (decision D-03). Replaces the
/// old boolean poll whose "not found in any queue → assume printed" rule
/// produced false-positive completions for purged or never-spooled jobs.
/// </summary>
public enum SpoolerOutcome
{
    /// <summary>The job has not been observed in a queue yet; keep polling.</summary>
    StillQueued = 0,

    /// <summary>The spooler reported the document as printed/completed.</summary>
    Printed = 1,

    /// <summary>The job was observed in a queue earlier and is now gone — the spooler purged it after success.</summary>
    PurgedAfterSeen = 2
}

/// <summary>
/// Monitors spooler jobs with the honest tri-state model. Implemented by the
/// Windows printer service; optional capability — callers use
/// <c>as ISpoolerMonitor</c> so fakes without spooler access keep working.
/// </summary>
public interface ISpoolerMonitor
{
    /// <summary>
    /// Classify the current physical outcome of a spooler job. Must be called
    /// repeatedly by the confirm loop; classifications are monotonic per job
    /// (StillQueued → Printed or PurgedAfterSeen).
    /// </summary>
    Task<SpoolerOutcome> GetOutcomeAsync(int spoolerJobId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read-only queue statistics for the Diagnostics screen (decision D-07).
/// Implemented by <c>SqliteJobQueue</c>; optional dependency — callers use
/// <c>as IQueueDiagnostics</c> or DI-resolve it directly.
/// </summary>
public interface IQueueDiagnostics
{
    /// <summary>Count of jobs per <see cref="JobStatus"/> (all statuses, zero included when queried).</summary>
    Task<IReadOnlyDictionary<JobStatus, int>> GetCountsByStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>The most recent <c>printed_at</c> in the queue, or null when nothing has printed yet.</summary>
    Task<DateTimeOffset?> GetLastPrintedAtAsync(CancellationToken cancellationToken = default);
}
