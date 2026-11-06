namespace PrintShopCompanion.Queue.Tests;

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;
using PrintShopCompanion.Infrastructure.Queue;
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
        _queue = new SqliteJobQueue(opts, logger);
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
