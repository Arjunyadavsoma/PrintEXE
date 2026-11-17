namespace PrintShopCompanion.Core.Contracts;

/// <summary>
/// Phase 3 (brief §4) — user-facing messages for each pairing-ticket error
/// code returned by <c>POST /api/v1/companion/pair</c>. Extracted from
/// <c>LoginViewModel</c> so the mapping is pure (no WPF dependency) and
/// unit-testable in the OS-agnostic Infrastructure.Tests project.
/// <para>See docs/PHASE3_CONTRACT_MATRIX.md Mismatch #4 for the actual SaaS
/// error codes (the published contract drifted: the server uses
/// <c>PAIRING_TICKET_UNAVAILABLE</c>/<c>PAIRING_TICKET_CONSUMED</c>, not the
/// contract's <c>TICKET_EXPIRED</c>/<c>TICKET_CONSUMED</c>; expired tickets
/// return <c>UNAUTHORIZED</c> with a distinguishing message).</para>
/// </summary>
public static class PairingTicketErrorMessages
{
    /// <summary>
    /// Map a <see cref="PairingTicketException"/> to a user-facing message.
    /// Returns null if the code is unrecognized (the caller should fall back
    /// to <see cref="PairingTicketException.Message"/>).
    /// </summary>
    public static string? Map(PairingTicketException ex) => ex.Code switch
    {
        "VALIDATION_ERROR" => "The pairing code must be exactly 64 hex characters, and the device name 1–120 characters. Check both and try again.",
        "UNAUTHORIZED" when ex.IsExpired => "The pairing code has expired. Generate a new one on the website (codes are valid for 5 minutes).",
        "UNAUTHORIZED" => "The pairing code was not recognized. Generate a new one on the website and copy it exactly.",
        "PAIRING_TICKET_CONSUMED" => "This pairing code was already used. Generate a new one on the website.",
        "PAIRING_TICKET_UNAVAILABLE" => "This pairing code is no longer active. Generate a new one on the website.",
        "NOT_FOUND" => "The shop that issued this pairing code no longer exists. Contact support.",
        "RATE_LIMITED" => "Too many pairing attempts. Wait a minute and try again.",
        _ => null,
    };
}
