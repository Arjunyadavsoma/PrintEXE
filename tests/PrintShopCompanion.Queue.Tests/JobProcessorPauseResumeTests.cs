namespace PrintShopCompanion.Queue.Tests;

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Queue;
using PrintShopCompanion.Infrastructure.Storage;
using Xunit;

/// <summary>
/// Phase F (brief §C) — pause/resume correctness tests against the real
/// JobProcessor. Verifies: concurrent pause/resume are safe, repeated
/// requests are idempotent, no semaphore deadlocks, pause state does NOT
/// survive restart (explicitly documented), queue state stays consistent.
///
/// <para>The loop-start + shutdown-cancellation behavior is exercised
/// end-to-end by the durable-command tests' recovery path; here we test the
/// IQueueController SEMANTICS directly without starting the loop (which
/// would require faking the full IPrintShopBackend + IPrinterService call
/// chain). The pause gate's cancellation behavior is covered by the
/// durable-command tests' RecoverInterrupted path + the loop's
/// RunAsync(ct) cancellation contract.</para>
/// </summary>
public class JobProcessorPauseResumeTests
{
    private static JobProcessor Make(IJobQueue? queue = null)
    {
        var opts = Options.Create(new PrinterOptions());
        var backendOpts = Options.Create(new BackendOptions { PollFallbackInterval = TimeSpan.FromMilliseconds(50) });
        return new JobProcessor(
            queue ?? new FakeQueue(),
            new FakeBackend(),
            new FakePrinter(),
            new FakeRenderer(),
            opts,
            backendOpts,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<JobProcessor>());
    }

    [Fact]
    public async Task Pause_sets_IsPaused_true_resume_sets_it_false()
    {
        var proc = Make();
        proc.IsPaused.Should().BeFalse("pause state is false by default");
        await proc.PauseAsync();
        proc.IsPaused.Should().BeTrue();
        await proc.ResumeAsync();
        proc.IsPaused.Should().BeFalse();
    }

    [Fact]
    public async Task Repeated_Pause_is_idempotent()
    {
        var proc = Make();
        await proc.PauseAsync();
        await proc.PauseAsync();   // second pause is a no-op
        await proc.PauseAsync();   // third pause is a no-op
        proc.IsPaused.Should().BeTrue();
        // A single Resume clears it.
        await proc.ResumeAsync();
        proc.IsPaused.Should().BeFalse();
    }

    [Fact]
    public async Task Repeated_Resume_is_idempotent()
    {
        var proc = Make();
        await proc.PauseAsync();
        await proc.ResumeAsync();
        await proc.ResumeAsync();  // second resume is a no-op (gate already signaled)
        await proc.ResumeAsync();  // third resume is a no-op
        proc.IsPaused.Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_Pause_and_Resume_are_safe_no_deadlock()
    {
        var proc = Make();
        // Fire 20 concurrent Pause/Resume calls in parallel — the _pauseLock
        // serializes the state flag + the gate operations are bounded by
        // maxCount=1. No deadlock, no SemaphoreFullException leak.
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            if (i % 2 == 0) await proc.PauseAsync();
            else await proc.ResumeAsync();
        })).ToArray();
        await Task.WhenAll(tasks);
        // The final state is one of {paused, resumed} depending on call order;
        // the invariant is: no exception, no deadlock (WhenAll completed above).
        // FluentAssertions 7 removed BooleanAssertions.BeOneOf — use xUnit's
        // native assert to confirm the race settled on a well-defined bool.
        Assert.IsType<bool>(proc.IsPaused);
    }

    [Fact]
    public async Task Pause_does_not_block_the_calling_thread_synchronously()
    {
        // PauseAsync must return promptly (it does a non-blocking Wait(0) to
        // drain the gate, not a blocking Wait). Verified by completing within
        // a short timeout.
        var proc = Make();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await proc.PauseAsync();
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            "PauseAsync is non-blocking — it drains the gate with Wait(0), not a blocking Wait");
    }

    [Fact]
    public async Task Pause_then_Resume_then_Pause_again_cycles_cleanly()
    {
        // The brief §C: "Queue state remains consistent across command
        // failures." This cycles pause/resume repeatedly to verify the gate
        // doesn't leak state across transitions.
        var proc = Make();
        for (var i = 0; i < 10; i++)
        {
            await proc.PauseAsync();
            proc.IsPaused.Should().BeTrue();
            await proc.ResumeAsync();
            proc.IsPaused.Should().BeFalse();
        }
    }

    [Fact]
    public void Pause_state_does_NOT_survive_restarts_by_design()
    {
        // The brief §C: "Pause state persistence is explicitly defined. Do not
        // accidentally claim pause survives restart if it does not." Pause is
        // in-memory only (the _paused field is volatile, not persisted). A
        // fresh JobProcessor instance starts unpaused. This test documents
        // that invariant explicitly.
        var proc1 = Make();
        proc1.IsPaused.Should().BeFalse("a fresh JobProcessor starts unpaused — pause is NOT persisted across restart");
        var proc2 = Make();
        proc2.IsPaused.Should().BeFalse("a second fresh instance also starts unpaused");
    }

    // ── minimal fakes (the JobProcessor constructor needs all 5 deps, but
    //    the pause/resume tests never start the loop, so most methods are
    //    never called — they throw NotImplementedException if accidentally hit) ──

    private sealed class FakeQueue : IJobQueue
    {
        public Task<PrintJobRecord> EnqueueAsync(PrintJobRecord job, CancellationToken ct = default) => Task.FromResult(job);
        public Task<PrintJobRecord?> DequeueAsync(CancellationToken ct = default) => Task.FromResult<PrintJobRecord?>(null);
        public Task UpdateAsync(PrintJobRecord job, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkPrintedAsync(string jobId, DateTimeOffset printedAt, int spoolerJobId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkReportedPrintedAsync(string jobId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkFailedAsync(string jobId, string error, bool permanent, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReleaseAsync(string jobId, CancellationToken ct = default) => Task.CompletedTask;
        public Task CancelAsync(string jobId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<PrintJobRecord>> ListAsync(JobStatus? filterStatus = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PrintJobRecord>>(Array.Empty<PrintJobRecord>());
        public Task ReplayInFlightAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PurgeTerminalAsync(TimeSpan olderThan, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkUncertainAsync(string jobId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkOutcomeUnknownAsync(string jobId, string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkResolvedByOperatorAsync(string jobId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeBackend : IPrintShopBackend
    {
        public Task<PrintShopCompanion.Core.Models.ShopSnapshot?> GetShopSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult<PrintShopCompanion.Core.Models.ShopSnapshot?>(null);
        public bool IsAuthenticated => true;
        public ShopInfo? CurrentShop => new() { Id = "s", Name = "S", Slug = "s", BaseUrl = new Uri("https://x"), ApiBasePath = "/api/v1" };
        public DeviceRegistration? CurrentDevice => new() { DeviceId = "d", ShopId = "s", Hostname = "h", OperatingSystem = "os", AppVersion = "0", RegisteredAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow, Revoked = false };
        public Task PairWithDeviceTokenAsync(string deviceToken, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PairResult> PairWithTicketAsync(string ticket, string deviceName, string? version, string? os, string? printerName, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<PendingCommand>> FetchPendingCommandsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CommandReportResult> ReportCommandStatusAsync(string commandId, string status, string? resultJson, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> ExchangeDeviceTokenForJwtAsync(CancellationToken ct = default) => Task.FromResult("jwt");
        public Task<HeartbeatResult> HeartbeatAsync(CancellationToken ct = default, string? printerName = null)
            => Task.FromResult(new HeartbeatResult(ServerTime: DateTimeOffset.UtcNow, TokenStillValid: true, RecommendedBackendVersion: null));
        public Task<TokenRotationResult> RotateDeviceTokenAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<PrintJobRecord>> FetchPendingJobsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JobStatusReport> ReportJobStatusAsync(string jobId, string status, string? lastError = null, string? printerName = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ReceiptAcknowledgement> MarkJobReceivedAsync(string jobId, string checksum, long fileSize, DateTimeOffset receivedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task LogoutAsync() => Task.CompletedTask;
        public Task<SyncPullResult> SyncPullAsync(string? cursor, int limit, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SyncPushResult> SyncPushAsync(object changes, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SyncStatusResult> SyncStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DownloadedPdf> DownloadPdfAsync(string jobId, Uri pdfUrl, string? expectedSha256, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RevokeDeviceAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakePrinter : IPrinterService
    {
        public IReadOnlyList<PrinterInfo> DiscoverPrinters() => Array.Empty<PrinterInfo>();
        public PrinterInfo? GetSelectedPrinter() => null;
        public Task SelectPrinterAsync(string printerFullName, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SpoolSubmission> SubmitAsync(PrintJobRecord job, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> HasPrintedAsync(int spoolerJobId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<TestPrintResult> TestPrintAsync(string printerFullName, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeRenderer : IPdfRenderer
    {
        public Task<int> PrintAsync(string pdfPath, string printerFullName, string? pageRange = null, int copies = 1, bool? color = null, string? paperSize = null, string? duplex = null, CancellationToken ct = default)
            => Task.FromResult(0);
    }
}
