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
