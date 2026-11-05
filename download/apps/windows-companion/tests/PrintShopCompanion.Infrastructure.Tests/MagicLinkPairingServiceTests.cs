namespace PrintShopCompanion.Infrastructure.Tests;

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Pairing;
using PrintShopCompanion.Infrastructure.Backend;
using PrintShopCompanion.Infrastructure.Queue;
using PrintShopCompanion.Core.Contracts;
using Xunit;

/// <summary>
/// Magic-link pairing flow tests. The companion's URL handler must reject
/// URLs that have the wrong scheme, the wrong host, no token, or an expired
/// expiry — even if the backend would otherwise accept them.
/// </summary>
public class MagicLinkPairingServiceTests
{
    private sealed class FakeBackend : IPrintShopBackend
    {
        public bool IsAuthenticated { get; set; }
        public ShopInfo? CurrentShop { get; set; }
        public DeviceRegistration? CurrentDevice { get; set; }
        public List<string> PairCalls { get; } = new();

        public Task<PairingResult> PairAsync(string pairingToken, CancellationToken ct = default)
        {
            PairCalls.Add(pairingToken);
            return Task.FromResult(new PairingResult(
                new DeviceToken("shop", "dev", "tok", DateTimeOffset.UtcNow, null, null),
                new DeviceRegistration { DeviceId = "dev", ShopId = "shop", Hostname = "h", OperatingSystem = "os", AppVersion = "0", RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow, Revoked = false },
                new ShopInfo("shop", "Demo Shop", new Uri("https://shop.local"), "/api")));
        }

        public Task<bool> RefreshTokenAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<HeartbeatResult> HeartbeatAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<PrintJobRecord>> FetchPendingJobsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task AcknowledgeJobAsync(string jobId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReportJobStatusAsync(string jobId, DateTimeOffset? printedAt, DateTimeOffset? failedAt, string? errorMessage, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DownloadedPdf> DownloadPdfAsync(string jobId, Uri pdfUrl, string? expectedSha256, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RevokeDeviceAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static MagicLinkPairingService Make(out FakeBackend backend)
    {
        backend = new FakeBackend();
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<MagicLinkPairingService>();
        return new MagicLinkPairingService(backend, logger);
    }

    [Fact]
    public async Task Rejects_wrong_scheme()
    {
        var svc = Make(out var be);
        var result = await svc.HandleMagicLinkAsync(new Uri("https://printshop.local/pair?token=abc"));
        result.Should().BeFalse();
        be.PairCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Rejects_wrong_host()
    {
        var svc = Make(out var be);
        var result = await svc.HandleMagicLinkAsync(new Uri("printshopcompanion://revoke?token=abc"));
        result.Should().BeFalse();
        be.PairCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Rejects_missing_token()
    {
        var svc = Make(out var be);
        var result = await svc.HandleMagicLinkAsync(new Uri("printshopcompanion://pair?shop=foo"));
        result.Should().BeFalse();
        be.PairCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Rejects_expired_url_expiry()
    {
        var svc = Make(out var be);
        var expires = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds();
        var result = await svc.HandleMagicLinkAsync(new Uri($"printshopcompanion://pair?token=abc&expires={expires}"));
        result.Should().BeFalse();
        be.PairCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Accepts_valid_pairing_link()
    {
        var svc = Make(out var be);
        var expires = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var result = await svc.HandleMagicLinkAsync(new Uri($"printshopcompanion://pair?token=abc123&expires={expires}"));
        result.Should().BeTrue();
        be.PairCalls.Should().ContainSingle().Which.Should().Be("abc123");
    }
}
