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
/// Tests for the durable queue's idempotency, retry, and crash-replay behaviour.
/// These are the project's non-negotiable guarantees and must never regress.
/// </summary>
public class SqliteJobQueueTests : IDisposable
{
    private readonly SqliteJobQueue _queue;
    private readonly string _tempDir;

    public SqliteJobQueueTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PrintShopTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var opts = Microsoft.Extensions.Options.Options.Create(new StorageOptions
        {
            QueueDbPath = Path.Combine(_tempDir, "queue.db"),
            TerminalJobTtl = TimeSpan.FromDays(7)
        });
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteJobQueue>();
        // v0.5: SqliteJobQueue now takes LocalStorageLayout + QueueKeyStore?.
        // For tests we pass null for the key store (no encryption) and a
        // minimal LocalStorageLayout pointing at the temp dir.
        var layout = new LocalStorageLayout(opts);
        _queue = new SqliteJobQueue(opts, layout, keyStore: null, logger);
    }

    [Fact]
    public async Task Enqueue_is_idempotent_on_JobId()
    {
        var job = MakeJob("J-1", "Job One");
        var inserted = await _queue.EnqueueAsync(job);

        // Re-enqueue the same JobId — should be a no-op (idempotent on JobId).
        var result = await _queue.EnqueueAsync(job);

        result.JobId.Should().Be("J-1");
        result.Title.Should().Be("Job One", "the second enqueue should NOT overwrite the original");
        var all = await _queue.ListAsync();
        all.Should().HaveCount(1);
    }

    [Fact]
    public async Task Dequeue_returns_pending_job_in_order()
    {
        await _queue.EnqueueAsync(MakeJob("J-1", "First"));
        await _queue.EnqueueAsync(MakeJob("J-2", "Second"));

        var one = await _queue.DequeueAsync();
        one!.JobId.Should().Be("J-1");

        var two = await _queue.DequeueAsync();
        two!.JobId.Should().Be("J-2");

        (await _queue.DequeueAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Failed_job_is_not_redequed_until_next_attempt_at_has_passed()
    {
        await _queue.EnqueueAsync(MakeJob("J-1", "Job One"));
        var job = await _queue.DequeueAsync();
        job!.JobId.Should().Be("J-1");
        job.AttemptCount.Should().Be(1);
        job.Status.Should().Be(JobStatus.Downloading);

        await _queue.MarkFailedAsync("J-1", "transient", permanent: false);

        // Immediately after, the failed job should NOT be dequeued (it has a future next_attempt_at).
        (await _queue.DequeueAsync()).Should().BeNull();
    }

    [Fact]
    public async Task MarkPrinted_sets_PrintedAt_and_status()
    {
        await _queue.EnqueueAsync(MakeJob("J-1", "Job One"));
        await _queue.DequeueAsync();
        var printedAt = DateTimeOffset.UtcNow;
        await _queue.MarkPrintedAsync("J-1", printedAt, spoolerJobId: 42);

        var all = await _queue.ListAsync();
        all[0].Status.Should().Be(JobStatus.Printed);
        all[0].PrintedAt.Should().Be(printedAt);
        all[0].SpoolerJobId.Should().Be(42);
    }

    [Fact]
    public async Task ReplayInFlight_returns_in_flight_jobs_to_Pending()
    {
        await _queue.EnqueueAsync(MakeJob("J-1", "Job One"));
        await _queue.DequeueAsync();   // pulls it to Downloading (in-flight)

        await _queue.ReplayInFlightAsync();
        var all = await _queue.ListAsync();
        all[0].Status.Should().Be(JobStatus.Pending);
    }

    [Fact]
    public async Task PurgeTerminal_drops_only_old_terminal_jobs()
    {
        await _queue.EnqueueAsync(MakeJob("J-1", "Old printed"));
        await _queue.DequeueAsync();
        await _queue.MarkPrintedAsync("J-1", DateTimeOffset.UtcNow, 1);
        await _queue.MarkReportedPrintedAsync("J-1");

        // Simulate "older than TTL" — purge with zero TTL drops everything.
        await _queue.PurgeTerminalAsync(TimeSpan.Zero);

        var all = await _queue.ListAsync();
        all.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_moves_job_to_Cancelled_state()
    {
        await _queue.EnqueueAsync(MakeJob("J-1", "Job One"));
        await _queue.CancelAsync("J-1");
        var all = await _queue.ListAsync();
        all[0].Status.Should().Be(JobStatus.Cancelled);
    }

    [Fact]
    public async Task ReplayInFlight_does_not_reset_UncertainSpooled_or_OutcomeUnknown()
    {
        // SAFETY INVARIANT (PrintJobRecord.cs UncertainSpooled doc + docs/AUDIT.md §5 bug #3):
        // UncertainSpooled and OutcomeUnknown mean "we don't know whether the paper came out".
        // Auto-replaying them to Pending on restart risks printing the same job twice.
        // They must persist across restarts and require explicit operator resolution.

        // UncertainSpooled: enqueue → dequeue (to Downloading) → MarkUncertain.
        await _queue.EnqueueAsync(MakeJob("J-uncertain", "Uncertain"));
        await _queue.DequeueAsync(); // Pending -> Downloading (required by MarkUncertainAsync WHERE clause)
        await _queue.MarkUncertainAsync("J-uncertain", "spooler purged before confirmation", CancellationToken.None);

        // OutcomeUnknown: enqueue → dequeue → MarkPrinted → MarkOutcomeUnknown.
        await _queue.EnqueueAsync(MakeJob("J-unknown", "Unknown"));
        await _queue.DequeueAsync();
        await _queue.MarkPrintedAsync("J-unknown", DateTimeOffset.UtcNow, spoolerJobId: 1, CancellationToken.None);
        await _queue.MarkOutcomeUnknownAsync("J-unknown", "backend report 5xx", CancellationToken.None);

        await _queue.ReplayInFlightAsync();
        var all = await _queue.ListAsync();
        all.Single(j => j.JobId == "J-uncertain").Status.Should().Be(JobStatus.UncertainSpooled,
            "uncertain spooler jobs must NOT be auto-replayed to Pending");
        all.Single(j => j.JobId == "J-unknown").Status.Should().Be(JobStatus.OutcomeUnknown,
            "outcome-unknown jobs must NOT be auto-replayed to Pending");
    }

    [Fact]
    public async Task UpdateAsync_round_trips_v05_columns()
    {
        // DATA-LOSS BUG FIX (docs/AUDIT.md §5 bugs #1 + #2): ReadRecord/WriteStatusAsync
        // previously dropped the v0.5 columns (color_mode, page_count, original_filename,
        // order_token, delivery_status, storage_status, checksum, file_size, received_at,
        // cloud_delete_after, local_delete_after, delivery_attempt_count, last_delivery_error).
        // After a restart the values were silently lost. This test verifies they survive an
        // enqueue → update → list round-trip through SQLite.
        var job = MakeJob("J-v05", "V05 Round-trip");
        job.ColorMode = "color";
        job.PageCount = 7;
        job.OriginalFilename = "invoice-204.pdf";
        job.OrderToken = "ord_abc";
        job.FileSize = 1234567L;
        job.Checksum = "sha256:abc123";
        await _queue.EnqueueAsync(job);

        // Mutate the v0.5 columns via a status update (DownloadPdfAsync path sets these).
        // PrintJobRecord is a class with set properties; mutate then call UpdateAsync.
        var fetched = (await _queue.ListAsync()).Single(j => j.JobId == "J-v05");
        fetched.DeliveryStatus = DeliveryStatus.Received;
        fetched.StorageStatus = StorageStatus.LocalVerified;
        fetched.ReceivedAt = DateTimeOffset.UtcNow;
        fetched.CloudDeleteAfter = DateTimeOffset.UtcNow.AddMinutes(15);
        fetched.LocalDeleteAfter = DateTimeOffset.UtcNow.AddMinutes(60);
        fetched.DeliveryAttemptCount = 3;
        fetched.LastDeliveryError = "transient timeout";
        await _queue.UpdateAsync(fetched, CancellationToken.None);

        // Re-read and assert the v0.5 columns survived the round-trip through SQLite.
        var roundTripped = (await _queue.ListAsync()).Single(j => j.JobId == "J-v05");
        roundTripped.ColorMode.Should().Be("color");
        roundTripped.PageCount.Should().Be(7);
        roundTripped.OriginalFilename.Should().Be("invoice-204.pdf");
        roundTripped.OrderToken.Should().Be("ord_abc");
        roundTripped.FileSize.Should().Be(1234567L);
        roundTripped.Checksum.Should().Be("sha256:abc123");
        roundTripped.DeliveryStatus.Should().Be(DeliveryStatus.Received);
        roundTripped.StorageStatus.Should().Be(StorageStatus.LocalVerified);
        roundTripped.ReceivedAt.Should().NotBeNull();
        roundTripped.CloudDeleteAfter.Should().NotBeNull();
        roundTripped.LocalDeleteAfter.Should().NotBeNull();
        roundTripped.DeliveryAttemptCount.Should().Be(3);
        roundTripped.LastDeliveryError.Should().Be("transient timeout");
    }

    private static PrintJobRecord MakeJob(string id, string title) => new()
    {
        JobId = id,
        ShopId = "shop_demo",
        DeviceId = "dev_001",
        Title = title,
        Copies = 1,
        Status = JobStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
        QueuedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
