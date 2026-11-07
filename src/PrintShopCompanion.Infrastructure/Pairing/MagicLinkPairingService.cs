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
        if (!string.Equals(url.Scheme, CompanionProtocol.UrlScheme, StringComparison.OrdinalIgnoreCase))
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
        var token = query["token"];
        if (string.IsNullOrWhiteSpace(token))
        {
            _logger.LogError("Magic link missing token query parameter");
            return false;
        }

        // Validate expiry (the token is signed by the backend, but we double-check the
        // URL-level expiry too — the backend will also reject an expired token).
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
            // v0.4.7+: the IPrintShopBackend contract changed from
            // PairAsync(token) → PairWithDeviceTokenAsync(token). The
            // production backend uses paste-token pairing (the shopkeeper
            // copies the device token from the web dashboard and pastes it
            // into PrintEXE), so the magic-link flow is now mostly obsolete.
            // We keep it for backward compatibility with shops that still
            // use the legacy printshopcompanion:// URL scheme — the token
            // from the URL is treated as a device token.
            await _backend.PairWithDeviceTokenAsync(token, cancellationToken);
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
