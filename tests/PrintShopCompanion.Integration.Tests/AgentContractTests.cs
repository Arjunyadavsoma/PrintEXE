namespace PrintShopCompanion.Integration.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Backend;
using PrintShopCompanion.Infrastructure.Storage;
using Xunit;

/// <summary>
/// End-to-end contract tests: the PRODUCTION client (HttpPrintShopBackend)
/// against a REAL HTTP server implementing the production agent contract.
/// These are the first integration tests against an HTTP server in this
/// repository (Phase 0 audit §14: "There is no integration test against any
/// HTTP server").
/// </summary>
public sealed class AgentContractTests : IDisposable
{
    private readonly FakePrintelyxBackend _backend;
    private readonly HttpPrintShopBackend _client;
    private readonly string _root;

    public AgentContractTests()
    {
        _backend = new FakePrintelyxBackend();
        _root = Path.Combine(Path.GetTempPath(), "psh-integration-" + Guid.NewGuid().ToString("N"));

        // v0.7.1 fix: isolate the layout root AND the device-token path inside
        // a per-test temp directory. Passing the temp dir as the 2nd positional
        // argument made it the LEGACY root override, so Root stayed at the real
        // %LocalAppData% and — critically — DeviceTokenPath defaulted to the
        // real %AppData%\PrintEXE\device-token.dat. The pairing tests then
        // persisted a token to that REAL path (DPAPI works on Windows; on
        // Linux ProtectedData throws non-fatally, which is why the
        // Unauthorized_calls_fail_closed test only passed here by accident:
        // the unauthorized client lazily restored the earlier test's token
        // from disk and never threw).
        var layout = new LocalStorageLayout(
            Options.Create(new StorageOptions
            {
                DeviceTokenPath = Path.Combine(_root, "Security", "device-token.dat")
            }),
            rootOverride: _root);
        _client = new HttpPrintShopBackend(
            new HttpClient { BaseAddress = new Uri(_backend.BaseUrl) },
            new StubMonitor(new BackendOptions { BaseUrl = _backend.BaseUrl }),
            NullLogger<HttpPrintShopBackend>.Instance,
            layout);
    }

    private sealed class StubMonitor : Microsoft.Extensions.Options.IOptionsMonitor<BackendOptions>
    {
        private readonly BackendOptions _value;
        public StubMonitor(BackendOptions value) => _value = value;
        public BackendOptions CurrentValue => _value;
        public BackendOptions Get(string? name) => _value;
        public System.IDisposable? OnChange(Action<BackendOptions?, string?> listener) => null;
    }

    [Fact]
    public async Task Pairing_with_valid_device_token_survives_heartbeat_validation()
    {
        await _client.PairWithDeviceTokenAsync(FakePrintelyxBackend.ValidDeviceToken);
        Assert.True(_client.IsAuthenticated);
    }

    [Fact]
    public async Task Pairing_with_wrong_token_is_rejected()
    {
        await Assert.ThrowsAnyAsync<Exception>(
            () => _client.PairWithDeviceTokenAsync("wrong-token"));
        Assert.False(_client.IsAuthenticated);
    }

    [Fact]
    public async Task Token_exchange_returns_a_jwt()
    {
        await _client.PairWithDeviceTokenAsync(FakePrintelyxBackend.ValidDeviceToken);
        var jwt = await _client.ExchangeDeviceTokenForJwtAsync();
        Assert.Equal("jwt-for-socketio-unit-test", jwt);
    }

    [Fact]
    public async Task Pending_jobs_map_to_records_with_download_urls()
    {
        await _client.PairWithDeviceTokenAsync(FakePrintelyxBackend.ValidDeviceToken);
        var jobs = await _client.FetchPendingJobsAsync();
        var job = Assert.Single(jobs);
        Assert.Equal("job-e2e-1", job.JobId);
        Assert.Equal(2, job.Copies);
        Assert.Equal("bw", job.ColorMode);
        Assert.NotNull(job.PdfUrl);
        Assert.Contains("/files/job-e2e-1.pdf", job.PdfUrl!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_verifies_sha256_of_the_served_bytes()
    {
        await _client.PairWithDeviceTokenAsync(FakePrintelyxBackend.ValidDeviceToken);
        var jobs = await _client.FetchPendingJobsAsync();
        var job = jobs[0];

        var good = await _client.DownloadPdfAsync(job.JobId, job.PdfUrl!, _backend.Sha256HexOfTestPdf());
        Assert.True(File.Exists(good.LocalPath));
        Assert.True(good.SizeBytes > 0);

        // A wrong checksum must be refused (integrity gate).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _client.DownloadPdfAsync(job.JobId, job.PdfUrl!, new string('0', 64)));
    }

    [Fact]
    public async Task Claim_is_atomic_and_completion_advances_the_state_machine()
    {
        await _client.PairWithDeviceTokenAsync(FakePrintelyxBackend.ValidDeviceToken);

        var claim = await _client.ReportJobStatusAsync("job-e2e-1", "printing", "HP Test Printer", errorMessage: null);
        Assert.True(claim.Applied);
        Assert.False(claim.Skipped);

        var complete = await _client.ReportJobStatusAsync("job-e2e-1", "completed", "HP Test Printer", errorMessage: null);
        Assert.True(complete.Applied);

        // Completed jobs are terminal: another claim is skipped with a reason.
        var second = await _client.ReportJobStatusAsync("job-e2e-1", "printing", "HP Test Printer", errorMessage: null);
        Assert.False(second.Applied);
        Assert.True(second.Skipped);
    }

    [Fact]
    public async Task Crash_leftover_claim_is_skipped_with_the_enriched_reason()
    {
        await _client.PairWithDeviceTokenAsync(FakePrintelyxBackend.ValidDeviceToken);
        _backend.SeedClaimed("job-e2e-1");

        var report = await _client.ReportJobStatusAsync("job-e2e-1", "printing", "HP Test Printer", errorMessage: null);
        Assert.False(report.Applied);
        Assert.True(report.Skipped);
        Assert.Contains("previous session", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Receipt_acknowledgement_schedules_cloud_cleanup()
    {
        await _client.PairWithDeviceTokenAsync(FakePrintelyxBackend.ValidDeviceToken);
        var receipt = await _client.MarkJobReceivedAsync(
            "job-e2e-1", "sha256:" + _backend.Sha256HexOfTestPdf(), 1024, DateTimeOffset.UtcNow);
        Assert.Equal("job-e2e-1", receipt.JobId);
        Assert.NotNull(receipt.CloudDeleteAfter);
    }

    [Fact]
    public async Task Full_lifecycle_follows_the_production_sequence()
    {
        // The exact sequence the JobProcessor drives (docs/AUDIT.md §4):
        // pair → pending → claim(printing) → received → completed.
        await _client.PairWithDeviceTokenAsync(FakePrintelyxBackend.ValidDeviceToken);
        var jobs = await _client.FetchPendingJobsAsync();
        var job = jobs[0];

        var claim = await _client.ReportJobStatusAsync(job.JobId, "printing", "HP Test Printer", errorMessage: null);
        Assert.True(claim.Applied);

        var receipt = await _client.MarkJobReceivedAsync(
            job.JobId, _backend.Sha256HexOfTestPdf(), 1024, DateTimeOffset.UtcNow);
        Assert.NotNull(receipt.CloudDeleteAfter);

        var complete = await _client.ReportJobStatusAsync(job.JobId, "completed", "HP Test Printer", errorMessage: null);
        Assert.True(complete.Applied);

        Assert.Equal(
            new[] { ("job-e2e-1", "printing"), ("job-e2e-1", "completed") },
            _backend.StatusLog.Select(s => (s.JobId, s.Status)).ToArray());
    }

    [Fact]
    public async Task Unauthorized_calls_fail_closed()
    {
        // No pairing — every authenticated call must throw.
        await Assert.ThrowsAnyAsync<Exception>(() => _client.FetchPendingJobsAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => _client.ExchangeDeviceTokenForJwtAsync());
    }

    public void Dispose()
    {
        _backend.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
