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
