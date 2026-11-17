namespace PrintShopCompanion.Infrastructure.Pairing;

using Microsoft.Extensions.Logging;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Shared.Constants;

/// <summary>
/// Magic-link pairing. The companion registers the <c>printshopcompanion://</c>
/// URL scheme with Windows during install (see <c>installer/PrintShopCompanion.Installer.wxs</c>).
/// When the OS launches the companion with a URL of the form
/// <c>printshopcompanion://pair?token=…&amp;shop=…&amp;expires=…</c>, the App's
/// <c>Startup.UriActivation</c> handler routes it to <see cref="HandleMagicLinkAsync"/>
/// which uses the token as a device token via
/// <see cref="IPrintShopBackend.PairWithDeviceTokenAsync"/>.
/// <para><b>v0.4.7+:</b> the production backend uses paste-token pairing (the
/// shopkeeper copies the device token from the web dashboard and pastes it
/// into PrintEXE). The magic-link flow is kept for backward compatibility
/// with shops that still use the legacy <c>printshopcompanion://</c> URL
/// scheme — the token from the URL is treated as a device token.</para>
/// </summary>
public sealed class MagicLinkPairingService : IPairingService
{
    private readonly IPrintShopBackend _backend;
    private readonly ILogger<MagicLinkPairingService> _logger;
    private readonly SemaphoreSlim _awaitingGate = new(1, 1);
    private bool _awaiting;
    private TaskCompletionSource<bool>? _pairingTcs;

    public MagicLinkPairingService(IPrintShopBackend backend, ILogger<MagicLinkPairingService> logger)
    {
        _backend = backend;
        _logger = logger;
    }

    public bool IsAwaitingMagicLink => _awaiting;

    public Task StartAwaitingAsync(CancellationToken cancellationToken = default)
    {
        _pairingTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _awaiting = true;
        return Task.CompletedTask;
    }

    public async Task<bool> HandleMagicLinkAsync(Uri url, CancellationToken cancellationToken = default)
    {
        // Phase 0 audit fix: accept BOTH schemes. The SaaS pairing dialog
        // emits printshop://pair?ticket=…&shop=… (POST /api/v1/companion/pairing/begin
        // returns pairingUrl in this form); the legacy
        // printshopcompanion://pair?token=… scheme is kept for backward compat.
        var schemeOk =
            string.Equals(url.Scheme, CompanionProtocol.UrlScheme, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(url.Scheme, "printshop", StringComparison.OrdinalIgnoreCase);
        if (!schemeOk)
        {
            _logger.LogWarning("Ignoring URL with scheme {Scheme}", url.Scheme);
            return false;
        }
        if (!string.Equals(url.Host, CompanionProtocol.PairHost, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Ignoring URL with host {Host} (expected {Want})", url.Host, CompanionProtocol.PairHost);
            return false;
        }

        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        var ticket = query["ticket"];
        var legacyToken = query["token"];

        if (string.IsNullOrWhiteSpace(ticket) && string.IsNullOrWhiteSpace(legacyToken))
        {
            _logger.LogError("Magic link missing both 'ticket' and 'token' query parameters");
            return false;
        }

        // Validate expiry when the URL carries one (the pairing ticket's real
        // expiry is enforced server-side — this is just a fast-fail check).
        if (long.TryParse(query["expires"], out var unixSec))
        {
            var expires = DateTimeOffset.FromUnixTimeSeconds(unixSec);
            if (expires < DateTimeOffset.UtcNow)
            {
                _logger.LogError("Magic link expired at {Expiry}", expires);
                return false;
            }
        }

        await _awaitingGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(ticket))
            {
                // Primary flow: the URL carries a single-use pairing ticket.
                // Exchange it via POST /api/v1/companion/pair for the long-lived
                // device token (which PairWithTicketAsync DPAPI-persists).
                var result = await _backend.PairWithTicketAsync(
                    ticket.Trim(), Environment.MachineName, cancellationToken: cancellationToken);
                _awaiting = false;
                _pairingTcs?.TrySetResult(true);
                _logger.LogInformation("Paired with shop '{Shop}' via deep-link pairing ticket", result.ShopName);
                return true;
            }

            // Legacy flow: the token from the URL is treated as a device token.
            await _backend.PairWithDeviceTokenAsync(legacyToken!.Trim(), cancellationToken);
            _awaiting = false;
            _pairingTcs?.TrySetResult(true);
            _logger.LogInformation("Paired with shop via magic-link token (treated as device token)");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pairing via magic link failed");
            _pairingTcs?.TrySetException(ex);
            return false;
        }
    }

    public Task CancelAsync(CancellationToken cancellationToken = default)
    {
        _awaiting = false;
        _pairingTcs?.TrySetCanceled(cancellationToken);
        return Task.CompletedTask;
    }
}
