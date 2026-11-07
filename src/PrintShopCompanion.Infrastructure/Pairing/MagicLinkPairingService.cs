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
/// which exchanges the one-time token for a long-lived device token via
/// <see cref="IPrintShopBackend.PairAsync"/>.
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
            var result = await _backend.PairAsync(token, cancellationToken);
            _awaiting = false;
            _pairingTcs?.TrySetResult(true);
            _logger.LogInformation("Paired with shop {ShopId} ({Shop})", result.Shop.ShopId, result.Shop.ShopName);
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
