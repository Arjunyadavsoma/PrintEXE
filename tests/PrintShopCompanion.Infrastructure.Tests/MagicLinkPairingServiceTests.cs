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

        // v0.4.7+ contract — paste-token pairing, JWT exchange, sync endpoints
        public Task PairWithDeviceTokenAsync(string deviceToken, CancellationToken ct = default)
        {
            PairCalls.Add(deviceToken);
            IsAuthenticated = true;
            return Task.CompletedTask;
        }
        public Task<string> ExchangeDeviceTokenForJwtAsync(CancellationToken ct = default)
            => Task.FromResult("test-jwt");
        public Task<HeartbeatResult> HeartbeatAsync(CancellationToken ct = default)
            => Task.FromResult(new HeartbeatResult(DateTimeOffset.UtcNow, TokenStillValid: true, RecommendedBackendVersion: null));
        public Task<IReadOnlyList<PrintJobRecord>> FetchPendingJobsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PrintJobRecord>>(new List<PrintJobRecord>());
        public Task<JobStatusReport> ReportJobStatusAsync(string jobId, string status, string? printerName, string? errorMessage, CancellationToken ct = default)
            => Task.FromResult(new JobStatusReport(jobId, status, Applied: true, Skipped: false, Reason: null));
        public Task<DownloadedPdf> DownloadPdfAsync(string jobId, Uri pdfUrl, string? expectedSha256, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task RevokeDeviceAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<SyncPullResult> SyncPullAsync(string? cursor, int limit, CancellationToken ct = default)
            => Task.FromResult(new SyncPullResult(new List<SyncChange>(), null, false));
        public Task<SyncPushResult> SyncPushAsync(object changes, CancellationToken ct = default)
            => Task.FromResult(new SyncPushResult(new List<SyncPushResultItem>()));
        public Task<SyncStatusResult> SyncStatusAsync(CancellationToken ct = default)
            => Task.FromResult(new SyncStatusResult("dev", "shop", null, null, null, true));
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
