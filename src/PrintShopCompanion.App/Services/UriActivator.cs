namespace PrintShopCompanion.App.Services;

using System.Web;

/// <summary>
/// Parses OS protocol activation. The WiX installer registers BOTH URL
/// schemes with Windows:
/// <list type="bullet">
///   <item><c>printshop://pair?ticket=…&amp;shop=…</c> — the scheme the
///   SaaS "Add Windows Device" dialog emits (<c>POST /api/v1/companion/pairing/begin</c>
///   returns <c>pairingUrl</c> in exactly this form). <b>This is the primary
///   deep link.</b></item>
///   <item><c>printshopcompanion://pair?token=…</c> — the legacy scheme kept
///   for backward compatibility with older dashboards.</item>
/// </list>
/// When the shopkeeper clicks the link, Windows launches the companion with
/// the URL as the first command-line argument. <see cref="TryConsumePendingPairingRequest"/>
/// surfaces that argument once so the Login screen can pre-fill the pairing
/// code. The user still confirms by clicking "Pair this computer" — a URL
/// arriving from an untrusted source NEVER auto-pairs (integration spec §7.3).
/// </summary>
public sealed class UriActivator
{
    /// <summary>The scheme the SaaS pairing dialog emits.</summary>
    public const string PrimaryScheme = "printshop";

    /// <summary>The scheme the installer has always registered (legacy).</summary>
    public const string LegacyScheme = "printshopcompanion";

    /// <summary>Host under both schemes that carries pairing parameters.</summary>
    public const string PairHost = "pair";

    /// <summary>A pairing request parsed from a launch URI.</summary>
    public sealed record PendingPairingRequest(string Ticket, string? LegacyToken, string? ShopId);

    private static bool _consumed;

    /// <summary>
    /// Returns the URL the OS launched us with, or null if we were launched
    /// normally (plain args like --version are ignored). Reads the first
    /// command-line argument when it is an absolute URI under one of the
    /// registered pairing schemes.
    /// </summary>
    public static Uri? GetActivationUri()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && Uri.TryCreate(args[1], UriKind.Absolute, out var u))
        {
            if (string.Equals(u.Scheme, PrimaryScheme, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(u.Scheme, LegacyScheme, StringComparison.OrdinalIgnoreCase))
                return u;
        }
        return null;
    }

    /// <summary>
    /// Try to read + CLEAR the pending pairing request from the command line.
    /// Consume-on-read: the Login screen picks it up once; later navigations
    /// to Login will not silently re-offer a stale (possibly expired) ticket.
    /// </summary>
    public static bool TryConsumePendingPairingRequest(out PendingPairingRequest request)
    {
        request = new PendingPairingRequest("", null, null);
        if (_consumed) return false;
        var uri = GetActivationUri();
        if (uri is null) return false;
        if (!string.Equals(uri.Host, PairHost, StringComparison.OrdinalIgnoreCase)) return false;

        var query = HttpUtility.ParseQueryString(uri.Query);
        var ticket = query["ticket"];
        var legacyToken = query["token"];
        var shop = query["shop"];
        if (string.IsNullOrWhiteSpace(ticket) && string.IsNullOrWhiteSpace(legacyToken))
            return false;

        _consumed = true;
        request = new PendingPairingRequest(ticket?.Trim() ?? "", legacyToken?.Trim(), shop?.Trim());
        return true;
    }
}
